using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;
using AIFrontend.Services.Configuration;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

// Capability-gated server RPC works identically for WSL and Remote. Legacy sources
// retain the WSL TUI file / per-source application-settings routes.
internal sealed class OpenCodeModelFavoriteStore(string sourceKey, OpenCodeClient? client = null,
    string? directory = null, CancellationToken cancellationToken = default)
{
    private readonly SettingsService _settings = new();
    private readonly WslHomeCommandRunner _runner = new();
    private readonly string _wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wsl.exe");
    private WslQuickAccessTarget? _target;
    private bool _rpc;
    private bool _fallbackReady;
    internal OpenCodeModelPickerFavorites Favorites { get; } = new();
    internal string Status { get; private set; } = "收藏仅保存在此应用。";

    internal async Task LoadAsync()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (client is not null && directory is not null)
        {
            _rpc = await client.SupportsModelFavoritesAsync(directory, cancellationToken);
            if (_rpc)
            {
                try
                {
                    Favorites.Replace(OpenCodeModelFavoritesRpc.Read(await client.PluginRpcAsync("model.favorites.get", directory, new { }, cancellationToken)));
                    Status = "收藏通过服务器插件与 OpenCode 共享；重新打开列表可读取最新收藏。";
                    return;
                }
                catch (OpenCodePluginRpcException ex) when (ex.IsUnsupported) { _rpc = false; }
            }
        }
        var settings = await _settings.LoadAsync();
        Favorites.Replace((settings.OpenCodeModelFavorites ?? Array.Empty<OpenCodeModelFavorite>())
            .Where(item => item.SourceId == sourceKey).Select(item => (item.ProviderId, item.ModelId)));
        Status = "服务器未提供收藏能力；收藏仅保存在此应用。";
        _fallbackReady = true;
        if (OpenCodeModelFavoritesRpc.Route(false, settings.OpenCode?.Kind == OpenCodeConnectionKind.Wsl)
            != OpenCodeModelFavoriteRoute.WslFile) return;
        if (settings.OpenCode is not { Kind: OpenCodeConnectionKind.Wsl, WslDistribution: { } distribution }) return;
        try
        {
            var home = await WslQuickAccessHome.ResolveAsync(distribution, _wsl, _runner, cancellationToken);
            var state = OpenCodeWslQuickAccess.Resolve(OpenCodeConnectionKind.Wsl, true, distribution,
                "~/.local/state/opencode/model.json", home);
            if (state.Target is not { } target) throw new IOException("无法定位模型收藏文件。");
            Favorites.Replace(OpenCodeModelFavoriteJson.Read(await ReadAsync(target.HostPath)));
            _target = target;
            Status = "服务器未提供收藏能力；改用 WSL 文件共享收藏，重新打开列表可读取最新收藏。";
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            Status = "无法读取 WSL 收藏；收藏仅保存在此应用。";
        }
    }

    private static async Task<string> ReadAsync(string path)
    {
        try { return await File.ReadAllTextAsync(path); }
        catch (FileNotFoundException) { return "{}"; }
        catch (DirectoryNotFoundException) { return "{}"; }
    }

    internal async Task SaveAsync(OpenCodeModelPickerRow row, bool favorite)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_rpc && client is not null && directory is not null)
        {
            // Merge a toggle against the latest server favorites, not the dialog's opening snapshot.
            var fresh = OpenCodeModelFavoritesRpc.Read(await client.PluginRpcAsync("model.favorites.get", directory, new { }, cancellationToken))
                .Where(item => item.ProviderId != row.ProviderId || item.ModelId != row.ModelId).ToList();
            if (favorite) fresh.Insert(0, (row.ProviderId, row.ModelId));
            var output = await client.PluginRpcAsync("model.favorites.set", directory, OpenCodeModelFavoritesRpc.SetInput(fresh), cancellationToken);
            Favorites.Replace(OpenCodeModelFavoritesRpc.Read(output));
            Status = "收藏通过服务器插件与 OpenCode 共享；重新打开列表可读取最新收藏。";
            return;
        }
        if (!_fallbackReady) throw new IOException("尚未确认服务器收藏能力，请重新打开模型列表。");
        if (_target is { } target)
        {
            string? temp = null;
            try
            {
                // Re-read at every toggle: retain the TUI's latest recent/variant/other favorites.
                var merged = OpenCodeModelFavoriteJson.Set(await ReadAsync(target.HostPath), row.ProviderId, row.ModelId, favorite);
                Directory.CreateDirectory(Path.GetDirectoryName(target.HostPath)!);
                var suffix = ".aif-" + Guid.NewGuid().ToString("N") + ".tmp";
                temp = target.HostPath + suffix;
                await File.WriteAllTextAsync(temp, merged, new UTF8Encoding(false));
                // Rename within WSL, on the same filesystem, after the complete temp file is closed.
                var result = await _runner.ReadAsync(new(_wsl, new[] { "--distribution", target.Distribution,
                    "--exec", "mv", "-f", "--", target.LinuxDirectory + suffix, target.LinuxDirectory }), cancellationToken);
                if (result is null) throw new IOException("无法保存 WSL 模型收藏。");
                temp = null;
                Favorites.Replace(OpenCodeModelFavoriteJson.Read(merged));
                return;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                _target = null;
                Status = "无法同步 WSL；收藏仅保存在此应用。";
            }
            finally
            {
                if (temp is not null) { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
        }
        var snapshot = Favorites.Keys.Select(key => new OpenCodeModelFavorite(sourceKey, key.ProviderId, key.ModelId)).ToArray();
        await _settings.UpdateAsync(settings => settings with
        {
            OpenCodeModelFavorites = (settings.OpenCodeModelFavorites ?? Array.Empty<OpenCodeModelFavorite>())
                .Where(item => item.SourceId != sourceKey).Concat(snapshot).ToArray(),
        });
    }
}
