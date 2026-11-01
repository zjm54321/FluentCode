# Commands and Attachments Validation Harness

This test harness verifies OpenCode slash command discovery, command execution, and prompt attachment encoding without requiring a running backend server or WinUI window.

---

## Running the Test

From Windows PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\opencode-commands-attachments\run.ps1
```

The runner links production sources from `src/FluentCode/Services/OpenCode/`, compiles with the Windows .NET 10 `csc` compiler (warnings as errors), and executes offline tests against controlled mock HTTP handlers.

---

## Tested API Surface

```csharp
// Slash command discovery and execution
Task<IReadOnlyList<OpenCodeCommandInfo>> GetCommandsAsync(CancellationToken cancellationToken = default);
Task<IReadOnlyList<OpenCodeCommandInfo>> GetCommandsAsync(string directory, CancellationToken cancellationToken);
Task ExecuteCommandAsync(string sessionId, string name, string? text = null,
    IReadOnlyList<PromptFileAttachment>? files = null, CancellationToken cancellationToken = default);

// Prompt submission with file attachments
Task<OpenCodeInboxUser> SendPromptAsync(string sessionId, string text,
    IReadOnlyList<PromptFileAttachment>? files, CancellationToken cancellationToken);

// Attachment preparation helper
IReadOnlyList<PromptFileAttachment> OpenCodeAttachment.Encode(IReadOnlyList<OpenCodeAttachmentDraft> drafts);
```

---

## Contract Invariants

### Slash Commands (`/api/session/{sessionID}/command`)
- **HTTP 204 Acknowledgement**: Successful command dispatch returns `204 No Content`. This confirms server receipt, not model execution completion.
- **Payload Schema**: Command execution POST sends `name` and `text`. If text is omitted, it serializes as an empty string (`"text": ""`).
- **Directory Query**: When `directory` is supplied to `GetCommandsAsync`, the client appends `location[directory]` to scope command discovery to that working directory.

### File Attachments & Data URLs
- **Quantity Cap**: Maximum 10 attachments per prompt request.
- **File Size Limits**: Maximum 10 MiB raw per individual file; maximum 20 MiB raw across all files combined.
- **Request Size Hard Limit**: The serialized HTTP request body must not exceed 28 MiB (accounting for Base64 encoding and JSON escaping overhead).
- **MIME Allowlist**: `image/png`, `image/jpeg`, `image/gif`, `image/webp`, `text/plain`, `text/markdown`, `application/pdf`, and `application/json`. Unsupported MIME types are rejected before transmission.
- **Path Sanitization**: Only canonical `data:` URLs are accepted. Windows and Unix directory paths and filenames are stripped before sending to prevent leaking host directory structures.
