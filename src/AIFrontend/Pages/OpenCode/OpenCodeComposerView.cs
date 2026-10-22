using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Appearance;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Dispatching;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI.Core;

namespace AIFrontend.Pages.OpenCode;

public sealed class OpenCodeComposerTokenEventArgs(OpenCodeComposerToken token) : EventArgs
{
    public OpenCodeComposerToken Token { get; } = token;
}
public sealed class OpenCodeComposerCommandSelectedEventArgs(OpenCodeComposerToken token, string name, string remainingArguments) : EventArgs
{
    public OpenCodeComposerToken Token { get; } = token;
    public string Name { get; } = name;
    public string RemainingArguments { get; } = remainingArguments;
}
public sealed class OpenCodeComposerAttachmentRemovedEventArgs(OpenCodeComposerToken token, Guid id) : EventArgs
{
    public OpenCodeComposerToken Token { get; } = token;
    public Guid Id { get; } = id;
}
public sealed class OpenCodeComposerTextEditedEventArgs(OpenCodeComposerToken token, string text) : EventArgs
{
    public OpenCodeComposerToken Token { get; } = token;
    public string Text { get; } = text;
    /// <summary>Set by the view only for input recorded against an unbound baseline (navigation-carry request).</summary>
    internal bool AllowUnboundCarry { get; init; }
    /// <summary>Set synchronously by the single page handler: <c>controller.TryApplyEditorInput(Token, Text, AllowUnboundCarry)</c>.</summary>
    internal OpenCodeComposerEditorAck? Acknowledgment { get; set; }
}

/// <summary>
/// 命令 / 附件伴随视图：位于原生消息输入框上方，只渲染控制器快照并发出携带令牌的意图事件。
/// </summary>
/// <remarks>
/// <para>不持有 client / source / gate / transport，也不发送任何请求；页面把事件转给控制器。
/// 输入框仍是页面原有 TextBox：本视图只做文本对账（控制器侧编辑写回、未同步文本推送）、
/// 只读切换与占位文案，键盘判定经 <see cref="HandleEditorKey"/> 由页面既有处理器调用。</para>
/// <para>“/”键本身不打开目录；只有经控制器确认的用户输入（提交的输入修订号 + 确认令牌/文本）使整段文本成为
/// 行首裸触发词（如 “/”、“/re”）时才自动打开，程序写回、绑定、渲染、恢复草稿都不会打开。按钮、Ctrl+/ 和重试为显式打开。
/// 选择目录行只暂存（服务器命令成为命令标签；AIFrontend 本机操作成为独立的本机操作标签），消费控制器证明的触发词，
/// 绝不执行、绝不发送；执行本机操作需要随后一次新的显式按键或点击。</para>
/// <para>目录权限全部来自控制器能力：视图持有自己的 <see cref="OpenCodeComposerCatalogLifetime"/>，打开事件携带它，
/// 关闭携带显示中的 <see cref="OpenCodeComposerCatalogHandle"/>，选择携带显示中的句柄 + 类型化标识 + 刷新后的令牌。</para>
/// </remarks>
internal sealed class OpenCodeComposerView : StackPanel
{
    private static FontFamily MonoFont => AppFonts.Monospace;
    private readonly Func<OpenCodeComposerSnapshot> _source;
    private readonly DispatcherQueue _dispatcher;
    private readonly OpenCodeComposerViewLifetime _lifetime;
    private readonly OpenCodeComposerAcceptedDraftHandoffs _handoffs;

    // Leading "+" button (wave 3 F1): one native Button + MenuFlyout replaces the old toolbar row.
    private readonly Button _addButton;
    private readonly MenuFlyoutItem _commandItem;
    private readonly MenuFlyoutItem _attachItem;
    // B7: 上下文（@） inserts a legal literal "@" (real file index candidates); 终端（!） stays disabled until B8.
    private readonly MenuFlyoutItem _contextItem;
    private readonly MenuFlyoutItem _terminalItem;
    private readonly MenuFlyoutSeparator _discardSeparator;
    private readonly MenuFlyoutItem _discardItem;
    private readonly Flyout _discardFlyout;
    private readonly TextBlock _summaryText;

    // Draft chips
    private readonly ScrollViewer _chipScroll;
    private readonly StackPanel _chipPanel;
    private readonly Border _commandChip;
    private readonly TextBlock _commandChipText;
    private readonly Button _commandChipRemove;
    private readonly Grid _chipRow;
    private readonly Border _intentChip;
    private readonly TextBlock _intentChipText;
    private readonly Button _intentRun;
    private readonly Button _intentRemove;
    private readonly TextBlock _intentHint;
    private readonly Dictionary<Guid, FrameworkElement> _attachmentChips = new();
    private OpenCodeComposerToken? _attachmentActivation;
    private long _attachmentViewGeneration = -1;

    // Status line (inactive / busy / protected / notes)
    private readonly Border _statusBar;
    private readonly FontIcon _statusIcon;
    private readonly ProgressRing _statusRing;
    private readonly TextBlock _statusText;
    private readonly Button _statusDiscardButton;
    private int _sideQuestionPending;

    internal void SetSideQuestionPending(int count)
    {
        _sideQuestionPending = count;
        RequestRender();
    }

    // Catalog menu
    private readonly Grid _menuSlot;
    private readonly Border _menu;
    private readonly TextBlock _menuTitle;
    private readonly ProgressRing _menuRing;
    private readonly TextBlock _menuStatus;
    private readonly Button _menuRetry;
    private readonly ListView _menuList;

    private TextBox? _editor;
    private Func<bool> _isComposing = static () => false;
    private string? _editorPlaceholder;
    // Placeholder splash (B15): rotated only by an explicit slot activation; feeds PlaceholderText only, never editor text.
    private readonly OpenCodeComposerSplash _splash = new();
    private bool _editorReadOnlyApplied;
    private int _slotTransitionDepth;
    private string? _slotUnavailableReason;

    private OpenCodeComposerSnapshot? _snapshot;
    private OpenCodeComposerEditorSync? _sync;
    private bool _composing;
    private readonly OpenCodeComposerCommandMenuSession _menuSession;
    private readonly OpenCodeComposerInputEvidenceSource _inputEvidence;
    private readonly Func<OpenCodeComposerCatalogLifetime>? _beginCatalogLifetime;
    private readonly Action<OpenCodeComposerCatalogLifetime>? _endCatalogLifetime;
    private OpenCodeComposerCatalogLifetime? _catalogLifetime;
    private OpenCodeComposerCatalogHandle? _renderedHandle;
    private IReadOnlyList<OpenCodeComposerMenuRow> _renderedRows = Array.Empty<OpenCodeComposerMenuRow>();
    private bool _applyingHighlight;
    // 菜单分组标题（重设计 v3 §7.2）占用列表项位置：Rows[i] 对应的 ListView 项下标。
    private int[] _rowItemIndex = Array.Empty<int>();
    private bool _menuShown;
    private bool _picking;
    private string? _localNote;
    private OpenCodeComposerToken? _localNoteToken;
    private string _fingerprint = string.Empty;

    // Retired authority-less catalog intents. They are inert (never raised, subscriptions ignored) so an unadapted page
    // still compiles but cannot open, dismiss or select by token/name; the core rejects those paths as well.
    internal event EventHandler<OpenCodeComposerTokenEventArgs>? CatalogRequested { add { } remove { } }
    internal event EventHandler<OpenCodeComposerTokenEventArgs>? CatalogDismissed { add { } remove { } }
    internal event EventHandler<OpenCodeComposerCommandSelectedEventArgs>? CommandSelected { add { } remove { } }

    /// <summary>Open/refresh with the view's captured lifetime → <c>controller.LoadCatalogAsync(e.Token, e.Lifetime, e.Refresh, ct)</c>.</summary>
    internal event EventHandler<OpenCodeComposerCatalogOpenRequestedEventArgs>? CatalogOpenRequested;
    /// <summary>Dismiss the displayed opening → <c>controller.DismissCatalog(e.Handle)</c>.</summary>
    internal event EventHandler<OpenCodeComposerCatalogDismissRequestedEventArgs>? CatalogDismissRequested;
    /// <summary>Stage a displayed row → <c>e.Accepted = controller.TrySelectCommand(e.Token, e.Handle, e.Identity)</c>. Never executes.</summary>
    internal event EventHandler<OpenCodeComposerCommandChosenEventArgs>? CommandChosen;
    /// <summary>Remove the staged native intent → <c>controller.DismissClientIntent(e.Token, e.IntentId)</c>.</summary>
    internal event EventHandler<OpenCodeComposerClientIntentEventArgs>? ClientIntentDismissed;
    /// <summary>
    /// Fresh explicit invocation of the staged native intent (never a server submission) →
    /// <c>ticket = controller.TryTakeClientAction(e.Token, e.IntentId)</c>; set <c>e.Accepted</c> only when a ticket was taken,
    /// then dispatch by <c>ticket.Kind</c>. With no subscriber nothing happens (fail closed; no legacy send fallback).
    /// </summary>
    internal event EventHandler<OpenCodeComposerClientIntentEventArgs>? ClientActionInvoked;
    /// <summary>Tab completion of the displayed trigger → <c>e.Accepted = controller.TryCompleteTrigger(e.Token, e.Handle, e.Completion)</c>.</summary>
    internal event EventHandler<OpenCodeComposerTriggerCompletionEventArgs>? CompletionRequested;
    /// <summary>
    /// A click confirmed a server command the catalog declared argument-free: the page runs its ordinary Send path
    /// (<c>RequestComposerSubmission</c>, with every existing check). Keyboard confirmation returns Submit instead.
    /// </summary>
    internal event EventHandler? SubmitRequested;
    internal event EventHandler<OpenCodeComposerTokenEventArgs>? CommandRemoved;
    internal event EventHandler<OpenCodeComposerTokenEventArgs>? PickRequested;
    internal event EventHandler<OpenCodeComposerAttachmentRemovedEventArgs>? AttachmentRemoved;
    internal event EventHandler<OpenCodeComposerTokenEventArgs>? DiscardRequested;
    internal event EventHandler<OpenCodeComposerTextEditedEventArgs>? TextEdited;
    /// <summary>Raised on the UI thread after a render whose visible/eligibility state changed (page refreshes Send/Stop).</summary>
    internal event EventHandler? StateChanged;

    /// <summary>Compatibility constructor: no catalog lifetime, so no menu can open (fail closed until the page adapts).</summary>
    internal OpenCodeComposerView(Func<OpenCodeComposerSnapshot> snapshotSource) : this(snapshotSource, null, null) { }

