using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.Appearance;
using FluentCode.Services.OpenCode;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
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
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace FluentCode.Pages.OpenCode;

public sealed class OpenCodeComposerTokenEventArgs(OpenCodeComposerToken token) : EventArgs
{
    public OpenCodeComposerToken Token { get; } = token;
}
public sealed class OpenCodeComposerAttachmentRemovedEventArgs(OpenCodeComposerToken token, Guid id) : EventArgs
{
    public OpenCodeComposerToken Token { get; } = token;
    public Guid Id { get; } = id;
}
/// <summary>Full-size viewing of a composer image attachment; the page owns the shared image viewer.</summary>
internal sealed class OpenCodeComposerAttachmentImageEventArgs(Guid id, string name, string mime, byte[] bytes, FrameworkElement anchor) : EventArgs
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public string Mime { get; } = mime;
    public byte[] Bytes { get; } = bytes;
    public FrameworkElement Anchor { get; } = anchor;
}
public sealed class OpenCodeComposerTextEditedEventArgs(OpenCodeComposerToken token, string text) : EventArgs
{
    public OpenCodeComposerToken Token { get; } = token;
    public string Text { get; } = text;
    /// <summary>Set by the view only for input recorded against an unbound baseline (navigation-carry request).</summary>
    internal bool AllowUnboundCarry { get; init; }
    /// <summary>One staged clipboard transaction; its native text edit also owns the chip spans and attachments.</summary>
    internal OpenCodeComposerImagePaste? ImagePaste { get; init; }
    internal OpenCodeComposerDocumentObservation? Observation { get; init; }
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
/// 选择目录行只暂存（服务器命令成为命令标签；FluentCode 本机操作成为独立的本机操作标签），消费控制器证明的触发词，
/// 绝不执行、绝不发送；执行本机操作需要随后一次新的显式按键或点击。</para>
/// <para>目录权限全部来自控制器能力：视图持有自己的 <see cref="OpenCodeComposerCatalogLifetime"/>，打开事件携带它，
/// 关闭携带显示中的 <see cref="OpenCodeComposerCatalogHandle"/>，选择携带显示中的句柄 + 类型化标识 + 刷新后的令牌。</para>
/// </remarks>
internal sealed partial class OpenCodeComposerView : StackPanel
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
    // Context inserts a legal "@"; terminal is a separate editor/transport owned by the page.
    private readonly MenuFlyoutItem _contextItem;
    private readonly MenuFlyoutItem _terminalItem;
    // Attachment limits: one status glyph (green ✓ / amber / red); the numbers live in its tooltip.
    private readonly ContentControl _limitIndicator;
    private readonly FontIcon _limitIcon;

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
    private readonly Button _recoveryClear;
    internal event Action? RecoveryClearRequested;
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

    private OpenCodeComposerEditor? _editor;
    private Func<bool> _isComposing = static () => false;
    private string? _editorPlaceholder;
    // Placeholder splash (B15): rotated only by an explicit slot activation; feeds PlaceholderText only, never editor text.
    private readonly OpenCodeComposerSplash _splash = new();
    private bool _editorReadOnlyApplied;
    private int _slotTransitionDepth;
    private string? _slotUnavailableReason;

    private OpenCodeComposerSnapshot? _snapshot;
    private OpenCodeComposerEditorSync? _sync;
    private readonly OpenCodeComposerComposition _composition = new();
    private bool _composing => _composition.IsActive;
    private readonly OpenCodeComposerCommandMenuSession _menuSession;
    private readonly OpenCodeComposerInputEvidenceSource _inputEvidence;
    private readonly Func<OpenCodeComposerCatalogLifetime> _beginCatalogLifetime;
    private readonly Action<OpenCodeComposerCatalogLifetime> _endCatalogLifetime;
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
    internal event EventHandler? TerminalRequested;
    internal event EventHandler? SlotGesturesRevoked;
    internal event EventHandler<OpenCodeComposerTokenEventArgs>? CommandRemoved;
    internal event EventHandler<OpenCodeComposerTokenEventArgs>? PickRequested;
    internal event EventHandler<OpenCodeComposerAttachmentRemovedEventArgs>? AttachmentRemoved;
    /// <summary>Click / Enter / Space on an image tile: the page opens its shared full-size image viewer.</summary>
    internal event EventHandler<OpenCodeComposerAttachmentImageEventArgs>? AttachmentImageOpened;
    internal event EventHandler<OpenCodeComposerTextEditedEventArgs>? TextEdited;
    /// <summary>Raised on the UI thread after a render whose visible/eligibility state changed (page refreshes Send/Stop).</summary>
    internal event EventHandler? StateChanged;
    internal event Action<bool>? CompositionChanged;
    /// <summary>The native composition's final text/selection callbacks have drained; page overlays may resume.</summary>
    internal event EventHandler? CompositionSettled;

    /// <param name="snapshotSource">Read-only accessor for the controller's current snapshot (e.g. <c>() =&gt; controller.Snapshot</c>).</param>
    /// <param name="beginCatalogLifetime"><c>controller.BeginCatalogLifetime</c>: called once per attached, loaded editor lifetime.</param>
    /// <param name="endCatalogLifetime"><c>controller.EndCatalogLifetime</c>: called with that exact capability on unload/reattach.</param>
    internal OpenCodeComposerView(Func<OpenCodeComposerSnapshot> snapshotSource,
        Func<OpenCodeComposerCatalogLifetime> beginCatalogLifetime, Action<OpenCodeComposerCatalogLifetime> endCatalogLifetime)
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
        _composition.Changed += active => CompositionChanged?.Invoke(active);
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

