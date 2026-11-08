// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Generic;

namespace FluentCode.Services.NativeUi;

// Host-boundary data shared by the native renderer and source-linked headless integration tests.
internal sealed record NativeUiAssistantRequest(string Message, object? Context, IReadOnlyDictionary<string, object?> FormState);
internal sealed record NativeUiHostResult(bool Accepted, string Message);
