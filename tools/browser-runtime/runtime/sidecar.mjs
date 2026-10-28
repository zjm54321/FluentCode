import {readFile} from 'node:fs/promises';
import {Worker} from 'node:worker_threads';
import {JsonPeer} from './json-peer.mjs';
import {createTunnelProxy} from './tunnel-proxy.mjs';

let proxy;
let audit;
let shuttingDown = false;
const peer = new JsonPeer(process.stdin, process.stdout, async (method, params, signal) => {
  switch (method) {
    case 'attachment.set': proxy.bind(params.leaseId ?? null); return null;
    case 'audit.start': return runAudit(params, signal);
    case 'audit.cancel': {
      const current = audit;
      if (current?.id === params.auditId) {
        current.abort.abort(new Error('Audit cancelled'));
        await current.finished;
      }
      return null;
    }
    case 'runtime.stop': setImmediate(() => void shutdown()); return null;
    default: throw new Error(`Unknown runtime method: ${method}`);
  }
});
peer.on('event', event => {
  if (event.event !== 'cdp' || !audit || event.auditId !== audit.id) return;
  if (!audit.sessions.has(event.sessionId)) return;
  if (event.method === 'Target.attachedToTarget') audit.sessions.add(event.params.sessionId);
  if (!audit.closed) audit.worker.postMessage({type: 'event', method: event.method, params: event.params, sessionId: event.sessionId});
});
peer.on('closed', () => void shutdown());
async function shutdown() {
  if (shuttingDown) return;
  shuttingDown = true;
  audit?.abort.abort(new Error('Runtime stopping'));
  await proxy?.close();
  // Also covers a broken parent that cannot consume cleanup callbacks.
  setTimeout(() => process.exit(0), 2200).unref();
  process.stdin.destroy();
}

async function runAudit(params, signal) {
  if (audit) throw new Error('An audit is already running');
  if (typeof params.auditId !== 'string' || !params.auditId || typeof params.url !== 'string') throw new Error('Invalid audit request');
  const abort = new AbortController();
  const linked = AbortSignal.any([signal, abort.signal, AbortSignal.timeout(50_000)]);
  const worker = new Worker(new URL('./audit-worker.mjs', import.meta.url), {
    workerData: {url: params.url}, stdout: true, stderr: true,
    resourceLimits: {maxOldGenerationSizeMb: 512},
  });
  worker.stdout.resume();
  worker.stderr.resume();
  let finish;
  const finished = new Promise(resolve => { finish = resolve; });
  const state = {id: params.auditId, worker, abort, sessions: new Set(), closed: false, finished};
  audit = state;
  let stop;
  try {
    return await new Promise((resolve, reject) => {
      stop = () => reject(linked.reason ?? new Error('Audit cancelled'));
      linked.addEventListener('abort', stop, {once: true});
      if (linked.aborted) stop();
      worker.once('error', reject);
      worker.once('exit', code => reject(new Error(`Audit worker exited (${code})`)));
      worker.on('message', message => {
        if (message.type === 'result') { resolve(message.result); return; }
        if (message.type === 'error') { reject(new Error(message.error)); return; }
        if (message.type !== 'cdp' || linked.aborted) return;
        void peer.call('cdp.send', {auditId: state.id, method: message.method,
          params: message.params, sessionId: message.sessionId}, linked).then(result => {
          if (message.method === 'Target.attachToTarget') state.sessions.add(result.sessionId);
          if (message.method === 'Target.detachFromTarget') state.sessions.delete(message.params.sessionId);
          if (!linked.aborted) worker.postMessage({type: 'response', id: message.id, result});
        }, error => {
          if (!linked.aborted) worker.postMessage({type: 'response', id: message.id, error: String(error.message)});
        });
      });
    });
  } finally {
    state.closed = true;
    linked.removeEventListener('abort', stop);
    abort.abort();
    await worker.terminate();
    // Cleanup requests deliberately do not use the cancelled audit signal.
    await Promise.all([...state.sessions].map(sessionId => peer.call('cdp.send', {
      auditId: state.id, method: 'Target.detachFromTarget', params: {sessionId}, sessionId: null,
    }, AbortSignal.timeout(2000)).catch(() => {})));
    if (audit === state) audit = undefined;
    finish();
  }
}

try {
  const catalogues = await Promise.all(['browser_protocol', 'js_protocol'].map(async name =>
    JSON.parse(await readFile(new URL(`./node_modules/devtools-protocol/json/${name}.json`, import.meta.url), 'utf8'))));
  const cdpEventNames = [...new Set(catalogues.flatMap(protocol => protocol.domains.flatMap(domain =>
    (domain.events ?? []).map(event => `${domain.domain}.${event.name}`))))].sort();
  proxy = await createTunnelProxy((method, params, signal) => peer.call(method, params, signal));
  await peer.send({event: 'runtime.ready', protocol: 1, proxy: proxy.options, cdpEventNames});
} catch (error) {
  process.stderr.write(`Browser runtime failed: ${error.message}\n`);
  await shutdown();
  process.exitCode = 1;
}
