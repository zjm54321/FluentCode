using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

internal enum OpenCodeModelBrand { Neutral, Gemini, Claude, OpenAI, Grok, DeepSeek, GLM, Kimi, Qwen }

// Display transformations only: IDs remain opaque, including variant casing and spelling.
internal static class OpenCodeModelPresentation
{
    internal static string VariantLabel(string? id) => id?.ToLowerInvariant() switch
    {
        null => "默认",
        "low" => "低",
        "medium" => "中",
        "high" => "高",
        "extra high" or "extra_high" or "extra-high" or "xhigh" => "极高",
        "max" => "最高",
        _ => id,
    };

    internal static bool VariantAvailable(OpenCodeModelInfo? model, string? id)
        => model is not null && model.Enabled != false && (id is null || (model.Variants?.Any(v => v.Id == id) ?? false));

    internal static OpenCodeModelBrand Brand(string? modelId, string? name)
    {
        // Match explicit family tokens even behind gateways; never infer every model's brand from provider ID.
        var text = (modelId ?? string.Empty) + " " + (name ?? string.Empty);
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        if (Regex.IsMatch(text, @"(?:^|[\s/.:_-])gemini(?:$|[\s/.:_-]|\d)", options)) return OpenCodeModelBrand.Gemini;
        if (Regex.IsMatch(text, @"(?:^|[\s/.:_-])claude(?:$|[\s/.:_-]|\d)", options)) return OpenCodeModelBrand.Claude;
        if (Regex.IsMatch(text, @"(?:^|[\s/.:_-])(?:gpt(?:$|[\s/.:_-]|\d)|chatgpt(?:$|[\s/.:_-])|o[134](?:$|[\s/.:_-]))", options)) return OpenCodeModelBrand.OpenAI;
        if (Regex.IsMatch(text, @"(?:^|[\s/.:_-])grok(?:$|[\s/.:_-]|\d)", options)) return OpenCodeModelBrand.Grok;
        if (Regex.IsMatch(text, @"(?:^|[\s/.:_-])deepseek(?:$|[\s/.:_-]|\d)", options)) return OpenCodeModelBrand.DeepSeek;
        if (Regex.IsMatch(text, @"(?:^|[\s/.:_-])(?:chat)?glm(?:$|[\s/.:_-]|\d)", options)) return OpenCodeModelBrand.GLM;
        if (Regex.IsMatch(text, @"(?:^|[\s/.:_-])(?:kimi|moonshot(?:ai)?)(?:$|[\s/.:_-]|\d)", options)) return OpenCodeModelBrand.Kimi;
        if (Regex.IsMatch(text, @"(?:^|[\s/.:_-])(?:qwen|qwq|qvq)(?:$|[\s/.:_-]|\d)", options)) return OpenCodeModelBrand.Qwen;
        return OpenCodeModelBrand.Neutral;
    }

    internal static string? Asset(OpenCodeModelBrand brand, bool dark) => brand switch
    {
        OpenCodeModelBrand.Gemini => "gemini.png",
        OpenCodeModelBrand.Claude => "claude.svg",
        OpenCodeModelBrand.OpenAI => dark ? "chatgpt.dark.svg" : "chatgpt.svg",
        OpenCodeModelBrand.Grok => dark ? "grok.dark.svg" : "grok.svg",
        OpenCodeModelBrand.DeepSeek => dark ? "deepseek.dark.svg" : "deepseek.svg",
        OpenCodeModelBrand.GLM => dark ? "glm.dark.svg" : "glm.svg",
        OpenCodeModelBrand.Kimi => dark ? "kimi.dark.png" : "kimi.png",
        OpenCodeModelBrand.Qwen => "qwen.png",
        _ => null,
    };

    internal static string CurrentName(OpenCodeModelCurrent current, IReadOnlyList<OpenCodeModelPickerRow> rows)
    {
        var key = current switch { OpenCodeModelCurrent.Explicit e => e.Key, OpenCodeModelCurrent.Inherited i => i.Default, _ => null };
        return key is null ? "尚未选择模型" : rows.FirstOrDefault(r => r.ProviderId == key.ProviderId && r.ModelId == key.ModelId)?.Name ?? "当前模型不可用";
    }

    internal static OpenCodeModelPickerRow? CurrentRow(OpenCodeModelPickerPresentation presentation)
    {
        var key = presentation.Current switch { OpenCodeModelCurrent.Explicit e => e.Key, OpenCodeModelCurrent.Inherited i => i.Default, _ => null };
        return key is null ? null : presentation.Rows.FirstOrDefault(r => r.Available && r.ProviderId == key.ProviderId && r.ModelId == key.ModelId);
    }

    // Strip only known scope boilerplate, never an arbitrary diagnostic supplied by a caller.
    internal static string? InformativeNote(string? note) => note switch
    {
        "仅影响之后的回合；确认前会重新读取，状态变化时需要重新选择。" => null,
        "仅用于此草稿首次创建；强度在输入卡工具栏单独选择。" => null,
        _ => note,
    };

    internal static IReadOnlyList<OpenCodeModelPickerRow> Order(IReadOnlyList<OpenCodeModelPickerRow> rows, string? sourceKey,
        OpenCodeRecentModels? recents = null)
    {
        var lookup = rows.ToDictionary(r => (r.ProviderId, r.ModelId));
        var catalog = rows.Select(r => new OpenCodeModelInfo { ProviderId = r.ProviderId, Id = r.ModelId, Name = r.Name });
        return (recents ?? OpenCodeRecentModels.Shared).Order(sourceKey, catalog)
            .Select(m => lookup[(m.ProviderId!, m.Id!)]).ToArray();
    }
}