        // ---- Leading "+" button (B7): 添加附件 · ── · 命令 / · 上下文 @ · 终端 ! ----
        // Built from OpenCodeComposerPlusMenu.Entries so the order/labels are verified offline against the same data.
        var addMenu = new MenuFlyout { Placement = FlyoutPlacementMode.TopEdgeAlignedLeft };
        MenuFlyoutItem? attach = null, command = null, context = null, terminal = null;
        foreach (var entry in OpenCodeComposerPlusMenu.Entries)
        {
            if (entry.Kind == OpenCodeComposerPlusMenuKind.Separator) { addMenu.Items.Add(new MenuFlyoutSeparator()); continue; }
            var item = new MenuFlyoutItem { Text = entry.Text, Icon = entry.Kind == OpenCodeComposerPlusMenuKind.Terminal ? OpenCodeQuickAccessStyle.TerminalIcon() : new FontIcon { Glyph = entry.Glyph } };
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
        _commandItem.KeyboardAcceleratorTextOverride = "/";
        ToolTipService.SetToolTip(_commandItem, OpenCodeComposerPlusMenu.CommandTooltip);
        _commandItem.Click += (_, _) => RequestCatalog();
        _contextItem = context!;
        _contextItem.KeyboardAcceleratorTextOverride = "@";
        _contextItem.Click += (_, _) => InsertContextTrigger();
        _terminalItem = terminal!;
        _terminalItem.KeyboardAcceleratorTextOverride = "!";
        _terminalItem.Click += (_, _) => TerminalRequested?.Invoke(this, EventArgs.Empty);
        ToolTipService.SetToolTip(_terminalItem, OpenCodeComposerPlusMenu.TerminalTooltip);
        AutomationProperties.SetHelpText(_terminalItem, OpenCodeComposerPlusMenu.TerminalTooltip);
        _addButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE710", FontSize = 16 },
            Width = 32,
            Height = 32,
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Flyout = addMenu,
        };
        AutomationProperties.SetAutomationId(_addButton, "OpenCodeComposerAddButton");
        AutomationProperties.SetName(_addButton, "添加附件、命令或上下文");
        ToolTipService.SetToolTip(_addButton, "添加");

