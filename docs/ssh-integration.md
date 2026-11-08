# SSH security / connection UI integration

Workspace: `/mnt/c/src/fluentcode-lanes/ssh-integrate`, branch `lane/ssh-integrate`.

## Merge

- Security base: `8caca06` (includes `284ab8e`).
- UI lane: `78dc4d6`, merged as `cc0dc6c`.
- Service/UI hooks and focused assertions: `d260577`.
- Follow-up `c386b75` keeps the changed-key action available when a later SSH command fails before resolved key fields can be attached; the copied command then contains explicit placeholders rather than guessed values.
- No merge conflicts. The security implementations of socket ownership, proxy isolation, strict trust, guarded startup, owned Jobs, bounded output and memory-only SSH passwords remain in place.
- Only this integration clone was written; no push and no artifacts are committed.
- Workspace commands: `git clone /mnt/c/src/fluentcode-lanes/ssh-security /mnt/c/src/fluentcode-lanes/ssh-integrate` and `git switch -c lane/ssh-integrate 8caca06` exited 0; `git fetch /mnt/c/src/fluentcode-lanes/ssh-remote lane/ssh-remote` and `git merge --no-ff 78dc4d6` exited 0. The requested `cmd.exe /c robocopy C:\src\fluentcode\artifacts\browser-runtime C:\src\fluentcode-lanes\ssh-integrate\artifacts\browser-runtime /E /NFL /NDL /NJH /NJS /NP` exited **1 (copied)**.

## Hooks and decisions

- `RecoverAsync(..., initial: true)` separates cold start from reconnect copy.
- `OpenCodeConnectionStatus` carries `Label`, `Detail`, `Failed`, optional `FailureKind` and `Warning`. WSL shares this status contract; the title bar no longer infers semantics from punctuation.
- Service progress uses the design review copy. Security-specific refusal messages remain explicit.
- Missing native executables/path errors are `MissingOpenSsh`, non-retryable; other native startup failures remain `Unavailable`.
- Fingerprints carry separate `Algorithm` / `Sha256` fields. Host key alias is separate from resolved address.
- A successful wildcard-listener warning survives service options, commit outcome, connection-page status and the main-page non-blocking InfoBar.
- D1/D4: “SSH 自动发现” stays fourth. D2: only SSH has a success card. D3: optional-features and copy-removal-command actions are inside the status card. D5: each fingerprint has an accessible copy button. D6: changed keys during recovery have a critical pill and a separate closable InfoBar. D7: “不能在这里输入 SSH 密码” remains.
- SSH-fetched service passwords are described as memory-only in the idle hint and saved-success result. Other modes keep their DPAPI behavior.
- The removal command is copy-only and PowerShell-quoted, including apostrophes. It uses the actual offending-record file, not the first writable acceptance file; unsupported native diagnostics leave an explicit file placeholder, and a later command without resolved fields still offers the action with a host/key-alias placeholder. No removal command is executed.

## Validation

Final candidate commands and evidence (paths relative to this workspace). PowerShell 7 test invocations prepend `C:\Program Files\dotnet` to PATH. Logs: `/tmp/opencode/sshint-*.log`; progress: `/tmp/opencode/sshint-progress.md`.

| Command | Exit | Result / evidence |
|---|---:|---|
| `tests\opencode-ssh\run.ps1 -EvidenceRoot .\artifacts\ssh-integrate-unit-20261108T020317Z` | 0 | **227 assertions**; compilation treats warnings as errors; before/after input hashes match. |
| `tests\opencode-connection\run.ps1 -EvidenceRoot .\artifacts\ssh-integrate-connection-20261108T020317Z` | 0 | **96 assertions**; compilation treats warnings as errors. |
| `bash ./tests/opencode-ssh/run-fixture.sh` | 0 | **31 assertions**, including typed fingerprints and the actual custom trust file for changed-key removal; `artifacts/ssh-integrate-live-20261108T020317Z/{results.txt,live-tunnel.json}`. |
| `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-debug.ps1` | 0 | Publish / runtime hashes pass; **0 warning lines, 0 error lines**; `artifacts/native-debug-20261108-020318-0c81e387/publish`. |
| `tests\opencode-ssh\capture.ps1 -PublishDirectory C:\src\fluentcode-lanes\ssh-integrate\artifacts\native-debug-20261108-020318-0c81e387\publish -EvidenceRoot .\artifacts\ssh-integrate-20261108T020749Z` | 1 | Light PNG captured; Dark selection failed through UI Automation. Exactly **two** launches; cap exhausted, no retry. |
| `git diff --check` and staged documentation diff check | 0 | No whitespace errors. |

Required new assertion coverage:
- (a) Cold-start labels never say reconnect; actual recovery labels carry the attempt count.
- (b) A detail ending in `。` and a detail without ellipsis both stay non-failed; changed keys carry typed failure kind, short label and full recovery instructions. WSL's existing stage assertions use typed labels too.
- (d) Native missing-executable execution yields `MissingOpenSsh`; Win32 2/3 are missing, 5 remains unavailable, and missing clients do not retry.
- (e) Fingerprint parsing and live confirmation provide separate algorithm / SHA256 fields; resolved address and key alias are separate.
- (f) A successful commit retains `Warning`; the UI-facing warning state has `Warning=true`, `Failed=false`; a normal success has no recovery override. Memory-only success copy is asserted.

