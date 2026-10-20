# Form DTO/input-policy regression harness

Production-linked, headless .NET 10 compiler harness. No application build, restore,
network, native notifications, Page events, or automatic default application.

```powershell
./tests/opencode-form-input/run.ps1 -EvidenceRoot '<owned evidence directory>'
# Optional comparison against preserved, unmodified original production sources:
./tests/opencode-form-input/run.ps1 -EvidenceRoot '<owned evidence directory>' -BaselineDirectory '<original source directory>'
```

The baseline directory must contain the original `OpenCodeDtos.Forms.cs` and
`OpenCodeInteractionPolicy.cs`. It is compiled separately; production files are
never rolled back. Both variants link the real permission DTO for the fingerprint
policy's dependency and the production `OpenCodeJson.Options` (Web defaults) for
deserialization. No copied policy implementation or mocks are used.

The runner uses cached Windows .NET 10 SDK/reference packs, sets its own TEMP/TMP
and the prescribed process-local SDK safety variables before invoking dotnet, and
records source/compiler/reference/response-file hashes before and after execution.

Three groups cover described choices/string metadata and answer constraints;
typed numeric/integer/boolean/multiselect defaults and invalid/unsupported inputs;
and serialization/fingerprint/extension-data preservation. Fixtures are synthetic
public-schema examples, not private server payloads.

Defaults remain typed metadata until separately integrated into the UI. Optional
unanswered values are still omitted; required defaults do not bypass user input.
String checks reject lengths unambiguously outside bounds under both UTF-16 and
Unicode-scalar counting. Ambiguous supplementary-character lengths remain subject
to server validation; no exact deployed-server length equivalence is claimed.

Final bounded execution evidence and UI API handoff:
`.slim/deepwork/opencode-v2-resumed-completion/phase4-form-input.md`.