        _limitIcon = new FontIcon { Glyph = "\uEC61", FontSize = 16 };
        _limitIndicator = new ContentControl
        {
            Content = _limitIcon,
            Width = 32,
            Height = 32,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsTabStop = true,
            UseSystemFocusVisuals = true,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(_limitIndicator, "OpenCodeComposerAttachmentLimit");
        ToolTipService.SetPlacement(_limitIndicator, PlacementMode.Top);

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
        commandChipRow.Children.Add(new FontIcon { Glyph = "\uE945", FontSize = 12, // Same lightning as the + menu's 命令. VerticalAlignment = VerticalAlignment.Center,
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
            VerticalAlignment = VerticalAlignment.Center, // Attachment tiles are taller than text chips.
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(_commandChip, "OpenCodeComposerCommandChip");

        // ---- Native-intent chip: an FluentCode action, visibly NOT a server command/attachment/text ----
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
        intentRow.Children.Add(OriginBadge(OpenCodeComposerCommandOrigin.FluentCode, emphasised: true));
        intentRow.Children.Add(_intentRun);
        intentRow.Children.Add(_intentRemove);
        _intentChip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 3, 3, 3),
            BorderThickness = new Thickness(1),
            Style = ThemedBorder("SubtleFillColorSecondaryBrush", "ControlStrokeColorDefaultBrush"),
            Child = intentRow,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(_intentChip, "OpenCodeComposerClientActionChip");
        _intentHint = Caption(string.Empty, 0.7);
        _intentHint.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_intentHint, AutomationLiveSetting.Polite);

        _chipPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        // Chips and attachment tiles: system add/delete (scale+fade in, fade out, neighbours slide); cleared under reduce-motion.
        FluentCode.Motion.FluentMotion.BindChildrenTransitions(_chipPanel, () => new TransitionCollection { new AddDeleteThemeTransition() });
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
        Place(_chipRow, _limitIndicator, 1);

        // ---- Status line ----
        _statusIcon = new FontIcon { FontSize = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        _statusRing = new ProgressRing { Width = 14, Height = 14, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top };
        _statusText = Caption(string.Empty, 0.9);
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        var statusGrid = new Grid { ColumnSpacing = 8 };
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyphHost = new Grid();
        glyphHost.Children.Add(_statusIcon);
        glyphHost.Children.Add(_statusRing);
        Place(statusGrid, glyphHost, 0);
        Place(statusGrid, _statusText, 1);
        _recoveryClear = new Button { Content = "清空并恢复", Padding = new Thickness(8, 2, 8, 3), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(_recoveryClear, "OpenCodeComposerRecoveryClear");
        AutomationProperties.SetName(_recoveryClear, "清空未同步输入并恢复编辑");
        _recoveryClear.Click += (_, _) => RecoveryClearRequested?.Invoke(); // Explicit recovery must not pass the held submission gate.
        Place(statusGrid, _recoveryClear, 2);
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
        FluentCode.Motion.FluentMotion.BindChildrenTransitions(_menuSlot, () => new TransitionCollection { new EntranceThemeTransition { FromVerticalOffset = 8 } });

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
    internal bool IsAutocompleteVisible => _menuShown || _mentionShown;

    internal bool PrepareShellMode()
    {
        if (!PrepareSubmission()) return false;
        DismissCatalog(focusEditor: false);
        CloseMention();
        _addButton.Flyout?.Hide();
        return true;
    }

    /// <summary>
    /// Attach the page's existing message TextBox. The view records input itself from TextChanging and tracks
    /// composition start/end (end only wakes rechecks); the page still forwards TextChanged via
    /// <see cref="NotifyEditorTextChanged"/> (flush only) and keys via <see cref="HandleEditorKey"/>.
    /// <paramref name="isComposing"/> is the page's focus-suppression predicate (IME/suppressed/inactive page).
    /// Reattaching detaches the previous editor and fences its pending callbacks and handoffs.
    /// </summary>
    internal void AttachEditor(OpenCodeComposerEditor editor, Func<bool> isComposing)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _isComposing = isComposing ?? throw new ArgumentNullException(nameof(isComposing));
        if (_editor is { } previous)
        {
            previous.DocumentChanging -= OnEditorDocumentChanging;
            previous.DocumentChanged -= NotifyEditorTextChanged;
            previous.NativeLayoutReady -= NotifyEditorTextChanged;
            previous.UndoRequested -= OnModelUndoRequested;
            previous.RemoveHandler(PointerReleasedEvent, new PointerEventHandler(OnChipPointerReleased));
            previous.PointerMoved -= OnChipPointerMoved;
            previous.PointerExited -= OnChipPointerExited;
            previous.PrepareModelOperation = null;
            previous.TextCompositionStarted -= OnEditorCompositionStarted;
            previous.TextCompositionEnded -= OnEditorCompositionEnded;
            previous.LostFocus -= OnEditorLostFocus;
            previous.SelectionChanged -= OnEditorSelectionChanged;
        }
        CloseMention();
        _mentionArmed = false;
        _editor = editor;
        editor.PrepareModelOperation = PrepareSubmission;
        OpenCodeComposerBitmapCache.SiteIcon = (link, dark) => OpenCodeLinkChipView.CreateIcon(link, dark);
        OpenCodeComposerBitmapCache.OnlineEnabled = () => OpenCodeLinkChipView.OnlineEnabled;
        OpenCodeComposerBitmapCache.IconOptions = () => OpenCodeLinkChipView.Monochrome.ToString();
        _editorPlaceholder = editor.PlaceholderText;
        _composition.Reset();
        // The ONLY application writer of editor.Text: guarded assign + actual read-back inside the sync.
        _sync = new OpenCodeComposerEditorSync(_source, () => editor.Text, text =>
            {
                _guardedWrite = true; // A guarded application write is never a typed "@" trigger.
                _mentionArmed = false;
                try
                {
                    if (!string.Equals(editor.Text, text, StringComparison.Ordinal))
                    {
                        editor.PresentPlainRecovery(text);
                    }
                }
                finally { _guardedWrite = false; }
            }, () => _composing, ApplyEditorInput, () => editor.Observation, snapshot =>
            {
                _guardedWrite = true;
                // Reconcile also delivers an identical ACKed snapshot with IsWriting=false.
                // That is not an application edit: keep the user's @ observation until evaluated.
                if (_sync?.IsWriting == true) _mentionArmed = false;
                try { editor.ApplySnapshot(snapshot); }
                finally { _guardedWrite = false; }
            });
        // Early, synchronous input observation (records only; no rendering, flyouts, focus or tree changes).
        editor.DocumentChanging += OnEditorDocumentChanging;
        editor.DocumentChanged += NotifyEditorTextChanged;
        editor.NativeLayoutReady += NotifyEditorTextChanged;
        editor.UndoRequested += OnModelUndoRequested;
        editor.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnChipPointerReleased), true);
        editor.PointerMoved += OnChipPointerMoved;
        editor.PointerExited += OnChipPointerExited;
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
        _catalogLifetime = _beginCatalogLifetime();
        _menuSession.Lifetime = _catalogLifetime;
    }

    private void EndCatalogLifetime()
    {
        var lifetime = _catalogLifetime;
        _catalogLifetime = null;
        _menuSession.Lifetime = null;
        if (lifetime is not null) _endCatalogLifetime(lifetime); // Exactly the capability this view began.
    }

    private OpenCodeComposerEditorAck? ApplyEditorInput(OpenCodeComposerToken basis, string text, bool allowCarry)
    {
        var paste = _imagePaste;
        _imagePaste = null; // Consume once; a reentrant later edit is an ordinary observation, not another paste commit.
        var args = new OpenCodeComposerTextEditedEventArgs(basis, text) { AllowUnboundCarry = allowCarry, ImagePaste = paste,
            Observation = _sync?.SubmittedObservation };
        TextEdited?.Invoke(this, args);
        return args.Acknowledgment; // Proof of acceptance; never a later snapshot comparison.
    }

    private void OnEditorDocumentChanging()
    {
        _inputEvidence.RecordTextChanging(); // Ledger record (unchanged logic) + synchronous C#-only evidence publication.
        if (!_guardedWrite) _mentionArmed = true; // A real user edit may open "@" candidates on the following TextChanged.
    }