    /// <param name="snapshotSource">Read-only accessor for the controller's current snapshot (e.g. <c>() =&gt; controller.Snapshot</c>).</param>
    /// <param name="beginCatalogLifetime"><c>controller.BeginCatalogLifetime</c>: called once per attached, loaded editor lifetime.</param>
    /// <param name="endCatalogLifetime"><c>controller.EndCatalogLifetime</c>: called with that exact capability on unload/reattach.</param>
    internal OpenCodeComposerView(Func<OpenCodeComposerSnapshot> snapshotSource,
        Func<OpenCodeComposerCatalogLifetime>? beginCatalogLifetime, Action<OpenCodeComposerCatalogLifetime>? endCatalogLifetime)
    {
        _source = snapshotSource ?? throw new ArgumentNullException(nameof(snapshotSource));
        _beginCatalogLifetime = beginCatalogLifetime;
        _endCatalogLifetime = endCatalogLifetime;
        _menuSession = new OpenCodeComposerCommandMenuSession(_source, FlushForIntent,
            () => _sync?.LastAcknowledgment, () => _sync?.State ?? OpenCodeComposerInputState.Clean, () => _composing);
        _menuSession.OpenRequested += (_, e) => CatalogOpenRequested?.Invoke(this, e);
        _menuSession.DismissRequested += (_, e) => CatalogDismissRequested?.Invoke(this, e);
        _menuSession.CommandChosen += (_, e) => CommandChosen?.Invoke(this, e);
        _menuSession.ClientIntentDismissed += (_, e) => ClientIntentDismissed?.Invoke(this, e);
        _menuSession.ClientActionInvoked += (_, e) => ClientActionInvoked?.Invoke(this, e);
        _menuSession.CompletionRequested += (_, e) => CompletionRequested?.Invoke(this, e);
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("OpenCodeComposerView must be created on a UI thread.");
        // Lifetime fence (no timer): generation-checked queued renders; unload/reattach cancel captured work.
        // A never-loaded view still renders, so constructor-first hookup (AttachEditor before insertion) works.
        _lifetime = new OpenCodeComposerViewLifetime(new DispatcherScheduler(_dispatcher), () => Render(_source()));
        _handoffs = new OpenCodeComposerAcceptedDraftHandoffs(_lifetime, () => _sync);
        // Catalog capability follows the ACTUAL view lifetime: unload/reattach end it; resume/attach begin a fresh one.
        _lifetime.Invalidated += EndCatalogLifetime;
        _lifetime.Invalidated += CloseMention;
        // Read-only input evidence over the EXISTING ledger (no second ledger/timer); lifetime changes invalidate captures.
        _inputEvidence = new OpenCodeComposerInputEvidenceSource(() => _sync, () => _lifetime.IsLive, () => _lifetime.Generation, () => _composing);
        _lifetime.Invalidated += _inputEvidence.LifetimeChanged;
        Loaded += (_, _) =>
        {
            _lifetime.OnLoaded();
            _inputEvidence.LifetimeChanged(); // Resume (or first load): a new observation step; old captures stay stale.
            if (_lifetime.IsLive && _editor is not null && _catalogLifetime is null) BeginCatalogLifetime();
        };
        Unloaded += (_, _) => _lifetime.OnUnloaded();
        Orientation = Orientation.Vertical;
        Spacing = 6;
        Margin = new Thickness(0);
        AutomationProperties.SetAutomationId(this, "OpenCodeComposerCompanion");
        AutomationProperties.SetName(this, "命令与附件");

        // ---- Leading "+" button (B7): 添加附件 · ── · 命令（/） · 上下文（@） · 终端（!） [· ── · 丢弃草稿…] ----
        // Built from OpenCodeComposerPlusMenu.Entries so the order/labels are verified offline against the same data.
        var addMenu = new MenuFlyout { Placement = FlyoutPlacementMode.TopEdgeAlignedLeft };
        MenuFlyoutItem? attach = null, command = null, context = null, terminal = null;
        foreach (var entry in OpenCodeComposerPlusMenu.Entries)
        {
            if (entry.Kind == OpenCodeComposerPlusMenuKind.Separator) { addMenu.Items.Add(new MenuFlyoutSeparator()); continue; }
            var item = new MenuFlyoutItem { Text = entry.Text, Icon = new FontIcon { Glyph = entry.Glyph } };
            AutomationProperties.SetAutomationId(item, entry.AutomationId);
            addMenu.Items.Add(item);
            switch (entry.Kind)
            {
                case OpenCodeComposerPlusMenuKind.Attach: attach = item; break;
                case OpenCodeComposerPlusMenuKind.Command: command = item; break;
                case OpenCodeComposerPlusMenuKind.Context: context = item; break;
                case OpenCodeComposerPlusMenuKind.Terminal: terminal = item; break;
            }
        }
        _attachItem = attach!;
        _attachItem.Click += (_, _) => RequestPick();
        _commandItem = command!;
        _commandItem.KeyboardAcceleratorTextOverride = "Ctrl+/";
        ToolTipService.SetToolTip(_commandItem, OpenCodeComposerPlusMenu.CommandTooltip);
        _commandItem.Click += (_, _) => RequestCatalog();
        _contextItem = context!;
        _contextItem.KeyboardAcceleratorTextOverride = "@";
        _contextItem.Click += (_, _) => InsertContextTrigger();
        _terminalItem = terminal!;
        _terminalItem.KeyboardAcceleratorTextOverride = "!";
        _terminalItem.IsEnabled = false; // B8 delivers terminal mode; B7 never pretends it works.
        ToolTipService.SetToolTip(_terminalItem, OpenCodeComposerPlusMenu.TerminalUnavailableTooltip);
        AutomationProperties.SetHelpText(_terminalItem, OpenCodeComposerPlusMenu.TerminalUnavailableTooltip);
        _discardFlyout = BuildDiscardFlyout();
        _discardSeparator = new MenuFlyoutSeparator { Visibility = Visibility.Collapsed };
        _discardItem = new MenuFlyoutItem { Text = "丢弃草稿…", Icon = new FontIcon { Glyph = "\uE74D" }, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(_discardItem, "OpenCodeComposerDiscardButton");
        _discardItem.Click += (_, _) => _discardFlyout.ShowAt(_addButton);
        addMenu.Items.Add(_discardSeparator);
        addMenu.Items.Add(_discardItem);
        _addButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE710", FontSize = 14 },
            Width = 32,
            Height = 32,
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Flyout = addMenu,
        };
        AutomationProperties.SetAutomationId(_addButton, "OpenCodeComposerAddButton");
        AutomationProperties.SetName(_addButton, "添加附件、命令或上下文");
        ToolTipService.SetToolTip(_addButton, "添加附件、命令、上下文");

        _summaryText = Caption(string.Empty, 0.75);
        _summaryText.VerticalAlignment = VerticalAlignment.Center;
        _summaryText.TextWrapping = TextWrapping.NoWrap;
        _summaryText.Visibility = Visibility.Collapsed;

        // ---- Chips: 命令 pill + 附件 pills (bounded horizontal strip) ----
        _commandChipText = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = ThemedForeground("TextBlock", "AccentTextFillColorPrimaryBrush"),
        };
        AppFonts.Apply(_commandChipText, AppFontSlot.Monospace); // Long-lived: follows live font changes.
        _commandChipRemove = ChipRemoveButton("OpenCodeComposerRemoveCommand");
        _commandChipRemove.Click += (_, _) => Raise(CommandRemoved);
        var commandChipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        commandChipRow.Children.Add(new FontIcon { Glyph = "\uE756", FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Style = ThemedForeground("FontIcon", "AccentTextFillColorPrimaryBrush") });
        commandChipRow.Children.Add(_commandChipText);
        commandChipRow.Children.Add(_commandChipRemove);
        _commandChip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 3, 3, 3),
            BorderThickness = new Thickness(1),
            Style = ThemedBorder("SystemFillColorAttentionBackgroundBrush", "AccentFillColorDefaultBrush"),
            Child = commandChipRow,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(_commandChip, "OpenCodeComposerCommandChip");

        // ---- Native-intent chip: an AIFrontend action, visibly NOT a server command/attachment/text ----
        _intentChipText = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 160,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        AppFonts.Apply(_intentChipText, AppFontSlot.Monospace);
        _intentRun = new Button { Content = "执行", Padding = new Thickness(8, 1, 8, 2), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(_intentRun, "OpenCodeComposerRunClientAction");
        _intentRun.Click += (_, _) => RunClientIntent();
        _intentRemove = ChipRemoveButton("OpenCodeComposerRemoveClientAction");
        _intentRemove.Click += (_, _) => RemoveClientIntent();
        var intentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        intentRow.Children.Add(new FontIcon { Glyph = "\uE8A7", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        intentRow.Children.Add(_intentChipText);
        intentRow.Children.Add(OriginBadge(OpenCodeComposerCommandOrigin.AIFrontend, emphasised: true));
        intentRow.Children.Add(_intentRun);
        intentRow.Children.Add(_intentRemove);
        _intentChip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 3, 3, 3),
            BorderThickness = new Thickness(1),
            Style = ThemedBorder("SubtleFillColorSecondaryBrush", "ControlStrokeColorDefaultBrush"),
            Child = intentRow,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(_intentChip, "OpenCodeComposerClientActionChip");
        _intentHint = Caption(string.Empty, 0.7);
        _intentHint.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_intentHint, AutomationLiveSetting.Polite);

        _chipPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _chipPanel.ChildrenTransitions = new TransitionCollection { new AddDeleteThemeTransition() };
        _chipPanel.Children.Add(_commandChip);
        _chipPanel.Children.Add(_intentChip);
        _chipScroll = new ScrollViewer
        {
            Content = _chipPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Padding = new Thickness(0, 0, 0, 2),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(_chipScroll, "OpenCodeComposerChips");
        AutomationProperties.SetName(_chipScroll, "已选命令与附件");

        _chipRow = new Grid { ColumnSpacing = 8, Visibility = Visibility.Collapsed };
        _chipRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _chipRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(_chipRow, _chipScroll, 0);
        Place(_chipRow, _summaryText, 1);

        // ---- Status line ----
        _statusIcon = new FontIcon { FontSize = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        _statusRing = new ProgressRing { Width = 14, Height = 14, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top };
        _statusText = Caption(string.Empty, 0.9);
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        _statusDiscardButton = new Button { Content = "丢弃草稿", Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(_statusDiscardButton, "OpenCodeComposerInactiveDiscard");
        _statusDiscardButton.Flyout = BuildDiscardFlyout();
        var statusGrid = new Grid { ColumnSpacing = 8 };
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyphHost = new Grid();
        glyphHost.Children.Add(_statusIcon);
        glyphHost.Children.Add(_statusRing);
        Place(statusGrid, glyphHost, 0);
        Place(statusGrid, _statusText, 1);
        Place(statusGrid, _statusDiscardButton, 2);
        _statusBar = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 6, 6),
            Child = statusGrid,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(_statusBar, "OpenCodeComposerStatus");
        AutomationProperties.SetLiveSetting(_statusText, AutomationLiveSetting.Polite);

        // ---- Catalog menu (inline, directly above the editor; added/removed for entrance motion) ----
        _menuTitle = new TextBlock { Text = "命令", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _menuRing = new ProgressRing { Width = 14, Height = 14, IsActive = false, Visibility = Visibility.Collapsed };
        _menuStatus = Caption(string.Empty, 0.7);
        _menuStatus.VerticalAlignment = VerticalAlignment.Center;
        _menuStatus.TextWrapping = TextWrapping.NoWrap;
        _menuStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetLiveSetting(_menuStatus, AutomationLiveSetting.Polite);
        _menuRetry = new Button { Content = "重试", Visibility = Visibility.Collapsed, Padding = new Thickness(8, 2, 8, 3) };
        AutomationProperties.SetAutomationId(_menuRetry, "OpenCodeComposerCatalogRetry");
        _menuRetry.Click += (_, _) => RequestCatalog();
        var menuClose = ChipRemoveButton("OpenCodeComposerCatalogClose");
        AutomationProperties.SetName(menuClose, "关闭命令目录");
        ToolTipService.SetToolTip(menuClose, "关闭（Esc）");
        menuClose.Click += (_, _) => DismissCatalog(focusEditor: true);
        var menuHeader = new Grid { ColumnSpacing = 8, Padding = new Thickness(12, 8, 6, 6) };
        menuHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        menuHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        menuHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        menuHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        menuHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(menuHeader, _menuTitle, 0);
        Place(menuHeader, _menuRing, 1);
        Place(menuHeader, _menuStatus, 2);
        Place(menuHeader, _menuRetry, 3);
        Place(menuHeader, menuClose, 4);

        _menuList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            MaxHeight = 232,
            Padding = new Thickness(0, 0, 0, 4),
        };
        AutomationProperties.SetAutomationId(_menuList, "OpenCodeComposerCatalogList");
        AutomationProperties.SetName(_menuList, "命令目录");
        _menuList.ItemClick += (_, e) =>
        {
            // Directly-added containers may report either the ListViewItem or its content; both carry the evidence
            // captured when THIS row was rendered (handle + rendered token + typed identity), never a name.
            if (e.ClickedItem is FrameworkElement { Tag: OpenCodeComposerMenuEvidence evidence }) Accept(evidence);
        };
        _menuList.SelectionChanged += (_, _) =>
        {
            if (_applyingHighlight) return;
            if (_menuList.SelectedItem is FrameworkElement { Tag: OpenCodeComposerMenuEvidence { Identity: { } identity } evidence })
                _menuSession.SetHighlight(evidence.Handle, identity);
            else if (_menuList.SelectedItem is not null)
                ApplyHighlight(_menuSession.Displayed); // A group header is never a selectable row.
        };
        _menuList.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape) { e.Handled = true; DismissCatalog(focusEditor: true); }
        };

        var menuStack = new StackPanel();
        menuStack.Children.Add(menuHeader);
        menuStack.Children.Add(_menuList);
        _menu = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Style = ThemedBorder("LayerFillColorDefaultBrush", "CardStrokeColorDefaultBrush"),
            Child = menuStack,
        };
        AutomationProperties.SetAutomationId(_menu, "OpenCodeComposerCatalog");
        AutomationProperties.SetName(_menu, "命令目录");
        _menuSlot = new Grid { Visibility = Visibility.Collapsed };
        _menuSlot.ChildrenTransitions = new TransitionCollection { new EntranceThemeTransition { FromVerticalOffset = 8 } };

        Children.Add(_chipRow);
        Children.Add(_intentHint);
        Children.Add(_statusBar);
        Children.Add(_menuSlot);
        _mentionSlot = BuildMentionPopup();
        Children.Add(_mentionSlot);
    }

    /// <summary>The leading "+" button (native Button + MenuFlyout). The page places it left of the editor.</summary>
    internal Button AddButton => _addButton;

    /// <summary>
    /// Pure, atomic, any-thread read of the last published input evidence over the existing ledger (no text/files).
    /// Page: capture right after a clean <see cref="PrepareSubmission"/>/<see cref="PrepareClientTextApplication"/>
    /// (require <c>IsClean</c>), then in CanStart use <c>OpenCodeComposerInputEvidence.StillCurrent(captured, InputEvidence)</c>.
    /// Grants no permission; source/draft guards remain Core/Page.
    /// </summary>
    internal OpenCodeComposerInputEvidence InputEvidence => _inputEvidence.Current;

    /// <summary>Last snapshot rendered (UI thread).</summary>
    internal OpenCodeComposerSnapshot? Snapshot => _snapshot;
    internal bool IsMenuVisible => _menuShown;

    /// <summary>
    /// Attach the page's existing message TextBox. The view records input itself from TextChanging and tracks
    /// composition start/end (end only wakes rechecks); the page still forwards TextChanged via
    /// <see cref="NotifyEditorTextChanged"/> (flush only) and keys via <see cref="HandleEditorKey"/>.
    /// <paramref name="isComposing"/> is the page's focus-suppression predicate (IME/suppressed/inactive page).
    /// Reattaching detaches the previous editor and fences its pending callbacks and handoffs.
    /// </summary>
    internal void AttachEditor(TextBox editor, Func<bool> isComposing)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _isComposing = isComposing ?? throw new ArgumentNullException(nameof(isComposing));
        if (_editor is { } previous)
        {
            previous.TextChanging -= OnEditorTextChanging;
            previous.TextCompositionStarted -= OnEditorCompositionStarted;
            previous.TextCompositionEnded -= OnEditorCompositionEnded;
            previous.LostFocus -= OnEditorLostFocus;
            previous.SelectionChanged -= OnEditorSelectionChanged;
        }
        CloseMention();
        _mentionArmed = false;
        _editor = editor;
        _editorPlaceholder = editor.PlaceholderText;
        _composing = false;
        // The ONLY application writer of editor.Text: guarded assign + actual read-back inside the sync.
        _sync = new OpenCodeComposerEditorSync(_source, () => editor.Text, text =>
            {
                _guardedWrite = true; // A guarded application write is never a typed "@" trigger.
                _mentionArmed = false;
                try
                {
                    editor.Text = text;
                    editor.SelectionStart = (editor.Text ?? string.Empty).Length;
                }
                finally { _guardedWrite = false; }
            }, () => _composing, ApplyEditorInput);
        // Early, synchronous input observation (records only; no rendering, flyouts, focus or tree changes).
        editor.TextChanging += OnEditorTextChanging;
        editor.TextCompositionStarted += OnEditorCompositionStarted;
        editor.TextCompositionEnded += OnEditorCompositionEnded;
        editor.LostFocus += OnEditorLostFocus;
        editor.SelectionChanged += OnEditorSelectionChanged;
        // Whatever the ledger already holds (including attach-time editor text) is pre-existing, never a slash intent.
        _menuSession.Initialize(_sync.InputRevision);
        _lifetime.OnEditorAttached(); // Fences callbacks/handoffs captured for a previous editor, then renders.
        if (_lifetime.IsLive) BeginCatalogLifetime(); // The previous capability was ended by the reattach invalidation.
    }

    private void BeginCatalogLifetime()
    {
        EndCatalogLifetime();
        if (_beginCatalogLifetime is null) return;
        _catalogLifetime = _beginCatalogLifetime();
        _menuSession.Lifetime = _catalogLifetime;
    }

    private void EndCatalogLifetime()
    {
        var lifetime = _catalogLifetime;
        _catalogLifetime = null;
        _menuSession.Lifetime = null;
        if (lifetime is not null) _endCatalogLifetime?.Invoke(lifetime); // Exactly the capability this view began.
    }

    private OpenCodeComposerEditorAck? ApplyEditorInput(OpenCodeComposerToken basis, string text, bool allowCarry)
    {
        var args = new OpenCodeComposerTextEditedEventArgs(basis, text) { AllowUnboundCarry = allowCarry };
        TextEdited?.Invoke(this, args);
        return args.Acknowledgment; // Proof of acceptance; never a later snapshot comparison.
    }

    private void OnEditorTextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
    {
        if (!ReferenceEquals(sender, _editor)) return;
        _inputEvidence.RecordTextChanging(); // Ledger record (unchanged logic) + synchronous C#-only evidence publication.
        if (!_guardedWrite) _mentionArmed = true; // A real user edit may open "@" candidates on the following TextChanged.
    }

    private void OnEditorCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
    {
        if (!ReferenceEquals(sender, _editor)) return;
        _composing = true;
        _inputEvidence.CompositionChanged();
    }

    // Composition end / focus loss are WAKE-UPS only: every check re-runs; nothing is authorised by them.
    private void OnEditorCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
    {
        if (!ReferenceEquals(sender, _editor)) return;
        _composing = false;
        _inputEvidence.CompositionChanged();
        WakeAfterComposition();
    }

    private void OnEditorLostFocus(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _editor) || !_composing) return;
        _composing = false;
        _inputEvidence.CompositionChanged();
        WakeAfterComposition();
    }

    private void WakeAfterComposition()
    {
        if (!_lifetime.IsLive) return;
        _handoffs.Wake();
        RequestRender();
    }

    /// <summary>
    /// UI thread. Flushes recorded input with its original token. False while composing, or while recorded input
    /// is pending/held (stale or unbound) — the caller must not submit. Never infers input from text differences.
    /// </summary>
    internal bool PrepareSubmission()
    {
        if (_sync is null || !_lifetime.IsLive || !_dispatcher.HasThreadAccess) return false;
        var ready = _sync.Prepare();
        _inputEvidence.Refresh();
        if (!ready) RequestRender();
        return ready;
    }

    /// <summary>Revoke view-owned gestures, not draft data or pending input. Slot activation separately
    /// adopts a clean ledger; a forced recovery separately suspends it without overwriting visible text.</summary>
    internal void RevokeSlotGestures()
    {
        if (_menuShown) _menuSession.Dismiss(this, _menuSession.Capture());
        CloseMention();
        _addButton.Flyout?.Hide();
        _discardFlyout.Hide();
        _picking = false;
        _localNote = null;
        _localNoteToken = null;
        _lifetime.OnEditorAttached(); // Existing generation fences queued UI and accepted-draft handoffs.
    }

    internal void BeginDraftSlotTransition() => _slotTransitionDepth++;
    internal void EndDraftSlotTransition()
    {
        if (_slotTransitionDepth > 0) _slotTransitionDepth--;
        if (_slotTransitionDepth == 0) RequestRender();
    }

    internal bool TryActivateDraftSlot(OpenCodeComposerToken activated, int start, int length)
    {
        if (_sync is null || !_sync.TryActivateClean(activated)) return false;
        _slotUnavailableReason = null;
        if (_editor is { } editor)
        {
            var count = (editor.Text ?? string.Empty).Length;
            start = Math.Clamp(start, 0, count);
            editor.Select(start, Math.Clamp(length, 0, count - start));
        }
        BeginCatalogLifetime();
        _inputEvidence.Refresh();
        RotatePlaceholderSplash(); // Session opened/switched or blank session ready: the ONLY splash trigger.
        return true;
    }

    /// <summary>
    /// Choose the next placeholder splash for the context just activated. Placeholder-only: no editor text, draft,
    /// selection or input event is touched; the pending render applies it (a staged command's argument hint still wins).
    /// </summary>
    private void RotatePlaceholderSplash()
    {
        var s = _source();
        _splash.ObserveContext(boundSession: s.IsActive && !s.IsUnboundDraft, blankSession: s.IsActive && s.IsUnboundDraft);
    }

    internal OpenCodeComposerEditorRecovery SuspendDraftSlotForRecovery(string reason)
    {
        _slotUnavailableReason = reason;
        var recovery = _sync?.SuspendForRecovery() ?? new(_editor?.Text ?? string.Empty, OpenCodeComposerInputState.HeldStale, _composing);
        if (_editor is { } editor) { editor.IsReadOnly = true; _editorReadOnlyApplied = true; }
        _inputEvidence.Refresh();
        return recovery;
    }

    internal void ResolveDraftSlotRecovery()
    {
        _sync?.ResolveRecovery(); // Explicit resolution only; no write/adoption occurs here.
        _slotUnavailableReason = null;
        _inputEvidence.Refresh();
    }

    internal bool TryPresentDraftSlotRecovery(string text)
    {
        var presented = _sync?.TryPresentRecovery(text) == true;
        _inputEvidence.Refresh();
        return presented;
    }

    internal bool PrepareDraftSlotPageUnload()
    {
        if (_sync is null || _composing || _sync.IsSuspendedForRecovery || !_dispatcher.HasThreadAccess) return false;
        if (_lifetime.IsLive) return PrepareClientTextApplication();
        // Child Unloaded may precede Page Unloaded. Never flush/reconcile/write an unloaded editor:
        // only clean, identical native/core text in the same activation is safe to capture.
        // A completed attachment edit may precede its queued render; capture its CURRENT core token/data,
        // without adopting the older text baseline as input/send authority or changing that baseline.
        var snapshot = _source();
        return _sync.State == OpenCodeComposerInputState.Clean && _sync.BaselineToken.Scope == snapshot.Token.Scope
            && _sync.BaselineToken.Draft == snapshot.Token.Draft
            && _editor is { } editor && string.Equals(editor.Text, _sync.EditorText, StringComparison.Ordinal)
            && (_sync.BaselineToken == snapshot.Token || string.Equals(editor.Text, snapshot.Text, StringComparison.Ordinal));
    }

    internal void ShowUnavailableDraftSlot(string reason)
    {
        _slotUnavailableReason = reason;
        if (_sync is { State: OpenCodeComposerInputState.Clean, IsSuspendedForRecovery: false } && !_composing)
            _sync.WriteSnapshot(_source()); // The REAL empty suspension snapshot, through the existing guard.
        if (_editor is { } editor) { editor.IsReadOnly = true; _editorReadOnlyApplied = true; }
        _inputEvidence.Refresh();
    }

    /// <summary>
    /// <see cref="OpenCodeComposerAcceptedDraftHandoff"/> implementation for the page bridge. Dispatches to this
    /// view's UI thread (generation captured now), flushes recorded input, keeps held input, waits for an unchanged
    /// draft's active composition (composition end only wakes a recheck), then calls <paramref name="finalize"/> and
    /// applies a cleared snapshot through the guarded writer in the same UI turn. Unload, reattachment, failed
    /// dispatch or <paramref name="lifetimeToken"/> abandon local clearing. The task never faults.
    /// </summary>
    internal Task HandoffAcceptedDraftAsync(OpenCodeComposerToken submittedToken,
        Func<OpenCodeComposerDraftFinalization> finalize, CancellationToken lifetimeToken)
        => _handoffs.Begin(submittedToken, finalize, lifetimeToken);

    /// <summary>Thread-safe, coalesced. Renders the controller's CURRENT snapshot on the UI thread.</summary>
    internal void RequestRender() => _lifetime.RequestRender();

    /// <summary>UI thread only. Prefer <see cref="RequestRender"/> from controller events.</summary>
    internal void Render(OpenCodeComposerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_dispatcher.HasThreadAccess) { RequestRender(); return; }
        if (_slotTransitionDepth != 0) return; // Core CAS publication cannot paint an intermediate old ledger.
        if (!_lifetime.IsLive) return; // Unloaded: no editor writes, events or focus; resume re-renders.
        _snapshot = snapshot;
        if (_localNoteToken is { } noteToken && noteToken != snapshot.Token) { _localNote = null; _localNoteToken = null; }
        // _picking is cleared only by ReportPickOutcome (a render queued before BeginPick must not clear it).

        var enhanced = snapshot.SelectedCommand is not null || snapshot.Attachments.Count != 0;
        var inactiveBound = snapshot.IsBoundDraft && !snapshot.IsActive;
        var canEdit = snapshot.IsActive && !snapshot.IsBusy;

        ReconcileEditor(snapshot, inactiveBound);
        // Newest acknowledged USER observation (watermarked; programmatic writes never produce one).
        if (_menuSession.EvaluateAcknowledged(this))
        {
            snapshot = _source();
            _snapshot = snapshot;
            enhanced = snapshot.SelectedCommand is not null || snapshot.Attachments.Count != 0;
            inactiveBound = snapshot.IsBoundDraft && !snapshot.IsActive;
            canEdit = snapshot.IsActive && !snapshot.IsBusy;
        }

        // "+" menu
        var full = snapshot.Attachments.Count >= OpenCodeAttachment.MaxFiles;
        var mentionAvailable = CurrentMentionSearch() is not null;
        _commandItem.IsEnabled = OpenCodeComposerPlusMenu.IsEnabled(OpenCodeComposerPlusMenuKind.Command, canEdit, full, MenuWanted(snapshot), mentionAvailable);
        _attachItem.IsEnabled = OpenCodeComposerPlusMenu.IsEnabled(OpenCodeComposerPlusMenuKind.Attach, canEdit, full, MenuWanted(snapshot), mentionAvailable);
        _contextItem.IsEnabled = OpenCodeComposerPlusMenu.IsEnabled(OpenCodeComposerPlusMenuKind.Context, canEdit, full, MenuWanted(snapshot), mentionAvailable)
            && _editor is { IsReadOnly: false };
        ToolTipService.SetToolTip(_contextItem, mentionAvailable ? OpenCodeComposerPlusMenu.ContextTooltip : OpenCodeComposerPlusMenu.ContextUnavailableTooltip);
        _terminalItem.IsEnabled = OpenCodeComposerPlusMenu.IsEnabled(OpenCodeComposerPlusMenuKind.Terminal, canEdit, full, MenuWanted(snapshot), mentionAvailable);
        if ((!canEdit || !mentionAvailable) && _mentionShown) CloseMention();
        _attachItem.Text = full ? "添加附件（已达 10 个上限）" : "添加附件";
        ToolTipService.SetToolTip(_attachItem, full
            ? "已达到 10 个附件上限；先移除部分附件再添加。"
            : "图片、文本、Markdown、PDF 或 JSON（最多 10 个；单个不超过 10 MB，合计不超过 20 MB）");
        var discard = (enhanced || snapshot.IsBoundDraft) && !inactiveBound ? Visibility.Visible : Visibility.Collapsed;
        _discardItem.Visibility = _discardSeparator.Visibility = discard;
        _discardItem.IsEnabled = !snapshot.IsBusy;
        _addButton.IsEnabled = _commandItem.IsEnabled || _attachItem.IsEnabled || _contextItem.IsEnabled || (discard == Visibility.Visible && _discardItem.IsEnabled);
        long total = 0;
        foreach (var a in snapshot.Attachments) total += a.Bytes;
        _summaryText.Text = OpenCodeComposerInputPolicy.AttachmentSummary(snapshot.Attachments.Count, total,
            OpenCodeAttachment.MaxFiles, OpenCodeAttachment.MaxTotalBytes);
        _summaryText.Visibility = _summaryText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        RenderChips(snapshot, canEdit);
        RenderStatus(snapshot, enhanced, inactiveBound);
        RenderMenu(_menuSession.Present(snapshot));
        // Collapsed when empty so the card shows only the editor row (no phantom gap above it).
        var any = _chipRow.Visibility == Visibility.Visible || _intentHint.Visibility == Visibility.Visible
            || _statusBar.Visibility == Visibility.Visible || _menuShown || _mentionShown;
        Margin = new Thickness(0, 0, 0, any ? 8 : 0);
        _inputEvidence.Refresh(); // After the render's flush/reconcile (no new observation).

        var fingerprint = Fingerprint(snapshot, _menuShown) + "|" + _sync?.State;
        if (fingerprint != _fingerprint)
        {
            _fingerprint = fingerprint;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool MenuWanted(OpenCodeComposerSnapshot s) => s.IsActive && s.CatalogState != OpenCodeComposerCatalogState.Closed;

    // ------------------------------------------------------------------
    // Editor reconciliation (the page's TextBox remains the only text surface)
    // ------------------------------------------------------------------

    private void ReconcileEditor(OpenCodeComposerSnapshot s, bool inactiveBound)
    {
        if (_editor is null || _sync is null) return;
        // Ledger reconciliation (phase3-sync-protocol §3): flush recorded input with its own token; only when no
        // unresolved input remains, adopt the current token or write controller text through the guard. A write
        // deferred by an active composition is retried by the composition-end wake-up (no timer).
        _sync.Reconcile();

        // Inactive bound drafts and held-stale input are shown read-only; reading a picked batch also blocks edits
        // that would invalidate the pick ticket. Legacy sending never touches this flag.
        var readOnly = !s.IsActive || _slotUnavailableReason is not null || inactiveBound
            || _sync.State == OpenCodeComposerInputState.HeldStale || (s.IsBusy && _picking);
        if (readOnly != _editorReadOnlyApplied)
        {
            _editorReadOnlyApplied = readOnly;
            _editor.IsReadOnly = readOnly;
        }
        // Precedence: staged command argument hint > splash (active) > neutral base placeholder (inactive/read-only).
        var placeholder = OpenCodeComposerSplash.Resolve(s.SelectedCommand, s.IsActive, _splash.Current.Text, _editorPlaceholder ?? string.Empty);
        if (!string.Equals(_editor.PlaceholderText, placeholder, StringComparison.Ordinal)) _editor.PlaceholderText = placeholder;
    }

    /// <summary>Page calls this from its existing TextChanged handler.</summary>
    internal void NotifyEditorTextChanged()
    {
        // Flushes RECORDED input only (TextChanging ledger); an echo of our own guarded write finds nothing.
        if (_sync is null || !_lifetime.IsLive) return;
        var flushed = _sync.Flush();
        _inputEvidence.Refresh();
        if (flushed != OpenCodeComposerInputState.Clean) { CloseMention(); RequestRender(); return; } // Show held status.
        // TextChanged (not TextChanging): the tree may change. Evaluate the acknowledged observation and keep a visible
        // menu's filter in step with the text, so key evidence is captured against what the user actually sees.
        if (_menuSession.EvaluateAcknowledged(this) || _menuShown) Render(_source());
        EvaluateMentionTrigger(); // After the command menu settled: the two lists are never shown together.
    }

    /// <summary>
    /// UI thread. Before a page-owned client-text application (EditDraft Apply / Undo restoration): flush recorded input
    /// and reconcile exactly like a submission boundary. False while composing or with pending/held input — do not apply.
    /// After <c>TryApplyClientText</c> returns an acknowledgment, call <see cref="Render"/>/<see cref="RequestRender"/>:
    /// the existing guarded reconciliation writes the accepted text into the editor (no new ledger).
    /// </summary>
    internal bool PrepareClientTextApplication() => PrepareSubmission();

    /// <summary>Flush before any token-bearing intent so recorded input is not stranded on an older token.</summary>
    private bool FlushForIntent()
    {
        if (_sync is null) return true;
        var clean = _sync.Flush() == OpenCodeComposerInputState.Clean;
        _inputEvidence.Refresh();
        return clean;
    }

    /// <summary>DispatcherQueue adapter for the WinUI-free lifetime fence.</summary>
    private sealed class DispatcherScheduler(DispatcherQueue dispatcher) : IOpenCodeComposerUiScheduler
    {
        public bool TryEnqueue(Action callback) => dispatcher.TryEnqueue(() => callback());
    }

    // ------------------------------------------------------------------
    // Keyboard
    // ------------------------------------------------------------------

    /// <summary>
    /// Run the composer key policy for one editor KeyDown/PreviewKeyDown. Menu navigation/acceptance/
    /// dismissal and explicit catalog opening are executed here; Submit is returned to the page,
    /// which routes it (legacy text sender vs controller RequestSubmit). Never returns Stop.
    /// </summary>
    internal OpenCodeComposerKeyDecision HandleEditorKey(KeyRoutedEventArgs e, bool shift, bool isComposing,
        TimeSpan sinceCompositionEnded, bool enterLatched, bool pageBusy)
    {
        ArgumentNullException.ThrowIfNull(e);
        var key = e.Key switch
        {
            VirtualKey.Enter => OpenCodeComposerKey.Enter,
            VirtualKey.Up => OpenCodeComposerKey.Up,
            VirtualKey.Down => OpenCodeComposerKey.Down,
            VirtualKey.Escape => OpenCodeComposerKey.Escape,
            VirtualKey.Tab => OpenCodeComposerKey.Tab,
            VirtualKey.Divide or (VirtualKey)191 => OpenCodeComposerKey.Slash,
            _ => OpenCodeComposerKey.Other,
        };
        if (key == OpenCodeComposerKey.Other) return OpenCodeComposerKeyDecision.Pass;
        if (!_lifetime.IsLive || !_dispatcher.HasThreadAccess)
        {
            // No live view: never let a key reach Send through a menu we cannot verify.
            if (key == OpenCodeComposerKey.Enter && !shift && !isComposing) { e.Handled = true; return new(OpenCodeComposerKeyAction.Swallow, true, true); }
            return OpenCodeComposerKeyDecision.Pass;
        }
        if (_mentionShown && HandleMentionKey(key, shift, isComposing, sinceCompositionEnded, enterLatched) is { } mention)
        {
            if (mention.Handled) e.Handled = true;
            return mention;
        }

        // Shared boundary (OpenCodeComposerCommandMenuSession.HandleKey): capture the displayed menu evidence at arrival,
        // flush, evaluate the acknowledged observation, recompute the presentation, then decide. Stale/closed/loading/
        // failed/empty/no-match visible menus consume and latch Enter; a staged native intent turns Submit into an
        // explicit invocation event (Swallow + latch), so the page's submit route never sees it.
        var (decision, outcome) = _menuSession.HandleKey(this, key, shift, key == OpenCodeComposerKey.Slash && IsControlDown(),
            isComposing, sinceCompositionEnded, enterLatched, pageBusy);
        if (decision.Handled) e.Handled = true;
        if (outcome == OpenCodeComposerMenuKeyOutcome.Opened) _localNote = null;
        Render(_source());
        return decision;
    }

    private static bool IsControlDown()
    {
        try
        {
            var state = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        }
        catch
        {
            return false; // Fail closed: no shortcut, the key types normally.
        }
    }

    // ------------------------------------------------------------------
    // Picker hand-off (adapter runs in the page with the owner Window)
    // ------------------------------------------------------------------

    private void RequestPick()
    {
        FlushForIntent();
        var fresh = _source();
        if (!fresh.IsActive || fresh.IsBusy) return;
        CloseMention();
        if (_menuShown) _menuSession.Dismiss(this, _menuSession.Capture());
        _picking = true;
        _localNote = null;
        PickRequested?.Invoke(this, new(_source().Token));
    }

    /// <summary>Page reports the adapter outcome. Never submits. Restores focus only if nothing else took it.</summary>
    internal void ReportPickOutcome(OpenCodeComposerPickOutcome outcome)
    {
        _picking = false;
        if (!_lifetime.IsLive) return; // Late picker completion after unload: no render, note or focus.
        var note = outcome switch
        {
            OpenCodeComposerPickOutcome.MissingOwner => "无法打开文件选择器：找不到所属窗口。未添加附件。",
            OpenCodeComposerPickOutcome.Failed => "文件选择器未能打开；原附件保持不变。",
            OpenCodeComposerPickOutcome.NotAllowed => "当前不能添加附件（会话已切换或正在处理）。",
            _ => null,
        };
        var fresh = _source();
        _localNote = note;
        _localNoteToken = note is null ? null : fresh.Token;
        Render(fresh);
        RestoreEditorFocus();
    }

    private void RestoreEditorFocus()
    {
        if (_editor is null || !_lifetime.IsLive || !IsLoaded || XamlRoot is null || !ReferenceEquals(_editor.XamlRoot, XamlRoot)
            || !_editor.IsEnabled || _menuShown || _isComposing()) return;
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        if (focused is null || ReferenceEquals(focused, _addButton) || focused is MenuFlyoutItem) _editor.Focus(FocusState.Programmatic);
    }

    // ------------------------------------------------------------------
    // Catalog menu
    // ------------------------------------------------------------------

    private void RequestCatalog()
    {
        _localNote = null;
        _menuSession.RequestExplicit(this); // Flushes first; explicit open clears the episode suppression.
        Render(_source());
    }

    private void DismissCatalog(bool focusEditor)
    {
        _menuSession.Dismiss(this, _menuSession.Capture()); // The displayed handle only.
        Render(_source());
        if (focusEditor && _editor is { IsEnabled: true }) _editor.Focus(FocusState.Programmatic);
    }

    private void Accept(OpenCodeComposerMenuEvidence evidence)
    {
        // Evidence was captured when this row was rendered; TrySelect flushes, recomputes and re-verifies it.
        var outcome = _menuSession.TrySelect(this, evidence);
        Render(_source());
        if (_editor is { IsEnabled: true }) _editor.Focus(FocusState.Programmatic);
        if (outcome == OpenCodeComposerMenuKeyOutcome.SelectedSubmit) SubmitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RunClientIntent()
    {
        var intent = _snapshot?.ClientIntent;
        if (intent is null) return;
        _menuSession.TryInvoke(this, intent.Id); // A fresh explicit gesture; the page takes the ticket.
        Render(_source());
    }

    private void RemoveClientIntent()
    {
        if (_snapshot?.ClientIntent is { } intent) _menuSession.DismissIntent(this, intent.Id);
        Render(_source());
        if (_editor is { IsEnabled: true }) _editor.Focus(FocusState.Programmatic);
    }

    private void ApplyHighlight(OpenCodeComposerMenuPresentation p)
    {
        var index = p.HighlightIndex;
        _applyingHighlight = true;
        try
        {
            index = index >= 0 && index < _rowItemIndex.Length ? _rowItemIndex[index] : -1;
            if (index < 0 || index >= _menuList.Items.Count) { _menuList.SelectedIndex = -1; return; }
            if (_menuList.SelectedIndex == index) return;
            _menuList.SelectedIndex = index;
            _menuList.ScrollIntoView(_menuList.Items[index]);
            if (_menuList.Items[index] is ListViewItem item)
            {
                var peer = FrameworkElementAutomationPeer.FromElement(item) ?? FrameworkElementAutomationPeer.CreatePeerForElement(item);
                peer?.RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
            }
        }
        finally { _applyingHighlight = false; }
    }

    private void RenderMenu(OpenCodeComposerMenuPresentation p)
    {
        if (!p.Visible)
        {
            if (_menuShown)
            {
                _menuSlot.Children.Clear();
                _menuSlot.Visibility = Visibility.Collapsed;
                _menuShown = false;
                if (_editor is not null) AutomationProperties.GetControlledPeers(_editor).Remove(_menuList);
            }
            _renderedHandle = null;
            _renderedRows = Array.Empty<OpenCodeComposerMenuRow>();
            _rowItemIndex = Array.Empty<int>();
            _menuList.Items.Clear();
            return;
        }
        if (!_menuShown)
        {
            CloseMention(); // The command catalog and the "@" candidates are mutually exclusive.
            _menuSlot.Visibility = Visibility.Visible;
            _menuSlot.Children.Add(_menu);
            _menuShown = true;
            if (_editor is not null && !AutomationProperties.GetControlledPeers(_editor).Contains(_menuList))
                AutomationProperties.GetControlledPeers(_editor).Add(_menuList);
        }

        _menuRing.IsActive = p.Status == OpenCodeComposerMenuStatus.Loading;
        _menuRing.Visibility = _menuRing.IsActive ? Visibility.Visible : Visibility.Collapsed;
        _menuRetry.Visibility = p.Status == OpenCodeComposerMenuStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
        _menuRetry.IsEnabled = _snapshot is { IsBusy: false };
        ToolTipService.SetToolTip(_menuRetry, "重新读取服务器命令");

        // Rebuild only when the opening or the shown rows changed (value equality), so the highlight/focus stay stable.
        if (!ReferenceEquals(_renderedHandle, p.Handle) || !_renderedRows.SequenceEqual(p.Rows))
        {
            _renderedHandle = p.Handle;
            _renderedRows = p.Rows;
            _menuList.Items.Clear();
            _rowItemIndex = new int[p.Rows.Count];
            OpenCodeComposerCommandOrigin? group = null;
            for (var i = 0; i < p.Rows.Count; i++)
            {
                var row = p.Rows[i];
                if (group != row.Origin)
                {
                    // Rows arrive grouped (AIFrontend first, then server); each group gets one non-selectable header.
                    group = row.Origin;
                    _menuList.Items.Add(GroupHeader(row.Origin));
                }
                _rowItemIndex[i] = _menuList.Items.Count;
                _menuList.Items.Add(MenuItem(row, new OpenCodeComposerMenuEvidence(true, p.Handle, p.Token, row.Identity)));
            }
        }
        else
        {
            // Same rows: refresh the token each row's evidence carries (a later edit keeping the same filter is a new basis).
            for (var i = 0; i < _rowItemIndex.Length && i < p.Rows.Count; i++)
                if (_rowItemIndex[i] < _menuList.Items.Count && _menuList.Items[_rowItemIndex[i]] is FrameworkElement item)
                {
                    var evidence = new OpenCodeComposerMenuEvidence(true, p.Handle, p.Token, p.Rows[i].Identity);
                    item.Tag = evidence;
                    if (item is ContentControl { Content: FrameworkElement content }) content.Tag = evidence;
                }
        }
        ApplyHighlight(p);
        _menuList.Visibility = p.Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _menuStatus.Text = _menuSession.Note ?? p.StatusText;
    }

    // ------------------------------------------------------------------
    // "@" context candidates (B7). A literal "@" at a line start or after whitespace (OpenCodeComposerCharTriggers) opens
    // a list whose rows come ONLY from the server file index the page provides (GET api/fs/find / api/fs/list).
    // Choosing a row edits the editor like typing/paste (native TextBox → TextChanging ledger); it never submits.
    // ------------------------------------------------------------------

    /// <summary>
    /// Page-provided real file-index search for the CURRENT workspace, or null when there is no client/directory
    /// (then "上下文（@）" is disabled with an honest tooltip and a typed "@" stays plain text).
    /// </summary>
    internal Func<OpenCodeComposerMentionSearch?>? MentionSearchProvider { get; set; }

    private Grid _mentionSlot = null!;
    private Border _mentionPanel = null!;
    private ListView _mentionList = null!;
    private TextBlock _mentionQueryText = null!;
    private TextBlock _mentionStatus = null!;
    private ProgressRing _mentionRing = null!;
    private bool _mentionShown;
    private bool _mentionArmed;
    private bool _guardedWrite;
    private bool _applyingMentionHighlight;
    private int _mentionHighlight = -1;
    private OpenCodeComposerMentionTrigger _mentionTrigger = OpenCodeComposerMentionTrigger.None;
    private IReadOnlyList<OpenCodeComposerMentionCandidate> _mentionRows = Array.Empty<OpenCodeComposerMentionCandidate>();
    private readonly OpenCodeComposerMentionQueries _mentionQueries = new();

    /// <summary>Whether the "@" candidate list is visible (UI thread).</summary>
    internal bool IsMentionVisible => _mentionShown;

    private OpenCodeComposerMentionSearch? CurrentMentionSearch()
    {
        try { return MentionSearchProvider?.Invoke(); }
        catch { return null; } // A failing provider only disables "@"; it never breaks rendering.
    }

    private Grid BuildMentionPopup()
    {
        var glyph = new FontIcon { Glyph = "\uE8A5", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
        var title = new TextBlock { Text = "上下文", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _mentionQueryText = new TextBlock
        {
            FontFamily = MonoFont,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 260,
            Opacity = 0.8,
        };
        _mentionRing = new ProgressRing { Width = 14, Height = 14, IsActive = false, Visibility = Visibility.Collapsed };
        _mentionStatus = Caption(string.Empty, 0.7);
        _mentionStatus.VerticalAlignment = VerticalAlignment.Center;
        _mentionStatus.TextWrapping = TextWrapping.NoWrap;
        _mentionStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetLiveSetting(_mentionStatus, AutomationLiveSetting.Polite);
        var close = ChipRemoveButton("OpenCodeComposerMentionClose");
        AutomationProperties.SetName(close, "关闭上下文候选");
        ToolTipService.SetToolTip(close, "关闭（Esc）");
        close.Click += (_, _) => { CloseMention(); if (_editor is { IsEnabled: true }) _editor.Focus(FocusState.Programmatic); };
        var header = new Grid { ColumnSpacing = 8, Padding = new Thickness(12, 8, 6, 6) };
        for (var i = 0; i < 5; i++)
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(header, glyph, 0);
        Place(header, title, 1);
        Place(header, _mentionQueryText, 2);
        Place(header, _mentionStatus, 3);
        Place(header, _mentionRing, 4);
        Place(header, close, 5);

        _mentionList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            MaxHeight = 232,
            Padding = new Thickness(0, 0, 0, 4),
        };
        AutomationProperties.SetAutomationId(_mentionList, "OpenCodeComposerMentionList");
        AutomationProperties.SetName(_mentionList, "上下文文件候选");
        _mentionList.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is FrameworkElement { Tag: OpenCodeComposerMentionCandidate candidate })
                AcceptMention(IndexOfMention(candidate));
        };
        _mentionList.SelectionChanged += (_, _) =>
        {
            if (_applyingMentionHighlight) return;
            if (_mentionList.SelectedItem is FrameworkElement { Tag: OpenCodeComposerMentionCandidate candidate })
                _mentionHighlight = IndexOfMention(candidate);
        };
        _mentionList.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Escape) return;
            e.Handled = true;
            CloseMention();
            if (_editor is { IsEnabled: true }) _editor.Focus(FocusState.Programmatic);
        };

        var footer = Caption("↑↓ 选择 · Enter / Tab 插入路径文本 · Esc 关闭 · 不上传文件", 0.6);
        footer.Margin = new Thickness(12, 0, 12, 8);
        footer.TextWrapping = TextWrapping.NoWrap;
        footer.TextTrimming = TextTrimming.CharacterEllipsis;

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_mentionList);
        stack.Children.Add(footer);
        _mentionPanel = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Style = ThemedBorder("LayerFillColorDefaultBrush", "CardStrokeColorDefaultBrush"),
            Child = stack,
        };
        AutomationProperties.SetAutomationId(_mentionPanel, "OpenCodeComposerMention");
        AutomationProperties.SetName(_mentionPanel, "上下文文件候选");
        var slot = new Grid { Visibility = Visibility.Collapsed };
        slot.ChildrenTransitions = new TransitionCollection { new EntranceThemeTransition { FromVerticalOffset = 8 } };
        return slot;
    }

    private int IndexOfMention(OpenCodeComposerMentionCandidate candidate)
    {
        for (var i = 0; i < _mentionRows.Count; i++) if (ReferenceEquals(_mentionRows[i], candidate)) return i;
        return -1;
    }

    private OpenCodeComposerMentionTrigger MentionAtCaret()
    {
        if (_editor is not { } editor || editor.SelectionLength != 0) return OpenCodeComposerMentionTrigger.None;
        return OpenCodeComposerCharTriggers.FindMention(editor.Text, editor.SelectionStart);
    }

    // Caret moves never OPEN candidates; they only close a list whose "@" token is no longer under the caret.
    private void OnEditorSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _editor) || !_mentionShown) return;
        if (MentionAtCaret() is not { Present: true } trigger || trigger.Start != _mentionTrigger.Start) CloseMention();
    }

    /// <summary>
    /// After a flushed TextChanged: a USER edit (TextChanging outside the guarded writer) leaving a legal "@" token under
    /// the caret opens/refreshes the list; anything else closes it. Never while composing, read-only, busy, or while the
    /// command catalog is shown.
    /// </summary>
    private void EvaluateMentionTrigger()
    {
        var armed = _mentionArmed;
        _mentionArmed = false;
        if (!armed && !_mentionShown) return;
        var s = _source();
        if (_editor is not { } editor || _composing || editor.IsReadOnly || _menuShown || !s.IsActive || s.IsBusy || _picking)
        {
            CloseMention();
            return;
        }
        var trigger = MentionAtCaret();
        if (!trigger.Present || (!armed && trigger.Start != _mentionTrigger.Start)) { CloseMention(); return; }
        if (CurrentMentionSearch() is not { } search) { CloseMention(); return; }
        if (_mentionShown && trigger == _mentionTrigger) return;
        _mentionTrigger = trigger;
        ShowMention();
        _ = QueryMentionsAsync(search, trigger.Query);
    }

    private void ShowMention()
    {
        _mentionQueryText.Text = "@" + OpenCodeComposerInputPolicy.Bound(_mentionTrigger.Query, 48);
        if (_mentionShown) return;
        _mentionShown = true;
        _mentionSlot.Visibility = Visibility.Visible;
        _mentionSlot.Children.Add(_mentionPanel);
        if (_editor is not null && !AutomationProperties.GetControlledPeers(_editor).Contains(_mentionList))
            AutomationProperties.GetControlledPeers(_editor).Add(_mentionList);
        RequestRender(); // Margin/eligibility follow the visible list.
    }

    private void CloseMention()
    {
        _mentionQueries.Cancel();
        _mentionRows = Array.Empty<OpenCodeComposerMentionCandidate>();
        _mentionHighlight = -1;
        _mentionTrigger = OpenCodeComposerMentionTrigger.None;
        if (!_mentionShown) return;
        _mentionShown = false;
        _mentionSlot.Children.Clear();
        _mentionSlot.Visibility = Visibility.Collapsed;
        _mentionList.Items.Clear();
        if (_editor is not null) AutomationProperties.GetControlledPeers(_editor).Remove(_mentionList);
        RequestRender();
    }

    private async Task QueryMentionsAsync(OpenCodeComposerMentionSearch search, string query)
    {
        // Never allow an old row to be accepted while a different query is pending.
        _mentionRows = Array.Empty<OpenCodeComposerMentionCandidate>();
        _mentionHighlight = -1;
        _mentionList.Items.Clear();
        SetMentionStatus(loading: true, "正在读取文件索引…");
        var result = await _mentionQueries.QueryAsync(search, query);
        if (result is null) return;
        void Apply()
        {
            if (!_mentionQueries.IsCurrent(result) || !_mentionShown || !_lifetime.IsLive) return;
            ApplyMentionRows(result.Rows, result.Error, query);
        }
        if (_dispatcher.HasThreadAccess) Apply(); else _dispatcher.TryEnqueue(Apply);
    }

    private void SetMentionStatus(bool loading, string text)
    {
        _mentionRing.IsActive = loading;
        _mentionRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        _mentionStatus.Text = text;
    }

    private void ApplyMentionRows(IReadOnlyList<OpenCodeComposerMentionCandidate> rows, string? failure, string query)
    {
        _mentionRows = rows;
        _mentionList.Items.Clear();
        foreach (var row in rows) _mentionList.Items.Add(MentionItem(row));
        _mentionList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetMentionStatus(false, failure ?? (rows.Count > 0
            ? (query.Length == 0 ? "工作区根目录" : $"{rows.Count} 个匹配")
            : (query.Length == 0 ? "工作区根目录为空" : "没有匹配的文件或文件夹")));
        _mentionHighlight = rows.Count > 0 ? 0 : -1;
        ApplyMentionHighlight();
    }

    private ListViewItem MentionItem(OpenCodeComposerMentionCandidate candidate)
    {
        var slash = candidate.Path.LastIndexOf('/');
        var name = slash >= 0 ? candidate.Path[(slash + 1)..] : candidate.Path;
        var parent = slash > 0 ? candidate.Path[..slash] : string.Empty;
        var icon = new FontIcon
        {
            Glyph = candidate.IsDirectory ? "\uE8B7" : "\uE8A5",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = candidate.IsDirectory ? 0.9 : 0.75,
        };
        var title = new TextBlock
        {
            Text = OpenCodeComposerInputPolicy.Bound(name, 64) + (candidate.IsDirectory ? "/" : string.Empty),
            FontFamily = MonoFont,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 5, 0, 5), Tag = candidate };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Place(row, icon, 0);
        Place(row, title, 1);
        if (parent.Length > 0)
        {
            var where = Caption(OpenCodeComposerInputPolicy.Bound(parent, 96), 0.6);
            where.FontFamily = MonoFont;
            where.TextWrapping = TextWrapping.NoWrap;
            where.TextTrimming = TextTrimming.CharacterEllipsis;
            where.VerticalAlignment = VerticalAlignment.Center;
            Place(row, where, 2);
        }
        var item = new ListViewItem { Content = row, Tag = candidate, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(item, (candidate.IsDirectory ? "文件夹 " : "文件 ") + candidate.Path);
        ToolTipService.SetToolTip(item, candidate.Path);
        return item;
    }

    private void ApplyMentionHighlight()
    {
        _applyingMentionHighlight = true;
        try
        {
            var index = _mentionHighlight;
            if (index < 0 || index >= _mentionList.Items.Count) { _mentionList.SelectedIndex = -1; return; }
            _mentionList.SelectedIndex = index;
            _mentionList.ScrollIntoView(_mentionList.Items[index]);
            if (_mentionList.Items[index] is ListViewItem item)
            {
                var peer = FrameworkElementAutomationPeer.FromElement(item) ?? FrameworkElementAutomationPeer.CreatePeerForElement(item);
                peer?.RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
            }
        }
        finally { _applyingMentionHighlight = false; }
    }

    /// <summary>
    /// Keys while the "@" list is visible. Enter never sends, including loading/empty/error states.
    /// </summary>
    private OpenCodeComposerKeyDecision? HandleMentionKey(OpenCodeComposerKey key, bool shift, bool isComposing,
        TimeSpan sinceCompositionEnded, bool enterLatched)
    {
        if (isComposing || sinceCompositionEnded < OpenCodeComposerInputPolicy.ImeEnterGuard) return null;
        var hasRows = _mentionRows.Count > 0;
        switch (key)
        {
            case OpenCodeComposerKey.Up:
            case OpenCodeComposerKey.Down:
                if (!hasRows || shift || IsControlDown()) return null;
                _mentionHighlight = OpenCodeComposerInputPolicy.MoveHighlight(_mentionHighlight, _mentionRows.Count,
                    key == OpenCodeComposerKey.Up ? -1 : 1);
                ApplyMentionHighlight();
                return new(OpenCodeComposerKeyAction.Swallow, true, false);
            case OpenCodeComposerKey.Escape:
                CloseMention();
                return new(OpenCodeComposerKeyAction.Swallow, true, false);
            case OpenCodeComposerKey.Tab:
                if (!hasRows || shift || IsControlDown()) return null;
                AcceptMention(Math.Max(0, _mentionHighlight));
                return new(OpenCodeComposerKeyAction.Swallow, true, false);
            case OpenCodeComposerKey.Enter:
                if (shift) return null;
                if (!hasRows) return new(OpenCodeComposerKeyAction.Swallow, true, true);
                if (!enterLatched) AcceptMention(Math.Max(0, _mentionHighlight));
                return new(OpenCodeComposerKeyAction.Swallow, true, true);
            default:
                return null;
        }
    }

    /// <summary>
    /// Replace the "@query" token under the caret with the chosen server path. A user-gesture edit through the native
    /// TextBox (exactly like typing/paste): TextChanging records it in the existing ledger and TextChanged flushes it with
    /// its own token. Never a guarded application write, never a submission.
    /// </summary>
    private void AcceptMention(int index)
    {
        if (!FlushForIntent()) { CloseMention(); return; }
        if (_editor is not { } editor || index < 0 || index >= _mentionRows.Count) return;
        var candidate = _mentionRows[index];
        var trigger = OpenCodeComposerCharTriggers.FindMention(editor.Text, editor.SelectionStart);
        var s = _source();
        if (!trigger.Present || trigger != _mentionTrigger || editor.IsReadOnly || _composing || !s.IsActive || s.IsBusy)
        {
            CloseMention();
            return;
        }
        CloseMention();
        var insertion = OpenCodeComposerCharTriggers.MentionInsertion(candidate);
        editor.Select(trigger.Start, trigger.Length);
        editor.SelectedText = insertion;
        editor.Select(trigger.Start + insertion.Length, 0);
        if (editor.FocusState == FocusState.Unfocused) editor.Focus(FocusState.Programmatic);
    }

    /// <summary>"+ → 上下文（@）": insert a legal literal "@" at the caret (same native edit path); the list then opens.</summary>
    private void InsertContextTrigger()
    {
        if (_editor is not { } editor || editor.IsReadOnly || _composing || CurrentMentionSearch() is null) return;
        if (!FlushForIntent()) return;
        var s = _source();
        if (!s.IsActive || s.IsBusy) return;
        if (_menuShown) _menuSession.Dismiss(this, _menuSession.Capture());
        editor.Focus(FocusState.Programmatic);
        var text = editor.Text ?? string.Empty;
        var caret = Math.Clamp(editor.SelectionStart + editor.SelectionLength, 0, text.Length);
        var (insert, offset) = OpenCodeComposerCharTriggers.ContextInsertion(text, caret);
        editor.Select(caret, 0);
        editor.SelectedText = insert;
        editor.Select(caret + offset, 0);
    }

    /// <summary>
    /// 命令菜单本机操作右侧“当前：…”取值（重设计 v3 §7.2）；由页面提供，返回 null 表示无可靠当前值（不显示）。
    /// </summary>
    internal Func<OpenCodeComposerClientActionKind, string?>? CurrentValueSource { get; set; }

    private static ListViewItem GroupHeader(OpenCodeComposerCommandOrigin origin)
    {
        var text = new TextBlock
        {
            Text = origin == OpenCodeComposerCommandOrigin.AIFrontend ? "AIFrontend 本机操作" : "服务端命令",
            Margin = new Thickness(0, 6, 0, 2),
        };
        if (OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey) is { } style) text.Style = style;
        var header = new ListViewItem
        {
            Content = text,
            IsEnabled = false,
            IsHitTestVisible = false,
            IsTabStop = false,
            MinHeight = 0,
            Tag = null,
        };
        AutomationProperties.SetAccessibilityView(header, AccessibilityView.Raw); // Each row's name already states its origin.
        return header;
    }

    private string? CurrentValue(OpenCodeComposerMenuRow row)
    {
        if (row.Identity is not OpenCodeComposerCommandIdentity.Client client || CurrentValueSource is not { } source) return null;
        try
        {
            return source(client.Kind) is { Length: > 0 } value ? OpenCodeComposerInputPolicy.Bound(value, 32) : null;
        }
        catch
        {
            return null; // A failing value source never breaks the menu.
        }
    }

    private ListViewItem MenuItem(OpenCodeComposerMenuRow row, OpenCodeComposerMenuEvidence evidence)
    {
        var name = new TextBlock
        {
            Text = "/" + OpenCodeComposerInputPolicy.Bound(row.Name, 48),
            FontFamily = MonoFont,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(name);
        // Groups already say where a row comes from; the badge only disambiguates same-named rows across origins.
        if (row.NameCollides) head.Children.Add(OriginBadge(row.Origin, emphasised: true));
        // Server argument placeholders (e.g. "$1 $ARGUMENTS") as quiet secondary text next to the name.
        var hints = row.ArgumentHints is { Count: > 0 } list ? OpenCodeComposerInputPolicy.Bound(string.Join(" ", list), 48) : null;
        if (hints is not null)
        {
            var h = Caption(hints, 0.7);
            h.FontFamily = MonoFont;
            h.TextWrapping = TextWrapping.NoWrap;
            h.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(h);
        }
        var current = CurrentValue(row);
        var headRow = new Grid { ColumnSpacing = 12 };
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headRow.Children.Add(head);
        if (current is not null)
        {
            var value = Caption("当前：" + current, 1);
            value.TextWrapping = TextWrapping.NoWrap;
            value.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(value, 1);
            headRow.Children.Add(value);
        }
        var stack = new StackPanel { Spacing = 1, Padding = new Thickness(0, 5, 0, 6), Tag = evidence };
        stack.Children.Add(headRow);
        var description = OpenCodeComposerInputPolicy.Bound(row.Description, OpenCodeComposerInputPolicy.MaxDescriptionLength);
        if (description.Length > 0)
        {
            var d = Caption(description, 0.7);
            d.TextWrapping = TextWrapping.NoWrap;
            d.TextTrimming = TextTrimming.CharacterEllipsis;
            stack.Children.Add(d);
        }
        if (!row.Selectable && row.Unavailable is { } reason)
        {
            var r = Caption("暂不可用：" + reason, 1);
            r.TextWrapping = TextWrapping.NoWrap;
            r.TextTrimming = TextTrimming.CharacterEllipsis;
            r.Style = ThemedCaption("SystemFillColorCautionBrush");
            stack.Children.Add(r);
            name.Opacity = 0.55;
        }
        var item = new ListViewItem { Content = stack, Tag = evidence, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var origin = OriginLabel(row.Origin);
        var automation = $"/{row.Name}，{origin}" + (row.NameCollides ? "（与另一来源的同名项不同）" : string.Empty)
            + (hints is not null ? "，参数：" + hints : row.KnownArgumentFree ? "，无参数，确认后直接发送" : string.Empty)
            + (current is not null ? "，当前：" + current : string.Empty)
            + (description.Length > 0 ? "，" + description : string.Empty)
            + (!row.Selectable && row.Unavailable is { } why ? "，暂不可用：" + why : string.Empty);
        AutomationProperties.SetName(item, automation);
        ToolTipService.SetToolTip(item, automation);
        return item;
    }

    private static string OriginLabel(OpenCodeComposerCommandOrigin origin)
        => origin == OpenCodeComposerCommandOrigin.AIFrontend ? "AIFrontend 本机操作" : "服务器命令";

    /// <summary>Small origin tag. Server commands run on the OpenCode service; AIFrontend actions run in this app.</summary>
    private static Border OriginBadge(OpenCodeComposerCommandOrigin origin, bool emphasised)
    {
        var native = origin == OpenCodeComposerCommandOrigin.AIFrontend;
        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 0, 5, 1),
            BorderThickness = new Thickness(emphasised ? 1 : 0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = native
                ? ThemedBorder("AccentFillColorSecondaryBrush", "AccentFillColorDefaultBrush")
                : ThemedBorder("ControlFillColorSecondaryBrush", "ControlStrokeColorDefaultBrush"),
            Child = new TextBlock
            {
                Text = native ? "AIFrontend" : "服务器",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Style = ThemedForeground("TextBlock", native ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"),
            },
        };
        AutomationProperties.SetAccessibilityView(badge, AccessibilityView.Raw); // The row/chip name already says it.
        return badge;
    }

    // ------------------------------------------------------------------
    // Chips
    // ------------------------------------------------------------------

    private void RenderChips(OpenCodeComposerSnapshot s, bool canEdit)
    {
        if (_attachmentActivation is not { } prior || prior.Scope != s.Token.Scope || prior.Draft != s.Token.Draft
            || _attachmentViewGeneration != _lifetime.Generation)
        {
            foreach (var chip in _attachmentChips.Values) _chipPanel.Children.Remove(chip);
            _attachmentChips.Clear();
            _attachmentActivation = s.Token;
            _attachmentViewGeneration = _lifetime.Generation;
        }
        var showCommand = s.SelectedCommand is not null;
        _commandChip.Visibility = showCommand ? Visibility.Visible : Visibility.Collapsed;
        if (showCommand)
        {
            var bounded = OpenCodeComposerInputPolicy.Bound(s.SelectedCommand, OpenCodeComposerInputPolicy.MaxChipNameLength);
            _commandChipText.Text = "/" + bounded;
            ToolTipService.SetToolTip(_commandChip, $"已选命令 /{bounded}：发送时执行，输入框内容作为参数。服务器确认接收不代表执行完成。");
            AutomationProperties.SetName(_commandChipRemove, $"移除命令 /{bounded}（保留参数文本）");
            ToolTipService.SetToolTip(_commandChipRemove, "移除命令（保留输入框中的参数）");
            _commandChipRemove.IsEnabled = canEdit;
        }

        // Native intent: separate from the server command, attachments and text, which all stay untouched.
        var intent = s.ClientIntent;
        _intentChip.Visibility = intent is not null ? Visibility.Visible : Visibility.Collapsed;
        if (intent is not null)
        {
            var name = NativeName(intent.Kind);
            _intentChipText.Text = "/" + name;
            var label = $"AIFrontend 本机操作 /{name}：{OpenCodeComposerCommandMenuSession.NativeDescription(intent.Kind)}";
            AutomationProperties.SetName(_intentChip, label);
            ToolTipService.SetToolTip(_intentChip, label + "。不是服务器命令，不会发送消息。");
            _intentRun.IsEnabled = s.CanInvokeClientAction;
            AutomationProperties.SetName(_intentRun, $"执行本机操作 /{name}");
            ToolTipService.SetToolTip(_intentRun, "执行（也可以再按一次 Enter）。会先打开确认或选择界面，不会直接发送消息。");
            AutomationProperties.SetName(_intentRemove, $"取消本机操作 /{name}（保留输入内容）");
            ToolTipService.SetToolTip(_intentRemove, "取消这个本机操作（输入框内容、已选命令和附件保持不变）");
            _intentRemove.IsEnabled = s.IsActive && !s.IsBusy;
        }
        var intentNote = intent is null ? null : _menuSession.Note;
        _intentHint.Text = intent is null ? string.Empty
            : intentNote ?? (s.CanInvokeClientAction
                ? "本机操作未能直接打开：按 Enter 或点“执行”重试。不会发送消息。"
                : "本机操作暂不能执行：" + OpenCodeComposerCommandMenuSession.UnavailableReason(intent.Kind, s) + "。");
        _intentHint.Visibility = intent is not null && s.IsActive ? Visibility.Visible : Visibility.Collapsed;

        // Diff attachment chips by id (keeps focus/transitions stable).
        var ids = new HashSet<Guid>(s.Attachments.Select(a => a.Id));
        foreach (var stale in _attachmentChips.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _chipPanel.Children.Remove(_attachmentChips[stale]);
            _attachmentChips.Remove(stale);
        }
        foreach (var a in s.Attachments)
        {
            if (!_attachmentChips.TryGetValue(a.Id, out var chip))
            {
                chip = AttachmentChip(a, s.Token);
                _attachmentChips[a.Id] = chip;
                _chipPanel.Children.Add(chip);
            }
            if (chip.Tag is Button remove) remove.IsEnabled = canEdit;
        }
        _chipScroll.Visibility = showCommand || intent is not null || s.Attachments.Count != 0 ? Visibility.Visible : Visibility.Collapsed;
        _chipRow.Visibility = _chipScroll.Visibility;
    }

    /// <summary>Displayed slash name of a native kind (the controller's own native rows carry the same names).</summary>
    private static string NativeName(OpenCodeComposerClientActionKind kind) => kind switch
    {
        OpenCodeComposerClientActionKind.NewSession => "new",
        OpenCodeComposerClientActionKind.ChooseSession => "sessions",
        OpenCodeComposerClientActionKind.ChooseAgent => "agents",
        OpenCodeComposerClientActionKind.ChooseModel => "models",
        OpenCodeComposerClientActionKind.Undo => "undo",
        OpenCodeComposerClientActionKind.Redo => "redo",
        OpenCodeComposerClientActionKind.EditDraft => "editor",
        _ => "action",
    };

    private FrameworkElement AttachmentChip(OpenCodeComposerAttachmentRow a, OpenCodeComposerToken activation)
    {
        var name = OpenCodeComposerInputPolicy.Bound(a.Name, OpenCodeComposerInputPolicy.MaxChipNameLength);
        var size = OpenCodeComposerInputPolicy.FormatBytes(a.Bytes);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new FontIcon
        {
            Glyph = OpenCodeComposerInputPolicy.GlyphForMime(a.Mime),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Style = ThemedForeground("FontIcon", "TextFillColorSecondaryBrush"),
        });
        row.Children.Add(new TextBlock
        {
            Text = name,
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var sizeText = Caption(size, 0.6);
        sizeText.TextWrapping = TextWrapping.NoWrap;
        sizeText.IsTextSelectionEnabled = false;
        sizeText.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(sizeText);
        var remove = ChipRemoveButton("OpenCodeComposerRemoveAttachment");
        AutomationProperties.SetName(remove, $"移除附件 {name}");
        ToolTipService.SetToolTip(remove, "移除此附件");
        var id = a.Id;
        var generation = _lifetime.Generation;
        FrameworkElement? renderedChip = null;
        remove.Click += (_, _) =>
        {
            var before = _source();
            if (!_lifetime.IsLive || generation != _lifetime.Generation || renderedChip is null
                || !_attachmentChips.TryGetValue(id, out var liveChip) || !ReferenceEquals(renderedChip, liveChip)
                || before.Token.Scope != activation.Scope || before.Token.Draft != activation.Draft || !FlushForIntent()) return;
            var fresh = _source();
            // Same-activation typing may advance Edit. Obtain that NEW gesture token only after
            // validating this still-rendered chip; never lend it to an old A→B→A callback.
            if (fresh.Token.Scope != activation.Scope || fresh.Token.Draft != activation.Draft) return;
            AttachmentRemoved?.Invoke(this, new(fresh.Token, id));
        };
        row.Children.Add(remove);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 3, 3, 3),
            BorderThickness = new Thickness(1),
            Style = ThemedBorder("ControlFillColorSecondaryBrush", "ControlStrokeColorDefaultBrush"),
            Child = row,
            Tag = remove,
        };
        renderedChip = chip;
        AutomationProperties.SetName(chip, $"附件 {name}，{size}");
        ToolTipService.SetToolTip(chip, $"{name} · {a.Mime} · {size}");
        return chip;
    }

    // ------------------------------------------------------------------
    // Status line: inactive draft > busy > local note > protection > controller note
    // ------------------------------------------------------------------

    private void RenderStatus(OpenCodeComposerSnapshot s, bool enhanced, bool inactiveBound)
    {
        string? text = null;
        string glyph = "\uE946"; // Info
        var tone = "neutral";
        var busy = false;
        var offerDiscard = false;
        if (_slotUnavailableReason is { } unavailable)
        {
            text = unavailable;
            glyph = "\uE7BA";
            tone = "caution";
            offerDiscard = _sync?.IsSuspendedForRecovery == true;
        }
        else if (inactiveBound)
        {
            text = (s.Note ?? "草稿属于原会话；返回原会话或明确丢弃。")
                + " 此草稿含命令或附件，不会转到当前会话；丢弃后才能在此输入新内容。";
            glyph = "\uE7BA";
            tone = "caution";
            offerDiscard = !s.IsBusy;
        }
        else if (_sync?.State == OpenCodeComposerInputState.HeldStale)
        {
            text = "这段输入未能同步到当前草稿（会话或草稿已变化），已保留显示但不能发送；丢弃草稿后可重新输入。";
            glyph = "\uE7BA";
            tone = "caution";
            offerDiscard = !s.IsBusy;
        }
        else if (s.IsBusy)
        {
            busy = true;
            text = _picking ? "正在选择或读取附件…" : "正在提交命令或附件…发送与停止暂不可用。";
        }
        else if (_localNote is not null)
        {
            text = _localNote;
            glyph = "\uE7BA";
            tone = "caution";
        }
        else if (s.IsActive && enhanced && s.IsProtected)
        {
            text = "此会话有结果尚未确认的操作或正在进行的交互，命令/附件暂不能提交；不会自动重试。";
            glyph = "\uE72E"; // Lock
            tone = "caution";
        }
        else if (s.Note is not null)
        {
            text = s.Note;
            tone = s.Note.Contains("未知", StringComparison.Ordinal) ? "caution" : "neutral";
            glyph = tone == "caution" ? "\uE7BA" : "\uE946";
        }

        if (_sideQuestionPending > 0)
        {
            var pending = $"/btw ({_sideQuestionPending}) · 正在回答旁路问题…";
            text = text is null ? pending : text + " · " + pending;
        }
        if (text is null) { _statusBar.Visibility = Visibility.Collapsed; _statusRing.IsActive = false; return; }
        _statusBar.Visibility = Visibility.Visible;
        _statusText.Text = text;
        _statusRing.IsActive = busy;
        _statusRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _statusIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        _statusIcon.Glyph = glyph;
        _statusIcon.Style = ThemedForeground("FontIcon", tone == "caution" ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
        _statusBar.Style = OpenCodeThemedXaml.BrushStyle("Border",
            ("Background", tone == "caution" ? "SystemFillColorCautionBackgroundBrush" : "SubtleFillColorSecondaryBrush"));
        _statusDiscardButton.Visibility = offerDiscard ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_statusBar, text);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private Flyout BuildDiscardFlyout()
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        OpenCodeComposerToken? opening = null;
        long generation = -1;
        flyout.Opening += (_, _) => { opening = _source().Token; generation = _lifetime.Generation; };
        var confirm = new Button { Content = "丢弃", Style = OpenCodeInteractionUi.TryStyle("AccentButtonStyle") };
        AutomationProperties.SetAutomationId(confirm, "OpenCodeComposerDiscardConfirm");
        var cancel = new Button { Content = "保留" };
        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            var fresh = _source();
            if (!_lifetime.IsLive || generation != _lifetime.Generation || opening != fresh.Token || fresh.IsBusy) return;
            opening = null; // One confirmation, never a reusable discard capability.
            DiscardRequested?.Invoke(this, new(fresh.Token));
            // Explicit discard is the only disposal of held input: only when it actually replaced the draft.
            if (_source().Token != fresh.Token && _lifetime.IsLive) { _sync?.ResolveDiscard(); _inputEvidence.Refresh(); }
            if (_editor is { IsEnabled: true }) _editor.Focus(FocusState.Programmatic);
        };
        cancel.Click += (_, _) => flyout.Hide();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        var body = new StackPanel { Spacing = 12, MaxWidth = 300 };
        body.Children.Add(new TextBlock { Text = "丢弃这份草稿？", FontWeight = FontWeights.SemiBold });
        body.Children.Add(OpenCodeInteractionUi.Caption("将移除已选命令、全部附件和输入内容。不会影响已发送的内容或任何未确认结果的保护。", 0.8));
        body.Children.Add(buttons);
        flyout.Content = body;
        return flyout;
    }

    private void Raise(EventHandler<OpenCodeComposerTokenEventArgs>? handler)
    {
        FlushForIntent();
        handler?.Invoke(this, new(_source().Token));
    }

    private static Button ChipRemoveButton(string automationId)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
            Width = 22,
            Height = 22,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    /// <summary>
    /// 说明文字：用 Caption 样式而非降低不透明度（重设计 v3 §1）。<paramref name="opacity"/> 小于 1 的旧调用
    /// 表示“次要”，映射为 CaptionSubtle（次级文字色，ThemeResource，随主题即时切换）。
    /// </summary>
    private static TextBlock Caption(string text, double opacity)
    {
        var t = OpenCodeInteractionUi.StyledText(text,
            opacity < 1 ? OpenCodeInteractionUi.CaptionSubtleStyleKey : OpenCodeInteractionUi.CaptionStyleKey);
        t.IsTextSelectionEnabled = false;
        return t;
    }

    private static Style? ThemedBorder(string background, string border)
        => OpenCodeThemedXaml.BrushStyle("Border", ("Background", background), ("BorderBrush", border));

    private static Style? ThemedForeground(string targetType, string foreground)
        => OpenCodeThemedXaml.BrushStyle(targetType, ("Foreground", foreground));

    /// <summary>Caption 字号 + 指定主题前景色（基于内置 CaptionTextBlockStyle）。</summary>
    private static Style? ThemedCaption(string foreground)
    {
        var style = ThemedForeground("TextBlock", foreground);
        if (style is not null && style.BasedOn is null && !style.IsSealed
            && OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionStyleKey) is { } caption)
        {
            style.BasedOn = caption;
        }

        return style;
    }

    private static void Place(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static string Fingerprint(OpenCodeComposerSnapshot s, bool menu)
        => string.Join('|', s.Token.GetHashCode(), s.Text.Length, s.SelectedCommand, s.CatalogState, s.Commands.Count,
            string.Join(',', s.Attachments.Select(a => a.Id)), s.HasDraft, s.IsBoundDraft, s.IsActive, s.IsBusy,
            s.CanSubmit, s.IsProtected, s.Note, menu, s.ClientIntent?.Id, s.CanInvokeClientAction);
}

