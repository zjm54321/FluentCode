using System;
using System.Collections.Generic;
using System.Globalization;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// 表单字段的展示投影（纯函数，无 WinUI 依赖）：类型化默认值 → 控件初始值、约束 → 可读提示。
/// </summary>
/// <remarks>
/// <para>
/// 只消费 <see cref="OpenCodeFormFieldPlan"/>（策略已校验的类型化计划），不重新解析原始 JSON。
/// 默认值只用于卡片<b>新建字段控件</b>时的初始可编辑内容；不是答案，不会自动提交，
/// 也不会在之后的刷新/启用状态变化中再次写入。最终答案仍只由
/// <see cref="OpenCodeFormInputPolicy.TryBuildAnswer"/> 从用户当前输入装配。
/// </para>
/// <para>
/// 数值一律用不变区域格式：number 使用往返格式（与答案装配器的 InvariantCulture 解析互逆），
/// integer 直接格式化 Int64，避免经 double 舍入。
/// </para>
/// </remarks>
internal static class OpenCodeFormFieldPresentation
{
    /// <summary>文本类控件（Text / Number / Integer）的初始文本；无默认值时为 null（保持空白）。</summary>
    internal static string? InitialText(OpenCodeFormFieldPlan field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var value = field.Default;
        if (value is null)
        {
            return null;
        }

        return field.Kind switch
        {
            OpenCodeFormFieldKind.Text => value.Text,
            OpenCodeFormFieldKind.Number => value.Number is { } number ? FormatNumber(number) : null,
            OpenCodeFormFieldKind.Integer => value.Integer is { } integer ? FormatInteger(integer) : null,
            _ => null,
        };
    }

    /// <summary>
    /// 单选下拉的初始选中下标（按选项 value 精确匹配，不按 label）；无默认值或不匹配时为 -1。
    /// </summary>
    internal static int InitialChoiceIndex(OpenCodeFormFieldPlan field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Kind is not (OpenCodeFormFieldKind.Choice or OpenCodeFormFieldKind.ChoiceWithCustom)
            || field.Default?.Text is not { } value)
        {
            return -1;
        }

        for (var i = 0; i < field.Options.Count; i++)
        {
            if (string.Equals(field.Options[i].Value, value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 可自定义单选的初始自定义文本：默认值不是任何选项 value 时才放入自定义框；否则为 null。
    /// </summary>
    internal static string? InitialCustomText(OpenCodeFormFieldPlan field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Kind == OpenCodeFormFieldKind.MultiSelect)
        {
            // 可自定义多选：策略已把默认数组中唯一的非选项值放入 Default.Text。
            return field.AllowsCustom ? field.Default?.Text : null;
        }

        if (field.Kind != OpenCodeFormFieldKind.ChoiceWithCustom || field.Default?.Text is not { } value)
        {
            return null;
        }

        return InitialChoiceIndex(field) >= 0 ? null : value;
    }

    /// <summary>布尔下拉（0 = 是，1 = 否）的初始下标；无默认值为 -1（未选择 = 不回答）。</summary>
    internal static int InitialBooleanIndex(OpenCodeFormFieldPlan field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Kind != OpenCodeFormFieldKind.Boolean)
        {
            return -1;
        }

        return field.Default?.Boolean switch
        {
            true => 0,
            false => 1,
            null => -1,
        };
    }

    /// <summary>多选复选框是否初始勾选（按选项 value 精确匹配）。</summary>
    internal static bool IsInitiallySelected(OpenCodeFormFieldPlan field, string optionValue)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Kind != OpenCodeFormFieldKind.MultiSelect || field.Default?.Selections is not { } selections)
        {
            return false;
        }

