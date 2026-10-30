using Microsoft.UI.Windowing;

namespace AIFrontend;

public sealed partial class MainWindow
{
    internal bool IsHiddenToTray { get; private set; }
    internal bool IsDarkTheme => RootElement.ActualTheme == Microsoft.UI.Xaml.ElementTheme.Dark;

    internal void HideToBackground()
    {
        if (_shellClosing || IsHiddenToTray) return;
        IsHiddenToTray = true;
        // Fence view ACK/foreground notifications and stop UI work before the HWND disappears.
        PushOpenCodeWindowVisibility(false);
        AppWindow.Hide();
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
