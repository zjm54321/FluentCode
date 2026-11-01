using System;
using FluentCode.Services.Notifications;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    partial void BeginComposerInterruptProtection(OpenCodeInteractionGate.Lease lease, Guid operationId,
        string fingerprint, ref bool started)
    {
        lease.BeginSessionControl(operationId, SessionControlKind.Interrupt, fingerprint);
        started = true;
    }
}