Delivery live-fixture signals were logged as `SIGNAL fixture sshd PID=13461` and `SIGNAL fixture sshd PID=13765`. Each came from the fixture's PidFile after cmdline/config ownership verification. Windows SSH Job/PID signals are separately logged with `SIGNAL owned Job PID=...`; there are no process-name or process-group kills. The runner completed its owned throwaway-directory cleanup with exit 0.

- Initial focused runs: SSH **224 assertions**, connection **96 assertions**, both exit 0.
- Initial live run: **30 assertions** passed, but runner exit 1 because its cleanup guard still expected the security-lane fixture prefix. The guard was corrected; its owned throwaway directory was cleaned with exit 0. Next live run: exit 0, **30 assertions**, including cleanup.
- Final-candidate validation was repeated after correcting removal-command file selection to the actual offending record, with new regression assertions for a changed key in a later trust file. The earlier evidence is not substituted for the final candidate.
- Initial Debug build/publish/runtime-hash wrapper: exit 0, no warning/error lines. Final-candidate build is recorded in the delivery table.
- Final delivery build uses `c386b75` source; its mixed console encoding was handled with byte-wise UTF-8/GBK warning/error matching over the log tail, after single-encoding diagnostic readers failed to decode it. This was a log-reader issue, not a build failure.
- Capture preflight before any launch: `RichEditPrototype count=0`; `quser` reported the user's RDP session as `运行中` (active). Capture is gated again immediately before launch; at most two app launches, no desktop input.
- An initial capture wrapper also gated on installed `FluentCode.exe` and stopped before launching anything (exit 1). This extra prerequisite was removed: the requested `RichEditPrototype` check remains, and the capture script's PID/HWND-owned startup-smoke instances are isolated from the installed app. The installed process was not stopped or modified.

### Capture evidence

- Immediately before the actual capture: `quser` reported `zhang`, `rdp-tcp#0`, session 2, `运行中`; `RichEditPrototype count=0`.
- Light startup-smoke marker: PID **3740**, HWND **1248192**. Dark marker: PID **25144**, HWND **723854**. Both markers reference the delivery publish directory. The script's cleanup targets only these owned processes; no desktop input was sent.
- PNG: `/mnt/c/src/fluentcode-lanes/ssh-integrate/artifacts/ssh-integrate-20261108T020749Z/connection-ssh-light.png` (1440×974). Read back and verified as the actual SSH-selected connection page with all four options and the memory-only password hint.
- Dark failed with exact message `SSH connection mode was not reachable through UI Automation.` No Dark PNG was produced. The launch cap prevents another attempt in this task. Log: `/tmp/opencode/sshint-capture-delivery.log`.

## Files changed relative to security base

Documentation:
- `docs/ssh.md`
- `docs/ssh-integration.md`

App and UI:
- `src/FluentCode/App.OpenCode.cs`
- `src/FluentCode/App.OpenCode.SshRecovery.cs`
- `src/FluentCode/App.OpenCode.WslRecovery.cs`
- `src/FluentCode/Pages/Account/OpenCodeConnectionView.cs`
- `src/FluentCode/Pages/OpenCode/OpenCodeSshQuickAccess.cs` (merged UI lane)
- `src/FluentCode/Pages/OpenCode/OpenCodeWslQuickAccessView.cs` (merged UI lane)
- `src/FluentCode/Pages/OpenCodeBrowserPreview.Composition.cs` (merged UI lane)
- `src/FluentCode/Pages/OpenCodePage.WslQuickAccess.cs` (merged UI lane)
- `src/FluentCode/Pages/OpenCodePage.xaml`
- `src/FluentCode/Pages/OpenCodePage.xaml.cs`

Services:
- `src/FluentCode/Services/OpenCode/Connection/OpenCodeConnectionStatus.cs`
- `src/FluentCode/Services/OpenCode/Connection/SshConnectionTarget.cs`
- `src/FluentCode/Services/OpenCode/Connection/SshHostKeyVerifier.cs`
- `src/FluentCode/Services/OpenCode/Connection/SshOpenCodeConnector.cs`
- `src/FluentCode/Services/OpenCode/Connection/SshOpenCodeRecovery.cs`
- `src/FluentCode/Services/OpenCode/Connection/SshOpenCodeServiceLocator.cs`
- `src/FluentCode/Services/OpenCode/Connection/SshOpenCodeTunnel.cs`
- `src/FluentCode/Services/OpenCode/Connection/WindowsSshProcess.cs`
- `src/FluentCode/Services/OpenCode/Connection/WslOpenCodeRecovery.cs`

Tests and fixture:
- `tests/opencode-connection/Program.cs`
- `tests/opencode-ssh/Program.cs`
- `tests/opencode-ssh/SecurityTests.cs`
- `tests/opencode-ssh/cleanup-fixture.ps1`
- `tests/opencode-ssh/rekey-fixture.sh`
- `tests/opencode-ssh/run-fixture.sh`

## Remaining scope

The only incomplete requested evidence is the Dark capture (failure and exhausted launch cap above). Service tests, live fixture, Debug build and diff checks pass.

The security lane's limitations in `docs/ssh-security-fixes.md` remain: ProxyJump is unsupported, user ProxyCommand/Match exec is trusted executable configuration, and automatic startup requires explicit verifiable loopback configuration. Visual polish should go to `@designer` with `src/FluentCode/Pages/Account/OpenCodeConnectionView.cs` (status actions / fingerprint rows) and `src/FluentCode/Pages/OpenCodePage.xaml` (non-blocking security InfoBar). Only the requested focused runners, live fixture, Debug wrapper, diff check and idle-page captures are in this integration validation scope.
