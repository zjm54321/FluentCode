using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Services.Notifications;

internal sealed record NotificationChoice(string Id, string Label, string Value);
internal sealed record NotificationInput(string Id, string Title, IReadOnlyList<NotificationChoice>? Choices);
internal sealed record NotificationField(string Key, bool Required, string? ChoiceId, string? TextId,
    IReadOnlyList<NotificationChoice> Choices, OpenCodeFormField Source);

/// <summary>Atomic projection within Windows' 5-input/5-choice budget; display metadata never becomes an answer.</summary>
internal static class NotificationFormPolicy
{
    internal const int MaxInputs = 5;
    internal const int MaxButtons = 5;
    internal const int MaxChoices = 5;

    internal static bool TryCreate(OpenCodeFormDetail form, out NotificationInput[] inputs, out NotificationField[] fields)
    {
        inputs = [];
        fields = [];
        if (form.Fields is not { Count: > 0 and <= MaxInputs }) return false;
        var inputList = new List<NotificationInput>();
        var fieldList = new List<NotificationField>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in form.Fields)
        {
            if (field is null || string.IsNullOrWhiteSpace(field.Key) || !keys.Add(field.Key)
                || field.Hidden == true || field.Type is not ("string" or "number" or "integer" or "boolean" or "multiselect")) return false;
            // Preserve unknown behavioral extensions as an App-only form, not an incorrectly simplified reply.
            if (field.AdditionalProperties?.Keys.Any(k => k is not ("metadata" or "placeholder" or "default"
                or "minLength" or "maxLength" or "minimum" or "maximum" or "minItems" or "maxItems")) == true) return false;
            string title = NotificationText.SingleLine(string.IsNullOrWhiteSpace(field.Title) ? field.Key : field.Title, 70, "问题");
            if (field.Required == true) title += "（必填）";
            var choices = new List<NotificationChoice>();
            if (field.Type == "boolean")
            {
                choices.Add(new("o0", "是", "true"));
                choices.Add(new("o1", "否", "false"));
            }
            else if (field.Options is { } options)
            {
                var values = new HashSet<string>(StringComparer.Ordinal);
                foreach (var option in options)
                {
                    if (option is null || string.IsNullOrWhiteSpace(option.Value) || !values.Add(option.Value)) return false;
                    string label = string.IsNullOrWhiteSpace(option.Label) ? option.Value : option.Label;
                    if (option.GetDescription().ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(option.GetDescription().GetString()))
                        label += " · " + NotificationText.SingleLine(option.GetDescription().GetString(), 24);
                    choices.Add(new("o" + choices.Count, NotificationText.SingleLine(
                        label, 40, "选项"), option.Value));
                }
            }
            if (field.Type == "multiselect" && choices.Count == 0) return false;
            string? choiceId = null, textId = null;
            if (choices.Count is > 0 and <= MaxChoices && field.Type != "multiselect")
            {
                choiceId = "i" + inputList.Count;
                inputList.Add(new(choiceId, title, choices.ToArray()));
            }
            if (choiceId is null || field.Custom == true)
            {
                textId = "i" + inputList.Count;
                string hint = field.Type == "multiselect" ? "输入 #序号或选项值，多个用逗号分隔（如 #1,#2）"
                    : choiceId is not null ? "或自己输入（填写后以此为准）" : choices.Count > 0 ? "输入 #序号（如 #1）或完整选项值" : title;
                inputList.Add(new(textId, hint, null));
            }
            if (inputList.Count > MaxInputs) return false;
            fieldList.Add(new(field.Key, field.Required == true, choiceId, textId, choices.ToArray(), field));
        }
        inputs = inputList.ToArray(); fields = fieldList.ToArray(); return true;
    }

    internal static bool IsQuestion(OpenCodeFormDetail form) => form.Metadata is { ValueKind: JsonValueKind.Object } metadata
        && metadata.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "question";

    internal static string Describe(OpenCodeFormDetail form)
    {
        // description is the question prompt for question-tool forms. Reserve the two body
        // elements for that prompt; combo option descriptions live beside their options instead.
        int budget = 240 / form.Fields!.Count;
        return NotificationText.SingleLine(string.Join("；", form.Fields.Select(f =>
        {
            string prompt = NotificationText.SingleLine(f.Title ?? f.Key, 32) + "：" + f.Description;
            bool numbered = f.Options is { Count: > 0 } && (f.Type == "multiselect" || f.Options.Count > MaxChoices);
            if (!numbered) return NotificationText.SingleLine(prompt, budget);
            string options = string.Join(" / ", f.Options!.Select((o, i) => $"#{i + 1} {o.Label}"));
            return NotificationText.SingleLine(prompt, budget / 2) + " " + NotificationText.SingleLine(options, budget / 2);
        })), 240);
    }

    internal static bool TryAnswer(IReadOnlyList<NotificationField> fields,
        IReadOnlyDictionary<string, string> input, out Dictionary<string, JsonElement> answer)
    {
        answer = new(StringComparer.Ordinal);
        var allowed = fields.SelectMany(f => new[] { f.ChoiceId, f.TextId }).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        if (input.Count > MaxInputs || input.Any(p => !allowed.Contains(p.Key) || p.Value is null || p.Value.Length > 4096)) return false;
        foreach (var field in fields)
        {
            string? text = field.TextId is not null && input.TryGetValue(field.TextId, out var t) && !string.IsNullOrWhiteSpace(t) ? t.Trim() : null;
            string? selected = field.ChoiceId is not null && input.TryGetValue(field.ChoiceId, out var s) ? s : null;
            var choice = field.Choices.FirstOrDefault(c => c.Id == selected);
            if (!string.IsNullOrEmpty(selected) && choice is null) return false;
            string? value = text ?? choice?.Value;
            if (value is null) { if (field.Required) return false; continue; }
            object result;
            switch (field.Source.Type)
            {
                case "boolean":
                    if (!bool.TryParse(value, out bool boolean)) return false;
                    result = boolean; break;
                case "number":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                        || !double.IsFinite(number) || !Bounds(field.Source, number)) return false;
                    result = number; break;
                case "integer":
                    if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer)
                        || !Bounds(field.Source, integer)) return false;
                    result = integer; break;
                case "multiselect":
                    var selections = new List<string>();
                    foreach (string part in value.Split([',', '，'], StringSplitOptions.TrimEntries))
                    {
                        string? resolved = Resolve(field, part);
                        if (resolved is null || resolved.Length == 0) return false;
                        if (!selections.Contains(resolved, StringComparer.Ordinal)) selections.Add(resolved);
                    }
                    if (!CountBounds(selections.Count, field.Source.GetMinItems(), field.Source.GetMaxItems())) return false;
                    result = selections; break;
                default:
                    if (text is not null && field.ChoiceId is null && field.Choices.Count > 0)
                    { value = Resolve(field, text); if (value is null) return false; }
                    if (!CountBounds(value.Length, field.Source.GetMinLength(), field.Source.GetMaxLength())) return false;
                    result = value; break;
            }
            answer.Add(field.Key, JsonSerializer.SerializeToElement(result));
        }
        return true;
    }

    private static string? Resolve(NotificationField field, string text)
    {
        // #N is the explicit ordinal syntax advertised in the toast. Bare numbers are
        // exact option values (or custom text), never implicit one-based indexes.
        var exact = field.Choices.FirstOrDefault(c => c.Value == text);
        if (text.StartsWith('#') && int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            if (index <= 0 || index > field.Choices.Count) return null;
            string ordinal = field.Choices[index - 1].Value;
            // Never choose between two different answers spelled with the same #N token.
            return exact is null || exact.Value == ordinal ? ordinal : null;
        }
        if (exact is not null) return exact.Value;
        return field.Source.Custom == true ? text : null;
    }
    private static bool CountBounds(int value, JsonElement min, JsonElement max) =>
        (min.ValueKind == JsonValueKind.Undefined || min.ValueKind == JsonValueKind.Number && min.TryGetInt32(out int a) && value >= a)
        && (max.ValueKind == JsonValueKind.Undefined || max.ValueKind == JsonValueKind.Number && max.TryGetInt32(out int b) && value <= b);
    private static bool Bounds(OpenCodeFormField field, double value) =>
        (field.GetMinimum().ValueKind == JsonValueKind.Undefined || field.GetMinimum().ValueKind == JsonValueKind.Number && field.GetMinimum().TryGetDouble(out double a) && value >= a)
        && (field.GetMaximum().ValueKind == JsonValueKind.Undefined || field.GetMaximum().ValueKind == JsonValueKind.Number && field.GetMaximum().TryGetDouble(out double b) && value <= b);
}