    private void OnEditorCompositionStarted(RichEditBox sender, TextCompositionStartedEventArgs args)
    {
        if (!ReferenceEquals(sender, _editor)) return;
        _composition.Start();
        _inputEvidence.CompositionChanged();
    }

    // Composition end / focus loss are WAKE-UPS only: every check re-runs; nothing is authorised by them.
    private void OnEditorCompositionEnded(RichEditBox sender, TextCompositionEndedEventArgs args)
    {
        if (!ReferenceEquals(sender, _editor)) return;
        var version = _composition.End();
        _inputEvidence.CompositionChanged();
        CompleteCompositionAfterCommit(version);
    }

    private void OnEditorLostFocus(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _editor) || !_composing) return;
        var version = _composition.End();
        _inputEvidence.CompositionChanged();
        CompleteCompositionAfterCommit(version);
    }

    private void CompleteCompositionAfterCommit(long version)
    {
        var generation = _lifetime.Generation;
        // TextCompositionEnded can precede the final TextChanging/TextChanged/SelectionChanged.
        // Keep their reconciliation and mention caret snapping away from TSF's commit stack.
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!_composition.TryComplete(version)) return;
            _inputEvidence.Refresh();
            if (_lifetime.IsCurrent(generation))
            {
                NotifyEditorTextChanged(); // Flush the final native text, then evaluate @ and / once.
                WakeAfterComposition();
            }
            CompositionSettled?.Invoke(this, EventArgs.Empty);
        });
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
        SlotGesturesRevoked?.Invoke(this, EventArgs.Empty);
        if (_menuShown) _menuSession.Dismiss(this, _menuSession.Capture());
        CloseMention();
        _addButton.Flyout?.Hide();
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
        _splash.ObserveContext(boundSession: s.IsActive && !s.IsUnboundDraft, blankSession: s.IsActive && s.IsUnboundDraft,
            family: OpenCodeWorkingPhrases.Family(SplashModelIdProvider?.Invoke()));
    }

    // Read only at draft-slot activation, never from rendering, typing, model-picker changes or a timer.
    internal Func<string?>? SplashModelIdProvider { get; set; }

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

    internal bool TryClearHeldInput(Func<OpenCodeComposerToken, OpenCodeComposerEditorAck?> clear)
    {
        if (_sync?.TryClearHeldInput(clear) != true) return false;
        Render(_source());
        _editor?.Focus(FocusState.Programmatic);
        return true;
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
        ToolTipService.SetToolTip(_attachItem, full ? "已达 10 个附件上限" : "添加附件");
        _addButton.IsEnabled = _commandItem.IsEnabled || _attachItem.IsEnabled || _contextItem.IsEnabled || _terminalItem.IsEnabled;
        long total = 0;
        foreach (var a in snapshot.Attachments) total += a.Bytes;
        RenderAttachmentLimit(snapshot.Attachments.Count, total);

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

    private OpenCodeComposerAttachmentLimitLevel? _limitLevel;

    private void RenderAttachmentLimit(int count, long total)
    {
        _limitIndicator.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (count <= 0) return;
        var level = OpenCodeComposerInputPolicy.AttachmentLimitLevel(count, total, OpenCodeAttachment.MaxFiles, OpenCodeAttachment.MaxTotalBytes);
        var details = OpenCodeComposerInputPolicy.AttachmentLimitDetails(count, total,
            OpenCodeAttachment.MaxFiles, OpenCodeAttachment.MaxTotalBytes, OpenCodeAttachment.MaxFileBytes);
        if (_limitLevel != level)
        {
            _limitLevel = level;
            (_limitIcon.Glyph, _limitIcon.Style) = level switch
            {
                OpenCodeComposerAttachmentLimitLevel.Over => ("\uEB90", ThemedForeground("FontIcon", "SystemFillColorCriticalBrush")),
                OpenCodeComposerAttachmentLimitLevel.Full => ("\uE7BA", ThemedForeground("FontIcon", "SystemFillColorCautionBrush")),
                _ => ("\uEC61", ThemedForeground("FontIcon", "SystemFillColorSuccessBrush")),
            };
        }
        if (details == _limitDetails) return; // An open tooltip survives unrelated renders.
        _limitDetails = details;
        ToolTipService.SetToolTip(_limitIndicator, details);
        AutomationProperties.SetName(_limitIndicator, details.Replace('\n', '，'));
    }
    private string? _limitDetails;

    private static bool MenuWanted(OpenCodeComposerSnapshot s) => s.IsActive && s.CatalogState != OpenCodeComposerCatalogState.Closed;

    // ------------------------------------------------------------------
    // Editor reconciliation (the page's TextBox remains the only text surface)
    // ------------------------------------------------------------------

    private void ReconcileEditor(OpenCodeComposerSnapshot s, bool inactiveBound)
    {
        if (_editor is null || _sync is null) return;
        // Neither text/selection nor read-only transitions may interrupt the native composition.
        if (_composing) return;
        // Ledger reconciliation (phase3-sync-protocol §3): flush recorded input with its own token; only when no
        // unresolved input remains, adopt the current token or write controller text through the guard. A write
        // deferred by an active composition is retried by the composition-end wake-up (no timer).
        _sync.Reconcile();

        // Only forced source recovery is read-only. Ordinary held input remains editable so a new genuine edit
        // can explicitly recover the current draft. Reading a picked batch also blocks edits
        // that would invalidate the pick ticket. Legacy sending never touches this flag.
        var readOnly = !s.IsActive || _slotUnavailableReason is not null || inactiveBound
            || _sync.IsSuspendedForRecovery || (s.IsBusy && _picking);
        if (readOnly != _editorReadOnlyApplied)
        {
            _editorReadOnlyApplied = readOnly;
            _editor.IsReadOnly = readOnly;
        }
        // New-session copy is fixed; existing sessions keep their activation-only splash.
        // A staged command argument hint still takes precedence in either context.
        var splash = s.IsUnboundDraft ? _editorPlaceholder ?? string.Empty : _splash.Current.Text;
        var placeholder = OpenCodeComposerSplash.Resolve(s.SelectedCommand, s.IsActive, splash, _editorPlaceholder ?? string.Empty);
        if (!string.Equals(_editor.PlaceholderText, placeholder, StringComparison.Ordinal)) _editor.PlaceholderText = placeholder;
    }

    /// <summary>Page calls this from its existing TextChanged handler.</summary>
    internal void NotifyEditorTextChanged()
    {
        // Flushes RECORDED input only (TextChanging ledger); an echo of our own guarded write finds nothing.
        if (_sync is null || !_lifetime.IsLive) return;
        if (_composing) { RequestRender(); return; }
        var flushed = _sync.Flush();
        _inputEvidence.Refresh();
        if (flushed != OpenCodeComposerInputState.Clean) { CloseMention(); RequestRender(); return; } // Show held status.
        // TextChanged (not TextChanging): the tree may change. Evaluate the acknowledged observation and keep a visible
        // menu's filter in step with the text, so key evidence is captured against what the user actually sees.
        if (_menuSession.EvaluateAcknowledged(this) || _menuShown) Render(_source());
        _sync.Reconcile(); // Applies an ACKed model operation without creating a native undo step.
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
        if (_composing) return false;
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
        if (isComposing || _composing) return OpenCodeComposerKeyDecision.Pass;
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
                RetireListSlot(_menuSlot);
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
            ReviveListSlot(_menuSlot, _menu);
            _menuShown = true;
            if (_editor is not null && !AutomationProperties.GetControlledPeers(_editor).Contains(_menuList))
                AutomationProperties.GetControlledPeers(_editor).Add(_menuList);
        }

        _menuRing.IsActive = p.Status == OpenCodeComposerMenuStatus.Loading;
        _menuRing.Visibility = _menuRing.IsActive ? Visibility.Visible : Visibility.Collapsed;
        _menuRetry.Visibility = p.Status == OpenCodeComposerMenuStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
        _menuRetry.IsEnabled = _snapshot is { IsBusy: false };
        ToolTipService.SetToolTip(_menuRetry, "刷新");

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
                    // Rows arrive grouped (FluentCode first, then server); each group gets one non-selectable header.
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
    // a list whose rows come ONLY from the registered agent/skill catalogs the page provides.
    // Choosing a row edits the editor like typing/paste (native TextBox → TextChanging ledger); it never submits.
    // ------------------------------------------------------------------

    /// <summary>
    /// Page-provided real file-index search for the CURRENT workspace, or null when there is no client/directory
    /// (then "上下文（@）" is disabled with an honest tooltip and a typed "@" stays plain text).
    /// </summary>
    internal Func<OpenCodeComposerMentionSearch?>? MentionSearchProvider { get; set; }
    internal event Action<OpenCodeComposerToken, bool>? ModelUndoRequested;
    internal event Action<OpenCodeComposerToken, OpenCodeComposerSelection>? DocumentSelectionChanged;
    private void OnModelUndoRequested(bool redo)
    {
        if (_composing || !PrepareSubmission()) return;
        ModelUndoRequested?.Invoke(_source().Token, redo);
        RequestRender();
    }

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

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_mentionList);
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
        FluentCode.Motion.FluentMotion.BindChildrenTransitions(slot, () => new TransitionCollection { new EntranceThemeTransition { FromVerticalOffset = 8 } });
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
        if (!ReferenceEquals(sender, _editor)) return;
        if (_editor is { } editor && !_composing && !_guardedWrite)
            DocumentSelectionChanged?.Invoke(_source().Token, editor.NativeSelection);
        if (!_mentionShown) return;
        if (MentionAtCaret() is not { Present: true } trigger || trigger.Start != _mentionTrigger.Start) CloseMention();
    }

    /// <summary>
    /// After a flushed TextChanged: a USER edit (TextChanging outside the guarded writer) leaving a legal "@" token under
    /// the caret opens/refreshes the list; anything else closes it. Never while composing, read-only, busy, or while the
    /// command catalog is shown.
    /// </summary>
    private void EvaluateMentionTrigger()
    {
        // Model-originated user edits (e.g. + > 上下文 or typing during chip paint) ACK before
        // the native surface is writable. Keep their arm until NativeLayoutReady rechecks it.
        if (_mentionArmed && _editor?.NativeLayoutPending == true) return;
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
        ReviveListSlot(_mentionSlot, _mentionPanel);
        if (_editor is not null && !AutomationProperties.GetControlledPeers(_editor).Contains(_mentionList))
            AutomationProperties.GetControlledPeers(_editor).Add(_mentionList);
        RequestRender(); // Margin/eligibility follow the visible list.
    }

    internal void RefreshMentionCandidates()
    {
        if (!_dispatcher.HasThreadAccess) { _dispatcher.TryEnqueue(RefreshMentionCandidates); return; }
        if (!_mentionShown) return;
        CloseMention();
        _mentionArmed = true;
        EvaluateMentionTrigger();
    }

    private void CloseMention()
    {
        _mentionQueries.Cancel();
        _mentionRows = Array.Empty<OpenCodeComposerMentionCandidate>();
        _mentionHighlight = -1;
        _mentionTrigger = OpenCodeComposerMentionTrigger.None;
        if (!_mentionShown) return;
        _mentionShown = false;
        RetireListSlot(_mentionSlot);
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
        SetMentionStatus(loading: true, "正在读取 agent 和 skill…");
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
            ? (query.Length == 0 ? "Agent 和 skill" : $"{rows.Count} 个匹配")
            : "没有匹配的 agent 或 skill"));
        _mentionHighlight = rows.Count > 0 ? 0 : -1;
        ApplyMentionHighlight();
    }

    private ListViewItem MentionItem(OpenCodeComposerMentionCandidate candidate)
    {
        var name = candidate.Id;
        var parent = candidate.IsSkill ? "Skill" : "Agent";
        // Agents use the same per-agent glyph and colour as the agent picker/toolbar; skills keep their neutral glyph.
        var icon = candidate.IsSkill
            ? new FontIcon { Glyph = "\uE734", FontSize = 14, Opacity = 0.9 }
            : OpenCodeAgentIconTheme.Create(candidate.Id, candidate.Color, 14);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Width = 16; // One icon column width for agents and skills.
        var title = new TextBlock
        {
            Text = "@" + OpenCodeComposerInputPolicy.Bound(name, 64),
            FontFamily = MonoFont,
            FontWeight = FontWeights.SemiBold,
            MaxWidth = 260, // Bound the Auto column so long IDs trim and the context kind stays visible.
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 4, 0, 4), Tag = candidate };
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
        AutomationProperties.SetName(item, parent + " " + candidate.Id);
        ToolTipService.SetToolTip(item, candidate.Description is { Length: > 0 } description ? description : "@" + candidate.Id);
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
    /// Replace the "@query" token under the caret with the chosen registered ID. A user-gesture edit through the native
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
        editor.Select(trigger.Start, trigger.Length);
        var document = editor.ModelDocument;
        var selection = editor.NativeSelection;
        var next = document.Replace(selection.Start, selection.End - selection.Start, new OpenCodeComposerRun[]
        {
            new OpenCodeComposerInlineToken(Guid.NewGuid(), OpenCodeComposerInlineKind.Mention, candidate.Id, candidate.Id,
                candidate.IsSkill, Color: candidate.Color), new OpenCodeComposerTextRun(" "),
        });
        editor.StageDocument(next, new(selection.Start + 2, selection.Start + 2));
        FlushForIntent();
        RequestRender();
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
            Text = origin == OpenCodeComposerCommandOrigin.FluentCode ? "FluentCode 本机操作" : "服务端命令",
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
            MaxWidth = 280, // Horizontal stacks measure unbounded; cap so long names trim instead of clipping hints.
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
        ToolTipService.SetToolTip(item, row.Selectable ? "/" + row.Name : row.Unavailable switch
        {
            "需要先清空输入框、已选命令和附件" or "需要输入框、已选命令和附件都为空" => "请先清空输入",
            _ => row.Unavailable ?? "/" + row.Name,
        });
        return item;
    }

    private static string OriginLabel(OpenCodeComposerCommandOrigin origin)
        => origin == OpenCodeComposerCommandOrigin.FluentCode ? "FluentCode 本机操作" : "服务器命令";

    /// <summary>Small origin tag. Server commands run on the OpenCode service; FluentCode actions run in this app.</summary>
    private static Border OriginBadge(OpenCodeComposerCommandOrigin origin, bool emphasised)
    {
        var native = origin == OpenCodeComposerCommandOrigin.FluentCode;
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
                Text = native ? "FluentCode" : "服务器",
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
            ToolTipService.SetToolTip(_commandChip, $"命令 /{bounded}");
            AutomationProperties.SetName(_commandChipRemove, $"移除命令 /{bounded}（保留参数文本）");
            ToolTipService.SetToolTip(_commandChipRemove, "移除命令");
            _commandChipRemove.IsEnabled = canEdit;
        }

        // Native intent: separate from the server command, attachments and text, which all stay untouched.
        var intent = s.ClientIntent;
        _intentChip.Visibility = intent is not null ? Visibility.Visible : Visibility.Collapsed;
        if (intent is not null)
        {
            var name = NativeName(intent.Kind);
            _intentChipText.Text = "/" + name;
            var label = $"FluentCode 本机操作 /{name}：{OpenCodeComposerCommandMenuSession.NativeDescription(intent.Kind)}";
            AutomationProperties.SetName(_intentChip, label);
            ToolTipService.SetToolTip(_intentChip, label);
            _intentRun.IsEnabled = s.CanInvokeClientAction;
            AutomationProperties.SetName(_intentRun, $"执行本机操作 /{name}");
            ToolTipService.SetToolTip(_intentRun, "执行（Enter）");
            AutomationProperties.SetName(_intentRemove, $"取消本机操作 /{name}（保留输入内容）");
            ToolTipService.SetToolTip(_intentRemove, "取消");
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
        var name = OpenCodeComposerInputPolicy.Bound(a.Presentation?.Name ?? a.Name, OpenCodeComposerInputPolicy.MaxChipNameLength);
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
        var chip = AttachmentTile(a, name, remove, anchor => OpenAttachmentImage(a, name, generation, renderedChip, anchor));
        renderedChip = chip;
        return chip;
    }

    /// <summary>Viewing is read-only: only a still-rendered tile of the live view may open its own bytes.</summary>
    private void OpenAttachmentImage(OpenCodeComposerAttachmentRow a, string name, int generation, FrameworkElement? rendered, FrameworkElement anchor)
    {
        if (a.Presentation is not { } image || !_lifetime.IsLive || generation != _lifetime.Generation || rendered is null
            || !_attachmentChips.TryGetValue(a.Id, out var live) || !ReferenceEquals(rendered, live)) return;
        AttachmentImageOpened?.Invoke(this, new(a.Id, name, a.Mime.StartsWith("image/", StringComparison.Ordinal) ? a.Mime : "image/png",
            image.Thumbnail, anchor));
    }

    // ------------------------------------------------------------------
    // Attachment tiles: images are a small center-cropped square (hover/focus = larger thumbnail,
    // click/Enter = full image); files keep the same height with type glyph + name. No sizes.
    // ------------------------------------------------------------------

    internal const double AttachmentTileSize = 52;
    private const double AttachmentPreviewSize = 240;

    private FrameworkElement AttachmentTile(OpenCodeComposerAttachmentRow a, string name, Button remove, Action<FrameworkElement> openImage)
    {
        var root = new Grid { Height = AttachmentTileSize, VerticalAlignment = VerticalAlignment.Center, Tag = remove };
        AutomationProperties.SetAutomationId(root, "OpenCodeComposerAttachment");
        AutomationProperties.SetName(root, "附件 " + name);
        if (a.Presentation is { } image)
        {
            var picture = new Image
            {
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _ = LoadAttachmentImageAsync(picture, image.Thumbnail, AttachmentTileSize, fill: true);
            var tile = new Button
            {
                Content = new Border { CornerRadius = new CornerRadius(7), Child = picture },
                Width = AttachmentTileSize,
                Height = AttachmentTileSize,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            AutomationProperties.SetAutomationId(tile, "OpenCodeComposerAttachmentImage");
            AutomationProperties.SetName(tile, "查看图片 " + name);
            ToolTipService.SetToolTip(tile, AttachmentImagePreview(image, name));
            tile.Click += (_, _) => openImage(tile);
            root.Children.Add(tile);
        }
        else
        {
            var label = new TextBlock
            {
                Text = name,
                FontSize = 12,
                MaxWidth = 112,
                MaxLines = 2,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var face = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            face.Children.Add(new FontIcon
            {
                Glyph = OpenCodeComposerInputPolicy.GlyphForMime(a.Mime),
                FontSize = 18,
                VerticalAlignment = VerticalAlignment.Center,
                Style = ThemedForeground("FontIcon", "TextFillColorSecondaryBrush"),
            });
            face.Children.Add(label);
            var file = new Border
            {
                MinWidth = AttachmentTileSize,
                Padding = new Thickness(12, 0, 28, 0),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Style = ThemedBorder("ControlFillColorSecondaryBrush", "ControlStrokeColorDefaultBrush"),
                Child = face,
            };
            AutomationProperties.SetAutomationId(file, "OpenCodeComposerAttachmentFile");
            ToolTipService.SetToolTip(file, $"{a.Name} · {a.Mime}");
            root.Children.Add(file);
        }

        StyleAttachmentRemove(remove);
        root.Children.Add(remove);
        // × stays reachable: revealed by hover or keyboard focus; once touched/pen-pressed it stays visible.
        bool hovered = false, focused = false, touched = false;
        void Reveal() => remove.Opacity = hovered || focused || touched ? 1 : 0;
        root.PointerEntered += (_, e) =>
        {
            hovered = true;
            if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) touched = true;
            Reveal();
        };
        root.PointerExited += (_, _) => { hovered = false; Reveal(); };
        root.GotFocus += (_, _) => { focused = true; Reveal(); };
        root.LostFocus += (_, _) => { focused = false; Reveal(); };
        Reveal();
        return root;
    }

    private static ToolTip AttachmentImagePreview(OpenCodeComposerAttachmentPresentation image, string name)
    {
        var picture = new Image
        {
            Stretch = Stretch.Uniform,
            MaxWidth = AttachmentPreviewSize,
            MaxHeight = AttachmentPreviewSize,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _ = LoadAttachmentImageAsync(picture, image.Thumbnail, AttachmentPreviewSize, fill: false);
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(new Border { CornerRadius = new CornerRadius(4), Child = picture, HorizontalAlignment = HorizontalAlignment.Left });
        var caption = Caption(name, 0.75);
        caption.MaxLines = 1;
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        caption.MaxWidth = AttachmentPreviewSize;
        content.Children.Add(caption);
        var tip = new ToolTip { Content = content };
        AutomationProperties.SetAutomationId(tip, "OpenCodeComposerAttachmentPreview");
        return tip;
    }

    private static void StyleAttachmentRemove(Button remove)
    {
        var rest = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xB8, 0x1C, 0x1C, 0x1C));
        var over = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xE6, 0x1C, 0x1C, 0x1C));
        var pressed = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x00, 0x00, 0x00));
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        var dim = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        remove.Resources["ButtonBackground"] = rest;
        remove.Resources["ButtonBackgroundPointerOver"] = over;
        remove.Resources["ButtonBackgroundPressed"] = pressed;
        remove.Resources["ButtonBackgroundDisabled"] = rest;
        remove.Resources["ButtonForeground"] = white;
        remove.Resources["ButtonForegroundPointerOver"] = white;
        remove.Resources["ButtonForegroundPressed"] = white;
        remove.Resources["ButtonForegroundDisabled"] = dim;
        remove.Background = rest;
        remove.Foreground = white;
        remove.Width = 22;
        remove.Height = 22;
        remove.CornerRadius = new CornerRadius(11);
        remove.Margin = new Thickness(0, 3, 3, 0);
        remove.HorizontalAlignment = HorizontalAlignment.Right;
        remove.VerticalAlignment = VerticalAlignment.Top;
        if (remove.Content is FontIcon glyph) glyph.FontSize = 9;
        FluentCode.Motion.FluentMotion.Bind(remove, enabled => remove.OpacityTransition = enabled
            ? new ScalarTransition { Duration = FluentCode.Motion.FluentMotion.Duration(FluentCode.Motion.FluentMotion.Faster) } : null);
    }

    /// <summary>
    /// "/" and "@" lists leave with a 83ms fade sinking 4px, then the slot empties and collapses. The entrance stays the
    /// slot's EntranceThemeTransition (167ms rise from 8px) and plays when <see cref="ReviveListSlot"/> re-adds the panel.
    /// </summary>
    private static void RetireListSlot(Grid slot)
        => FluentCode.Motion.FluentMotion.SlideFadeOut(slot, new System.Numerics.Vector3(0, 4, 0), FluentCode.Motion.FluentMotion.Faster, () =>
        {
            slot.Children.Clear();
            slot.Visibility = Visibility.Collapsed;
            FluentCode.Motion.FluentMotion.Settle(slot);
        });

    /// <summary>Shows a list slot again; cancels a pending exit (the panel may still be attached) without a double add.</summary>
    private static void ReviveListSlot(Grid slot, UIElement panel)
    {
        FluentCode.Motion.FluentMotion.Settle(slot);
        slot.Visibility = Visibility.Visible;
        if (!slot.Children.Contains(panel))
        {
            slot.Children.Clear();
            slot.Children.Add(panel);
        }
    }

    /// <summary>Decodes at display size (logical px): <paramref name="fill"/> sizes the shorter side, else the longer; never upscales.</summary>
    private static async Task LoadAttachmentImageAsync(Image image, byte[] bytes, double side, bool fill)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); }
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var (width, height) = (decoder.PixelWidth, decoder.PixelHeight);
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelType = DecodePixelType.Logical };
            var target = (int)Math.Ceiling(side);
            if ((fill ? Math.Min(width, height) : Math.Max(width, height)) > target)
            {
                if ((width <= height) == fill) bitmap.DecodePixelWidth = target;
                else bitmap.DecodePixelHeight = target;
            }
            await bitmap.SetSourceAsync(stream);
            image.Source = bitmap;
        }
        catch (Exception) { image.Source = null; }
    }

    // ------------------------------------------------------------------
    // Status line: recovery/inactive draft > picking > local note > protection > controller note.
    // Submission progress belongs only to the send button, never a second row above the editor.
    // ------------------------------------------------------------------

    private void RenderStatus(OpenCodeComposerSnapshot s, bool enhanced, bool inactiveBound)
    {
        string? text = null;
        string glyph = "\uE946"; // Info
        var tone = "neutral";
        var busy = false;
        if (_slotUnavailableReason is { } unavailable)
        {
            text = unavailable;
            glyph = "\uE7BA";
            tone = "caution";
        }
        else if (inactiveBound)
        {
            text = (s.Note ?? "草稿属于原会话；请返回原会话继续。")
                + " 此草稿含命令或附件，不会转到当前会话。";
            glyph = "\uE7BA";
            tone = "caution";
        }
        else if (_sync?.State is OpenCodeComposerInputState.HeldStale or OpenCodeComposerInputState.HeldUnbound)
        {
            text = "这段输入未能同步到当前草稿，已保留但不能发送；继续编辑可恢复到当前草稿，也可清空并恢复。";
            glyph = "\uE7BA";
            tone = "caution";
        }
        else if (s.IsBusy && _picking)
        {
            busy = true;
            text = "正在选择或读取附件…";
        }
        else if (_localNote is not null)
        {
            text = _localNote;
            glyph = "\uE7BA";
            tone = "caution";
        }
        else if (!s.IsBusy && s.IsActive && enhanced && s.IsProtected)
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

        if (_sideQuestionPending > 0 && !s.IsBusy)
        {
            var pending = $"/btw ({_sideQuestionPending}) · 正在回答旁路问题…";
            // Visual only: on its own the pending answer reads as quiet progress; protection notices keep their icon/tone.
            if (text is null) busy = true;
            text = text is null ? pending : text + " · " + pending;
        }
        _recoveryClear.Visibility = _sync?.State is OpenCodeComposerInputState.HeldStale or OpenCodeComposerInputState.HeldUnbound
            ? Visibility.Visible : Visibility.Collapsed;
        _recoveryClear.IsEnabled = !_composing;
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
        AutomationProperties.SetName(_statusBar, text);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

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
