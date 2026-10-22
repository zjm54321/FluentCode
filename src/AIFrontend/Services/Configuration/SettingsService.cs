using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;

namespace AIFrontend.Services.Configuration;

/// <summary><see cref="SettingsService.Changed"/> 的事件参数。</summary>
public sealed class AppSettingsChangedEventArgs : EventArgs
{
    /// <summary>创建事件参数。</summary>
    /// <param name="settings">变更后的完整设置快照。</param>
    public AppSettingsChangedEventArgs(AppSettings settings)
    {
        Settings = settings;
    }

    /// <summary>变更后的完整设置快照。</summary>
    public AppSettings Settings { get; }
}

/// <summary>
/// 应用设置的加载/保存服务（<c>%LOCALAPPDATA%\AIFrontend\settings.json</c>；不含任何密钥明文）。
/// </summary>
/// <remarks>
/// 并发与线程（重要）：
/// <list type="bullet">
/// <item><description>同一数据目录内操作由共享信号量串行化；保存前检查旧文件，损坏则拒绝覆盖。</description></item>
/// <item><description><see cref="Changed"/> 在释放存储锁后触发，无线程亲和保证；
/// UI 更新必须自行调度到 DispatcherQueue。</description></item>
/// <item><description>文件不存在时 <see cref="LoadAsync"/> 返回默认空设置且<strong>不创建文件</strong>；
/// 文件存在但解析/校验失败时抛出 <see cref="SettingsCorruptedException"/>，
/// 原文件保留，绝不静默覆盖。</description></item>
/// <item><description>保存采用“最后写入获胜”的快照语义，不做自动合并。</description></item>
/// </list>
/// </remarks>
public sealed class SettingsService
{
    private readonly SemaphoreSlim _gate;
    private readonly string _dataDirectory;
    private readonly string _settingsFilePath;

    /// <summary>创建服务。</summary>
    /// <param name="dataDirectory">数据根目录；null 使用默认（<c>%LOCALAPPDATA%\AIFrontend</c>）。测试可传入临时目录。</param>
    public SettingsService(string? dataDirectory = null)
    {
        _dataDirectory = AppDataPaths.GetRootDirectory(dataDirectory);
        _settingsFilePath = AppDataPaths.GetSettingsFilePath(_dataDirectory);
        _gate = AtomicFile.GateForDirectory(_dataDirectory);
    }

    /// <summary>数据根目录（绝对路径）。</summary>
    public string DataDirectory => _dataDirectory;

    /// <summary>settings.json 的绝对路径（诊断用）。</summary>
    public string SettingsFilePath => _settingsFilePath;

    /// <summary>最近一次 LoadAsync/SaveAsync 成功的快照；从未成功过时为 null。</summary>
    public AppSettings? Current { get; private set; }

    /// <summary>设置成功加载/保存后在锁外触发；并发调用不保证事件顺序，UI 可读取 Current 获取最新值。</summary>
    public event EventHandler<AppSettingsChangedEventArgs>? Changed;

    /// <summary>
    /// 从磁盘加载设置。文件不存在时返回默认空设置（<see cref="AppSettings.Empty"/>）。
    /// 解析失败/校验失败抛出 <see cref="SettingsCorruptedException"/>（原文件保留）。
    /// </summary>
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        AppSettings settings;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[]? bytes = await AtomicFile.ReadAllBytesOrNullAsync(_settingsFilePath, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                settings = AppSettings.Empty;
            }
            else
            {
                settings = ParseSettings(bytes);
            }

