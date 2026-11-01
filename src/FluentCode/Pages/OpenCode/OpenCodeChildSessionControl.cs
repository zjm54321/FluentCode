using System;
using System.Collections.Generic;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

// Read-only child-session identity. Messages are the current child's accepted assistant/switch rows in server order.
// A model reference is kept whole: never borrow a variant from an older/different model or from the parent.
internal sealed record OpenCodeChildSessionControl(string? Agent, OpenCodeModelRef? Model, string? ModelName, string? Effort)
{
    internal static OpenCodeChildSessionControl Project(string? sessionAgent, OpenCodeModelRef? sessionModel,
        IEnumerable<(string? Agent, OpenCodeModelRef? Model)> messages, Func<OpenCodeModelRef, OpenCodeModelInfo?> catalog)
    {
        var agent = Known(sessionAgent);
        var model = sessionModel;
        foreach (var message in messages)
        {
            if (Known(message.Agent) is { } actualAgent) agent = actualAgent;
            if (message.Model is { } actualModel) model = actualModel;
        }
        if (Known(model?.Id) is null || Known(model?.ProviderId) is null) model = null;
        var name = model is null ? null : Known(catalog(model)?.Name);
        var effort = model is null ? null : model.Variant is null ? OpenCodeModelPresentation.VariantLabel(null)
            : Known(model.Variant) is { } variant ? OpenCodeModelPresentation.VariantLabel(variant) : null;
        return new(agent, model, name, effort);
    }

    private static string? Known(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
