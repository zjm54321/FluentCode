using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// 交互快照指纹：对已建模 DTO 做稳定序列化后的 SHA-256（大写十六进制）。
/// </summary>
/// <remarks>
/// 用途：提交权限决定或表单回答前，用重新查询到的 DTO 与用户确认时显示的快照比对；
/// 不一致时不得提交（scope/schema 可能已被服务端或另一客户端改变）。
/// 只保留摘要，不保留原始 JSON（其中可能含敏感元数据）。
/// </remarks>
internal static class OpenCodeInteractionFingerprint
{
    /// <summary>权限请求指纹（全部已建模字段，含 metadata/source）。</summary>
    internal static string ForPermission(OpenCodePermissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Hash(request);
    }

    /// <summary>
    /// 表单指纹。Form.Detail 比 Form.Info 多 state 字段；投影只包含列表/详情共有的已建模字段，
    /// 使 Info（列表快照）与 Detail（提交前重查快照）可直接比较。
    /// </summary>
    internal static string ForForm(OpenCodeFormInfo form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return Hash(new FormProjection(form.Id, form.SessionId, form.Title, form.Fields, form.Metadata));
    }

    private static string Hash<T>(T value)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private sealed record FormProjection(
        string? Id,
        string? SessionId,
        string? Title,
        List<OpenCodeFormField>? Fields,
        JsonElement? Metadata);
}

/// <summary>表单字段在本页可安全使用的原生表达方式。</summary>
internal enum OpenCodeFormFieldKind
{
    /// <summary>纯文本输入（string 无 options）。</summary>
    Text,

    /// <summary>单选下拉（string 有 options，custom 不为 true）。</summary>
    Choice,

    /// <summary>单选下拉 + 自定义文本框（string 有 options 且 custom=true；非空白自定义优先）。</summary>
    ChoiceWithCustom,

    /// <summary>三态选择（boolean；未选择 = 不回答）。</summary>
    Boolean,

    /// <summary>数字文本输入（number）。</summary>
    Number,

    /// <summary>整数文本输入（integer）。</summary>
    Integer,

    /// <summary>
    /// 多选复选框组（multiselect；答案为该选项 value 的字符串数组）。custom=true 时
    /// <see cref="OpenCodeFormFieldPlan.AllowsCustom"/> 为 true：非空白自定义文本（去首尾空白）追加在所勾选项之后。
    /// </summary>
    MultiSelect,
}

/// <summary>单个可表达选项（value 为提交值，label 为展示文本）。</summary>
internal sealed record OpenCodeFormOptionPlan(string Value, string Label)
{
    public string? Description { get; init; }
}

/// <summary>仅供 UI 显式呈现的类型化默认值；不会由答案装配器自动填入。成员与字段 Kind 对应。</summary>
internal sealed record OpenCodeFormDefaultValue(
    string? Text = null,
    double? Number = null,
    long? Integer = null,
    bool? Boolean = null,
    IReadOnlyList<string>? Selections = null);

