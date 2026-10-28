// Narrow CDP adapter adapted from OpenCode gui-extensions/src/browser/lighthouse.ts.
// MIT copyright notice: OpenCode-LICENSE.txt. Lighthouse remains Apache-2.0.
import {EventEmitter} from 'node:events';
import {parentPort, workerData} from 'node:worker_threads';

const sessions = new Map();
const pending = new Map();
let sequence = 0;
const send = (method, params = {}, sessionId = null) => new Promise((resolve, reject) => {
  const id = ++sequence;
  if (pending.size >= 256) { reject(new Error('Audit CDP callback limit exceeded')); return; }
  pending.set(id, {resolve, reject});
  parentPort.postMessage({type: 'cdp', id, method, params, sessionId});
});
function session(id) {
  if (sessions.has(id)) return sessions.get(id);
  const value = Object.assign(new EventEmitter(), {
    id: () => id,
    send: (method, params) => send(method, params, id),
    detach: async () => {
      if (!sessions.delete(id)) return;
      await send('Target.detachFromTarget', {sessionId: id});
    },
  });
  sessions.set(id, value);
  return value;
}
parentPort.on('message', message => {
  if (message.type === 'response') {
    const callback = pending.get(message.id);
    pending.delete(message.id);
    if (message.error) callback?.reject(new Error(message.error)); else callback?.resolve(message.result);
    return;
  }
  if (message.type !== 'event') return;
  const owner = sessions.get(message.sessionId);
  if (!owner) return;
  if (message.method === 'Target.attachedToTarget') owner.emit('sessionattached', session(message.params.sessionId));
  owner.emit(message.method, message.params);
  owner.emit('*', message.method, message.params);
});

try {
  const {snapshot, generateReport} = await import('lighthouse');
  const info = await send('Target.getTargetInfo');
  const attached = await send('Target.attachToTarget', {targetId: info.targetInfo.targetId, flatten: true});
  const root = session(attached.sessionId);
  const page = {url: () => workerData.url, target: () => ({createCDPSession: async () => root})};
  const result = await snapshot(page, {
    flags: {
      onlyCategories: ['accessibility', 'seo', 'best-practices'],
      formFactor: 'desktop',
      screenEmulation: {disabled: true},
      throttlingMethod: 'provided',
      disableStorageReset: true,
      disableFullPageScreenshot: true,
    },
  });
  if (!result) throw new Error('Lighthouse did not produce a snapshot report.');
  const reports = ['json', 'html'].map(format => {
    const data = Buffer.from(generateReport(result.lhr, format));
    if (data.length > 5 * 1024 * 1024) throw new Error(`Lighthouse ${format} report exceeds the 5 MiB protocol file limit`);
    return {name: `lighthouse.${format}`, mime: format === 'json' ? 'application/json' : 'text/html', data: data.toString('base64')};
  });
  const value = {
    scores: Object.values(result.lhr.categories).map(({id, title, score}) => ({id, title, score})),
    failures: Object.values(result.lhr.audits).filter(audit => audit.score !== null && audit.score < 1)
      .map(({id, title, description}) => ({id, title, description})).slice(0, 100),
  };
  parentPort.postMessage({type: 'result', result: {value, reports}});
} catch (error) {
  parentPort.postMessage({type: 'error', error: String(error?.message ?? error)});
}
// Main thread owns final cleanup even when this worker is forcibly terminated.
