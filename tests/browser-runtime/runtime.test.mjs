import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {createHash} from 'node:crypto';
import {once} from 'node:events';
import {createServer, request} from 'node:http';
import net from 'node:net';
import path from 'node:path';
import {test} from 'node:test';
import {setTimeout as delay} from 'node:timers/promises';
import {fileURLToPath} from 'node:url';
import {JsonPeer} from '../../artifacts/browser-runtime/json-peer.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const listen = async server => { server.listen(0, '127.0.0.1'); await once(server, 'listening'); return server.address().port; };
const close = async server => { server.closeAllConnections?.(); await new Promise(resolve => server.close(resolve)); };
const digest = data => createHash('sha256').update(data).digest('hex');

async function fixture() {
  const mappings = new Map();
  const tunnels = new Map();
  const calls = [];
  let sequence = 0;
  let maxWrite = 0;
  let rejected = false;
  let holdOpen;
  const child = spawn(process.execPath, [path.join(root, 'artifacts/browser-runtime/sidecar.mjs')], {windowsHide: true, stdio: ['pipe', 'pipe', 'pipe']});
  let errors = '';
  child.stderr.on('data', chunk => { errors = (errors + chunk).slice(-4096); });
  const peer = new JsonPeer(child.stdout, child.stdin, async (method, input, signal) => {
    if (method === 'cdp.send') return await new Promise((resolve, reject) => {
      signal.addEventListener('abort', () => reject(signal.reason), {once: true});
    });
    calls.push({method, ...input});
    if (method === 'tunnel.open') {
      if (holdOpen) await holdOpen(signal);
      if (rejected) throw new Error('Controlled tunnel failure');
      const port = mappings.get(input.target.host);
      assert.ok(port, `Unmapped target ${input.target.host}`);
      const socket = net.connect({host: '127.0.0.1', port, allowHalfOpen: true});
      socket.on('error', () => {});
      await once(socket, 'connect', {signal});
      const id = `t${++sequence}`;
      tunnels.set(id, {socket, leaseId: input.leaseId, reading: false, writing: false});
      return id;
    }
    const tunnel = tunnels.get(input.tunnelId);
    if (!tunnel && method === 'tunnel.close') return null;
    assert.ok(tunnel, `Unknown tunnel ${input.tunnelId}`);
    assert.equal(input.leaseId, tunnel.leaseId, 'Tunnel rebound to a new lease');
    const socket = tunnel.socket;
    if (method === 'tunnel.close') { socket.destroy(); tunnels.delete(input.tunnelId); return null; }
    if (method === 'tunnel.write') {
      assert.equal(tunnel.writing, false, 'Concurrent writes');
      tunnel.writing = true;
      const data = Buffer.from(input.data, 'base64');
      assert.ok(data.length <= 65536);
      maxWrite = Math.max(maxWrite, data.length);
      try {
        await new Promise((resolve, reject) => (input.end ? socket.end.bind(socket) : socket.write.bind(socket))(data, error => error ? reject(error) : resolve()));
      } finally { tunnel.writing = false; }
      return null;
    }
    assert.equal(method, 'tunnel.read');
    assert.equal(tunnel.reading, false, 'More than one pending tunnel read');
    tunnel.reading = true;
    try {
      for (;;) {
        signal.throwIfAborted();
        const data = socket.read(65536) ?? socket.read();
        if (data) { assert.ok(data.length <= 65536); return {data: data.toString('base64'), eof: false}; }
        if (socket.readableEnded || socket.destroyed) return {data: '', eof: true};
        await new Promise((resolve, reject) => {
          const done = () => { cleanup(); resolve(); };
          const cancel = () => { cleanup(); reject(signal.reason); };
          const cleanup = () => {
            for (const event of ['readable', 'end', 'close']) socket.off(event, done);
            signal.removeEventListener('abort', cancel);
          };
          for (const event of ['readable', 'end', 'close']) socket.once(event, done);
          signal.addEventListener('abort', cancel, {once: true});
        });
      }
    } finally { tunnel.reading = false; }
  });
  peer.on('closed', () => {});
  const ready = await Promise.race([
    once(peer, 'event').then(([event]) => event),
    once(child, 'exit').then(([code]) => { throw new Error(`Runtime exited ${code}: ${errors}`); }),
    delay(10000, null, {ref: false}).then(() => { throw new Error('Runtime startup timeout'); }),
  ]);
  assert.equal(ready.event, 'runtime.ready');
  const proxy = new URL(ready.proxy.address);
  const auth = `Basic ${Buffer.from(`${ready.proxy.username}:${ready.proxy.password}`).toString('base64')}`;
  const bind = leaseId => peer.call('attachment.set', {leaseId});
  await bind('lease-A');
  return {
    child, peer, ready, proxy, auth, mappings, calls, bind,
    set reject(value) { rejected = value; },
    set holdOpen(value) { holdOpen = value; },
    get maxWrite() { return maxWrite; },
    async dispose() {
      for (const tunnel of tunnels.values()) tunnel.socket.destroy();
      if (child.exitCode === null && child.signalCode === null) { const exit = once(child, 'exit'); child.kill(); await exit; }
    },
  };
}
function get(f, url, {authorization = f.auth, body = null} = {}) {
  return new Promise((resolve, reject) => {
    const outgoing = request({hostname: f.proxy.hostname, port: f.proxy.port, path: url, method: body ? 'POST' : 'GET',
      headers: authorization ? {'Proxy-Authorization': authorization} : {}}, response => {
      const chunks = [];
      response.on('data', chunk => chunks.push(chunk));
      response.once('error', reject);
      response.once('end', () => resolve({status: response.statusCode, headers: response.headers, body: Buffer.concat(chunks).toString()}));
    });
    outgoing.once('error', reject);
    outgoing.end(body);
  });
}
async function raw(f, line) {
  const socket = net.connect({host: f.proxy.hostname, port: Number(f.proxy.port), allowHalfOpen: true});
  await once(socket, 'connect');
  socket.write(`${line}\r\nHost: remote.invalid\r\nProxy-Authorization: ${f.auth}\r\n\r\n`);
  let bytes = Buffer.alloc(0);
  while (!bytes.includes('\r\n\r\n')) { const [data] = await once(socket, 'data'); bytes = Buffer.concat([bytes, data]); }
  return {socket, headers: bytes.toString()};
}

