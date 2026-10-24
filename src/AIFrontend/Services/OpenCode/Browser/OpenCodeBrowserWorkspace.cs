using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Services.OpenCode.Browser;

/// <summary>Session-owned pages outlive overlay visibility. Composition supplies availability and server text reads.</summary>
public sealed class OpenCodeBrowserWorkspace : IOpenCodeBrowserWorkspace, IDisposable
{
    private readonly List<OpenCodeBrowserPage> _pages = new();
    private readonly Grid _hiddenHost;
    private readonly Grid _shownHost;
    private Func<(OpenCodeBrowserAvailability Availability, string? Reason)> _availability = () => (OpenCodeBrowserAvailability.Detached, "此会话尚未接入浏览器");
    public OpenCodeBrowserWorkspace(string sessionId, Grid hiddenHost, Grid shownHost)
    {
        SessionId = sessionId; _hiddenHost = hiddenHost; _shownHost = shownHost;
    }
    public string SessionId { get; }
    public IReadOnlyList<IOpenCodeBrowserPage> Pages => _pages.ToArray();
    public IOpenCodeBrowserPage? Shown { get; private set; }
    public string? FocusedTabId { get; private set; }
    public OpenCodeBrowserAvailability Availability => _availability().Availability;
    public string? AvailabilityReason => _availability().Reason;
    public Func<string, CancellationToken, Task<string>>? ReadPreviewTextAsync { get; set; }
    public Func<string, CancellationToken, Task<OpenCodeBrowserPreviewContent>>? ReadPreviewAsync { get; set; }
    public Func<string, bool, string?>? PreviewPathUnavailableReason { get; set; }
    public Func<string, bool, Task>? OpenPreviewPathAsync { get; set; }
    public event EventHandler? PagesChanged;
    public event EventHandler? AvailabilityChanged;
    public event EventHandler? OverlayChanged;
    public event EventHandler<OpenCodeBrowserPage>? PageActivatedByAgent;
    public void SetAvailabilityProvider(Func<(OpenCodeBrowserAvailability, string?)> provider) { _availability = provider; NotifyAvailabilityChanged(); }
    public void NotifyAvailabilityChanged() => AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    public IOpenCodeBrowserPage OpenPage(string url)
    {
        var page = CreatePage(url); Show(page); return page;
    }
    public OpenCodeBrowserPage CreatePage(string url, bool agent = false, string? previewPath = null, bool startNavigation = true)
    {
        // Validate before adding a tab; failed input must not leave a ghost page.
        var normalized = OpenCodeBrowserPolicy.NormalizeUrl(url);
        OpenCodeBrowserPage? page = null;
        page = new OpenCodeBrowserPage(Remove, uri => CreatePage(uri, page?.AgentActivity is not null), agent, previewPath);
        _pages.Insert(agent && _pages.Count > 0 ? 1 : 0, page);
        _hiddenHost.Children.Add(page.Surface);
        page.View.IsTabStop = false;
        page.PropertyChanged += (_, _) => PagesChanged?.Invoke(this, EventArgs.Empty);
        PagesChanged?.Invoke(this, EventArgs.Empty);
        if (agent) PageActivatedByAgent?.Invoke(this, page);
        if (previewPath is null && startNavigation) page.Navigate(normalized);
        return page;
    }
    public OpenCodeBrowserPage? Find(string id) => _pages.Find(p => p.Id == id);
    public void Show(IOpenCodeBrowserPage page)
    {
        if (page is not OpenCodeBrowserPage owned || !_pages.Contains(owned)) throw new OpenCodeBrowserException("not_found", "页面不属于此会话");
        HideOverlay(); Shown = owned; FocusedTabId = owned.Id;
        Move(owned.Surface, _shownHost); _pages.Remove(owned); _pages.Insert(0, owned);
        owned.View.IsTabStop = true;
        OverlayChanged?.Invoke(this, EventArgs.Empty); PagesChanged?.Invoke(this, EventArgs.Empty);
    }
    public void ActivateByAgent(OpenCodeBrowserPage page, bool switchShown = true)
    {
        FocusedTabId = page.Id;
        if (switchShown && Shown is not null) Show(page);
        PageActivatedByAgent?.Invoke(this, page); PagesChanged?.Invoke(this, EventArgs.Empty);
    }
    public void HideOverlay()
    {
        if (Shown is not OpenCodeBrowserPage page) return;
        Move(page.Surface, _hiddenHost); page.View.IsTabStop = false; Shown = null; OverlayChanged?.Invoke(this, EventArgs.Empty);
        PagesChanged?.Invoke(this, EventArgs.Empty);
    }
    private static void Move(Grid surface, Grid to)
    {
        if (ReferenceEquals(surface.Parent, to)) return;
        (surface.Parent as Panel)?.Children.Remove(surface); to.Children.Add(surface);
    }
    private void Remove(OpenCodeBrowserPage page)
    {
        if (!_pages.Contains(page)) return;
        if (ReferenceEquals(Shown, page)) HideOverlay();
        _pages.Remove(page); if (FocusedTabId == page.Id) FocusedTabId = null;
        page.DisposePage(); (page.Surface.Parent as Panel)?.Children.Remove(page.Surface);
        PagesChanged?.Invoke(this, EventArgs.Empty);
    }
    public OpenCodeBrowserState GetState() => new(_pages.Select(p => p.ToTab()).ToArray(), FocusedTabId);
    public void Dispose() { foreach (var page in _pages.ToArray()) Remove(page); }
}
