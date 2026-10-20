using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

internal static class Program
{
    private static int checks;

    private static int Main()
    {
        DescribedStringAndAnswers();
        TypedConstraintsAndFailClosed();
        SerializationAndFingerprint();
        Console.WriteLine($"PASS 3 focused groups; {checks} assertions. Headless DTO/policy only; UI/default hookup pending.");
        return 0;
    }

    private static void DescribedStringAndAnswers()
    {
        var form = Parse(Fixtures.DescribedChoice);
        var plan = Plan(form);
        var field = plan.Fields[0];
        Check(field.Kind == OpenCodeFormFieldKind.Choice && field.Options[0].Description == "Continue the task", "described choice");
        Check(field.Placeholder == "Choose explicitly" && field.Default?.Text == "yes"
            && field.MinLength == 3 && field.MaxLength == 8, "typed string metadata");
        var good = Answer(plan, new() { ["decision"] = new(Choice: "yes") });
        Check(good["decision"].ValueKind == JsonValueKind.String && good["decision"].GetString() == "yes", "typed answer");
        BadAnswer(plan, new() { ["decision"] = new(Choice: "no") }, "known option still violates minLength");
        BadAnswer(plan, new() { ["decision"] = new(Choice: "forged") }, "unknown option");
        BadAnswer(plan, new(), "required default not automatically answered");
        var optional = Plan(form with { Fields = new() { form.Fields![0] with { Required = false } } });
        Check(Answer(optional, new()).Count == 0, "optional default remains metadata, not an answer");

        var text = FieldPlan("""{"key":"q","type":"string","minLength":2,"maxLength":4,"default":"ok","placeholder":"Type here"}""");
        Check(Answer(text, new() { ["q"] = new(Text: "用户") })["q"].GetString() == "用户", "BMP length");
        BadAnswer(text, new() { ["q"] = new(Text: "a") }, "text lower bound");
        BadAnswer(text, new() { ["q"] = new(Text: "abcde") }, "text upper bound");
        Check(Answer(text, new() { ["q"] = new(Text: " ") }).Count == 0, "existing optional whitespace omission");
        Check(Answer(text, new() { ["q"] = new(Text: "😀") }).Count == 1, "ambiguous UTF16/scalar minimum deferred to server");
        var unicodeMax = FieldPlan("""{"key":"q","type":"string","maxLength":1}""");
        Check(Answer(unicodeMax, new() { ["q"] = new(Text: "😀") }).Count == 1, "ambiguous UTF16/scalar maximum deferred to server");
        BadAnswer(unicodeMax, new() { ["q"] = new(Text: "😀😀") }, "unambiguous Unicode upper bound");
        var custom = FieldPlan("""{"key":"q","type":"string","custom":true,"minLength":2,"maxLength":3,"default":"zz","options":[{"value":"ok","label":"OK"}]}""");
        Check(custom.Fields[0].Default?.Text == "zz", "custom default not required to be an option");
        Check(Answer(custom, new() { ["q"] = new(Text: "zz", Choice: "ok") })["q"].GetString() == "zz", "custom input precedence retained");
        BadAnswer(custom, new() { ["q"] = new(Text: "long", Choice: "ok") }, "custom constraints checked");
        Console.WriteLine("PASS group 1: real DTO -> accepted described/minLength/placeholder/default plan -> typed answers; no default application");
    }

