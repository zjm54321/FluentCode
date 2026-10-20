# Billion Context offline connector checks

Run on Windows with an installed .NET 10 SDK/reference pack:

```powershell
pwsh -NoProfile -File tests/billion-context/run.ps1
```

The runner links the three production `Services/Billion/*.cs` files directly, enables
nullable analysis and warnings-as-errors, and performs no restore, application launch,
OpenCode request, or real Billion request. All requests use fake `HttpMessageHandler`
transports. It emits a temporary evidence directory containing `compile.rsp`, the
compiled test assembly/runtime configuration, and `results.txt`.

Three groups cover health responses; multi-megabyte content omission, telemetry,
schema drift, malformed JSON, and size limits; and transport/authorization boundaries.
The real `HttpClientHandler` configuration is inspected without sending a request.
Redirect responses are supplied by fakes; no real redirect server is contacted.

## Connector boundary

- Only explicit GET health and GET detail for one validated OpenCode session ID.
- HTTP origins restricted to `127.0.0.1`, `localhost`, and `[::1]`; no userinfo,
  base path, query, or fragment. Default: `http://127.0.0.1:18787`.
- No config, session enumeration, cache-report, status, write, toggle, UI, app wiring,
  OpenCode credential sharing, proxy, cookies, logging, or cross-request cache.
- Default deadline: 5 seconds, including response streaming. Configurable up to
  30 seconds. Caller cancellation propagates; timeout/transport failures return
  Unavailable. Detail 404 means NoRecord (IDs need not match Billion records).
- HTTP 200 with a JSON object is accepted across versions. Missing or mistyped
  telemetry is Unknown (`null`, source enum `Unknown`, or kind `"Unknown"`).
  Invalid JSON, excessive depth, or an excessive body is Malformed, with no partial
  telemetry returned. Non-200 response bodies are not read.
- The incremental `Utf8JsonReader` traverses unselected containers without storing
  them. Content strings (`handoffHtml`, `handoffMd`, titles, hints, summaries,
  `ledger.lines`, etc.) are not converted into .NET strings or included in results.
  One incomplete JSON token can occupy the pooled byte buffer until complete;
  this is not a zero-buffer parser. Pooled bytes are cleared on return.
- Detail reads are limited to 32 MiB (plus one over-limit detection byte); health
  to 64 KiB. Selected metadata strings are limited to 128 UTF-8 bytes.
- An injected handler is trusted test infrastructure: it must not add authentication
  or implement its own redirect behavior. The connector owns/disposes the handler.
- CompressionToggle is Unavailable: “Billion 无会话级压缩开关；全局配置写入未授权”.

Live compatibility remains unverified by this suite; parent-session observations
of Billion 0.1.189 are not reproduced or treated as an offline test result.
