// Adapted from OpenCode plugin-browser/src/proxy.ts (MIT; see OpenCode-LICENSE.txt).
import {randomBytes, timingSafeEqual} from 'node:crypto';
import {Agent, createServer, request} from 'node:http';
import {Duplex} from 'node:stream';

export const CHUNK_BYTES = 64 * 1024;
export const REALM = 'OpenCode Browser Proxy';

export async function createTunnelProxy(rpc) {
  const username = randomBytes(16).toString('hex');
  const password = randomBytes(32).toString('hex');
  const expected = Buffer.from(`Basic ${Buffer.from(`${username}:${password}`).toString('base64')}`);
  const clients = new Set();
  const tunnels = new Set();
  const captured = new WeakMap();
  let current;
  let closed = false;
  const authorized = value => {
    const actual = Buffer.from(typeof value === 'string' ? value : '');
    return actual.length === expected.length && timingSafeEqual(actual, expected);
  };
  const connect = async (lease, target, signal) => {
    if (closed || !lease || lease !== current || lease.abort.signal.aborted) throw new Error('Browser proxy detached');
    // A destination matching this exact listener could impersonate its proxy-auth
    // realm in engines without an explicit isProxy challenge flag. Check before
    // tunnel.open for HTTP, CONNECT and websocket/subresource traffic alike.
    // Other loopback hosts/ports still denote the remote server's own network.
    const listener = server.address();
    if (target.host === listener.address && target.port === listener.port) throw new Error('Browser proxy cannot target its own listener');
    const linked = AbortSignal.any([signal, lease.abort.signal]);
    const id = await rpc('tunnel.open', {leaseId: lease.id, target}, linked);
    const tunnel = new TunnelSocket(rpc, lease.id, id);
    tunnels.add(tunnel);
    tunnel.once('close', () => tunnels.delete(tunnel));
    if (linked.aborted || closed || lease !== current) {
      tunnel.destroy();
      throw new Error('Browser proxy attachment changed');
    }
    return tunnel;
  };
  const server = createServer({maxHeaderSize: CHUNK_BYTES}, (incoming, response) => {
    const lease = captured.get(incoming.socket);
    if (!authorized(incoming.headers['proxy-authorization'])) {
      response.writeHead(407, {'Proxy-Authenticate': `Basic realm="${REALM}"`, Connection: 'close'});
      response.end();
      return;
    }
    if (!lease || lease.abort.signal.aborted) { response.writeHead(503); response.end(); return; }
    void forward(incoming, response, (target, signal) => connect(lease, target, signal)).catch(() => {
      if (!response.headersSent) { response.writeHead(502); response.end(); }
      else response.destroy();
    });
  });
  server.maxConnections = 128;
  server.requestTimeout = 30_000;
  server.headersTimeout = 10_000;
  server.on('connection', socket => {
    captured.set(socket, current); // Immutable per TCP connection, including keep-alive requests.
    clients.add(socket);
    socket.on('error', () => socket.destroy());
    socket.once('close', () => clients.delete(socket));
  });
  const upgrade = (incoming, socket, head, isConnect) => {
    const lease = captured.get(socket);
    void (async () => {
      if (!authorized(incoming.headers['proxy-authorization'])) {
        socket.end(`HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm="${REALM}"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n`);
        return;
      }
      if (!lease || lease.abort.signal.aborted) {
        socket.end('HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n'); return;
      }
      const url = parseURL(isConnect ? `https://${incoming.url ?? ''}` : incoming.url);
      if (!url || (!isConnect && incoming.headers.upgrade?.toLowerCase() !== 'websocket')) {
        socket.end('HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n'); return;
      }
      const abort = new AbortController();
      const cancel = () => abort.abort();
      socket.once('close', cancel);
      socket.pause();
      try {
        const tunnel = await connect(lease, targetOf(url), abort.signal);
        if (socket.destroyed) { tunnel.destroy(); return; }
        if (isConnect) socket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
        else {
          const headers = forwardedHeaders(incoming.headers);
          headers.host = url.host;
          headers.connection = 'Upgrade';
          headers.upgrade = 'websocket';
          tunnel.write(`${incoming.method} ${url.pathname}${url.search} HTTP/1.1\r\n${Object.entries(headers)
            .flatMap(([key, value]) => (Array.isArray(value) ? value : [value]).map(item => `${key}: ${item}\r\n`)).join('')}\r\n`);
        }
        if (head.length) tunnel.write(head);
        socket.once('close', () => tunnel.destroy());
        tunnel.once('close', () => socket.destroy());
        socket.pipe(tunnel);
        tunnel.pipe(socket);
        socket.resume();
      } finally { socket.off('close', cancel); }
    })().catch(() => {
      if (!socket.destroyed) socket.end('HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n');
    });
  };
  server.on('connect', (incoming, socket, head) => upgrade(incoming, socket, head, true));
  server.on('upgrade', (incoming, socket, head) => upgrade(incoming, socket, head, false));
  server.on('clientError', (_error, socket) => {
    if (!socket.destroyed) socket.end('HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n');
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => { server.off('error', reject); resolve(); });
  });
  const detach = () => {
    current?.abort.abort(new Error('Browser attachment detached'));
    current = undefined;
    for (const tunnel of tunnels) tunnel.destroy();
    for (const socket of clients) socket.destroy();
  };
  return {
    options: {address: `http://127.0.0.1:${server.address().port}`, username, password, realm: REALM, bypassList: '<-loopback>'},
    bind(leaseId) {
      if (closed) throw new Error('Browser proxy closed');
      detach();
      if (leaseId !== null) {
        if (typeof leaseId !== 'string' || !leaseId || leaseId.length > 128) throw new Error('Invalid attachment capability');
        current = {id: leaseId, abort: new AbortController()};
      }
    },
    async close() {
      if (closed) return;
      closed = true;
      detach();
      await new Promise(resolve => server.close(resolve));
    },
  };
}