    private static void TypedConstraintsAndFailClosed()
    {
        var plan = Plan(Parse("""
            {"fields":[
              {"key":"n","type":"number","minimum":1.5,"maximum":3.5,"default":2.5},
              {"key":"i","type":"integer","minimum":1.2,"maximum":4.8,"default":3},
              {"key":"b","type":"boolean","default":false},
              {"key":"m","type":"multiselect","minItems":2,"maxItems":2,"default":["a","b"],
               "options":[{"value":"a","label":"A"},{"value":"b","label":"B"},{"value":"c","label":"C"}]}]}
            """));
        Check(plan.Fields[0].Minimum == 1.5 && plan.Fields[0].Maximum == 3.5 && plan.Fields[0].Default?.Number == 2.5, "finite numeric metadata");
        Check(plan.Fields[1].Default?.Integer == 3 && plan.Fields[2].Default?.Boolean == false
            && plan.Fields[3].Default!.Selections!.SequenceEqual(new[] { "a", "b" }), "typed integer/bool/array defaults");
        Check(plan.Fields[3].MinItems == 2 && plan.Fields[3].MaxItems == 2, "multiselect bounds metadata");
        var good = Answer(plan, new()
        {
            ["n"] = new(Text: "1.5"), ["i"] = new(Text: "4"), ["b"] = new(Boolean: false),
            ["m"] = new(Selections: new[] { "a", "b", "b" }),
        });
        Check(good["n"].ValueKind == JsonValueKind.Number && good["i"].GetInt64() == 4
            && good["b"].ValueKind == JsonValueKind.False && good["m"].GetArrayLength() == 2, "typed answer and existing dedup behavior");
        Check(Answer(plan, new()).Count == 0, "all optional defaults stay metadata");
        BadAnswer(plan, new() { ["n"] = new(Text: "3.6") }, "numeric maximum");
        BadAnswer(plan, new() { ["n"] = new(Text: "1.4") }, "numeric minimum");
        BadAnswer(plan, new() { ["n"] = new(Text: "NaN") }, "nonfinite answer");
        BadAnswer(plan, new() { ["i"] = new(Text: "1") }, "fractional integer minimum");
        BadAnswer(plan, new() { ["i"] = new(Text: "5") }, "fractional integer maximum");
        BadAnswer(plan, new() { ["i"] = new(Text: "2.5") }, "noninteger answer");
        BadAnswer(plan, new() { ["m"] = new(Selections: new[] { "a", "a" }) }, "minimum after dedup");
        BadAnswer(plan, new() { ["m"] = new(Selections: new[] { "a", "b", "c" }) }, "multiselect maximum");
        // D4: multiselect + custom=true is answerable in the App; custom text is APPENDED after ticked options.
        var multiCustom = FieldPlan("""{"key":"m","type":"multiselect","custom":true,"required":true,"maxItems":3,"options":[{"value":"a","label":"A"},{"value":"b","label":"B"}]}""");
        Check(multiCustom.Fields[0].Kind == OpenCodeFormFieldKind.MultiSelect && multiCustom.Fields[0].AllowsCustom, "multiselect+custom planned (D4)");
        Check(!plan.Fields[3].AllowsCustom, "plain multiselect does not allow custom");
        var appended = Answer(multiCustom, new() { ["m"] = new(Text: "  my own  ", Selections: new[] { "b", "a" }) })["m"];
        Check(appended.ValueKind == JsonValueKind.Array
            && appended.EnumerateArray().Select(e => e.GetString()).SequenceEqual(new[] { "b", "a", "my own" }), "custom text appended (trimmed) after ticked options");
        var onlyCustom = Answer(multiCustom, new() { ["m"] = new(Text: "solo", Selections: Array.Empty<string>()) })["m"];
        Check(onlyCustom.GetArrayLength() == 1 && onlyCustom[0].GetString() == "solo", "custom text alone satisfies required");
        var dedup = Answer(multiCustom, new() { ["m"] = new(Text: "a", Selections: new[] { "a" }) })["m"];
        Check(dedup.GetArrayLength() == 1, "custom equal to ticked value not duplicated");
        Check(Answer(multiCustom, new() { ["m"] = new(Text: "   ", Selections: new[] { "a" }) })["m"].GetArrayLength() == 1, "whitespace custom ignored");
        BadAnswer(multiCustom, new() { ["m"] = new(Text: " ", Selections: Array.Empty<string>()) }, "required multiselect+custom with nothing");
        BadAnswer(multiCustom, new() { ["m"] = new(Text: "x", Selections: new[] { "forged" }) }, "custom never legitimises forged ticks");
        var cappedCustom = FieldPlan("""{"key":"m","type":"multiselect","custom":true,"maxItems":2,"options":[{"value":"a","label":"A"},{"value":"b","label":"B"}]}""");
        BadAnswer(cappedCustom, new() { ["m"] = new(Text: "x", Selections: new[] { "a", "b" }) }, "custom text counts toward maxItems");
        BadAnswer(plan, new() { ["m"] = new(Text: "x", Selections: new[] { "a", "b" }) }, "plain multiselect rejects custom text");
        var customDefault = FieldPlan("""{"key":"m","type":"multiselect","custom":true,"default":["a","mine"],"options":[{"value":"a","label":"A"}]}""").Fields[0];
        Check(customDefault.Default!.Selections!.SequenceEqual(new[] { "a" }) && customDefault.Default.Text == "mine"
            && OpenCodeFormFieldPresentation.InitialCustomText(customDefault) == "mine"
            && OpenCodeFormFieldPresentation.HasVisiblePrefill(customDefault), "one non-option default becomes custom prefill");
        Check(FieldPlan("""{"key":"m","type":"multiselect","custom":true,"minItems":2,"options":[{"value":"a","label":"A"}]}""").Fields[0].MinItems == 2,
            "minItems may count the custom entry");
        Check(OpenCodeFormFieldPresentation.QuestionSummary(multiCustom) == "多选 · 2 个选项 · 可自己输入 · 必答", "summary mentions custom input");
        Check(OpenCodeFormFieldPresentation.ConstraintHint(multiCustom.Fields[0]) == "最多选择 3 项（自己输入的内容算 1 项）", "count hint mentions custom");
        foreach (var field in new[]
        {
            """{"key":"q","type":"multiselect","custom":true,"default":["x","y"],"options":[{"value":"a","label":"A"}]}""",
            """{"key":"q","type":"multiselect","custom":true,"default":[" "],"options":[{"value":"a","label":"A"}]}""",
            """{"key":"q","type":"multiselect","custom":true,"minItems":3,"options":[{"value":"a","label":"A"}]}""",
            """{"key":"q","type":"multiselect","custom":true}""",
        })
        {
            Check(!OpenCodeFormInputPolicy.TryCreatePlan(Parse("{\"fields\":[" + field + "]}"), out var rejected, out var why) && rejected is null && why.Count > 0,
                "multiselect+custom fail-closed: " + field);
        }

        var exact = FieldPlan("""{"key":"q","type":"integer","maximum":9007199254740992}""");
        BadAnswer(exact, new() { ["q"] = new(Text: "9007199254740993") }, "Int64 comparison does not round input to double");
        Check(FieldPlan("""{"key":"q","type":"integer","default":2.0}""").Fields[0].Default?.Integer == 2, "integral numeric default encoding");
        Check(FieldPlan("""{"key":"q","type":"string","minLength":2e0}""").Fields[0].MinLength == 2, "integral bound encoding");

        // Representative invalid metadata and explicitly unsupported features, not an exhaustive matrix.
        foreach (var field in new[]
        {
            """{"key":"q","type":"string","minLength":"2"}""",
            """{"key":"q","type":"string","maxLength":null}""",
            """{"key":"q","type":"string","minLength":-1}""",
            """{"key":"q","type":"string","minLength":1.5}""",
            """{"key":"q","type":"string","minLength":1e-29}""",
            """{"key":"q","type":"string","minLength":2147483648}""",
            """{"key":"q","type":"string","placeholder":false}""",
            """{"key":"q","type":"string","minLength":4,"maxLength":2}""",
            """{"key":"q","type":"string","minLength":3,"default":"no"}""",
            """{"key":"q","type":"string","default":null}""",
            """{"key":"q","type":"string","options":[{"value":"a","label":"A","description":42}]}""",
            """{"key":"q","type":"string","options":[{"value":"a","label":"A","description":null}]}""",
            """{"key":"q","type":"string","options":[{"value":"a","label":"A","unknown":true}]}""",
            """{"key":"q","type":"string","default":"b","options":[{"value":"a","label":"A"}]}""",
            """{"key":"q","type":"number","minimum":4,"maximum":2}""",
            """{"key":"q","type":"number","minimum":4,"default":2}""",
            """{"key":"q","type":"number","minimum":"Infinity"}""",
            """{"key":"q","type":"number","maximum":1e400}""",
            """{"key":"q","type":"number","default":"NaN"}""",
            """{"key":"q","type":"number","default":{"_tag":"Infinity"}}""",
            """{"key":"q","type":"integer","default":2.5}""",
            """{"key":"q","type":"integer","default":2.000000000000000000000000000001}""",
            """{"key":"q","type":"integer","minimum":1e30}""",
            """{"key":"q","type":"integer","maximum":9007199254740993}""",
            """{"key":"q","type":"integer","minimum":1.2,"maximum":1.8}""",
            """{"key":"q","type":"boolean","default":"false"}""",
            """{"key":"q","type":"boolean","minLength":2}""",
            """{"key":"q","type":"multiselect","minItems":2,"options":[{"value":"a","label":"A"}]}""",
            """{"key":"q","type":"multiselect","minItems":1,"default":[],"options":[{"value":"a","label":"A"}]}""",
            """{"key":"q","type":"multiselect","default":["a","a"],"options":[{"value":"a","label":"A"}]}""",
            """{"key":"q","type":"multiselect","default":[false],"options":[{"value":"a","label":"A"}]}""",
            """{"key":"q","type":"multiselect","default":["b"],"options":[{"value":"a","label":"A"}]}""",
            """{"key":"q","type":"string","pattern":"[a-z]+"}""",
            """{"key":"q","type":"string","format":"email"}""",
            """{"key":"q","type":"string","when":{}}""",
            """{"key":"q","type":"string","hidden":true}""",
            """{"key":"q","type":"external"}""",
            """{"key":"q","type":"string","futureConstraint":true}""",
        })
        {
            var accepted = OpenCodeFormInputPolicy.TryCreatePlan(Parse("{\"fields\":[" + field + "]}"), out var invalid, out var reasons);
            Check(!accepted && invalid is null && reasons.Count > 0, "explicit invalid/unsupported: " + field);
        }
        Console.WriteLine("PASS group 2: finite typed constraints/defaults, bad answers, malformed metadata, unknown/advanced fail-closed");
    }