            Current = settings;
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, new AppSettingsChangedEventArgs(settings));
        return settings;
    }

    /// <summary>
    /// 校验并以原子方式写入 settings.json；成功后刷新 <see cref="Current"/> 并触发 <see cref="Changed"/>。
    /// 校验失败抛出 <see cref="SettingsValidationException"/>，不写入任何文件。
    /// </summary>
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        List<string> errors = CollectErrors(settings);
        if (errors.Count > 0)
        {
            throw new SettingsValidationException(
                $"设置未通过校验，未写入任何文件；问题：{string.Join("；", errors)}", errors);
        }

        // 先分离调用方可变集合；等待锁期间不再读取调用方的数据。
        byte[] bytes = ConfigurationJson.SerializeToUtf8Bytes(settings);
        AppSettings snapshot = ConfigurationJson.Deserialize<AppSettings>(bytes);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[]? existing = await AtomicFile.ReadAllBytesOrNullAsync(_settingsFilePath, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                _ = ParseSettings(existing);
            }
            await AtomicFile.WriteAllBytesAsync(_settingsFilePath, bytes, cancellationToken).ConfigureAwait(false);
            Current = snapshot;
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, new AppSettingsChangedEventArgs(snapshot));
    }

    private AppSettings ParseSettings(byte[] bytes)
    {
        AppSettings settings;
        try
        {
            settings = ConfigurationJson.Deserialize<AppSettings>(bytes);
        }
        catch (JsonException ex)
        {
            throw new SettingsCorruptedException(
                $"设置文件不是有效的 JSON 或结构不匹配：{_settingsFilePath}", _settingsFilePath, ex);
        }

        List<string> errors = CollectErrors(settings);
        if (errors.Count > 0)
        {
            throw new SettingsCorruptedException(
                $"设置文件校验失败（原文件保留）：{_settingsFilePath}；问题：{string.Join("；", errors)}",
                _settingsFilePath);
        }
        return settings;
    }

    private static List<string> CollectErrors(AppSettings settings)
    {
        var errors = new List<string>();

        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion)
        {
            errors.Add($"schemaVersion={settings.SchemaVersion}，当前仅支持 {AppSettings.CurrentSchemaVersion}");
        }

        if (!Enum.IsDefined(settings.Theme))
        {
            errors.Add($"theme 取值非法：{(int)settings.Theme}");
        }

        AIFrontend.Services.Appearance.AppFontDefaults.Validate(settings.Fonts, errors);
        if (!Enum.IsDefined(settings.CodeTheme)) errors.Add("codeTheme 取值非法");

        if (settings.OpenCodeProjectDirectories is { } directories)
        {
            var keys = new HashSet<(string SourceId, string Directory)>();
            foreach (var bookmark in directories)
            {
                if (bookmark is null || string.IsNullOrWhiteSpace(bookmark.SourceId) || string.IsNullOrWhiteSpace(bookmark.Directory))
                    errors.Add("openCodeProjectDirectories 条目缺少 sourceId/directory");
                else if (!keys.Add((bookmark.SourceId, bookmark.Directory)))
                    errors.Add("openCodeProjectDirectories 含重复 sourceId/directory");
            }
        }

        if (settings.OpenCode is null)
        {
            errors.Add("openCode 不能为 null");
        }
        else
        {
            ValidateOptionalUrl(settings.OpenCode.BaseUrl, "openCode.baseUrl", errors);
            ValidateOptionalStorageId(settings.OpenCode.CredentialId, "openCode.credentialId", errors);
            ValidateOpenCodeConnectionKind(settings.OpenCode, errors);
        }

        HashSet<string>? providerIds = null;
        if (settings.Providers is null)
        {
            errors.Add("providers 不能为 null");
        }
        else
        {
            providerIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ProviderEndpoint? provider in settings.Providers)
            {
                if (provider is null)
                {
                    errors.Add("providers 含 null 条目");
                    continue;
                }

                if (!AppDataPaths.IsValidStorageId(provider.Id))
                {
                    errors.Add($"provider.id 非法：'{provider.Id}'（须为 1-64 字符，仅字母/数字/'.'/'_'/'-'）");
                }
                else if (!providerIds.Add(provider.Id))
                {
                    errors.Add($"provider.id 重复：'{provider.Id}'");
                }

                if (string.IsNullOrWhiteSpace(provider.DisplayName))
                {
                    errors.Add($"provider('{provider.Id}').displayName 不能为空");
                }

                if (!IsAbsoluteHttpUrl(provider.BaseUrl))
                {
                    errors.Add($"provider('{provider.Id}').baseUrl 必须是绝对的 http/https URL，且不含 userinfo、query 或 fragment");
                }

                if (!Enum.IsDefined(provider.Protocol))
                {
                    errors.Add($"provider('{provider.Id}').protocol 取值非法：{(int)provider.Protocol}");
                }

                ValidateOptionalStorageId(provider.CredentialId, $"provider('{provider.Id}').credentialId", errors);
            }
        }

        if (settings.Models is null)
        {
            errors.Add("models 不能为 null");
        }
        else
        {
            var modelKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (ModelDescriptor? model in settings.Models)
            {
                if (model is null)
                {
                    errors.Add("models 含 null 条目");
                    continue;
                }

                if (providerIds is not null &&
                    (string.IsNullOrWhiteSpace(model.ProviderId) || !providerIds.Contains(model.ProviderId)))
                {
                    errors.Add($"model('{model.ModelId}').providerId 不存在：'{model.ProviderId}'");
                }

                if (string.IsNullOrWhiteSpace(model.ModelId))
                {
                    errors.Add($"model 的 modelId 不能为空（providerId='{model.ProviderId}'）");
                }
                else if (!modelKeys.Add(model.ProviderId + "\u0000" + model.ModelId))
                {
                    errors.Add($"模型重复：'{model.ProviderId}/{model.ModelId}'");
                }

                if (!Enum.IsDefined(model.Origin))
                {
                    errors.Add($"model('{model.ModelId}').origin 取值非法：{(int)model.Origin}");
                }

                if (model.Capabilities is null)
                {
                    errors.Add($"model('{model.ModelId}').capabilities 不能为 null（未知请用 source=unknown）");
                }
                else
                {
                    if (!Enum.IsDefined(model.Capabilities.Source))
                    {
                        errors.Add($"model('{model.ModelId}').capabilities.source 取值非法：{(int)model.Capabilities.Source}");
                    }

                    ValidateReasoningCapabilities(model, errors);
                }
            }
        }

        if (settings.SelectedModel is { } selected &&
            settings.Models is not null &&
            settings.FindModel(selected) is null)
        {
            errors.Add($"selectedModel 指向不存在的模型：'{selected.ProviderId}/{selected.ModelId}'");
        }

        return errors;
    }

    private static void ValidateReasoningCapabilities(ModelDescriptor model, List<string> errors)
    {
        ReasoningCapabilities? reasoning = model.Capabilities.Reasoning;
        if (reasoning is null)
        {
            return;
        }

        if (reasoning.EffortValues is null)
        {
            errors.Add($"model('{model.ModelId}').capabilities.reasoning.effortValues 不能为 null（明确不支持请用空数组）");
        }
        else
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string? effort in reasoning.EffortValues)
            {
                if (string.IsNullOrWhiteSpace(effort))
                {
                    errors.Add($"model('{model.ModelId}').capabilities.reasoning.effortValues 含空值");
                }
                else if (!seen.Add(effort))
                {
                    errors.Add($"model('{model.ModelId}').capabilities.reasoning.effortValues 重复：'{effort}'");
                }
            }
        }

        if (reasoning.MinThinkingBudgetTokens is { } min && min <= 0)
        {
            errors.Add($"model('{model.ModelId}').capabilities.reasoning.minThinkingBudgetTokens 必须为正数");
        }

        if (reasoning.MaxThinkingBudgetTokens is { } max && max <= 0)
        {
            errors.Add($"model('{model.ModelId}').capabilities.reasoning.maxThinkingBudgetTokens 必须为正数");
        }

        if (reasoning.MinThinkingBudgetTokens is { } lower &&
            reasoning.MaxThinkingBudgetTokens is { } upper &&
            lower > upper)
        {
            errors.Add($"model('{model.ModelId}').capabilities.reasoning 预算区间非法：min > max");
        }
    }

    private static void ValidateOptionalUrl(string? url, string field, List<string> errors)
    {
        if (url is null)
        {
            return;
        }

        if (!IsAbsoluteHttpUrl(url))
        {
            errors.Add($"{field} 必须是绝对的 http/https URL，且不含 userinfo、query 或 fragment");
        }
    }

    private static void ValidateOptionalStorageId(string? id, string field, List<string> errors)
    {
        if (id is null)
        {
            return;
        }

        if (!AppDataPaths.IsValidStorageId(id))
        {
            errors.Add($"{field} 非法：'{id}'");
        }
    }

    /// <summary>B11：类型字段可空（旧配置）；Wsl 必须带合法发行版名，Remote/旧配置不得带发行版名。</summary>
    private static void ValidateOpenCodeConnectionKind(OpenCodeSettings openCode, List<string> errors)
    {
        if (openCode.Kind is { } kind && !Enum.IsDefined(kind))
        {
            errors.Add("openCode.kind 取值非法");
            return;
        }

        if (openCode.Kind == OpenCodeConnectionKind.Wsl)
        {
            if (!OpenCodeSettings.IsValidWslDistributionName(openCode.WslDistribution))
            {
                errors.Add("openCode.wslDistribution 缺失或非法（Kind=Wsl 时必填）");
            }
        }
        else if (openCode.WslDistribution is not null)
        {
            errors.Add("openCode.wslDistribution 仅允许在 Kind=Wsl 时出现");
        }
    }

    private static bool IsAbsoluteHttpUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);
}
