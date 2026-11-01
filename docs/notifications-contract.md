# FluentCode notification service

`src/FluentCode/Services/Notifications/CompletionNotificationService*.cs` implements
OpenCode completion, permission and Form notifications. The application owns one
service and one shared interaction gate; see [application integration](app-integration-contract.md)
and [OpenCode notification integration](notification-integration-contract.md).

## Registration and lifetime

- Subscribe to `NotificationInvoked` before registration.
- Register with `AppNotificationManager.Default.Register("FluentCode", icon)`;
  the unpackaged WinAppSDK registration is derived from this executable.
- Registration, notification creation and platform errors are isolated from the
  conversation. `Submitted` means the platform accepted the submission, not that
  the user saw a banner.
- Shutdown closes admission, cancels/drains owned interaction work and unregisters
  the service. UI handling is marshalled to the owning dispatcher.

## Completion notifications

- `NotificationSource` contains OpenCode only.
- `ObserveOpenCodeMessages` consumes observed messages. The first snapshot creates
  a historical baseline; it does not replay old completions.
- Only a newly observed stable idle ID with `outcome=succeeded` can produce a
  completion notification. Silence, HTTP acceptance, failure or interruption do
  not imply success.
- The body is fixed completion text. Activation uses an opaque runtime token,
  not a session ID, endpoint, message body, URL or credential.
- Deduplication is bounded and in-memory. A foreground visible session can
  suppress a notification without later replaying it.
- `HandleCompletionActivation` resolves only a trusted, live runtime target.
  A cold/unknown token cannot manufacture a navigation target or perform a mutation.

## Permission and Form interactions

- `NotifyPermissionAsync`, `NotifyFormAsync` and
  `HandleInteractionActivationAsync` use a trusted OpenCode source and the same
  pending recheck/nonce/claim coordinator as page interactions.
- Each body/action uses a random nonce. Activation does not select a client,
  endpoint, session or decision from untrusted external arguments.
- Before mutation, re-fetch pending state and verify identity and the recorded
  request fingerprint. Duplicate/concurrent actions share the claim/gate boundary.
- Permission actions are Once / Always / Reject. Always applies only the shown
  patterns; reject invalidates affected pending permission actions conservatively.
- Native Forms support text and supported single-choice/custom-choice fields.
  All questions must be representable within native input limits; unsupported or
  unknown constraints require application UI rather than silently dropping fields.
- Validate required answers, known input keys and choices before sending. Answers
  are not logged or persisted by the notification service.
- A mutation with an uncertain transport outcome remains `OutcomeUnknown` and
  is not automatically retried. Only the specified successful response resolves it.
- Nonce/target mappings do not survive restart; cold activations require fresh
  application-side verification, never reconstructed approval.

## Current checks

The [critical suite](test-suite.md) includes `tests/notification-form/run.ps1`.
Its headless checks cover policy and native notification markup; operating-system
banner visibility and desktop registration are separate from those checks.