    private static void SerializationAndFingerprint()
    {
        var form = Parse(Fixtures.DescribedChoice);
        var serialized = JsonSerializer.Serialize(form);
        using var json = JsonDocument.Parse(serialized);
        var field = json.RootElement.GetProperty("fields")[0];
        Check(field.GetProperty("minLength").GetInt32() == 3 && field.GetProperty("maxLength").GetInt32() == 8
            && field.GetProperty("placeholder").GetString() == "Choose explicitly" && field.GetProperty("default").GetString() == "yes"
            && field.GetProperty("options")[0].GetProperty("description").GetString() == "Continue the task", "metadata serialized unchanged");
        Check(field.EnumerateObject().Count(p => p.Name == "default") == 1 && !field.TryGetProperty("Default", out _), "no accessor duplication");
        Check(form.Fields![0].AdditionalProperties!.ContainsKey("minLength")
            && form.Fields[0].Options![0].AdditionalProperties!.ContainsKey("description"), "legacy consumers retain extension-data guard");
        var fingerprint = OpenCodeInteractionFingerprint.ForForm(form);
        Check(fingerprint == OpenCodeInteractionFingerprint.ForForm(Parse(serialized)), "round-trip fingerprint");
        var detail = JsonSerializer.Deserialize<OpenCodeFormDetail>(Fixtures.DescribedChoice.TrimEnd().TrimEnd('}') + ",\"state\":{\"status\":\"pending\"}}", OpenCodeJson.Options)!;
        Check(fingerprint == OpenCodeInteractionFingerprint.ForForm(detail), "Info/Detail projection unchanged");
        foreach (var replacement in new[] { ("\"minLength\":3", "\"minLength\":4"), ("\"maxLength\":8", "\"maxLength\":7"),
            ("Choose explicitly", "New placeholder"), ("\"default\":\"yes\"", "\"default\":\"no\""), ("Continue the task", "Changed description") })
            Check(fingerprint != OpenCodeInteractionFingerprint.ForForm(Parse(Fixtures.DescribedChoice.Replace(replacement.Item1, replacement.Item2))), "metadata fingerprint change " + replacement.Item1);
        foreach (var fieldJson in new[]
        {
            """{"key":"q","type":"number","minimum":1,"maximum":2,"default":1.5}""",
            """{"key":"q","type":"integer","minimum":1,"maximum":2,"default":1}""",
            """{"key":"q","type":"boolean","default":false}""",
            """{"key":"q","type":"multiselect","minItems":1,"maxItems":2,"default":["a"],"options":[{"value":"a","label":"A"},{"value":"b","label":"B"}]}""",
            """{"key":"q","type":"string","futureConstraint":{"nested":true}}""",
        })
        {
            var dto = Parse("{\"fields\":[" + fieldJson + "]}");
            var roundTrip = Parse(JsonSerializer.Serialize(dto));
            Check(OpenCodeInteractionFingerprint.ForForm(dto) == OpenCodeInteractionFingerprint.ForForm(roundTrip), "numeric/boolean/array/unknown roundtrip");
            foreach (var property in dto.Fields![0].AdditionalProperties!)
            {
                Check(roundTrip.Fields![0].AdditionalProperties![property.Key].GetRawText() == property.Value.GetRawText(), "raw metadata preserved " + property.Key);
                var changed = dto with { Fields = new() { dto.Fields[0] with { AdditionalProperties = new(dto.Fields[0].AdditionalProperties!) } } };
                changed.Fields[0].AdditionalProperties!.Remove(property.Key);
                Check(OpenCodeInteractionFingerprint.ForForm(changed) != OpenCodeInteractionFingerprint.ForForm(dto), "fingerprint preserves " + property.Key);
            }
        }
        Console.WriteLine($"Fixture fingerprint: {fingerprint}");
        Console.WriteLine("PASS group 3: serialization, metadata fingerprints, legacy extension-data preservation");
    }