        foreach (var selected in selections)
        {
            if (string.Equals(selected, optionValue, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 默认值是否会在控件中产生可见的预填内容（空文本/空数组等显式默认不产生可见内容，不提示）。
    /// </summary>
    internal static bool HasVisiblePrefill(OpenCodeFormFieldPlan field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Default is null)
        {
            return false;
        }

        switch (field.Kind)
        {
            case OpenCodeFormFieldKind.Text:
            case OpenCodeFormFieldKind.Number:
            case OpenCodeFormFieldKind.Integer:
                return !string.IsNullOrEmpty(InitialText(field));
            case OpenCodeFormFieldKind.Choice:
                return InitialChoiceIndex(field) >= 0;
            case OpenCodeFormFieldKind.ChoiceWithCustom:
                return InitialChoiceIndex(field) >= 0 || !string.IsNullOrEmpty(InitialCustomText(field));
            case OpenCodeFormFieldKind.Boolean:
                return InitialBooleanIndex(field) >= 0;
            case OpenCodeFormFieldKind.MultiSelect:
                return field.Default.Selections is { Count: > 0 } || !string.IsNullOrEmpty(InitialCustomText(field));
            default:
                return false;
        }
    }

    /// <summary>
    /// 字段约束的简短可读提示（长度、数值范围、选择数量）；无可提示约束时为 null。
    /// 仅用于展示，校验仍由策略执行（服务端仍是最终校验者）。
    /// </summary>
    internal static string? ConstraintHint(OpenCodeFormFieldPlan field)
    {
        ArgumentNullException.ThrowIfNull(field);
        switch (field.Kind)
        {
            case OpenCodeFormFieldKind.Text:
                return LengthHint(field.MinLength, field.MaxLength, "长度");
            case OpenCodeFormFieldKind.ChoiceWithCustom:
                return LengthHint(field.MinLength, field.MaxLength, "自定义内容长度");
            case OpenCodeFormFieldKind.Number:
                return RangeHint(field.Minimum, field.Maximum, FormatNumber);
            case OpenCodeFormFieldKind.Integer:
                return RangeHint(field.Minimum, field.Maximum, FormatIntegerBound);
            case OpenCodeFormFieldKind.MultiSelect:
                var countHint = CountHint(field.MinItems, field.MaxItems);
                return countHint is not null && field.AllowsCustom ? countHint + "（自己输入的内容算 1 项）" : countHint;
            default:
                return null;
        }
    }

    /// <summary>选项说明列表（“标签：说明”，仅含带说明的选项）；无任何说明时为空。</summary>
    internal static IReadOnlyList<string> OptionDescriptionLines(OpenCodeFormFieldPlan field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var lines = new List<string>();
        foreach (var option in field.Options)
        {
            if (!string.IsNullOrWhiteSpace(option.Description))
            {
                lines.Add(option.Label + "：" + option.Description);
            }
        }

        return lines;
    }

    /// <summary>“其他（自己输入）”选项的固定文案（单选可自定义时追加为最后一项）。</summary>
    internal const string OtherChoiceLabel = "其他（自己输入）";

    /// <summary>question 工具给整份表单的通用英文标题（OpenCode 固定写 "Questions"），不作为界面标题显示。</summary>
    internal const string GenericQuestionFormTitle = "Questions";

    /// <summary>
    /// 时间线条目与提问卡片的标题（单行）：单题表单用该题标题；多题时表单标题若缺失或只是通用的 “Questions”，
    /// 用“首题标题 等 N 个问题”（与时间线工具行一致），否则用表单标题；都缺失时为“（无标题提问）”。
    /// 只读 schema 文本，不含用户输入；换行、游离的 CR 与控制字符按 <see cref="OpenCodeDisplayText.Line"/> 清理。
    /// </summary>
    internal static string QuestionTitle(string? formTitle, IReadOnlyList<string> fieldLabels)
    {
        ArgumentNullException.ThrowIfNull(fieldLabels);
        if (fieldLabels.Count == 1 && OpenCodeDisplayText.Line(fieldLabels[0]) is { Length: > 0 } only)
        {
            return only;
        }

        var title = OpenCodeDisplayText.Line(formTitle);
        if (fieldLabels.Count > 1 && (title.Length == 0 || string.Equals(title, GenericQuestionFormTitle, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var label in fieldLabels)
            {
                if (OpenCodeDisplayText.Line(label) is { Length: > 0 } first)
                {
                    return first + " 等 " + fieldLabels.Count.ToString(CultureInfo.InvariantCulture) + " 个问题";
                }
            }
        }

        return title.Length == 0 ? "（无标题提问）" : title;
    }

    /// <summary>
    /// 时间线紧凑条目的一行说明，如“单选 · 2 个选项 · 可自己输入”；多题时为“3 个问题 · 必答 2 个”。
    /// </summary>
    internal static string QuestionSummary(OpenCodeFormPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Fields.Count != 1)
        {
            var required = 0;
            foreach (var field in plan.Fields)
            {
                if (field.Required)
                {
                    required++;
                }
            }

            return required > 0
                ? $"{plan.Fields.Count} 个问题 · 必答 {required} 个"
                : $"{plan.Fields.Count} 个问题";
        }

        var only = plan.Fields[0];
        var parts = new List<string>(4);
        switch (only.Kind)
        {
            case OpenCodeFormFieldKind.Choice:
                parts.Add("单选");
                parts.Add($"{only.Options.Count} 个选项");
                break;
            case OpenCodeFormFieldKind.ChoiceWithCustom:
                parts.Add("单选");
                parts.Add($"{only.Options.Count} 个选项");
                parts.Add("可自己输入");
                break;
            case OpenCodeFormFieldKind.MultiSelect:
                parts.Add("多选");
                parts.Add($"{only.Options.Count} 个选项");
                if (only.AllowsCustom)
                {
                    parts.Add("可自己输入");
                }

                break;
            case OpenCodeFormFieldKind.Boolean:
                parts.Add("是 / 否");
                break;
            case OpenCodeFormFieldKind.Number:
            case OpenCodeFormFieldKind.Integer:
                parts.Add("填写数字");
                break;
            default:
                parts.Add("填写文字");
                break;
        }

        if (only.Required)
        {
            parts.Add("必答");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>多选“已选 N 项”提示。</summary>
    internal static string SelectedCountText(int count) => count > 0 ? $"已选 {count} 项" : "尚未选择";

    /// <summary>number 默认值/边界的稳定不变区域往返格式。</summary>
    internal static string FormatNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>integer 默认值的精确 Int64 格式（不经 double）。</summary>
    internal static string FormatInteger(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string FormatIntegerBound(double value)
    {
        // 策略保证整数边界若超出 2^53 则为可精确表示的 Int64；整数值按整数显示，非整数边界保留原值。
        if (Math.Floor(value) == value && value >= -9.2233720368547758E18 && value < 9.2233720368547758E18)
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }

        return FormatNumber(value);
    }

    private static string? LengthHint(int? min, int? max, string subject)
    {
        if (min is { } low && max is { } high)
        {
            return low == high
                ? $"{subject}须为 {low} 个字符"
                : $"{subject} {low}–{high} 个字符";
        }

        if (min is { } onlyLow && onlyLow > 0)
        {
            return $"{subject}至少 {onlyLow} 个字符";
        }

        if (max is { } onlyHigh)
        {
            return $"{subject}最多 {onlyHigh} 个字符";
        }

        return null;
    }

    private static string? RangeHint(double? min, double? max, Func<double, string> format)
    {
        if (min is { } low && max is { } high)
        {
            return $"取值范围 {format(low)} 至 {format(high)}";
        }

        if (min is { } onlyLow)
        {
            return $"不小于 {format(onlyLow)}";
        }

        if (max is { } onlyHigh)
        {
            return $"不大于 {format(onlyHigh)}";
        }

        return null;
    }

    private static string? CountHint(int? min, int? max)
    {
        if (min is { } low && max is { } high)
        {
            return low == high ? $"请选择 {low} 项" : $"请选择 {low}–{high} 项";
        }

        if (min is { } onlyLow && onlyLow > 0)
        {
            return $"至少选择 {onlyLow} 项";
        }

        if (max is { } onlyHigh)
        {
            return $"最多选择 {onlyHigh} 项";
        }

        return null;
    }
}
