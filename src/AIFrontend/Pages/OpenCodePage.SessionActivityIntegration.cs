using System;
using AIFrontend.Services.Notifications;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    partial void BeginComposerInterruptProtection(OpenCodeInteractionGate.Lease lease, Guid operationId,
        string fingerprint, ref bool started)
    {
        lease.BeginSessionControl(operationId, SessionControlKind.Interrupt, fingerprint);
        started = true;
    }
}