    private static OpenCodeFormInfo Parse(string json) => JsonSerializer.Deserialize<OpenCodeFormInfo>(json, OpenCodeJson.Options)!;
    private static OpenCodeFormPlan FieldPlan(string field) => Plan(Parse("{\"fields\":[" + field + "]}"));
    private static OpenCodeFormPlan Plan(OpenCodeFormInfo form)
    {
        Check(OpenCodeFormInputPolicy.TryCreatePlan(form, out var plan, out var reasons), "plan: " + string.Join("; ", reasons));
        return plan!;
    }

    private static Dictionary<string, JsonElement> Answer(OpenCodeFormPlan plan, Dictionary<string, OpenCodeFormFieldInput> input)
    {
        Check(OpenCodeFormInputPolicy.TryBuildAnswer(plan, input, out var answer, out var errors), "answer: " + string.Join("; ", errors));
        return answer;
    }

    private static void BadAnswer(OpenCodeFormPlan plan, Dictionary<string, OpenCodeFormFieldInput> input, string name)
        => Check(!OpenCodeFormInputPolicy.TryBuildAnswer(plan, input, out var answer, out var errors) && answer.Count == 0 && errors.Count > 0, name);

    private static void Check(bool value, string message)
    {
        checks++;
        if (!value) throw new Exception(message);
    }
}