/// <summary>单个字段的可表达投影。</summary>
internal sealed record OpenCodeFormFieldPlan(
    string Key,
    string Label,
    string? Description,
    bool Required,
    OpenCodeFormFieldKind Kind,
    IReadOnlyList<OpenCodeFormOptionPlan> Options)
{
    public string? Placeholder { get; init; }
    public OpenCodeFormDefaultValue? Default { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public int? MinItems { get; init; }
    public int? MaxItems { get; init; }

    /// <summary>仅 MultiSelect：服务端 custom=true，允许在勾选项之后追加一条自定义文本（用户决定 D4）。</summary>
    public bool AllowsCustom { get; init; }
}

/// <summary>整表单的可表达投影（全部字段均受支持时才存在）。</summary>
internal sealed record OpenCodeFormPlan(IReadOnlyList<OpenCodeFormFieldPlan> Fields);

/// <summary>单字段的用户输入快照（卡片按字段类型填充对应成员；未填 = null/空）。</summary>
internal readonly record struct OpenCodeFormFieldInput(
    string? Text = null,
    string? Choice = null,
    bool? Boolean = null,
    IReadOnlyList<string>? Selections = null);

/// <summary>
/// 真实 V2 Form 的可表达性投影与答案装配（纯策略，无 WinUI 依赖）。
/// </summary>
/// <remarks>
/// <para>
/// 原则与 docs/notifications-and-tray.md 的通知侧矩阵一致：全有或全无，不静默丢题、
/// 不伪造答案、不猜测未建模约束。应用侧在此之上额外安全支持 number / integer / boolean /
/// multiselect（通知 SDK 无法表达而本页可以原生表达的类型）；external 与一切无法确证语义的
/// 字段仍明确不支持，整个表单不可提交（取消表单仍可用）。
/// </para>
/// <para>
/// 服务端仍是最终校验者：本策略检查本地 JSON 类型、选项成员关系及可确定的常用边界，
/// 不把“通过本地检查”解释为“服务端一定接受”。
/// </para>
/// </remarks>
internal static class OpenCodeFormInputPolicy
{
    /// <summary>
    /// 尝试把表单投影为可原生表达的字段计划。任一字段不可表达时返回 false，
    /// 并在 <paramref name="unsupportedReasons"/> 列出全部原因（不静默丢弃任何字段）。
    /// </summary>
    internal static bool TryCreatePlan(
        OpenCodeFormInfo form,
        out OpenCodeFormPlan? plan,
        out IReadOnlyList<string> unsupportedReasons)
    {
        var reasons = new List<string>();
        plan = null;

        if (form is null)
        {
            reasons.Add("表单数据为空。");
            unsupportedReasons = reasons;
            return false;
        }

        var fields = form.Fields;
        if (fields is not { Count: > 0 })
        {
            reasons.Add("表单没有可表达的字段（fields 为空）。");
            unsupportedReasons = reasons;
            return false;
        }

        var fieldPlans = new List<OpenCodeFormFieldPlan>(fields.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in fields)
        {
            if (field is null)
            {
                reasons.Add("表单包含无法解析的字段条目。");
                continue;
            }

            var key = field.Key;
            // Display text only (Key and option Value stay exact): stray CR / controls in model-authored titles
            // would otherwise split the question card's label into one fragment per line.
            var label = OpenCodeDisplayText.Line(field.Title) is { Length: > 0 } title ? title : (key ?? "（无 key）");
            var description = OpenCodeDisplayText.Block(field.Description) is { Length: > 0 } body ? body : null;

            if (string.IsNullOrWhiteSpace(key))
            {
                reasons.Add("字段缺少 key。");
                continue;
            }

            if (!keys.Add(key))
            {
                reasons.Add($"字段 key 重复：{key}。");
                continue;
            }

            if (field.Hidden == true)
            {
                reasons.Add($"字段“{label}”被标记为 hidden；本页不猜测隐藏字段的语义，不静默跳过。");
                continue;
            }

            var unknown = UnknownProperties(field.AdditionalProperties,
                "placeholder", "default", "minLength", "maxLength", "minimum", "maximum", "minItems", "maxItems");
            if (unknown.Count > 0)
            {
                reasons.Add($"字段“{label}”带有本页未建模的约束（{string.Join("、", unknown)}）；无法确认其语义，不能安全表达。");
                continue;
            }

            var options = new List<OpenCodeFormOptionPlan>();
            if (field.Options is { } rawOptions)
            {
                if (rawOptions.Count == 0)
                {
                    reasons.Add($"字段“{label}”的选项列表为空；不猜测其含义。");
                    continue;
                }

                var values = new HashSet<string>(StringComparer.Ordinal);
                var optionsValid = true;
                foreach (var option in rawOptions)
                {
                    if (option is null || string.IsNullOrWhiteSpace(option.Value))
                    {
                        reasons.Add($"字段“{label}”包含缺少 value 的选项。");
                        optionsValid = false;
                        break;
                    }

                    if (UnknownProperties(option.AdditionalProperties, "description").Count > 0
                        || (Present(option.GetDescription()) && option.GetDescription().ValueKind != JsonValueKind.String))
                    {
                        reasons.Add($"字段“{label}”的选项带有未建模的附加字段或非字符串 description；不能安全表达。");
                        optionsValid = false;
                        break;
                    }

                    if (!values.Add(option.Value))
                    {
                        reasons.Add($"字段“{label}”的选项 value 重复：{option.Value}。");
                        optionsValid = false;
                        break;
                    }

                    var optionLabel = OpenCodeDisplayText.Line(option.Label) is { Length: > 0 } shown ? shown : OpenCodeDisplayText.Line(option.Value);
                    options.Add(new OpenCodeFormOptionPlan(option.Value, optionLabel.Length > 0 ? optionLabel : option.Value)
                    {
                        Description = Present(option.GetDescription())
                            && OpenCodeDisplayText.Block(option.GetDescription().GetString()) is { Length: > 0 } optionDescription ? optionDescription : null,
                    });
                }

                if (!optionsValid)
                {
                    continue;
                }
            }

            var previousCount = fieldPlans.Count;
            switch (field.Type)
            {
                case OpenCodeFormFieldTypes.String:
                    fieldPlans.Add(new OpenCodeFormFieldPlan(
                        key,
                        label,
                        description,
                        field.Required == true,
                        options.Count == 0
                            ? OpenCodeFormFieldKind.Text
                            : field.Custom == true
                                ? OpenCodeFormFieldKind.ChoiceWithCustom
                                : OpenCodeFormFieldKind.Choice,
                        options));
                    break;

                case OpenCodeFormFieldTypes.Boolean when options.Count == 0 && field.Custom != true:
                    fieldPlans.Add(new OpenCodeFormFieldPlan(
                        key, label, description, field.Required == true, OpenCodeFormFieldKind.Boolean, options));
                    break;

                case OpenCodeFormFieldTypes.Number when options.Count == 0 && field.Custom != true:
                    fieldPlans.Add(new OpenCodeFormFieldPlan(
                        key, label, description, field.Required == true, OpenCodeFormFieldKind.Number, options));
                    break;

                case OpenCodeFormFieldTypes.Integer when options.Count == 0 && field.Custom != true:
                    fieldPlans.Add(new OpenCodeFormFieldPlan(
                        key, label, description, field.Required == true, OpenCodeFormFieldKind.Integer, options));
                    break;

                case OpenCodeFormFieldTypes.Multiselect:
                    if (field.Options is null || options.Count == 0)
                    {
                        reasons.Add($"字段“{label}”（multiselect）缺少可用选项；不能安全表达。");
                        break;
                    }

                    // custom=true（D4）：勾选项 + 追加一条自定义文本，答案仍为字符串数组。
                    fieldPlans.Add(new OpenCodeFormFieldPlan(
                        key, label, description, field.Required == true, OpenCodeFormFieldKind.MultiSelect, options)
                    {
                        AllowsCustom = field.Custom == true,
                    });
                    break;

                case OpenCodeFormFieldTypes.External:
                    reasons.Add($"字段“{label}”类型为 external；本页不会打开外部链接或代为完成，明确不支持。");
                    break;

                default:
                    reasons.Add($"字段“{label}”的类型（{field.Type ?? "缺失"}）或 options/custom 组合本页不支持；不猜测其语义。");
                    break;
            }

            if (fieldPlans.Count > previousCount)
            {
                if (TryAddMetadata(field, fieldPlans[previousCount], out var enriched, out var reason))
                {
                    fieldPlans[previousCount] = enriched;
                }
                else
                {
                    reasons.Add($"字段“{label}”的输入元数据无效或不支持：{reason}。");
                }
            }
        }

        unsupportedReasons = reasons;
        if (reasons.Count > 0)
        {
            return false;
        }

        plan = new OpenCodeFormPlan(fieldPlans);
        return true;
    }

    /// <summary>
    /// 用字段计划校验并装配答案。任一项无效时返回 false 且不产生部分答案。
    /// 可选字段未回答一律省略（不伪造默认值）；必填字段未回答报错。
    /// </summary>
    internal static bool TryBuildAnswer(
        OpenCodeFormPlan plan,
        IReadOnlyDictionary<string, OpenCodeFormFieldInput> input,
        out Dictionary<string, JsonElement> answer,
        out IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(input);

        answer = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var errorList = new List<string>();

        var plannedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in plan.Fields)
        {
            plannedKeys.Add(field.Key);
        }

        foreach (var key in input.Keys)
        {
            if (!plannedKeys.Contains(key))
            {
                errorList.Add($"提交包含未知字段：{key}。");
            }
        }

        foreach (var field in plan.Fields)
        {
            input.TryGetValue(field.Key, out var value);

            switch (field.Kind)
            {
                case OpenCodeFormFieldKind.Text:
                {
                    var text = value.Text;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        RequireOrSkip(field, "填写", errorList);
                        break;
                    }

                    answer.Add(field.Key, JsonSerializer.SerializeToElement(text));
                    break;
                }

                case OpenCodeFormFieldKind.Choice:
                {
                    var choice = value.Choice;
                    if (choice is null)
                    {
                        RequireOrSkip(field, "选择", errorList);
                        break;
                    }

                    if (!IsKnownOption(field, choice))
                    {
                        errorList.Add($"“{field.Label}”的选择不在服务端提供的选项内。");
                        break;
                    }

                    answer.Add(field.Key, JsonSerializer.SerializeToElement(choice));
                    break;
                }

                case OpenCodeFormFieldKind.ChoiceWithCustom:
                {
                    var text = value.Text;
                    var choice = value.Choice;
                    if (choice is not null && !IsKnownOption(field, choice))
                    {
                        errorList.Add($"“{field.Label}”的选择不在服务端提供的选项内。");
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        // 非空白自定义文本优先于所选选项（与通知侧策略一致）。
                        answer.Add(field.Key, JsonSerializer.SerializeToElement(text));
                        break;
                    }

                    if (choice is not null)
                    {
                        answer.Add(field.Key, JsonSerializer.SerializeToElement(choice));
                        break;
                    }

                    RequireOrSkip(field, "填写", errorList);
                    break;
                }

                case OpenCodeFormFieldKind.Boolean:
                {
                    if (value.Boolean is { } boolean)
                    {
                        answer.Add(field.Key, JsonSerializer.SerializeToElement(boolean));
                        break;
                    }

                    if (field.Required)
                    {
                        errorList.Add($"“{field.Label}”为必填，请选择是或否。");
                    }

                    break;
                }

                case OpenCodeFormFieldKind.Number:
                {
                    var text = value.Text;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        RequireOrSkip(field, "填写", errorList);
                        break;
                    }

                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                        || !double.IsFinite(number))
                    {
                        errorList.Add($"“{field.Label}”不是有效数字（请使用小数点，如 3.14）。");
                        break;
                    }

                    answer.Add(field.Key, JsonSerializer.SerializeToElement(number));
                    break;
                }

                case OpenCodeFormFieldKind.Integer:
                {
                    var text = value.Text;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        RequireOrSkip(field, "填写", errorList);
                        break;
                    }

                    if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    {
                        errorList.Add($"“{field.Label}”不是有效整数（如 42）。");
                        break;
                    }

                    answer.Add(field.Key, JsonSerializer.SerializeToElement(integer));
                    break;
                }

                case OpenCodeFormFieldKind.MultiSelect:
                {
                    var selections = value.Selections ?? Array.Empty<string>();
                    if (!field.AllowsCustom && !string.IsNullOrEmpty(value.Text))
                    {
                        errorList.Add($"“{field.Label}”不接受自定义内容。");
                        break;
                    }

                    var custom = field.AllowsCustom && !string.IsNullOrWhiteSpace(value.Text) ? value.Text!.Trim() : null;
                    if (selections.Count == 0 && custom is null)
                    {
                        if (field.Required)
                        {
                            errorList.Add($"“{field.Label}”为必填，请至少选择一项。");
                        }

                        break;
                    }

                    var distinct = new List<string>(selections.Count);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var forged = false;
                    foreach (var selection in selections)
                    {
                        if (selection is null || !IsKnownOption(field, selection))
                        {
                            errorList.Add($"“{field.Label}”的选择不在服务端提供的选项内。");
                            forged = true;
                            break;
                        }

                        if (seen.Add(selection))
                        {
                            distinct.Add(selection);
                        }
                    }

                    if (forged)
                    {
                        break;
                    }

                    // 自定义文本追加在所勾选项之后；与已勾选值完全相同时不重复（与通知侧去重一致）。
                    if (custom is not null && seen.Add(custom))
                    {
                        distinct.Add(custom);
                    }

                    answer.Add(field.Key, JsonSerializer.SerializeToElement(distinct));
                    break;
                }
            }
        }

        foreach (var field in plan.Fields)
        {
            if (answer.TryGetValue(field.Key, out var value) && ConstraintError(field, value) is { } reason)
            {
                errorList.Add($"“{field.Label}”{reason}。");
            }
        }

        if (errorList.Count > 0)
        {
            // 有错误时不返回任何部分答案（调用方只应使用成功时的完整答案）。
            answer.Clear();
        }

        errors = errorList;
        return errorList.Count == 0;
    }

    private static bool TryAddMetadata(
        OpenCodeFormField source,
        OpenCodeFormFieldPlan field,
        out OpenCodeFormFieldPlan enriched,
        out string? reason)
    {
        enriched = field;
        reason = null;
        var isString = source.Type == OpenCodeFormFieldTypes.String;
        var isNumeric = source.Type is OpenCodeFormFieldTypes.Number or OpenCodeFormFieldTypes.Integer;
        var isMulti = source.Type == OpenCodeFormFieldTypes.Multiselect;
        if ((!isString && (Present(source.GetPlaceholder()) || Present(source.GetMinLength()) || Present(source.GetMaxLength())))
            || (!isNumeric && (Present(source.GetMinimum()) || Present(source.GetMaximum())))
            || (!isMulti && (Present(source.GetMinItems()) || Present(source.GetMaxItems()))))
        {
            reason = "约束或 placeholder 与字段类型不匹配";
            return false;
        }

        if (Present(source.GetPlaceholder()) && source.GetPlaceholder().ValueKind != JsonValueKind.String)
        {
            reason = "placeholder 必须为字符串";
            return false;
        }

        if (!TryCount(source.GetMinLength(), out var minLength) || !TryCount(source.GetMaxLength(), out var maxLength)
            || !TryCount(source.GetMinItems(), out var minItems) || !TryCount(source.GetMaxItems(), out var maxItems))
        {
            reason = "minLength/maxLength/minItems/maxItems 必须为可表示的非负整数（Int32）";
            return false;
        }

        if (!TryBound(source.GetMinimum(), out var minimum) || !TryBound(source.GetMaximum(), out var maximum))
        {
            reason = "minimum/maximum 必须为有限数字；不支持特殊数值编码";
            return false;
        }

        if (field.Kind == OpenCodeFormFieldKind.Integer
            && (!SafeIntegerBound(source.GetMinimum(), minimum) || !SafeIntegerBound(source.GetMaximum(), maximum)))
        {
            reason = "整数边界超出可安全表示的范围；不能舍入约束";
            return false;
        }

        if (minLength > maxLength || minimum > maximum || minItems > maxItems
            || (field.Kind == OpenCodeFormFieldKind.Integer && minimum is { } lower && maximum is { } upper
                && Math.Ceiling(lower) > Math.Floor(upper))
            || (field.Kind == OpenCodeFormFieldKind.Integer
                && ((minimum is { } intMin && new BigInteger(Math.Ceiling(intMin)) > long.MaxValue)
                    || (maximum is { } intMax && new BigInteger(Math.Floor(intMax)) < long.MinValue)))
            || (isMulti && minItems > field.Options.Count + (field.AllowsCustom ? 1 : 0)))
        {
            reason = "上下界矛盾或没有可用的选项数量/整数";
            return false;
        }

        enriched = field with
        {
            Placeholder = Present(source.GetPlaceholder()) && OpenCodeDisplayText.Line(source.GetPlaceholder().GetString()) is { Length: > 0 } placeholder ? placeholder : null,
            MinLength = minLength,
            MaxLength = maxLength,
            Minimum = minimum,
            Maximum = maximum,
            MinItems = minItems,
            MaxItems = maxItems,
        };
        if (!Present(source.GetDefault()))
        {
            return true;
        }

        if (!TryDefault(enriched, source.GetDefault(), out var defaultValue))
        {
            reason = "default 的类型、数值范围或选项成员无效；不支持特殊数值编码";
            return false;
        }

        // Defaults are validated, but remain metadata: no mutation of input or automatic answer.
        if (ConstraintError(enriched, source.GetDefault()) is { } defaultError)
        {
            reason = $"default {defaultError}";
            return false;
        }

        enriched = enriched with { Default = defaultValue };
        return true;
    }

    private static bool Present(JsonElement value) => value.ValueKind != JsonValueKind.Undefined;

    private static List<string> UnknownProperties(Dictionary<string, JsonElement>? properties, params string[] supported)
    {
        var unknown = new List<string>();
        if (properties is not null)
        {
            foreach (var key in properties.Keys)
            {
                if (Array.IndexOf(supported, key) < 0) unknown.Add(key);
            }
        }
        return unknown;
    }

    private static bool TryCount(JsonElement value, out int? count)
    {
        count = null;
        if (!Present(value)) return true;
        if (!TryInteger(value, out var number) || number < 0 || number > int.MaxValue) return false;
        count = (int)number;
        return true;
    }

    private static bool TryBound(JsonElement value, out double? bound)
    {
        bound = null;
        if (!Present(value)) return true;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)) return false;
        bound = number;
        return true;
    }

    private static bool TryInteger(JsonElement value, out long integer)
    {
        integer = 0;
        if (value.ValueKind != JsonValueKind.Number) return false;
        if (value.TryGetInt64(out integer)) return true;
        // Accept exact integral JSON numbers such as 2.0/2e0, without decimal/double
        // rounding of fractional defaults. Bound magnitude before parsing the raw token.
        if (!value.TryGetDouble(out var approximate) || !double.IsFinite(approximate)
            || approximate < long.MinValue || approximate > long.MaxValue || Math.Truncate(approximate) != approximate
            || !BigInteger.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var exact)
            || exact < long.MinValue || exact > long.MaxValue) return false;
        integer = (long)exact;
        return true;
    }

    private static bool SafeIntegerBound(JsonElement source, double? bound)
        => bound is null || Math.Abs(bound.Value) <= 9007199254740991d
            || (TryInteger(source, out var integer) && new BigInteger(integer) == new BigInteger(bound.Value));

    private static bool TryDefault(OpenCodeFormFieldPlan field, JsonElement value, out OpenCodeFormDefaultValue? result)
    {
        result = null;
        switch (field.Kind)
        {
            case OpenCodeFormFieldKind.Text:
            case OpenCodeFormFieldKind.Choice:
            case OpenCodeFormFieldKind.ChoiceWithCustom:
                if (value.ValueKind != JsonValueKind.String) return false;
                var text = value.GetString()!;
                if (field.Kind == OpenCodeFormFieldKind.Choice && !IsKnownOption(field, text)) return false;
                result = new OpenCodeFormDefaultValue(Text: text);
                return true;
            case OpenCodeFormFieldKind.Boolean:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                result = new OpenCodeFormDefaultValue(Boolean: value.GetBoolean());
                return true;
            case OpenCodeFormFieldKind.Number:
                if (!TryBound(value, out var number) || number is null) return false;
                result = new OpenCodeFormDefaultValue(Number: number);
                return true;
            case OpenCodeFormFieldKind.Integer:
                if (!TryInteger(value, out var integer)) return false;
                result = new OpenCodeFormDefaultValue(Integer: integer);
                return true;
            case OpenCodeFormFieldKind.MultiSelect:
                if (value.ValueKind != JsonValueKind.Array) return false;
                var selections = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                string? customDefault = null;
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) return false;
                    var selection = item.GetString()!;
                    if (!seen.Add(selection)) return false;
                    if (IsKnownOption(field, selection))
                    {
                        selections.Add(selection);
                        continue;
                    }

                    // custom=true 时至多一条非选项值，作为自定义框的初始内容（仍需用户手动提交）。
                    if (!field.AllowsCustom || customDefault is not null || string.IsNullOrWhiteSpace(selection)) return false;
                    customDefault = selection;
                }
                result = new OpenCodeFormDefaultValue(Text: customDefault, Selections: selections.AsReadOnly());
                return true;
            default:
                return false;
        }
    }

    private static string? ConstraintError(OpenCodeFormFieldPlan field, JsonElement value)
    {
        switch (field.Kind)
        {
            case OpenCodeFormFieldKind.Text:
            case OpenCodeFormFieldKind.Choice:
            case OpenCodeFormFieldKind.ChoiceWithCustom:
                var text = value.GetString()!;
                // No server validator source/version is assumed. BMP lengths agree; for supplementary
                // characters, reject only when BOTH UTF-16 and scalar counts violate the bound.
                // Ambiguous lengths remain server-authoritative, not a claim of exact equivalence.
                var scalarLength = 0;
                foreach (var rune in text.EnumerateRunes()) scalarLength++;
                if (field.MinLength is { } minLength && text.Length < minLength)
                    return $"长度不能小于 {minLength}";
                if (field.MaxLength is { } maxLength && scalarLength > maxLength)
                    return $"长度不能大于 {maxLength}";
                break;
            case OpenCodeFormFieldKind.Number:
                var number = value.GetDouble();
                if (field.Minimum is { } minimum && number < minimum) return $"不能小于 {minimum.ToString(CultureInfo.InvariantCulture)}";
                if (field.Maximum is { } maximum && number > maximum) return $"不能大于 {maximum.ToString(CultureInfo.InvariantCulture)}";
                break;
            case OpenCodeFormFieldKind.Integer:
                TryInteger(value, out var integer);
                // Do not round Int64 input to double during comparisons at the 2^53 boundary.
                if (field.Minimum is { } integerMin && new BigInteger(integer) < new BigInteger(Math.Ceiling(integerMin)))
                    return $"不能小于 {integerMin.ToString(CultureInfo.InvariantCulture)}";
                if (field.Maximum is { } integerMax && new BigInteger(integer) > new BigInteger(Math.Floor(integerMax)))
                    return $"不能大于 {integerMax.ToString(CultureInfo.InvariantCulture)}";
                break;
            case OpenCodeFormFieldKind.MultiSelect:
                var count = value.GetArrayLength();
                if (field.MinItems is { } minItems && count < minItems) return $"至少选择 {minItems} 项";
                if (field.MaxItems is { } maxItems && count > maxItems) return $"最多选择 {maxItems} 项";
                break;
        }

        return null;
    }

    private static bool IsKnownOption(OpenCodeFormFieldPlan field, string value)
    {
        foreach (var option in field.Options)
        {
            if (string.Equals(option.Value, value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void RequireOrSkip(OpenCodeFormFieldPlan field, string verb, List<string> errors)
    {
        if (field.Required)
        {
            errors.Add($"“{field.Label}”为必填，请{verb}。");
        }
    }
}
