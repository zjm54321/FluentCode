import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {once} from 'node:events';
import {existsSync} from 'node:fs';
import {mkdir, readFile, rm} from 'node:fs/promises';
import {createServer} from 'node:http';
import path from 'node:path';
import {test} from 'node:test';
import {setTimeout as delay} from 'node:timers/promises';
import {fileURLToPath} from 'node:url';
import {JsonPeer} from '../../artifacts/browser-runtime/json-peer.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const chromePath = process.env.BROWSER_RUNTIME_TEST_CHROME ?? 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';

test('real Lighthouse 13.6.0 snapshot through flattened CDP callbacks', {timeout: 60000, skip: !existsSync(chromePath)}, async () => {
  const profile = path.join(root, 'artifacts', `browser-runtime-audit-${process.pid}`);
  await mkdir(profile, {recursive: true});
  const server = createServer((request, response) => {
    response.setHeader('Content-Type', 'text/html');
    if (request.url === '/frame') { response.end('<!doctype html><html lang="en"><title>Child frame</title><h2>Child renderer</h2></html>'); return; }
    response.end(`<!doctype html><html lang="en"><head><title>Controlled runtime snapshot</title><meta name="description" content="A controlled Lighthouse snapshot fixture"><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><main><h1>Runtime audit</h1><button></button><img src="data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"><iframe title="Child renderer" src="http://localhost:${server.address().port}/frame"></iframe></main></body></html>`);
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const url = `http://127.0.0.1:${server.address().port}/`;
  const chrome = spawn(chromePath, ['--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`,
    '--no-first-run', '--no-default-browser-check', '--disable-background-networking', '--disable-component-update',
    '--disable-sync', '--disable-extensions', '--site-per-process', 'about:blank'], {windowsHide: true, stdio: 'ignore'});
  let socket;
  let child;
  const callbacks = new Map();
  let sequence = 0;
  let peer;
  let activeAudit;
  const methods = [];
  const owned = new Set();
  const detached = new Set();
  let holdEvaluation = false;
  let evaluationStarted;
  let cleanupObserved;
  const send = (method, params = {}, sessionId = null) => new Promise((resolve, reject) => {
    const id = ++sequence;
    callbacks.set(id, {resolve, reject});
    socket.send(JSON.stringify({id, method, params, ...(sessionId ? {sessionId} : {})}));
  });
  try {
    let endpoint;
    for (let attempt = 0; attempt < 200; attempt++) {
      try {
        const lines = (await readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).trim().split(/\r?\n/);
        endpoint = `ws://127.0.0.1:${lines[0]}${lines[1]}`;
        break;
      } catch { await delay(50); }
    }
    assert.ok(endpoint, 'Controlled Chrome did not expose its debugging endpoint');
    socket = new WebSocket(endpoint);
    await once(socket, 'open');
    let baseSession;
    socket.addEventListener('message', event => {
      const message = JSON.parse(event.data);
      if (message.id) {
        const callback = callbacks.get(message.id);
        callbacks.delete(message.id);
        if (message.error) callback?.reject(new Error(message.error.message)); else callback?.resolve(message.result);
      } else if (activeAudit && peer) {
        if (owned.has(message.sessionId) && message.method === 'Target.attachedToTarget') owned.add(message.params.sessionId);
        void peer.send({event: 'cdp', auditId: activeAudit, method: message.method, params: message.params,
          sessionId: message.sessionId === baseSession ? null : message.sessionId ?? null}).catch(() => {});
      }
    });
    const targets = await send('Target.getTargets');
    const target = targets.targetInfos.find(info => info.type === 'page');
    baseSession = (await send('Target.attachToTarget', {targetId: target.targetId, flatten: true})).sessionId;
    await send('Page.enable', {}, baseSession);
    await send('Page.navigate', {url}, baseSession);
    for (let attempt = 0; attempt < 100; attempt++) {
      const state = await send('Runtime.evaluate', {expression: 'document.readyState', returnByValue: true}, baseSession);
      if (state.result.value === 'complete') break;
      await delay(50);
    }
    child = spawn(process.execPath, [path.join(root, 'artifacts/browser-runtime/sidecar.mjs')], {windowsHide: true, stdio: ['pipe', 'pipe', 'pipe']});
    child.stderr.on('data', data => process.stderr.write(data));
    peer = new JsonPeer(child.stdout, child.stdin, async (method, input, signal) => {
      assert.equal(method, 'cdp.send');
      methods.push(input.method);
      if (input.sessionId) assert.ok(owned.has(input.sessionId), 'Audit used a foreign CDP session');
      if (input.method === 'Target.detachFromTarget') {
        assert.ok(owned.has(input.params.sessionId), 'Audit detached a foreign session');
        detached.add(input.params.sessionId);
        cleanupObserved?.();
      }
      if (holdEvaluation && input.method === 'Runtime.evaluate') {
        evaluationStarted();
        return new Promise((_resolve, reject) => signal.addEventListener('abort', () => reject(signal.reason), {once: true}));
      }
      const result = await send(input.method, input.params, input.sessionId ?? baseSession);
      if (input.method === 'Target.attachToTarget') owned.add(result.sessionId);
      return result;
    });
    await once(peer, 'event');
    activeAudit = 'real-controlled-audit';
    const started = performance.now();
    const result = await peer.call('audit.start', {auditId: activeAudit, url}, AbortSignal.timeout(55000));
    assert.ok(performance.now() - started < 58000);
    assert.deepEqual(result.value.scores.map(score => score.id).sort(), ['accessibility', 'best-practices', 'seo']);
    assert.ok(result.value.failures.some(failure => failure.id === 'button-name'));
    assert.equal(result.reports.length, 2);
    for (const report of result.reports) assert.ok(Buffer.from(report.data, 'base64').length <= 5 * 1024 * 1024);
    const json = JSON.parse(Buffer.from(result.reports.find(report => report.name.endsWith('.json')).data, 'base64'));
    assert.equal(json.lighthouseVersion, '13.6.0');
    assert.equal(json.gatherMode, 'snapshot');
    assert.equal(json.configSettings.formFactor, 'desktop');
    assert.equal(json.configSettings.screenEmulation.disabled, true);
    assert.equal(json.fullPageScreenshot, undefined);
    assert.match(Buffer.from(result.reports.find(report => report.name.endsWith('.html')).data, 'base64').toString(), /Lighthouse/);
    for (const forbidden of ['Page.navigate', 'Page.captureScreenshot', 'Emulation.setDeviceMetricsOverride', 'Storage.clearDataForOrigin'])
      assert.ok(!methods.includes(forbidden), `Snapshot unexpectedly used ${forbidden}`);
    assert.ok(owned.size > 1, 'Expected a dedicated child renderer session');
    assert.equal(detached.size, owned.size);
    // The caller's original page session still works after audit cleanup.
    assert.equal((await send('Runtime.evaluate', {expression: 'document.title', returnByValue: true}, baseSession)).result.value, 'Controlled runtime snapshot');
    console.log(`Real snapshot: ${result.value.scores.map(score => `${score.id}=${score.score}`).join(', ')}; ${Math.round(performance.now() - started)}ms; owned sessions=${owned.size}`);

    // Cancel after real sessions were attached, not merely during worker startup.
    owned.clear();
    detached.clear();
    activeAudit = 'cancel-owned-audit';
    holdEvaluation = true;
    const evaluating = new Promise(resolve => { evaluationStarted = resolve; });
    const cleaned = new Promise(resolve => { cleanupObserved = resolve; });
    const cancel = new AbortController();
    const cancelledAudit = peer.call('audit.start', {auditId: activeAudit, url}, cancel.signal);
    await Promise.race([evaluating, delay(10000, null, {ref: false}).then(() => { throw new Error('Audit did not reach evaluation'); })]);
    cancel.abort(new Error('Controlled owned-session cancellation'));
    await assert.rejects(cancelledAudit, /cancellation/);
    await Promise.race([cleaned, delay(5000, null, {ref: false}).then(() => { throw new Error('Cancelled audit did not detach owned sessions'); })]);
    assert.ok(owned.size > 0 && detached.size > 0);
    assert.equal((await send('Runtime.evaluate', {expression: 'document.title', returnByValue: true}, baseSession)).result.value, 'Controlled runtime snapshot');
    console.log(`Cancelled worker cleaned audit-owned sessions; original page remains usable`);
  } finally {
    if (child && child.exitCode === null) { const exit = once(child, 'exit'); child.kill(); await exit; }
    socket?.close();
    if (chrome.exitCode === null) { const exit = once(chrome, 'exit'); chrome.kill(); await exit; }
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
    // Chrome may hold its profile briefly while renderer processes exit.
    await rm(profile, {recursive: true, force: true, maxRetries: 20, retryDelay: 100});
  }
});
