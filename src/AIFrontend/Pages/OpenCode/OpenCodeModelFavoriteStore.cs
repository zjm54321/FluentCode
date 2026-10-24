using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

internal sealed class OpenCodeModelFavoriteStore(OpenCodeClient? client, string? directory, CancellationToken cancellationToken = default)
{
    internal OpenCodeModelPickerFavorites Favorites { get; } = new();
    internal bool Available { get; private set; }
    internal string Status => Available ? "收藏通过 Desktop Bridge 与 OpenCode 共享。" : "需要 Desktop Bridge 插件";

    internal async Task LoadAsync()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (client is null || directory is null) return;
        var bridge = await client.DesktopBridge.NegotiateAsync(client, directory, cancellationToken);
        if (!bridge.Supports("model.favorites.v1")) return;
        Favorites.Replace(OpenCodeModelFavoritesRpc.Read(await client.PluginRpcAsync("model.favorites.get", directory, new { }, cancellationToken)));
        Available = true;
    }

    internal async Task SaveAsync(OpenCodeModelPickerRow row, bool favorite)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Available || client is null || directory is null) throw new InvalidOperationException("需要 Desktop Bridge 插件");
        var fresh = OpenCodeModelFavoritesRpc.Read(await client.PluginRpcAsync("model.favorites.get", directory, new { }, cancellationToken))
            .Where(item => item.ProviderId != row.ProviderId || item.ModelId != row.ModelId).ToList();
        if (favorite) fresh.Insert(0, (row.ProviderId, row.ModelId));
        var output = await client.PluginRpcAsync("model.favorites.set", directory, OpenCodeModelFavoritesRpc.SetInput(fresh), cancellationToken);
        Favorites.Replace(OpenCodeModelFavoritesRpc.Read(output));
    }
}
