using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FluentCode.Services.Appearance;

/// <summary>三个可由用户选择的字体槽位。</summary>
public enum AppFontSlot
{
    /// <summary>正文字体（界面 / 无衬线，中英文）。</summary>
    Body = 0,

    /// <summary>衬线字体。</summary>
    Serif = 1,

    /// <summary>等宽字体（代码 / 路径 / 命令）。</summary>
    Monospace = 2,
}

/// <summary>
/// 用户字体选择（持久化在 <c>settings.json</c> 的 <c>fonts</c> 节点）。
/// </summary>
/// <remarks>
/// 每个槽位保存<strong>一个</strong>已安装字体族名（不含逗号回退链）；<c>null</c> 表示“跟随系统/默认”。
/// 三个槽位和字号都为默认时整个节点为 <c>null</c>、不写入文件；旧文件未指定字号时仍为 14 DIP。
/// 回退链由 <see cref="AppFontDefaults.Resolve(AppFontSettings?, AppFontSlot)"/> 在运行时拼接。
/// </remarks>
public sealed record AppFontSettings
{
    /// <summary>正文基准字号（DIP）；null = 默认 14。严格 JSON 数值，不接受数字字符串。</summary>
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public double? TextSize { get; init; }

    /// <summary>正文字体族名；null = 系统默认（Segoe UI Variable + Microsoft YaHei UI 回退）。</summary>
    public string? Body { get; init; }

    /// <summary>衬线字体族名；null = 默认衬线回退链。</summary>
    public string? Serif { get; init; }

    /// <summary>等宽字体族名；null = 默认等宽回退链（Cascadia Mono, Consolas）。</summary>
    public string? Monospace { get; init; }

    /// <summary>读取指定槽位。</summary>
    public string? Get(AppFontSlot slot) => slot switch
    {
        AppFontSlot.Body => Body,
        AppFontSlot.Serif => Serif,
        AppFontSlot.Monospace => Monospace,
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };
}

/// <summary>
/// 字体默认值、规范化、校验与回退链拼接（纯逻辑，不依赖 WinUI，可无头测试）。
/// </summary>
public static class AppFontDefaults
{
    public const double DefaultTextSize = 14;
    public const double MinTextSize = 14;
    public const double MaxTextSize = 24;

    public static bool IsValidTextSize(double value)
        => double.IsFinite(value) && value >= MinTextSize && value <= MaxTextSize;

    public static double ResolveTextSize(AppFontSettings? settings)
        => settings?.TextSize is double value && IsValidTextSize(value) ? value : DefaultTextSize;

    /// <summary>只重置字号，不改变任何字体族选择。</summary>
    public static AppFontSettings? WithTextSize(AppFontSettings? settings, double? value)
        => Normalize((settings ?? new AppFontSettings()) with { TextSize = value });

    /// <summary>字体族名最大长度（与 DirectWrite 实际族名长度相比留有充足余量）。</summary>
    public const int MaxFamilyNameLength = 128;

    /// <summary>
    /// 正文默认：WinUI 内置 <c>XamlAutoFontFamily</c>（Segoe UI Variable，并按语言自动回退到 Microsoft YaHei UI 等），
    /// 与框架内置控件 / 文字样式的默认值完全一致。
    /// </summary>
    public const string BodyDefault = "XamlAutoFontFamily";

    /// <summary>用户自选正文字体时追加的回退链（保证中文与缺字字符仍有合适字形）。</summary>
    public const string BodyFallback = "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI";

    /// <summary>衬线默认回退链：西文 Cambria，中文 Noto Serif SC / 思源宋体，最后宋体。</summary>
    public const string SerifDefault = "Cambria, Noto Serif SC, Source Han Serif SC, SimSun";

    /// <summary>等宽默认回退链（系统自带）。</summary>
    public const string MonospaceDefault = "Cascadia Mono, Consolas";

    /// <summary>槽位的默认回退链。</summary>
    public static string DefaultFor(AppFontSlot slot) => slot switch
    {
        AppFontSlot.Body => BodyDefault,
        AppFontSlot.Serif => SerifDefault,
        AppFontSlot.Monospace => MonospaceDefault,
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    /// <summary>
    /// 计算槽位最终应用的字体族字符串：未选择 → 默认回退链；已选择 → “所选字体, 该槽位回退链”。
    /// </summary>
    public static string Resolve(AppFontSettings? settings, AppFontSlot slot)
    {
        string? chosen = NormalizeFamilyName(settings?.Get(slot));
        if (chosen is null)
        {
            return DefaultFor(slot);
        }

        string fallback = slot == AppFontSlot.Body ? BodyFallback : DefaultFor(slot);
        return chosen + ", " + fallback;
    }

    /// <summary>规范化族名：去首尾空白；空 / 非法返回 null（视为默认）。</summary>
    public static string? NormalizeFamilyName(string? name)
    {
        if (name is null)
        {
            return null;
        }

        string trimmed = name.Trim();
        return IsValidFamilyName(trimmed) ? trimmed : null;
    }

    /// <summary>族名是否合法：非空、长度受限、不含逗号（回退链分隔符）/ 控制字符 / '#'（URI 片段语法）。</summary>
    public static bool IsValidFamilyName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxFamilyNameLength || name.Trim().Length != name.Length)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (c == ',' || c == '#' || char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>规范化整组设置：字体与字号都为默认时返回 null（不写入文件）。</summary>
    public static AppFontSettings? Normalize(AppFontSettings? settings)
    {
        if (settings is null)
        {
            return null;
        }

        var normalized = new AppFontSettings
        {
            Body = NormalizeFamilyName(settings.Body),
            Serif = NormalizeFamilyName(settings.Serif),
            Monospace = NormalizeFamilyName(settings.Monospace),
            TextSize = ResolveTextSize(settings) == DefaultTextSize ? null : ResolveTextSize(settings),
        };

        return normalized.Body is null && normalized.Serif is null && normalized.Monospace is null && normalized.TextSize is null
            ? null
            : normalized;
    }

    /// <summary>返回把指定槽位设为 <paramref name="familyName"/>（null = 恢复默认）后的规范化设置。</summary>
    public static AppFontSettings? With(AppFontSettings? settings, AppFontSlot slot, string? familyName)
    {
        AppFontSettings current = settings ?? new AppFontSettings();
        string? value = NormalizeFamilyName(familyName);
        AppFontSettings next = slot switch
        {
            AppFontSlot.Body => current with { Body = value },
            AppFontSlot.Serif => current with { Serif = value },
            AppFontSlot.Monospace => current with { Monospace = value },
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };
        return Normalize(next);
    }

    /// <summary>校验持久化值（供 SettingsService 在读写时拒绝损坏 / 非法配置）。</summary>
    public static void Validate(AppFontSettings? settings, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (settings is null)
        {
            return;
        }

        ValidateSlot(settings.Body, "fonts.body", errors);
        ValidateSlot(settings.Serif, "fonts.serif", errors);
        ValidateSlot(settings.Monospace, "fonts.monospace", errors);
        if (settings.TextSize is double size && !IsValidTextSize(size))
        {
            errors.Add($"fonts.textSize 必须是 {MinTextSize}–{MaxTextSize} 之间的有限数值（DIP）");
        }
    }

    private static void ValidateSlot(string? value, string field, List<string> errors)
    {
        if (value is not null && !IsValidFamilyName(value))
        {
            errors.Add($"{field} 不是合法的字体族名（1-{MaxFamilyNameLength} 字符，不含逗号/控制字符/#，无首尾空白）");
        }
    }
}
