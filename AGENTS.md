# Agent Guidelines for FluentCode

This document provides concise instructions for automated agents working on the FluentCode repository.

---

## 1. Repository Structure & Scope

- **Application Source**: Located in `src/FluentCode/`. All C# / XAML development belongs here.
- **Vendored Dependencies**: Located in `src/ThirdParty/` (e.g., `WindowsTerminalControl`). Do not edit vendored code unless explicitly instructed.
- **Reference Repositories**: Read-only dependencies may be cloned under `.slim/clonedeps/repos/` per `.slim/clonedeps.json` (such as `ranxianglei/billion-context`). Never edit files inside `.slim/`.
- **Scripts**: Entry points in `scripts/` automate building, publishing, running, and icon generation.
- **Tools**: Browser runtime preparation scripts in `tools/browser-runtime/`.
- **Documentation**: All architecture and developer documentation resides under `docs/`.

---

## 2. Build & Verification Rules

1. **Browser Runtime Prerequisite**:
   From a fresh clone or after updating browser runtime dependencies, run:
   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\browser-runtime\prepare.ps1
   ```
   This generates the uncommitted `artifacts/browser-runtime/` bundle required by MSBuild.

2. **Native Build & Publish**:
   Use the tracked PowerShell wrappers:
   - Debug: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-debug.ps1`
   - Release: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-release.ps1`
   - Build-only shortcut: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Configuration Debug`
   
   Both build scripts support an optional `-EvidenceRoot <NewPath>` to redirect logs and publish outputs.

3. **Running**:
   Launch the compiled output via:
   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\run.ps1 -Configuration Debug
   ```

4. **Testing**:
   Run the 28-suite automated test battery via:
   ```bash
   python3 tests/run-suite.py --root "artifacts/scripts-move-suite-$(date -u +%Y%m%dT%H%M%SZ)" --execute
   ```
   *Requirement*: The `--root` directory must be newly created. The driver verifies that tracked and untracked files remain immutable during the test run.

---

## 3. Implementation Invariants

- **Security & DPAPI**: Sensitive values (passwords, tokens, API keys) must be protected using `DpapiSecretStore` (`%LOCALAPPDATA%\FluentCode\settings.json`). Never log, display, or serialize plaintext credentials.
- **Fail-Closed Protection**: Use `OpenCodeInteractionGate` leases before sending mutations (permissions, forms, cancellations). Ambiguous transport failures must resolve to `OutcomeUnknown` rather than being retried blindly.
- **UI Thread Safety**: All HWND, WinUI element, taskbar, and notification dispatch operations must execute on the window STA / UI thread.
- **Airspace Safety**: The native Windows Terminal control is hosted as a child HWND. When XAML flyouts, overlays, or modals are visible, ensure the native HWND is synchronously hidden to prevent airspace clipping issues.
- **Documentation Updates**: Whenever modifying public contracts, UI tokens, or protocols, update the corresponding file under `docs/`.
