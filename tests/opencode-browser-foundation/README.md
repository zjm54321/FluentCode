# Browser foundation contracts

Run `run.ps1 -EvidenceRoot <absolute-output-directory>` with .NET 10 on Windows.

The harness source-links production Page, Workspace, dispatcher, options, navigation and proxy policy. Platform
stand-ins supply the UI queue/visual tree; injected fake drivers and runtime test ownership and sequencing.
It checks CDP session forwarding and cross-thread cancellation, generation/commit/completion behavior,
takeover/activity preservation, explicit focus, runtime-before-native creation, stable proxy/profile/lease binding,
ordered disposal, missing-runtime failure and private proxy authentication/arguments.

It does not execute the native WebView2 adapter, capture pixels, download files or run Lighthouse. Compile that
adapter against real WinUI/WebView2 projections and use the parent's native smoke coverage for those boundaries.
The independent peer harness covers real transport, immutable lease lifecycle and long-lived tunnel reads.
