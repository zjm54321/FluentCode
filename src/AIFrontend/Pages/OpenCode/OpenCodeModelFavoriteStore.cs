using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;
using AIFrontend.Services.Configuration;

namespace AIFrontend.Pages.OpenCode;

// No favorites HTTP endpoint in the live 2.1.3 OpenAPI. WSL shares the TUI's state file;
// Remote/unavailable WSL uses SettingsService, never a guessed server endpoint.
internal sealed class OpenCodeModelFavoriteStore(string sourceKey)
{
    private readonly SettingsService _settings = new();
    private readonly WslHomeCommandRunner _runner = new();
    private readonly string _wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wsl.exe");
    private WslQuickAccessTarget? _target;
    internal OpenCodeModelPickerFavorites Favorites { get; } = new();
    internal string Status { get; private set; } = "收藏仅保存在此应用。";

    internal async Task LoadAsync()
    {
        var settings = await _settings.LoadAsync();
        Favorites.Replace((settings.OpenCodeModelFavorites ?? Array.Empty<OpenCodeModelFavorite>())
            .Where(item => item.SourceId == sourceKey).Select(item => (item.ProviderId, item.ModelId)));
        if (settings.OpenCode is not { Kind: OpenCodeConnectionKind.Wsl, WslDistribution: { } distribution }) return;
        try
        {
            var home = await WslQuickAccessHome.ResolveAsync(distribution, _wsl, _runner, CancellationToken.None);
            var state = OpenCodeWslQuickAccess.Resolve(OpenCodeConnectionKind.Wsl, true, distribution,
                "~/.local/state/opencode/model.json", home);
            if (state.Target is not { } target) throw new IOException("无法定位模型收藏文件。");
            Favorites.Replace(OpenCodeModelFavoriteJson.Read(await ReadAsync(target.HostPath)));
            _target = target;
            Status = "收藏与此 WSL 发行版的 OpenCode 共享；重新打开列表可读取最新收藏。";
        }
        catch (Exception)
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
                    "--exec", "mv", "-f", "--", target.LinuxDirectory + suffix, target.LinuxDirectory }), CancellationToken.None);
                if (result is null) throw new IOException("无法保存 WSL 模型收藏。");
                temp = null;
                Favorites.Replace(OpenCodeModelFavoriteJson.Read(merged));
                return;
            }
            catch (Exception)
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
