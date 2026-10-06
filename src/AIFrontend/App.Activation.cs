using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIFrontend.Services.Notifications;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace AIFrontend;

public partial class App
{
    private AppInstance? _primaryInstance;
    private AppActivationPipe? _activationPipe;
    private volatile bool _activationRegistered;

    private async Task<bool> BecomePrimaryInstanceAsync()
    {
        // Keep the SDK-generated Main: its STA, ComWrappersSupport and DispatcherQueueSynchronizationContext
        // remain intact. OnLaunched has a pumping dispatcher before an asynchronous redirect is awaited.
        var instance = AppInstance.FindOrRegisterForKey("AIFrontend.Main");
        if (instance.IsCurrent)
        {
            _primaryInstance = instance;
            instance.Activated += OnRedirectedActivation;
            try
            {
                _activationPipe = new AppActivationPipe(payload => RouteNotificationActivation(payload.Arguments, payload.UserInput, false),
                    () => RecordDiagnostic("activation-ipc-rejected"));
            }
            catch (Exception ex) { RecordDiagnostic("activation-ipc-unavailable", error: ex); }
            return true;
        }

        // The secondary never creates a Window, a service/coordinator/client, or calls a mutation boundary.
        // Register before GetActivatedEventArgs so the SDK recognizes notification activation correctly.
        var manager = AppNotificationManager.Default;
        var forwarder = new SecondaryActivationForwarder(payload => AppActivationPipe.ForwardAsync(checked((int)instance.ProcessId), payload),
            () => RecordDiagnostic("activation-secondary-transfer-failed"));
        void ForwardCurrentNotification(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
        {
            try
            {
                forwarder.Forward(() =>
                {
                    if (args.Arguments.Count > 8 || args.UserInput.Count > 16) throw new ArgumentException("Activation bounds.");
                    return AppActivationPipe.Snapshot(new Dictionary<string, string>(args.Arguments),
                        new Dictionary<string, string>(args.UserInput));
                });
            }
            catch { RecordDiagnostic("activation-secondary-transfer-failed"); }
        }
        manager.NotificationInvoked += ForwardCurrentNotification;
        bool registered = false;
        try
        {
            manager.Register();
            registered = true;
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            await instance.RedirectActivationToAsync(activation);
            return false;
        }
        finally
        {
            try
            {
                if (registered) manager.Unregister();
            }
            finally
            {
                manager.NotificationInvoked -= ForwardCurrentNotification;
                await forwarder.StopAndDrainAsync();
            }
        }
    }

    private void ReadInitialActivationAfterRegistration()
    {
        if (_completionNotifications?.Status != NotificationServiceStatus.Initialized)
        { RecordDiagnostic("activation-registration-unavailable"); return; }
        _activationRegistered = true;
        RouteActivation(AppInstance.GetCurrent().GetActivatedEventArgs(), coldStart: true);
    }

    private void OnRedirectedActivation(object? sender, AppActivationArguments args)
    {
        // Do not enqueue raw AppActivationArguments or user input in UI state. Extract/process immediately;
        // only the service's validated structured result/target crosses PostToApp.
        RouteActivation(args, coldStart: false);
    }

    private void RouteActivation(AppActivationArguments activation, bool coldStart)
    {
        if (_closing) return;
        try
        {
            if (activation.Kind != ExtendedActivationKind.AppNotification)
            { PostToApp(() => _window?.Activate()); return; }
            if (activation.Data is not AppNotificationActivatedEventArgs notification)
            { PublishActivationNeedsApp("activation-data-unavailable"); return; }
            RouteNotificationActivation(new Dictionary<string, string>(notification.Arguments),
                new Dictionary<string, string>(notification.UserInput), coldStart);
        }
        catch (Exception ex) { RecordDiagnostic("activation-failed", error: ex); PublishActivationNeedsApp("activation-needs-app"); }
    }

    private void RouteNotificationActivation(IReadOnlyDictionary<string, string> arguments,
        IReadOnlyDictionary<string, string> userInput, bool coldStart)
    {
        if (_closing) return;
        try
        {
            var service = _completionNotifications;
            if (!_activationRegistered || service?.Status != NotificationServiceStatus.Initialized)
            { PublishActivationNeedsApp("activation-owner-not-ready"); return; }
            if (arguments.TryGetValue("nonce", out string? nonce))
            {
                // No permanent nonce consumption here: navigation can repeat and InvalidInput can be corrected.
                // The coordinator claim + shared gate provide mutation single-flight across all entry points.
                if (nonce is null || nonce.Length > 256 || arguments.Count != 1)
                { PublishActivationNeedsApp("activation-invalid"); return; }
                if (coldStart) { PublishActivationNeedsApp("activation-cold-nonce"); return; }
                _ = service.HandleInteractionActivationAsync(arguments, userInput, _appLifetime.Token);
                return;
            }
            // Same resolver as the native callback. It accepts only committed, unexpired runtime bindings;
            // cold/unknown tokens cannot manufacture a session. Completion routing never invokes mutation.
            service.HandleCompletionActivation(arguments, userInput);
        }
        catch (Exception ex) { RecordDiagnostic("activation-failed", error: ex); PublishActivationNeedsApp("activation-needs-app"); }
    }

    private void PublishActivationNeedsApp(string code)
    {
        RecordDiagnostic(code);
        OnOpenCodeInteraction(this, new NotificationInteractionEventArgs(new(NotificationInteractionStatus.NeedsApp,
            "请在应用中选择当前会话并重新验证；本次激活未提交任何决定。")));
    }

    private void StopActivationOwnership()
    {
        _activationRegistered = false;
        if (_primaryInstance is not null)
        {
            _primaryInstance.Activated -= OnRedirectedActivation;
            // Keep the instance key until process exit/drain finishes. Unregistering it here could allow
            // a fresh process to mutate while this one's in-flight operation still has an unknown result.
        }
        _activationPipe?.Stop();
    }
}
