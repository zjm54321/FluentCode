using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Models;

namespace FluentCode.Services.Configuration;

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
/// 应用设置的加载/保存服务（<c>%LOCALAPPDATA%\FluentCode\settings.json</c>；不含任何密钥明文）。
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
    /// <param name="dataDirectory">数据根目录；null 使用默认（<c>%LOCALAPPDATA%\FluentCode</c>）。测试可传入临时目录。</param>
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
        RaiseChanged(settings);
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
        RaiseChanged(snapshot);
    }

    /// <summary>在同一存储锁内读取、变换并提交设置，避免并发读改写丢失字段。</summary>
    public async Task<AppSettings> UpdateAsync(
        Func<AppSettings, AppSettings> transform,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transform);

        AppSettings snapshot;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[]? existing = await AtomicFile.ReadAllBytesOrNullAsync(_settingsFilePath, cancellationToken).ConfigureAwait(false);
            AppSettings current = existing is null ? AppSettings.Empty : ParseSettings(existing);
            AppSettings next = transform(current) ?? throw new InvalidOperationException("设置变换不能返回 null。");

            List<string> errors = CollectErrors(next);
            if (errors.Count > 0)
            {
                throw new SettingsValidationException(
                    $"设置未通过校验，未写入任何文件；问题：{string.Join("；", errors)}", errors);
            }

            byte[] bytes = ConfigurationJson.SerializeToUtf8Bytes(next);
            snapshot = ConfigurationJson.Deserialize<AppSettings>(bytes);
            await AtomicFile.WriteAllBytesAsync(_settingsFilePath, bytes, cancellationToken).ConfigureAwait(false);
            Current = snapshot;
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged(snapshot);
        return snapshot;
    }

    /// <summary>通知每个订阅者；单个订阅者失败不阻断其余订阅者，也不把已提交的写入伪装成失败。</summary>
    private void RaiseChanged(AppSettings settings)
    {
        if (Changed is not { } handlers) return;
        var args = new AppSettingsChangedEventArgs(settings);
        List<Exception>? failures = null;
        foreach (EventHandler<AppSettingsChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception ex) { (failures ??= new()).Add(ex); }
        }
        if (failures is not null) throw new SettingsChangeNotificationException(failures);
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

        FluentCode.Services.Appearance.AppFontDefaults.Validate(settings.Fonts, errors);
        if (settings.TerminalAppearance is null) errors.Add("terminalAppearance 不能为空");
        else if (settings.TerminalAppearance.ValidationError() is { } terminalError) errors.Add(terminalError);
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

        if (settings.Browser is null) errors.Add("browser 不能为空。");
        else if (settings.Browser.ValidationError() is { } browserError) errors.Add(browserError);
        if (settings.NativeUiMap is null) errors.Add("nativeUiMap 不能为空。");
        else
        {
            if (settings.NativeUiMap.ValidationError() is { } mapError) errors.Add(mapError);
            ValidateOptionalStorageId(settings.NativeUiMap.TianDiTuCredentialId, "nativeUiMap.tianDiTuCredentialId", errors);
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

        return errors;
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
