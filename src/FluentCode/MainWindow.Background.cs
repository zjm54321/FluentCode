using Microsoft.UI.Windowing;

namespace FluentCode;

public sealed partial class MainWindow
{
    internal bool IsHiddenToTray { get; private set; }
    internal bool IsDarkTheme => RootElement.ActualTheme == Microsoft.UI.Xaml.ElementTheme.Dark;

    internal void HideToBackground()
    {
        if (_shellClosing || IsHiddenToTray) return;
        IsHiddenToTray = true;
        _ = SaveOpenCodeTabsAsync(); // Hidden to the tray is how most runs end up before the process is killed.
        // Fence view ACK/foreground notifications and stop UI work before the HWND disappears.
        PushOpenCodeWindowVisibility(false);
        AppWindow.Hide();
    }

    /// <summary>Hand the current tab strip to its state file and wait until it is written. UI thread; never throws.</summary>
    internal System.Threading.Tasks.Task SaveOpenCodeTabsAsync()
    {
        try { _cachedOpenCodePage?.PublishTabsNow(); }
        catch (System.Exception) { /* Saving tabs must never stand in the way of hiding or closing. */ }
        return Services.OpenCode.OpenCodeSavedTabsStore.Shared.FlushAsync();
    }

    internal void ShowFromBackground()
    {
        if (_shellClosing) return;
        IsHiddenToTray = false;
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        Services.Background.TrayIconNative.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        UpdateOpenCodeWindowVisibility();
    }
}