async function forward(incoming, response, connect) {
  const url = parseURL(incoming.url);
  if (!url || url.protocol !== 'http:') { response.writeHead(400); response.end(); return; }
  const abort = new AbortController();
  const cancel = () => abort.abort();
  incoming.once('aborted', cancel);
  response.once('close', cancel);
  const agent = new Agent({keepAlive: false, maxSockets: 1});
  try {
    const tunnel = await connect(targetOf(url), abort.signal);
    // There is deliberately no OS TCP/DNS path to the requested target.
    agent.createConnection = () => tunnel;
    const headers = forwardedHeaders(incoming.headers);
    headers.host = url.host;
    headers.connection = 'close';
    await new Promise((resolve, reject) => {
      const upstream = request({agent, hostname: url.hostname, port: url.port || 80,
        path: `${url.pathname}${url.search}`, method: incoming.method, headers, signal: abort.signal}, result => {
        response.writeHead(result.statusCode ?? 502, result.statusMessage, {...forwardedHeaders(result.headers), connection: 'close'});
        result.once('error', reject);
        response.once('finish', resolve);
        result.pipe(response);
      });
      upstream.once('error', reject);
      incoming.pipe(upstream);
    });
  } finally {
    incoming.off('aborted', cancel);
    response.off('close', cancel);
    agent.destroy();
  }
}

function forwardedHeaders(input) {
  const headers = {...input};
  headers.connection?.split(',').forEach(name => delete headers[name.trim().toLowerCase()]);
  for (const name of ['connection', 'keep-alive', 'proxy-authenticate', 'proxy-authorization', 'proxy-connection', 'te', 'trailer', 'transfer-encoding', 'upgrade']) delete headers[name];
  return headers;
}
function parseURL(value) {
  if (!value || !URL.canParse(value)) return;
  const url = new URL(value);
  if (!['http:', 'https:', 'ws:', 'wss:'].includes(url.protocol) || url.username || url.password || !url.hostname) return;
  return url;
}
function targetOf(url) {
  return {host: url.hostname.replace(/^\[|\]$/g, ''), port: Number(url.port || (['https:', 'wss:'].includes(url.protocol) ? 443 : 80))};
}

class TunnelSocket extends Duplex {
  connecting = false;
  #abort = new AbortController();
  #pending = false;
  constructor(rpc, leaseId, tunnelId) {
    super({highWaterMark: CHUNK_BYTES, allowHalfOpen: true});
    this.rpc = (method, params, signal) => rpc(method, {leaseId, tunnelId, ...params}, signal);
    this.on('error', () => this.destroy());
  }
  _read() {
    if (this.#pending || this.destroyed) return;
    this.#pending = true;
    void this.rpc('tunnel.read', {}, this.#abort.signal).then(result => {
      this.#pending = false;
      if (this.destroyed) return;
      const data = Buffer.from(result.data, 'base64');
      if (data.length > CHUNK_BYTES || (!data.length && !result.eof)) throw new Error('Invalid tunnel read chunk');
      if (data.length) { const more = this.push(data); if (!result.eof && more) this._read(); }
      if (result.eof) this.push(null);
    }).catch(error => this.destroy(error));
  }
  _write(chunk, encoding, callback) {
    const data = typeof chunk === 'string' ? Buffer.from(chunk, encoding) : chunk;
    void (async () => {
      for (let offset = 0; offset < data.length; offset += CHUNK_BYTES) {
        await this.rpc('tunnel.write', {data: data.subarray(offset, offset + CHUNK_BYTES).toString('base64'), end: false}, this.#abort.signal);
      }
    })().then(() => callback(), callback);
  }
  _final(callback) {
    void this.rpc('tunnel.write', {data: '', end: true}, this.#abort.signal).then(() => callback(), callback);
  }
  _destroy(error, callback) {
    this.#abort.abort();
    void this.rpc('tunnel.close', {}, AbortSignal.timeout(2000)).catch(() => {}).then(() => callback(error));
  }
  setKeepAlive() { return this; }
  setNoDelay() { return this; }
  setTimeout(_timeout, callback) { if (callback) this.once('timeout', callback); return this; }
  ref() { return this; }
  unref() { return this; }
}
