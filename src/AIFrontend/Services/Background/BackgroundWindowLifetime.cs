namespace AIFrontend.Services.Background;

internal enum WindowCloseAction { Hide, Shutdown, Ignore }

/// <summary>UI-thread close policy. Explicit exit cannot be intercepted by close-to-tray.</summary>
internal sealed class BackgroundWindowLifetime
{
    internal bool ExitRequested { get; private set; }
    internal bool ShutdownStarted { get; private set; }

    internal void RequestExit() => ExitRequested = true;

    internal WindowCloseAction DecideClose(bool runInBackground, bool trayAvailable) =>
        ShutdownStarted ? WindowCloseAction.Ignore :
        runInBackground && trayAvailable && !ExitRequested ? WindowCloseAction.Hide : WindowCloseAction.Shutdown;

    internal bool TryBeginShutdown()
    {
        if (ShutdownStarted) return false;
        ShutdownStarted = true;
        return true;
    }
}
