// Internal, bounded bidirectional JSON-lines protocol. stdout is protocol-only.
import {EventEmitter} from 'node:events';

export const MAX_LINE = 16 * 1024 * 1024;
const MAX_QUEUED = 24 * 1024 * 1024;
export class JsonPeer extends EventEmitter {
  #pending = new Map();
  #incoming = new Map();
  #buffer = Buffer.alloc(0);
  #queued = 0;
  #sequence = 0;
  #closed;
  constructor(input, output, handler) {
    super();
    this.output = output;
    this.handler = handler;
    input.on('data', chunk => {
      try {
        this.#buffer = Buffer.concat([this.#buffer, chunk]);
        let end;
        while ((end = this.#buffer.indexOf(10)) >= 0) {
          if (end > MAX_LINE) throw new Error('Runtime message exceeds 16 MiB');
          const line = this.#buffer.subarray(0, end);
          this.#buffer = this.#buffer.subarray(end + 1);
          if (line.length) this.#receive(JSON.parse(line.toString('utf8')));
        }
        if (this.#buffer.length > MAX_LINE) throw new Error('Runtime message exceeds 16 MiB');
      } catch (error) { this.close(error); }
    });
    input.on('end', () => this.close(new Error('Runtime stdin closed')));
    input.on('error', error => this.close(error));
    output.on('error', error => this.close(error));
  }
  send(message) {
    if (this.#closed) return Promise.reject(this.#closed);
    const line = Buffer.from(`${JSON.stringify(message)}\n`);
    if (line.length > MAX_LINE || this.#queued + line.length > MAX_QUEUED) {
      const error = new Error('Runtime protocol output limit exceeded');
      this.close(error);
      return Promise.reject(error);
    }
    this.#queued += line.length;
    return new Promise((resolve, reject) => this.output.write(line, error => {
      this.#queued -= line.length;
      if (error) reject(error); else resolve();
    }));
  }
  async call(method, params, signal) {
    if (this.#pending.size >= 512) throw new Error('Runtime callback limit exceeded');
    if (signal?.aborted) throw signal.reason;
    const id = `n${++this.#sequence}`;
    let cancel;
    try {
      return await new Promise((resolve, reject) => {
        cancel = () => {
          if (!this.#pending.delete(id)) return;
          void this.send({cancel: id}).catch(() => {});
          reject(signal.reason ?? new Error('Cancelled'));
        };
        this.#pending.set(id, {resolve, reject});
        signal?.addEventListener('abort', cancel, {once: true});
        void this.send({id, method, params}).catch(error => {
          this.#pending.delete(id);
          reject(error);
        });
      });
    } finally { signal?.removeEventListener('abort', cancel); }
  }
  #receive(message) {
    if (message.cancel) { this.#incoming.get(message.cancel)?.abort(new Error('Cancelled')); return; }
    if (message.event) { this.emit('event', message); return; }
    if (typeof message.id !== 'string') throw new Error('Runtime message requires an id');
    if (!message.method) {
      const pending = this.#pending.get(message.id);
      this.#pending.delete(message.id);
      if (message.error) pending?.reject(new Error(message.error.message));
      else pending?.resolve(message.result);
      return;
    }
    if (this.#incoming.size >= 512 || this.#incoming.has(message.id)) throw new Error('Runtime request limit exceeded');
    const controller = new AbortController();
    this.#incoming.set(message.id, controller);
    void Promise.resolve().then(() => this.handler(message.method, message.params ?? {}, controller.signal))
      .then(result => this.send({id: message.id, result: result ?? null}),
        error => this.send({id: message.id, error: {message: String(error?.message ?? error)}}))
      .catch(error => this.close(error))
      .finally(() => this.#incoming.delete(message.id));
  }
  close(error = new Error('Runtime closed')) {
    if (this.#closed) return;
    this.#closed = error;
    for (const pending of this.#pending.values()) pending.reject(error);
    for (const controller of this.#incoming.values()) controller.abort(error);
    this.#pending.clear();
    this.emit('closed', error);
  }
}