test('authenticated HTTP, bounded ordered chunks, and no target-network fallback', {timeout: 15000}, async () => {
  const f = await fixture();
  let hits = 0;
  const origin = createServer(async (incoming, response) => {
    hits++;
    assert.equal(incoming.headers['proxy-authorization'], undefined);
    const chunks = [];
    for await (const chunk of incoming) chunks.push(chunk);
    response.end(digest(Buffer.concat(chunks)));
  });
  const port = await listen(origin);
  f.mappings.set('remote.invalid', port);
  try {
    const challenge = await get(f, 'http://remote.invalid/', {authorization: null});
    assert.equal(challenge.status, 407);
    assert.equal(challenge.headers['proxy-authenticate'], 'Basic realm="OpenCode Browser Proxy"');
    assert.equal((await get(f, 'http://remote.invalid/', {authorization: 'Basic wrong'})).status, 407);
    const body = Buffer.alloc(300000, 83);
    assert.equal((await get(f, 'http://remote.invalid/body', {body})).body, digest(body));
    assert.ok(f.maxWrite <= 65536 && f.maxWrite > 0);
    f.reject = true;
    assert.equal((await get(f, `http://127.0.0.1:${port}/direct`)).status, 502);
    assert.equal(hits, 1, 'Rejected tunnel must not connect directly even to a reachable target');
    assert.ok(f.ready.cdpEventNames.includes('Target.attachedToTarget'));
  } finally { await f.dispose(); await close(origin); }
});

test('self-listener destinations never open tunnels; other remote loopback remains routable', {timeout: 15000}, async () => {
  const f = await fixture();
  const origin = createServer((incoming, response) => {
    assert.equal(incoming.headers['proxy-authorization'], undefined);
    response.end('remote loopback');
  });
  const port = await listen(origin);
  for (const host of ['127.0.0.1', 'localhost', '::1']) f.mappings.set(host, port);
  try {
    assert.equal((await get(f, `${f.proxy.href}subresource.js`)).status, 502);
    assert.equal((await get(f, `http://0x7f000001:${f.proxy.port}/canonical-ipv4`)).status, 502);
    const connected = await raw(f, `CONNECT 127.0.0.1:${f.proxy.port} HTTP/1.1`);
    assert.match(connected.headers, /^HTTP\/1.1 502/);
    connected.socket.destroy();
    const upgraded = await raw(f, `GET ws://127.0.0.1:${f.proxy.port}/socket HTTP/1.1\r\nUpgrade: websocket\r\nConnection: Upgrade`);
    assert.match(upgraded.headers, /^HTTP\/1.1 502/);
    upgraded.socket.destroy();
    assert.equal(f.calls.filter(call => call.method === 'tunnel.open').length, 0, 'Self-listener rejection must precede tunnel.open');

    assert.equal((await get(f, `http://127.0.0.1:${port}/remote-ipv4`)).body, 'remote loopback');
    assert.equal((await get(f, `http://localhost:${f.proxy.port}/remote-localhost`)).body, 'remote loopback');
    assert.equal((await get(f, `http://[::1]:${f.proxy.port}/remote-ipv6`)).body, 'remote loopback');
    const remote = await raw(f, `CONNECT 127.0.0.1:${port} HTTP/1.1`);
    assert.match(remote.headers, /^HTTP\/1.1 200/);
    remote.socket.destroy();
    const remoteLocalhost = await raw(f, `CONNECT localhost:${f.proxy.port} HTTP/1.1`);
    assert.match(remoteLocalhost.headers, /^HTTP\/1.1 200/);
    remoteLocalhost.socket.destroy();
    assert.deepEqual(f.calls.filter(call => call.method === 'tunnel.open').map(call => call.target), [
      {host: '127.0.0.1', port},
      {host: 'localhost', port: Number(f.proxy.port)},
      {host: '::1', port: Number(f.proxy.port)},
      {host: '127.0.0.1', port},
      {host: 'localhost', port: Number(f.proxy.port)},
    ]);
  } finally { await f.dispose(); await close(origin); }
});

