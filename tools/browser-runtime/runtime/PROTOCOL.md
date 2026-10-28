# Internal browser runtime protocol v1

Start the prepared `node.exe sidecar.mjs` with redirected stdin/stdout/stderr.
UTF-8 JSON, one object per line. stdout contains protocol only. No API client,
remote API URL, API authorization header, directory, session ID or connection ID
is passed to Node. This is a local process protocol, not a public RPC endpoint.

Both peers may send `{id,method,params}` concurrently. Reply `{id,result}` or
`{id,error:{message}}`. IDs are strings; C# uses `c1...`, Node uses `n1...`.
`{cancel:id}` cancels a pending request. Events have `event` instead of `id`.
Late responses to cancelled requests are ignored; C# must close any tunnel/session
that completes opening after cancellation, using its captured original owner.

## Node methods (called by C#)

- Startup event `{event:"runtime.ready",protocol:1,proxy:{address,username,password,
  realm,bypassList},cdpEventNames:[...]}`. The address is HTTP loopback with an
  ephemeral port; its lifetime and generated credentials span attachment changes.
  The event catalogue comes from the locked `devtools-protocol` JSON assets.
- `attachment.set {leaseId:string|null}`. This is a locally generated opaque
  attachment capability, not a server-side identity. Detach destroys all accepted
  sockets/tunnels and aborts pending opens, leaving the listener alive. Replacement
  does the same before binding a new capability. Every accepted TCP socket retains
  its original lease; detached sockets cannot adopt a subsequent attachment.
- `audit.start {auditId:string,url:string}` -> `{value:{scores,failures},reports:[
  {name,mime,data:base64},...]}`. One audit per runtime, dedicated disposable worker.
  The worker budget is 50 seconds, plus bounded session cleanup. Snapshot only:
  accessibility/seo/best-practices, desktop, no screen emulation, provided
  throttling, no storage reset or full-page screenshot. Reports are genuine
  Lighthouse-generated JSON and HTML, each at most 5 MiB. C# supplies these bytes
  to its existing backend retention/export pipeline, never desktop file paths.
- `runtime.stop {}`. Process/pipe loss also closes the listener, never bypasses it.
- `audit.cancel {auditId}`. Cancels that audit and acknowledges only after its
  worker and bounded cleanup have finished, allowing immediate subsequent audits.

## C# callbacks (called by Node)

- `tunnel.open {leaseId,target:{host,port}}` -> string tunnel ID.
- `tunnel.read {leaseId,tunnelId}` -> `{data:base64,eof:boolean}`. At most 64 KiB;
  wait for bytes/EOF/cancellation, with **no idle timeout** and one pending read
  per socket. A final chunk may contain both bytes and `eof:true`.
- `tunnel.write {leaseId,tunnelId,data:base64,end:boolean}` -> null. At most 64 KiB,
  sequential acknowledged writes; `end:true` half-closes the outgoing direction.
- `tunnel.close {leaseId,tunnelId}` -> null. Use the original captured RPC identity
  and an independent bounded cleanup token, even after that attachment is cancelled.
- `cdp.send {auditId,method,params,sessionId:string|null}` -> CDP result object.
  Calls go directly to the supplied page CDP facade (which dispatches WebView2
  calls on its UI context), never through the already-held browser command gate.
  Null denotes the existing page transport. At null, the adapter uses only
  `Target.getTargetInfo`, `Target.attachToTarget` and cleanup
  `Target.detachFromTarget`. Other commands target audit-owned flattened sessions.

Subscribe to `"*"` before starting an audit. Forward each received event as
`{event:"cdp",auditId,method,params,sessionId}` in receive order. Normalize only the
caller's existing page session to null. The adapter emits both the named CDP event
and `"*"`; `Target.attachedToTarget` creates child EventEmitter sessions and emits
`sessionattached`. Track all audit-created sessions also in C#: on failure,
cancellation or process loss, detach only these sessions and remove subscriptions.
Observe late attach results for cleanup even after cancellation stops waiting.

## Bounds and backpressure

128 accepted proxy connections, 64 KiB HTTP headers and stream high-water marks,
512 protocol requests/callbacks, 16 MiB per JSON line, 24 MiB queued output.
Overflow fails the runtime closed. No direct socket/DNS target fallback exists:
the Node HTTP Agent always receives the tunnel-backed Duplex. CONNECT and raw
websocket upgrades use that same transport, preserving half-close and pipe
backpressure. The application must keep using the exact authenticated proxy
endpoint if the runtime fails, not switch to its native network stack.

Before any `tunnel.open`, all proxy paths reject destinations whose canonical
host and port exactly equal this listener's own host/port. This prevents a
self-proxy loop or origin/proxy authentication ambiguity, including subresources.
Other loopback hosts and ports remain valid remote-server tunnel destinations;
there is no blanket localhost restriction or target DNS lookup in Node.

Proxy and CDP adapter portions derive from OpenCode `plugin-browser/src/proxy.ts`
and `gui-extensions/src/browser/lighthouse.ts` respectively, copyright 2025
opencode, MIT; complete notice is included in `OpenCode-LICENSE.txt`.
