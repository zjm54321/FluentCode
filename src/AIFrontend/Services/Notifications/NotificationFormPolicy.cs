using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Services.Notifications;

internal sealed record NotificationChoice(string Id, string Label, string Value);
internal sealed record NotificationInput(string Id, string Title, IReadOnlyList<NotificationChoice>? Choices);
internal sealed record NotificationField(string Key, bool Required, string? ChoiceId, string? TextId,
    IReadOnlyList<NotificationChoice> Choices);

/// <summary>Conservative all-or-nothing projection of real V2 string fields; no tool-input/Question inference.</summary>
internal static class NotificationFormPolicy
{
    internal const int MaxInputs = 5;
    internal const int MaxButtons = 5;
    // A conservative policy limit, not a claim that the SDK accepts more options.
    internal const int MaxChoices = 5;

    internal static bool TryCreate(OpenCodeFormDetail form, out NotificationInput[] inputs,
        out NotificationField[] fields)
    {
        inputs = [];
        fields = [];
        if (form.Fields is not { Count: > 0 and <= MaxInputs } || !SafeText(form.Title, 200)) return false;
        var inputList = new List<NotificationInput>();
        var fieldList = new List<NotificationField>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in form.Fields)
        {
            if (field is null || field.Type != OpenCodeFormFieldTypes.String || field.Hidden == true
                || field.AdditionalProperties is { Count: > 0 } || !SafeText(field.Key, 128)
                || !keys.Add(field.Key!) || (field.Title is not null && !SafeText(field.Title, 200))
                || (field.Description is not null && !SafeText(field.Description, 200))) return false;

            string title = field.Title ?? field.Key!;
            if (!string.IsNullOrEmpty(field.Description)) title += " — " + field.Description;
            if (field.Required == true) title += "（必填）";
            string? choiceId = null;
            string? textId = null;
            var choices = new List<NotificationChoice>();
            if (field.Options is not null)
            {
                if (field.Options.Count is < 1 or > MaxChoices) return false;
                var values = new HashSet<string>(StringComparer.Ordinal);
                foreach (var option in field.Options)
                {
                    if (option is null || !SafeText(option.Value, 256) || !SafeText(option.Label, 128)
                        || !values.Add(option.Value!) || option.AdditionalProperties is { Count: > 0 }) return false;
                    choices.Add(new("o" + choices.Count, option.Label!, option.Value!));
                }
                choiceId = "i" + inputList.Count;
                inputList.Add(new(choiceId, title, choices.ToArray()));
            }
            if (choiceId is null || field.Custom == true)
            {
                textId = "i" + inputList.Count;
                inputList.Add(new(textId, choiceId is null ? title : title + "（自定义优先）", null));
            }
            if (inputList.Count > MaxInputs) return false;
            fieldList.Add(new(field.Key!, field.Required == true, choiceId, textId, choices.ToArray()));
        }
        inputs = inputList.ToArray();
        fields = fieldList.ToArray();
        return true;
    }

    internal static bool TryAnswer(IReadOnlyList<NotificationField> fields,
        IReadOnlyDictionary<string, string> input, out Dictionary<string, JsonElement> answer)
    {
        answer = new(StringComparer.Ordinal);
        var allowed = new HashSet<string>(fields.SelectMany(f => new[] { f.ChoiceId, f.TextId })
            .Where(id => id is not null).Select(id => id!), StringComparer.Ordinal);
        if (input.Count > MaxInputs || input.Any(p => !allowed.Contains(p.Key)
            || p.Value is null || p.Value.Length > 4096)) return false;
        foreach (var field in fields)
        {
            string? text = field.TextId is not null && input.TryGetValue(field.TextId, out var t) ? t : null;
            string? selection = field.ChoiceId is not null && input.TryGetValue(field.ChoiceId, out var s) ? s : null;
            // Even when custom wins, a forged nonempty selection is not accepted.
            var choice = field.Choices.FirstOrDefault(c => c.Id == selection);
            if (!string.IsNullOrEmpty(selection) && choice is null) return false;
            string? value = !string.IsNullOrWhiteSpace(text) ? text : choice?.Value;
            if (value is null)
            {
                if (field.Required) return false;
                continue; // Optional unanswered field; never invent a default.
            }
            answer.Add(field.Key, JsonSerializer.SerializeToElement(value));
        }
        return true;
    }

    private static bool SafeText(string? value, int limit) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= limit && !value.Any(char.IsControl);
}
