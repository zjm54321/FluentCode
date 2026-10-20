# Commands and attachments: offline backend evidence

Run from Windows:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/opencode-commands-attachments/run.ps1
```

The runner links the real `Services/OpenCode/*.cs` sources (excluding the unrelated pairing service), compiles with Windows .NET 10 `csc` and warnings-as-errors, and uses only fake `HttpMessageHandler` responses. No packages, configuration, UI, real services, models, or ACP operations are involved.

## Public entry points

```csharp
Task<IReadOnlyList<OpenCodeCommandInfo>> GetCommandsAsync(CancellationToken cancellationToken = default);
Task ExecuteCommandAsync(string sessionId, string name, string? text = null,
    IReadOnlyList<PromptFileAttachment>? files = null, CancellationToken cancellationToken = default);
Task<OpenCodeInboxUser> SendPromptAsync(string sessionId, string text,
    IReadOnlyList<PromptFileAttachment>? files, CancellationToken cancellationToken);
IReadOnlyList<PromptFileAttachment> OpenCodeAttachment.Encode(IReadOnlyList<OpenCodeAttachmentDraft> drafts);
```

`OpenCodeAttachmentDraft(string Name, string? Mime, byte[] Bytes)` has no disk/network behavior. `PromptFileAttachment(string Uri, string? Name = null, string? Description = null)` is revalidated and its filename stripped at the final send boundary, including when constructed directly by a caller. All validation errors are `ArgumentException` with a reason and without echoing filenames, paths, or contents. The whole batch is rejected before HTTP; the transport never sends a partial batch.

The new prompt overload requires its fourth argument, avoiding ambiguity in existing `SendPromptAsync(id, text, default)` calls. Null/empty files delegate to the old overload. **`OpenCodeClient.cs` and `PromptBody` were not modified.**

## Contract clarification

The approved feature note omits command success response details. Existing local OpenAPI snapshots `/tmp/opencode/v2-openapi-session-check.json`, `/tmp/opencode/v2-openapi-diagnostics.json`, and `/tmp/opencode/openapi.json` agree at `paths["/api/session/{sessionID}/command"].post`:

- Successful callback: **204 No Content**, not an inbox receipt. Use existing `SendForNoContentAsync`, not JSON parsing. This acknowledges the server callback, not model completion.
- The snapshot's request schema has `required: ["name", "text"]`. The public API still accepts omitted/null text, but serializes it as `"text":""`. No `order`, `delivery`, `agents`, `skills`, or local-command fields are sent.
- Directory reads use `SendForJsonAsync` and `RequireData<List<JsonElement>>`; malformed entries are ignored, duplicate names retain their first valid occurrence, malformed envelopes still fail.

These are pre-existing local snapshots, not a new server request or proof of the running server's exact version. Name validation is a client policy (1–256 characters, no whitespace/control characters/slashes, no `.`/`..`), not an inferred server regex.

Transport exceptions remain unchanged: explicit HTTP rejection is `OpenCodeApiException`; timeout/disconnect/unexpected response are `OpenCodeTimeoutException`/`OpenCodeTransportException`/`OpenCodeProtocolException`; caller cancellation retains its token. After a mutation starts, these ambiguous failures do not prove it was rejected. There is no automatic retry, result reconciliation, or invented inbox ID for commands. Existing endpoint-template fallback yields sanitized `api/request` labels for command errors; no shared transport partial was changed just to improve that label.

## Limits and boundaries

- At most 10 attachments; each at most 10 MiB raw; total at most 20 MiB raw.
- Entire serialized request at most 28 MiB, including actual text, name/description metadata, Base64 and JSON escaping. Exact serialized bytes are counted before HTTP, without materializing that JSON as a string. Standard JSON escaping can enlarge `+` as well as non-ASCII text, so some otherwise in-range raw payloads are rejected by the final request cap.
- MIME allowlist: image/png, image/jpeg, image/gif, image/webp, text/plain, text/markdown, application/pdf, application/json. Missing/blank/octet-stream MIME falls back to an allowed extension; explicitly unsupported MIME is rejected. This is type admission, not content sniffing or malware scanning.
- Only canonical Base64 `data:` URLs are accepted. Windows/Unix paths, `file:`, HTTP(S), URL-escaped data, data-URL parameters, whitespace/noncanonical Base64, and unsupported MIME are rejected. Both slash styles and drive-relative prefixes are stripped from filenames.
- File reading, target source/session binding, late-result guards, shared mutation leases/unknown protection, IME, picker focus, slash menu, and all UI integration remain out of this backend lane. No Pages/App/Notifications/Phase-1 files were changed.

## Recorded run

2026-10-09: **PASS, 3 groups / 157 assertions**, Windows .NET 10 headless compilation with warnings-as-errors.

1. Catalog paths/auth, valid/invalid/duplicate rows, empty and malformed envelopes, HTTP error sanitization.
2. Command/prompt exact request fields, 204 acknowledgement, original prompt byte-level baseline (including escaped text), invalid names/IDs, timeout/disconnect/IO/cancellation/protocol failure, and no retries.
3. Binary data URLs, MIME whitelist/fallback, Windows/UNC/Unix/mixed basename stripping, exact quantity/per-file/total/request limits, escaped Base64 inflation, direct-DTO bypass rejection, path sentinel absence and whole-batch zero-request failures.

Evidence directory:

`C:\Users\Zhang\AppData\Local\Temp\opencode-commands-attachments-8871b9be787343268dfac944116715d3`

WSL equivalent:

`/mnt/c/Users/Zhang/AppData/Local/Temp/opencode-commands-attachments-8871b9be787343268dfac944116715d3`

Artifacts: `results.txt`, `compile.rsp`, `source-hashes.json`, executable test DLL/runtimeconfig. Hashes identify the actual linked production sources despite parallel work in other lanes. No full app build or UI validation was performed by this lane.