/// <summary>
/// 代码构建视图的主题资源桥（重设计 v3 §1）：经 XAML 解析器创建带 <c>{ThemeResource}</c> 引用的元素/样式，
/// 使浅色/深色/高对比度切换时由框架即时重取画刷（代码中经 TryBrush 取到的画刷不会随主题更新）。
/// 只引用 WinUI 内置资源键，不定义任何颜色。
/// </summary>
internal static class OpenCodeThemedXaml
{
    private const string Namespaces =
        " xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"" +
        " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    private static readonly Dictionary<string, Style?> Styles = new(StringComparer.Ordinal);

    /// <summary>
    /// 只设置主题画刷属性的样式（如 Border.Background / TextBlock.Foreground），按键缓存复用。
    /// <paramref name="setters"/> 为 (属性名, 内置主题资源键)。解析失败返回 null（控件回退默认外观）。
    /// </summary>
    internal static Style? BrushStyle(string targetType, params (string Property, string ResourceKey)[] setters)
    {
        var key = new System.Text.StringBuilder(targetType);
        foreach (var (property, resource) in setters)
        {
            key.Append('|').Append(property).Append('=').Append(resource);
        }

        var cacheKey = key.ToString();
        if (Styles.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var xaml = new System.Text.StringBuilder("<Style").Append(Namespaces).Append(" TargetType=\"").Append(targetType).Append("\">");
        foreach (var (property, resource) in setters)
        {
            xaml.Append("<Setter Property=\"").Append(property).Append("\" Value=\"{ThemeResource ")
                .Append(resource).Append("}\" />");
        }

        xaml.Append("</Style>");
        Style? style;
        try
        {
            style = Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml.ToString()) as Style;
        }
        catch
        {
            style = null;
        }

        Styles[cacheKey] = style;
        return style;
    }

    /// <summary>解析一个元素片段（片段内可使用 {ThemeResource}）；失败返回 null。</summary>
    internal static T? Load<T>(string elementName, string attributes, string inner = "") where T : class
    {
        try
        {
            return Microsoft.UI.Xaml.Markup.XamlReader.Load("<" + elementName + Namespaces + " " + attributes + ">" + inner + "</" + elementName + ">") as T;
        }
        catch
        {
            return null;
        }
    }
}