test('CONNECT preserves half-close and websocket upgrade uses only the tunnel', {timeout: 15000}, async () => {
  const f = await fixture();
  const echo = net.createServer({allowHalfOpen: true}, socket => {
    const chunks = [];
    socket.on('data', chunk => chunks.push(chunk));
    socket.on('end', () => socket.end(Buffer.concat([Buffer.from('after-fin:'), ...chunks])));
  });
  const websocket = createServer();
  const upgradedSockets = new Set();
  websocket.on('upgrade', (incoming, socket) => {
    upgradedSockets.add(socket);
    socket.once('close', () => upgradedSockets.delete(socket));
    socket.once('end', () => socket.end());
    assert.equal(incoming.headers['proxy-authorization'], undefined);
    const accept = createHash('sha1').update(`${incoming.headers['sec-websocket-key']}258EAFA5-E914-47DA-95CA-C5AB0DC85B11`).digest('base64');
    socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
    socket.on('data', data => socket.write(data));
  });
  f.mappings.set('remote.invalid', await listen(echo));
  f.mappings.set('ws.invalid', await listen(websocket));
  try {
    const connected = await raw(f, 'CONNECT remote.invalid:443 HTTP/1.1');
    assert.match(connected.headers, /^HTTP\/1.1 200/);
    const chunks = [];
    connected.socket.on('data', chunk => chunks.push(chunk));
    const ended = once(connected.socket, 'end');
    connected.socket.end('payload');
    await ended;
    assert.equal(Buffer.concat(chunks).toString(), 'after-fin:payload');
    connected.socket.destroy();
    const upgraded = await raw(f, 'GET ws://ws.invalid/chat HTTP/1.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13');
    assert.match(upgraded.headers, /^HTTP\/1.1 101/);
    const reply = once(upgraded.socket, 'data');
    const frame = Buffer.from([0x81, 0x82, 1, 2, 3, 4, 0x69, 0x6b]);
    upgraded.socket.write(frame);
    assert.deepEqual((await reply)[0], frame);
    upgraded.socket.destroy();
  } finally {
    await f.dispose();
    for (const socket of upgradedSockets) socket.destroy();
    await close(echo); await close(websocket);
  }
});

test('detach aborts pending opens, listener stays stable, reconnect never rebinds old sockets', {timeout: 15000}, async () => {
  const f = await fixture();
  const origin = createServer((_incoming, response) => response.end('new attachment'));
  f.mappings.set('remote.invalid', await listen(origin));
  let opened;
  const started = new Promise(resolve => { opened = resolve; });
  let cancelled = false;
  f.holdOpen = signal => new Promise((_resolve, reject) => {
    opened();
    signal.addEventListener('abort', () => { cancelled = true; reject(signal.reason); }, {once: true});
  });
  try {
    const old = get(f, 'http://remote.invalid/old').catch(() => null);
    await started;
    await f.bind(null);
    await old;
    assert.equal(cancelled, true);
    assert.equal((await get(f, 'http://remote.invalid/detached')).status, 503);
    f.holdOpen = null;
    await f.bind('lease-B');
    assert.equal((await get(f, 'http://remote.invalid/new')).body, 'new attachment');
    assert.deepEqual(f.calls.filter(call => call.method === 'tunnel.open').map(call => call.leaseId), ['lease-A', 'lease-B']);
    const exit = once(f.child, 'exit');
    f.child.kill();
    await exit;
    await assert.rejects(get(f, 'http://remote.invalid/failed-runtime'));
  } finally { await f.dispose(); await close(origin); }
});

test('long-idle SSE has no tunnel-read timeout; audit worker cancellation leaves proxy alive', {timeout: 45000}, async () => {
  const f = await fixture();
  const origin = createServer((_incoming, response) => {
    response.writeHead(200, {'Content-Type': 'text/event-stream'});
    response.write('data: first\n\n');
    setTimeout(() => response.end('data: after-31-seconds\n\n'), 31000);
  });
  f.mappings.set('remote.invalid', await listen(origin));
  try {
    const abort = new AbortController();
    const running = f.peer.call('audit.start', {auditId: 'cancel-test', url: 'http://remote.invalid/'}, abort.signal);
    await delay(1500);
    abort.abort(new Error('Test audit cancellation'));
    await assert.rejects(running, /cancellation/);
    const response = await get(f, 'http://remote.invalid/events');
    assert.equal(response.status, 200);
    assert.match(response.body, /first[\s\S]*after-31-seconds/);
  } finally { await f.dispose(); await close(origin); }
});
