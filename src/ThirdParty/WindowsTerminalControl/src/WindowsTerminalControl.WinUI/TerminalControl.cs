using Microsoft.Terminal.Wpf;
using Microsoft.UI.Content;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.DirectComposition;
using Windows.Win32.Graphics.Dxgi;
using WinRT;
using WinUIEx.Messaging;
using WindowsTerminalControl.Core;

namespace WindowsTerminalControl.WinUI;

#pragma warning disable CS8305

/// <summary>
/// Hosts the native Windows Terminal renderer in a WinUI 3 control.
/// </summary>
public sealed partial class TerminalControl : UserControl, IDisposable
{
    private const uint DefaultDpi = 96;
    private readonly bool _useComposition;
    private readonly Grid _presenter;
    private readonly NativeMethods.ScrollCallback _scrollCallback;
    private readonly ScrollBar _terminalScrollBar;
    private readonly NativeMethods.WriteCallback _writeCallback;
#if HAS_CONTENT_EXTERNAL_OUTPUT_LINK
    private ContentExternalOutputLink? _contentExternalOutputLink;
#endif
    private IDCompositionDevice? _compositionDevice;
    private IDCompositionTarget? _compositionTarget;
    private IDCompositionVisual? _compositionVisual;
    private object? _controlSurface;
    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dDeviceContext;
    private ITerminalConnection? _connection;
    private TerminalSafeHandle? _terminal;
    private WindowMessageMonitor? _terminalMessageMonitor;
    private nint _terminalHostWindow;
    private nint _terminalWindow;
    private int _columns = 120;
    private int _rows = 30;
    private int _windowHeight = -1;
    private int _windowWidth = -1;
    private int _windowX = int.MinValue;
    private int _windowY = int.MinValue;
    private int _pressedMouseButtons;
    private long _lastClickTime;
    private int _lastClickButton;
    private Point _lastClickPosition;
    private bool _connectionStarted;
    private bool _disposed;
    private bool _initialized;
    private bool _updatingScrollBar;
    private int _wheelDelta;
    private TerminalSize? _fixedSize;
    private bool _fixedGridApplied;
    private NativeMethods.TerminalSizeNative _fixedPixels;
    private bool _hostVisible = true;
    private XamlRoot? _subscribedRoot;
    private TerminalAppearance _appearance = new();
    private long _nativeDownTime;
    private int _nativeDownX, _nativeDownY;
    private bool _nativeUpNearDown, _swallowLeftUp, _swallowAccelerator;

    /// <summary>Live font, palette and cursor settings without replacing the terminal parser.</summary>
    public TerminalAppearance Appearance
    {
        get => _appearance;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _appearance = value;
            ApplyTheme();
            _fixedGridApplied = false; _windowWidth = -1;
            UpdateLayoutAndTerminalSize();
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TerminalControl"/> class using composition hosting.
    /// </summary>
    public TerminalControl() : this(TerminalHostMode.Composition)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TerminalControl"/> class.
    /// </summary>
    /// <param name="hostMode">The native renderer hosting mode.</param>
    public TerminalControl(TerminalHostMode hostMode)
    {
        _useComposition = hostMode == TerminalHostMode.Composition;
        _presenter = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 12, 12, 12))
        };
        _terminalScrollBar = new ScrollBar
        {
            Width = 14,
            HorizontalAlignment = HorizontalAlignment.Right,
            IndicatorMode = ScrollingIndicatorMode.MouseIndicator,
            IsTabStop = false,
            Orientation = Orientation.Vertical,
            SmallChange = 1
        };
        InitializeVisualTree();

        _writeCallback = OnNativeWrite;
        _scrollCallback = OnNativeScroll;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        LayoutUpdated += OnLayoutUpdated;
        SizeChanged += OnSizeChanged;
        GotFocus += OnGotFocus;
        LostFocus += OnLostFocus;
        ActualThemeChanged += (_, _) => { ApplyTheme(); _fixedGridApplied = false; _windowWidth = -1; UpdateLayoutAndTerminalSize(); };
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        CharacterReceived += OnCharacterReceived;

        _presenter.PointerMoved += OnPointerMoved;
        _presenter.PointerPressed += OnPointerPressed;
        _presenter.PointerReleased += OnPointerReleased;
        _presenter.PointerWheelChanged += OnPointerWheelChanged;
        _presenter.PointerCanceled += OnPointerCanceled;
        _presenter.PointerCaptureLost += OnPointerCaptureLost;
        _presenter.PointerExited += OnPointerExited;
        _terminalScrollBar.ValueChanged += OnScrollBarValueChanged;
    }

    /// <summary>
    /// Occurs when the native terminal renderer cannot be initialized.
    /// </summary>
    public event EventHandler<Exception>? InitializationFailed;

    /// <summary>
    /// Occurs after the native terminal renderer is initialized.
    /// </summary>
    public event EventHandler? Initialized;

    /// <summary>
    /// Gets or sets the terminal transport connection.
    /// </summary>
    public ITerminalConnection? Connection
    {
        get => _connection;
        set
        {
            if (ReferenceEquals(_connection, value))
            {
                return;
            }

            DetachConnection();
            _connection = value;
            AttachConnection();
        }
    }

    /// <summary>
    /// Gets a value indicating whether the composition host is active.
    /// </summary>
    public bool IsCompositionActive =>
#if HAS_CONTENT_EXTERNAL_OUTPUT_LINK
        _initialized && _contentExternalOutputLink is not null;
#else
        false;
#endif

    /// <summary>
    /// Gets a value indicating whether composition hosting was requested.
    /// </summary>
    public bool UsesComposition => _useComposition;

    /// <summary>Authoritative remote grid. Null retains upstream auto-fit behavior.</summary>
    public TerminalSize? FixedSize
    {
        get => _fixedSize;
        set
        {
            if (value is { } size && (size.Columns < 2 || size.Rows < 1 || size.Columns > 1000 || size.Rows > 1000))
                throw new ArgumentOutOfRangeException(nameof(value));
            _fixedSize = value; _fixedGridApplied = false; _windowWidth = -1;
            UpdateLayoutAndTerminalSize();
        }
    }

    /// <summary>Current parser geometry, independent of the visible clipped host extent.</summary>
    public TerminalSize TerminalSize => new((ushort)_columns, (ushort)_rows);

    /// <summary>Borrowed HWND for non-destructive UI Automation and diagnostics.</summary>
    public nint NativeWindowHandle => _terminalWindow;

    /// <summary>Whether the native HWND owns keyboard focus.</summary>
    public bool HasTerminalFocus => CanUseTerminal && NativeMethods.GetFocus() == _terminalWindow;

    /// <summary>Whether native selection currently exists; never retrieves/clears its text.</summary>
    public bool HasSelection => CanUseTerminal && NativeMethods.TerminalIsSelectionActive(_terminal!);

    /// <summary>Restores coordinated XAML/native focus after a temporary airspace suppression.</summary>
    public void FocusTerminal()
    {
        if (!CanUseTerminal || !_hostVisible) return;
        Focus(FocusState.Programmatic);
        FocusNativeTerminal();
    }

    /// <summary>Controls airspace without destroying parser state or acquiring focus.</summary>
    public void SetHostVisible(bool visible)
    {
        if (_hostVisible == visible) return;
        _hostVisible = visible;
        if (!CanUseTerminal) return;
        if (_terminalHostWindow != 0) NativeMethods.ShowWindow(_terminalHostWindow, visible ? NativeMethods.ShowWindowNoActivate : 0);
        NativeMethods.ShowWindow(_terminalWindow, visible ? NativeMethods.ShowWindowNoActivate : 0);
    }

    /// <summary>Proposes a grid for explicit Fit without resizing the local parser or remote PTY.</summary>
    public TerminalSize MeasureViewport()
    {
        if (!CanUseTerminal || XamlRoot is null) throw new InvalidOperationException("Terminal is not initialized.");
        var scale = XamlRoot.RasterizationScale;
        Marshal.ThrowExceptionForHR(NativeMethods.TerminalCalculateResize(_terminal!,
            Math.Max(1, (int)Math.Floor(_presenter.ActualWidth * scale)),
            Math.Max(1, (int)Math.Floor(_presenter.ActualHeight * scale)), out var dimensions));
        return new TerminalSize(checked((ushort)dimensions.X), checked((ushort)dimensions.Y));
    }

    /// <summary>
    /// Gets or initializes an optional provider for the control origin relative to the app window, in logical pixels.
    /// </summary>
    public Func<Point>? WindowOriginProvider { get; init; }

    /// <summary>
    /// Updates the native renderer bounds after its surrounding window layout changes.
    /// </summary>
    public void UpdateHostBounds()
    {
        UpdateLayoutAndTerminalSize();
    }

    /// <summary>
    /// Copies the active terminal selection to the clipboard.
    /// </summary>
    public void CopySelection()
    {
        if (!CanUseTerminal || !NativeMethods.TerminalIsSelectionActive(_terminal!))
        {
            return;
        }

        var selection = NativeMethods.TerminalGetSelection(_terminal!);
        if (!string.IsNullOrEmpty(selection))
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(selection);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
    }

    /// <summary>Hides the built-in scrollbar so a host can draw its own outside the native window.</summary>
    public bool ShowScrollBar
    {
        get => _terminalScrollBar.Visibility == Visibility.Visible;
        set => _terminalScrollBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Current local scrollback position.</summary>
    public TerminalScrollState ScrollState
        => new((int)_terminalScrollBar.Value, (int)_terminalScrollBar.ViewportSize, (int)(_terminalScrollBar.Maximum + _terminalScrollBar.ViewportSize));

    /// <summary>Raised after native output or a local scroll moves the viewport or grows the buffer.</summary>
    public event EventHandler<TerminalScrollState>? ScrollStateChanged;

    /// <summary>Scrolls local history only; never writes to or resizes the connection.</summary>
    public void ScrollTo(int viewTop)
    {
        if (!CanUseTerminal) return;
        _terminalScrollBar.Value = Math.Clamp(viewTop, 0, Math.Max(0, (int)_terminalScrollBar.Maximum));
    }

    /// <summary>Primary-button presses on the native window, raised before the renderer sees them.</summary>
    public event EventHandler<TerminalClickEventArgs>? TerminalClicked;

    /// <summary>Host shortcuts: (key, ctrl, shift) returns true to consume the key instead of sending it to the terminal.</summary>
    public Func<VirtualKey, bool, bool, bool>? AcceleratorFilter { get; set; }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_subscribedRoot is not null) { _subscribedRoot.Changed -= OnXamlRootChanged; _subscribedRoot = null; }
        LayoutUpdated -= OnLayoutUpdated;
        DetachConnection();
        CleanupComposition();
        DetachTerminalMessageMonitor();
        _terminal?.Dispose();
        _terminal = null;
        DestroyTerminalHostWindow();
        _terminalWindow = nint.Zero;
        GC.SuppressFinalize(this);
    }

    private bool CanUseTerminal => !_disposed && _terminal is not null && !_terminal.IsInvalid && !_terminal.IsClosed;

    private void InitializeVisualTree()
    {
        IsTabStop = true;
        AutomationProperties.SetName(this, "Windows Terminal");

        var root = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 12, 12, 12))
        };
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_terminalScrollBar, 1);
        root.Children.Add(_presenter);
        root.Children.Add(_terminalScrollBar);
        Content = root;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized || _disposed)
        {
            return;
        }

        try
        {
            InitializeTerminal();
            _initialized = true;
            _subscribedRoot = XamlRoot;
            _subscribedRoot.Changed += OnXamlRootChanged;
            AttachConnection();
            if (_hostVisible) Focus(FocusState.Programmatic);
            Initialized?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            CleanupComposition();
            DetachTerminalMessageMonitor();
            _terminal?.Dispose();
            _terminal = null;
            DestroyTerminalHostWindow();
            _terminalWindow = nint.Zero;
            InitializationFailed?.Invoke(this, exception);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }

    private void InitializeTerminal()
    {
        if (_useComposition && !IsCompositionSupported())
        {
            throw new NotSupportedException("Composition hosting requires ContentExternalOutputLink, absent from Windows App SDK 1.8. Select ChildHwnd explicitly; no automatic fallback is performed.");
        }

        var appWindowId = XamlRoot?.ContentIslandEnvironment?.AppWindowId ?? throw new InvalidOperationException("The control is not connected to an AppWindow.");
        var parentWindow = Microsoft.UI.Win32Interop.GetWindowFromWindowId(appWindowId);
        if (parentWindow == nint.Zero)
        {
            throw new InvalidOperationException("The parent HWND could not be resolved.");
        }

        var terminalParent = parentWindow;
        if (!_useComposition)
        {
            var style = _hostVisible ? NativeMethods.WindowStyleTerminalHost : NativeMethods.WindowStyleTerminalHost & ~0x10000000u;
            _terminalHostWindow = NativeMethods.CreateWindowEx(0, "STATIC", null, style, 0, 0, 1, 1, parentWindow, nint.Zero, nint.Zero, nint.Zero);
            if (_terminalHostWindow == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            terminalParent = _terminalHostWindow;
        }

        var result = NativeMethods.CreateTerminal(terminalParent, out _terminalWindow, out _terminal);
        Marshal.ThrowExceptionForHR(result);
        if (!_hostVisible) NativeMethods.ShowWindow(_terminalWindow, 0);
        NativeMethods.TerminalRegisterWriteCallback(_terminal, _writeCallback);
        NativeMethods.TerminalRegisterScrollCallback(_terminal, _scrollCallback);
        AttachTerminalMessageMonitor();

        if (_useComposition)
        {
            MakeTerminalWindowLayered();
        }

        ApplyTheme();
        ApplyFixedGrid();
        UpdateLayoutAndTerminalSize();
        NativeMethods.ShowWindow(_terminalWindow, _hostVisible ? NativeMethods.ShowWindowNoActivate : 0);
        if (_useComposition)
        {
            CreateCompositionSurface();
            SetWindowCloaked(true);
        }
    }

    private void MakeTerminalWindowLayered()
    {
        Marshal.SetLastPInvokeError(0);
        var extendedStyle = NativeMethods.GetWindowLongPtr(_terminalWindow, NativeMethods.WindowLongExtendedStyle);
        if (extendedStyle == nint.Zero && Marshal.GetLastWin32Error() != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        Marshal.SetLastPInvokeError(0);
        var previousStyle = NativeMethods.SetWindowLongPtr(_terminalWindow, NativeMethods.WindowLongExtendedStyle, extendedStyle | NativeMethods.WindowExLayered | NativeMethods.WindowExComposited);
        if (previousStyle == nint.Zero && Marshal.GetLastWin32Error() != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        NativeMethods.SetWindowPos(_terminalWindow, nint.Zero, 0, 0, 0, 0, NativeMethods.SetWindowPosNoMove | NativeMethods.SetWindowPosNoSize | NativeMethods.SetWindowPosNoZOrder | NativeMethods.SetWindowPosNoActivate | NativeMethods.SetWindowPosFrameChanged);
    }

    private void CreateCompositionSurface()
    {
#if HAS_CONTENT_EXTERNAL_OUTPUT_LINK
        var driverTypes = new[] { D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP };
        HRESULT result = default;

        foreach (var driverType in driverTypes)
        {
            result = PInvoke.D3D11CreateDevice(null!, driverType, new HMODULE(nint.Zero), D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, ReadOnlySpan<D3D_FEATURE_LEVEL>.Empty, 7, out _d3dDevice, out _d3dDeviceContext);
            if (result.Succeeded)
            {
                break;
            }
        }

        ThrowIfFailed(result, "D3D11CreateDevice failed.");
        if (_d3dDevice is null)
        {
            throw new InvalidOperationException("D3D11 did not return a device.");
        }

        var dxgiDevice = (IDXGIDevice)_d3dDevice;
        result = PInvoke.DCompositionCreateDevice(dxgiDevice, out _compositionDevice);
        ThrowIfFailed(result, "DCompositionCreateDevice failed.");
        if (_compositionDevice is null)
        {
            throw new InvalidOperationException("DirectComposition did not return a device.");
        }

        result = _compositionDevice.CreateVisual(out _compositionVisual);
        ThrowIfFailed(result, "IDCompositionDevice.CreateVisual failed.");
        result = _compositionDevice.CreateSurfaceFromHwnd(new HWND(_terminalWindow), out _controlSurface);
        ThrowIfFailed(result, "IDCompositionDevice.CreateSurfaceFromHwnd failed.");
        if (_compositionVisual is null || _controlSurface is null)
        {
            throw new InvalidOperationException("The terminal HWND could not be converted into a DirectComposition surface.");
        }

        result = _compositionVisual.SetContent(_controlSurface);
        ThrowIfFailed(result, "IDCompositionVisual.SetContent failed.");

        var compositor = ElementCompositionPreview.GetElementVisual(_presenter).Compositor;
        _contentExternalOutputLink = ContentExternalOutputLink.Create(compositor);
        _contentExternalOutputLink.BackgroundColor = Windows.UI.Color.FromArgb(255, 12, 12, 12);
        _contentExternalOutputLink.IsAboveContent = false;
        _compositionTarget = _contentExternalOutputLink.As<IDCompositionTarget>();
        Marshal.ThrowExceptionForHR(_compositionTarget.SetRoot(_compositionVisual));

        _contentExternalOutputLink.PlacementVisual.Clip = compositor.CreateInsetClip();
        ElementCompositionPreview.SetElementChildVisual(_presenter, _contentExternalOutputLink.PlacementVisual);
        UpdatePlacementVisual();
        ThrowIfFailed(_compositionDevice.Commit(), "IDCompositionDevice.Commit failed.");
#else
        throw new NotSupportedException("ContentExternalOutputLink is not available in the stable Windows App SDK 1.8 build.");
#endif
    }

    private static bool IsCompositionSupported()
    {
#if HAS_CONTENT_EXTERNAL_OUTPUT_LINK
        return ContentExternalOutputLink.IsSupported();
#else
        return false;
#endif
    }

    private void ApplyTheme()
    {
        if (!CanUseTerminal)
        {
            return;
        }

        var colors = _appearance.Colors(ActualTheme == ElementTheme.Light);
        var cursor = _appearance.CursorShape switch { "Block" => 1, "Underline" => 3, _ => 5 };
        var theme = new NativeMethods.TerminalTheme
        {
            DefaultBackground = colors.Background,
            DefaultForeground = colors.Foreground,
            DefaultSelectionBackground = colors.Selection,
            CursorStyle = (NativeMethods.CaretStyle)(cursor + (_appearance.CursorBlink ? 0 : 1)),
            ColorTable = colors.Palette
        };

        var color = Windows.UI.Color.FromArgb(255, (byte)colors.Background, (byte)(colors.Background >> 8), (byte)(colors.Background >> 16));
        _presenter.Background = new SolidColorBrush(color);
        if (Content is Grid root) root.Background = new SolidColorBrush(color);
        NativeMethods.TerminalSetTheme(_terminal!, theme, _appearance.FontFamily, _appearance.FontSize, GetDpi());
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLayoutAndTerminalSize();
    }

    private void ApplyFixedGrid()
    {
        if (_fixedGridApplied || _fixedSize is not { } grid || !CanUseTerminal) return;
        Marshal.ThrowExceptionForHR(NativeMethods.TerminalTriggerResizeWithDimension(_terminal!,
            new NativeMethods.TerminalSizeNative { X = grid.Columns, Y = grid.Rows }, out _fixedPixels));
        _columns = grid.Columns; _rows = grid.Rows;
        _fixedGridApplied = true;
    }

    private void OnLayoutUpdated(object? sender, object e)
    {
        UpdateLayoutAndTerminalSize();
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (CanUseTerminal)
        {
            NativeMethods.TerminalDpiChanged(_terminal!, GetDpi());
            ApplyTheme();
            _fixedGridApplied = false; _windowWidth = -1;
        }

        UpdateLayoutAndTerminalSize();
    }

    private void UpdateLayoutAndTerminalSize()
    {
        if (!CanUseTerminal || _presenter.ActualWidth <= 0 || _presenter.ActualHeight <= 0 || XamlRoot is null)
        {
            return;
        }

        var scale = XamlRoot.RasterizationScale;
        var width = Math.Max(1, checked((int)Math.Round(_presenter.ActualWidth * scale)));
        var height = Math.Max(1, checked((int)Math.Round(_presenter.ActualHeight * scale)));
        var origin = GetPresenterOriginInPixels(scale);
        var sizeChanged = width != _windowWidth || height != _windowHeight;
        var boundsChanged = sizeChanged || origin.X != _windowX || origin.Y != _windowY;
        if (!boundsChanged)
        {
            return;
        }

        if (sizeChanged)
        {
            NativeMethods.TerminalSizeNative dimensions;
            int result;
            if (_fixedSize is { } grid)
            {
                if (!_fixedGridApplied)
                {
                    ApplyFixedGrid();
                }
                dimensions = new NativeMethods.TerminalSizeNative { X = grid.Columns, Y = grid.Rows };
                result = 0;
            }
            else result = NativeMethods.TerminalTriggerResize(_terminal!, width, height, out dimensions);
            Marshal.ThrowExceptionForHR(result);
            if (dimensions.X > 0 && dimensions.Y > 0 && (dimensions.X != _columns || dimensions.Y != _rows))
            {
                _columns = dimensions.X;
                _rows = dimensions.Y;
                _connection?.Resize(checked((uint)_rows), checked((uint)_columns));
            }
        }

        if (_useComposition)
        {
            NativeMethods.SetWindowPos(_terminalWindow, nint.Zero, origin.X, origin.Y, width, height, NativeMethods.SetWindowPosNoZOrder | NativeMethods.SetWindowPosNoActivate);
        }
        else
        {
            NativeMethods.SetWindowPos(_terminalHostWindow, nint.Zero, origin.X, origin.Y, width, height, NativeMethods.SetWindowPosNoActivate);
            NativeMethods.SetWindowPos(_terminalWindow, nint.Zero, 0, 0,
                _fixedSize is null ? width : _fixedPixels.X, _fixedSize is null ? height : _fixedPixels.Y,
                NativeMethods.SetWindowPosNoZOrder | NativeMethods.SetWindowPosNoActivate);
        }

        _windowX = origin.X;
        _windowY = origin.Y;
        _windowWidth = width;
        _windowHeight = height;

        UpdatePlacementVisual();
    }

    private (int X, int Y) GetPresenterOriginInPixels(double scale)
    {
        try
        {
            var point = WindowOriginProvider?.Invoke() ?? _presenter.TransformToVisual(null).TransformPoint(new Point(0, 0));

            return (checked((int)Math.Round(point.X * scale)), checked((int)Math.Round(point.Y * scale)));
        }
        catch (InvalidOperationException)
        {
            return (0, 0);
        }
    }

    private void UpdatePlacementVisual()
    {
#if HAS_CONTENT_EXTERNAL_OUTPUT_LINK
        if (_contentExternalOutputLink is null || XamlRoot is null || _presenter.ActualWidth <= 0 || _presenter.ActualHeight <= 0)
        {
            return;
        }

        var scale = (float)XamlRoot.RasterizationScale;
        var pixelWidth = (float)Math.Max(1, Math.Round(_presenter.ActualWidth * scale));
        var pixelHeight = (float)Math.Max(1, Math.Round(_presenter.ActualHeight * scale));
        _contentExternalOutputLink.PlacementVisual.Size = new Vector2(pixelWidth, pixelHeight);
        _contentExternalOutputLink.PlacementVisual.Scale = new Vector3(1 / scale, 1 / scale, 1);
#endif
    }

    private int GetDpi()
    {
        return checked((int)Math.Round(DefaultDpi * (XamlRoot?.RasterizationScale ?? 1)));
    }

    private void AttachConnection()
    {
        if (!_initialized || _connection is null || _connectionStarted || !CanUseTerminal)
        {
            return;
        }

        _connection.TerminalOutput += OnTerminalOutput;
        _connection.Resize(checked((uint)_rows), checked((uint)_columns));
        _connectionStarted = true;
        _connection.Start();
    }

    private void DetachConnection()
    {
        if (_connection is not null)
        {
            _connection.TerminalOutput -= OnTerminalOutput;
        }

        _connectionStarted = false;
    }

    private void OnTerminalOutput(object? sender, TerminalOutputEventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            SendOutput(e.Data);
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => SendOutput(e.Data));
        }
    }

    private void SendOutput(string data)
    {
        if (CanUseTerminal && !string.IsNullOrEmpty(data))
        {
            NativeMethods.TerminalSendOutput(_terminal!, data);
        }
    }

    private void OnNativeWrite(string data)
    {
        _connection?.WriteInput(data);
    }

    private void OnNativeScroll(int viewTop, int viewHeight, int bufferSize)
    {
        DispatcherQueue.TryEnqueue(() => UpdateScrollBar(viewTop, viewHeight, bufferSize));
    }

    private void UpdateScrollBar(int viewTop, int viewHeight, int bufferSize)
    {
        _updatingScrollBar = true;
        try
        {
            _terminalScrollBar.Minimum = 0;
            _terminalScrollBar.Maximum = Math.Max(0, bufferSize - viewHeight);
            _terminalScrollBar.ViewportSize = Math.Max(1, viewHeight);
            _terminalScrollBar.LargeChange = Math.Max(1, viewHeight - 1);
            _terminalScrollBar.Value = Math.Clamp(viewTop, _terminalScrollBar.Minimum, _terminalScrollBar.Maximum);
        }
        finally
        {
            _updatingScrollBar = false;
        }
        ScrollStateChanged?.Invoke(this, ScrollState);
    }

    private void OnScrollBarValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_updatingScrollBar && CanUseTerminal)
        {
            NativeMethods.TerminalUserScroll(_terminal!, checked((int)e.NewValue));
            RequestRedraw();
            ScrollStateChanged?.Invoke(this, ScrollState);
        }
    }

    private bool _redrawPending;

    /// <summary>
    /// The native user-scroll export moves the viewport without scheduling a frame (WM_PAINT/InvalidateRect do not
    /// help); the window kept showing the old rows until output or a layout change. A same-DPI notification is the
    /// exported call that repaints everything; it is coalesced to one per dispatcher turn during drags and wheel bursts.
    /// </summary>
    private void RequestRedraw()
    {
        if (_redrawPending) return;
        _redrawPending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            _redrawPending = false;
            if (!CanUseTerminal) return;
            NativeMethods.TerminalDpiChanged(_terminal!, GetDpi());
            // A trailing repaint once the scroll settles: the first frame can still be composed from the old rows.
            await System.Threading.Tasks.Task.Delay(80);
            if (!_redrawPending && CanUseTerminal) NativeMethods.TerminalDpiChanged(_terminal!, GetDpi());
        });
    }

    private bool NativeLeftButton(uint message, nint lParam)
    {
        var x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        var y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
        var dx = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SystemMetricDoubleClickWidth) / 2);
        var dy = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SystemMetricDoubleClickHeight) / 2);
        if (message == NativeMethods.WmLeftButtonUp)
        {
            _nativeUpNearDown = Math.Abs(x - _nativeDownX) <= dx && Math.Abs(y - _nativeDownY) <= dy;
            if (_swallowLeftUp) { _swallowLeftUp = false; return true; }
            return false;
        }
        var now = Environment.TickCount64;
        // A double-click is the second press, near the first, within the system time, after a release that did not drag.
        var second = message == NativeMethods.WmLeftButtonDoubleClick
            || _nativeDownTime != 0 && now - _nativeDownTime <= NativeMethods.GetDoubleClickTime() && _nativeUpNearDown
                && Math.Abs(x - _nativeDownX) <= dx && Math.Abs(y - _nativeDownY) <= dy;
        _nativeDownTime = second ? 0 : now; _nativeDownX = x; _nativeDownY = y; _nativeUpNearDown = false;
        var args = new TerminalClickEventArgs(second ? 2 : 1);
        TerminalClicked?.Invoke(this, args);
        if (args.Handled) _swallowLeftUp = true;
        return args.Handled;
    }

    private bool Accelerator(ushort virtualKey)
        => AcceleratorFilter is { } filter && filter((VirtualKey)virtualKey, NativeMethods.IsKeyDown((int)VirtualKey.Control), NativeMethods.IsKeyDown((int)VirtualKey.Shift));

    private bool ScrollViewportWithMouseWheel(nuint wheelState)
    {
        // The standalone child HWND does not implement wheel-to-history scrolling.
        // Use the same local-only API as the scrollbar; never resize or write to the PTY.
        if (_terminalScrollBar.Maximum <= 0 || (wheelState & 0x000C) != 0
            || !NativeMethods.SystemParametersInfo(0x0068, 0, out var lines, 0)) return false;
        _wheelDelta += unchecked((short)(wheelState >> 16));
        var steps = _wheelDelta / 120;
        _wheelDelta %= 120;
        if (steps != 0)
        {
            var distance = lines == uint.MaxValue ? _terminalScrollBar.LargeChange : lines;
            var top = Math.Clamp(_terminalScrollBar.Value - steps * distance, 0, _terminalScrollBar.Maximum);
            _terminalScrollBar.Value = top; // ValueChanged drives the native viewport; the API does not echo a callback.
        }
        return true;
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        FocusNativeTerminal();
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (NativeMethods.GetFocus() != _terminalWindow)
        {
            SetTerminalFocused(false);
        }
    }

    private void FocusNativeTerminal()
    {
        if (!CanUseTerminal || !_hostVisible)
        {
            return;
        }

        NativeMethods.SetFocus(_terminalWindow);
        SetTerminalFocused(true);
    }

    private void AttachTerminalMessageMonitor()
    {
        _terminalMessageMonitor = new WindowMessageMonitor(_terminalWindow);
        _terminalMessageMonitor.WindowMessageReceived += OnTerminalWindowMessage;
    }

    private void DetachTerminalMessageMonitor()
    {
        if (_terminalMessageMonitor is null)
        {
            return;
        }

        _terminalMessageMonitor.WindowMessageReceived -= OnTerminalWindowMessage;
        _terminalMessageMonitor.Dispose();
        _terminalMessageMonitor = null;
    }

    private void OnTerminalWindowMessage(object? sender, WindowMessageEventArgs args)
    {
        if (!CanUseTerminal)
        {
            return;
        }

        var message = args.Message;
        switch (message.MessageId)
        {
            case NativeMethods.WmMouseWheel:
                if (ScrollViewportWithMouseWheel(message.WParam))
                { args.Handled = true; args.Result = nint.Zero; }
                break;

            case NativeMethods.WmSetFocus:
                SetTerminalFocused(true);
                break;

            case NativeMethods.WmKillFocus:
                SetTerminalFocused(false);
                break;

            case NativeMethods.WmMouseActivate:
                Focus(FocusState.Pointer);
                FocusNativeTerminal();
                break;

            case NativeMethods.WmLeftButtonDown:
            case NativeMethods.WmLeftButtonDoubleClick:
            case NativeMethods.WmLeftButtonUp:
                if (NativeLeftButton(message.MessageId, message.LParam)) { args.Handled = true; args.Result = nint.Zero; }
                break;

            case NativeMethods.WmKeyDown:
            case NativeMethods.WmSystemKeyDown:
                UnpackKeyMessage(message.WParam, message.LParam, out var virtualKey, out var scanCode, out var flags);
                if (Accelerator(virtualKey)) { _swallowAccelerator = true; args.Handled = true; args.Result = nint.Zero; break; }
                NativeMethods.TerminalSendKeyEvent(_terminal!, virtualKey, scanCode, flags, true);
                break;

            case NativeMethods.WmKeyUp:
            case NativeMethods.WmSystemKeyUp:
                UnpackKeyMessage(message.WParam, message.LParam, out virtualKey, out scanCode, out flags);
                if (_swallowAccelerator && virtualKey is not (0x10 or 0x11 or 0x12)) { _swallowAccelerator = false; args.Handled = true; args.Result = nint.Zero; break; }
                NativeMethods.TerminalSendKeyEvent(_terminal!, virtualKey, scanCode, flags, false);
                break;

            case NativeMethods.WmChar:
            case NativeMethods.WmSystemChar:
                if (_swallowAccelerator) { args.Handled = true; args.Result = nint.Zero; break; }
                UnpackKeyMessage(message.WParam, message.LParam, out virtualKey, out scanCode, out flags);
                NativeMethods.TerminalSendCharEvent(_terminal!, unchecked((char)virtualKey), scanCode, flags);
                break;
        }
    }

    private static void UnpackKeyMessage(nuint wParam, nint lParam, out ushort virtualKey, out ushort scanCode, out ushort flags)
    {
        var scanCodeAndFlags = unchecked((ulong)lParam.ToInt64()) >> 16;
        virtualKey = unchecked((ushort)wParam);
        scanCode = unchecked((ushort)(scanCodeAndFlags & 0x00FF));
        flags = unchecked((ushort)(scanCodeAndFlags & 0xFF00));
    }

    private void SetTerminalFocused(bool focused)
    {
        if (!CanUseTerminal)
        {
            return;
        }

        NativeMethods.TerminalSetFocused(_terminal!, focused);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!CanUseTerminal)
        {
            return;
        }

        var status = e.KeyStatus;
        if (Accelerator(checked((ushort)e.Key))) { e.Handled = true; return; }
        NativeMethods.TerminalSendKeyEvent(_terminal!, checked((ushort)e.Key), checked((ushort)status.ScanCode), GetKeyFlags(status), true);
        e.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!CanUseTerminal)
        {
            return;
        }

        var status = e.KeyStatus;
        NativeMethods.TerminalSendKeyEvent(_terminal!, checked((ushort)e.Key), checked((ushort)status.ScanCode), GetKeyFlags(status), false);
        e.Handled = true;
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (!CanUseTerminal)
        {
            return;
        }

        var status = args.KeyStatus;
        NativeMethods.TerminalSendCharEvent(_terminal!, checked((char)args.Character), checked((ushort)status.ScanCode), GetKeyFlags(status));
        args.Handled = true;
    }

    private static ushort GetKeyFlags(Windows.UI.Core.CorePhysicalKeyStatus status)
    {
        var flags = 0;
        if (status.IsExtendedKey)
        {
            flags |= 0x0100;
        }

        if (status.IsMenuKeyDown)
        {
            flags |= 0x2000;
        }

        if (status.WasKeyDown)
        {
            flags |= 0x4000;
        }

        if (status.IsKeyReleased)
        {
            flags |= 0x8000;
        }

        return checked((ushort)flags);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        SendPointerMessage(NativeMethods.WmMouseMove, e.GetCurrentPoint(_presenter));
        e.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_presenter);
        var message = GetButtonMessage(point.Properties.PointerUpdateKind, true, point.Position);
        if (message == 0)
        {
            return;
        }

        Focus(FocusState.Pointer);
        FocusNativeTerminal();
        _presenter.CapturePointer(e.Pointer);
        SendPointerMessage(message, point);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_presenter);
        var message = GetButtonMessage(point.Properties.PointerUpdateKind, false, point.Position);
        if (message != 0)
        {
            SendPointerMessage(message, point);
        }

        _presenter.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!CanUseTerminal)
        {
            return;
        }

        var point = e.GetCurrentPoint(_presenter);
        var position = ToPixelPoint(point.Position);
        var screenPoint = new NativeMethods.NativePoint(position.X, position.Y);
        NativeMethods.ClientToScreen(_terminalWindow, ref screenPoint);
        var message = point.Properties.IsHorizontalMouseWheel ? NativeMethods.WmMouseHorizontalWheel : NativeMethods.WmMouseWheel;
        var keyState = GetMouseKeyState(point.Properties);
        var wheelState = unchecked((nuint)((uint)(ushort)keyState | ((uint)(ushort)point.Properties.MouseWheelDelta << 16)));
        NativeMethods.SendMessage(_terminalWindow, message, wheelState, PackPoint(screenPoint.X, screenPoint.Y));
        e.Handled = true;
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        ReleasePressedMouseButtons(e.GetCurrentPoint(_presenter));
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        ReleasePressedMouseButtons(e.GetCurrentPoint(_presenter));
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (CanUseTerminal)
        {
            NativeMethods.SendMessage(_terminalWindow, NativeMethods.WmMouseLeave, 0, nint.Zero);
        }
    }

    private uint GetButtonMessage(PointerUpdateKind updateKind, bool pressed, Point position)
    {
        var button = updateKind switch
        {
            PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => 1,
            PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => 2,
            PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => 3,
            _ => 0
        };

        if (button == 0)
        {
            return 0;
        }

        if (pressed)
        {
            _pressedMouseButtons |= 1 << (button - 1);
            var pixelPosition = ToPixelPoint(position);
            var now = Environment.TickCount64;
            var isDoubleClick = button == _lastClickButton && now - _lastClickTime <= NativeMethods.GetDoubleClickTime() && Math.Abs(pixelPosition.X - _lastClickPosition.X) <= NativeMethods.GetSystemMetrics(NativeMethods.SystemMetricDoubleClickWidth) && Math.Abs(pixelPosition.Y - _lastClickPosition.Y) <= NativeMethods.GetSystemMetrics(NativeMethods.SystemMetricDoubleClickHeight);
            _lastClickButton = isDoubleClick ? 0 : button;
            _lastClickTime = now;
            _lastClickPosition = new Point(pixelPosition.X, pixelPosition.Y);

            return (button, isDoubleClick) switch
            {
                (1, false) => NativeMethods.WmLeftButtonDown,
                (1, true) => NativeMethods.WmLeftButtonDoubleClick,
                (2, false) => NativeMethods.WmRightButtonDown,
                (2, true) => NativeMethods.WmRightButtonDoubleClick,
                (3, false) => NativeMethods.WmMiddleButtonDown,
                (3, true) => NativeMethods.WmMiddleButtonDoubleClick,
                _ => 0
            };
        }

        _pressedMouseButtons &= ~(1 << (button - 1));
        return button switch
        {
            1 => NativeMethods.WmLeftButtonUp,
            2 => NativeMethods.WmRightButtonUp,
            3 => NativeMethods.WmMiddleButtonUp,
            _ => 0
        };
    }

    private void SendPointerMessage(uint message, PointerPoint point)
    {
        if (!CanUseTerminal)
        {
            return;
        }

        var position = ToPixelPoint(point.Position);
        NativeMethods.SendMessage(_terminalWindow, message, GetMouseKeyState(point.Properties), PackPoint(position.X, position.Y));
    }

    private void ReleasePressedMouseButtons(PointerPoint point)
    {
        if ((_pressedMouseButtons & 1) != 0)
        {
            SendPointerMessage(NativeMethods.WmLeftButtonUp, point);
        }

        if ((_pressedMouseButtons & 2) != 0)
        {
            SendPointerMessage(NativeMethods.WmRightButtonUp, point);
        }

        if ((_pressedMouseButtons & 4) != 0)
        {
            SendPointerMessage(NativeMethods.WmMiddleButtonUp, point);
        }

        _pressedMouseButtons = 0;
    }

    private nuint GetMouseKeyState(PointerPointProperties properties)
    {
        var state = 0u;
        if (properties.IsLeftButtonPressed || (_pressedMouseButtons & 1) != 0)
        {
            state |= NativeMethods.MouseKeyLeftButton;
        }

        if (properties.IsRightButtonPressed || (_pressedMouseButtons & 2) != 0)
        {
            state |= NativeMethods.MouseKeyRightButton;
        }

        if (properties.IsMiddleButtonPressed || (_pressedMouseButtons & 4) != 0)
        {
            state |= NativeMethods.MouseKeyMiddleButton;
        }

        if (NativeMethods.IsKeyDown((int)VirtualKey.Shift))
        {
            state |= NativeMethods.MouseKeyShift;
        }

        if (NativeMethods.IsKeyDown((int)VirtualKey.Control))
        {
            state |= NativeMethods.MouseKeyControl;
        }

        return state;
    }

    private (int X, int Y) ToPixelPoint(Point point)
    {
        var scale = XamlRoot?.RasterizationScale ?? 1;

        return (checked((int)Math.Round(point.X * scale)), checked((int)Math.Round(point.Y * scale)));
    }

    private static nint PackPoint(int x, int y)
    {
        return unchecked((nint)((uint)(ushort)x | ((uint)(ushort)y << 16)));
    }

    private void SetWindowCloaked(bool cloaked)
    {
        var value = cloaked ? 1 : 0;
        var result = NativeMethods.DwmSetWindowAttribute(_terminalWindow, NativeMethods.DwmWindowAttributeCloak, ref value, sizeof(int));
        Marshal.ThrowExceptionForHR(result);
    }

    private void CleanupComposition()
    {
        if (_subscribedRoot is not null)
        {
            _subscribedRoot.Changed -= OnXamlRootChanged;
            _subscribedRoot = null;
        }

        if (_useComposition && _terminalWindow != nint.Zero)
        {
            try
            {
                SetWindowCloaked(false);
            }
            catch (Exception)
            {
            }
        }

        ElementCompositionPreview.SetElementChildVisual(_presenter, null);
#if HAS_CONTENT_EXTERNAL_OUTPUT_LINK
        _contentExternalOutputLink?.Dispose();
        _contentExternalOutputLink = null;
#endif
        DisposeComObject(_compositionTarget);
        DisposeComObject(_compositionVisual);
        DisposeComObject(_controlSurface);
        DisposeComObject(_compositionDevice);
        DisposeComObject(_d3dDeviceContext);
        DisposeComObject(_d3dDevice);
        _compositionTarget = null;
        _compositionVisual = null;
        _controlSurface = null;
        _compositionDevice = null;
        _d3dDeviceContext = null;
        _d3dDevice = null;
    }

    private void DestroyTerminalHostWindow()
    {
        if (_terminalHostWindow == nint.Zero)
        {
            return;
        }

        NativeMethods.DestroyWindow(_terminalHostWindow);
        _terminalHostWindow = nint.Zero;
    }

    private static void DisposeComObject(object? value)
    {
        if (value is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static void ThrowIfFailed(HRESULT result, string message)
    {
        if (result.Failed)
        {
            throw new COMException(message, result.Value);
        }
    }

    private static uint ToColorRef(byte red, byte green, byte blue)
    {
        return red | ((uint)green << 8) | ((uint)blue << 16);
    }

    private sealed class TerminalSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private TerminalSafeHandle() : base(true)
        {
        }

        protected override bool ReleaseHandle()
        {
            NativeMethods.DestroyTerminal(handle);

            return true;
        }
    }

    private static class NativeMethods
    {
        private const string TerminalControlLibrary = "Microsoft.Terminal.Control.dll";

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);

        internal const int DwmWindowAttributeCloak = 13;
        internal const int ShowWindowNoActivate = 4;
        internal const int SystemMetricDoubleClickWidth = 36;
        internal const int SystemMetricDoubleClickHeight = 37;
        internal const int WindowLongExtendedStyle = -20;
        internal const nint WindowExLayered = 0x00080000;
        internal const nint WindowExComposited = 0x02000000;
        internal const uint WindowStyleTerminalHost = 0x56000000;
        internal const uint SetWindowPosNoSize = 0x0001;
        internal const uint SetWindowPosNoMove = 0x0002;
        internal const uint SetWindowPosNoZOrder = 0x0004;
        internal const uint SetWindowPosNoActivate = 0x0010;
        internal const uint SetWindowPosFrameChanged = 0x0020;
        internal const uint MouseKeyLeftButton = 0x0001;
        internal const uint MouseKeyRightButton = 0x0002;
        internal const uint MouseKeyShift = 0x0004;
        internal const uint MouseKeyControl = 0x0008;
        internal const uint MouseKeyMiddleButton = 0x0010;
        internal const uint WmSetFocus = 0x0007;
        internal const uint WmKillFocus = 0x0008;
        internal const uint WmKeyDown = 0x0100;
        internal const uint WmKeyUp = 0x0101;
        internal const uint WmChar = 0x0102;
        internal const uint WmSystemKeyDown = 0x0104;
        internal const uint WmSystemKeyUp = 0x0105;
        internal const uint WmSystemChar = 0x0106;
        internal const uint WmMouseActivate = 0x0021;
        internal const uint WmMouseMove = 0x0200;
        internal const uint WmLeftButtonDown = 0x0201;
        internal const uint WmLeftButtonUp = 0x0202;
        internal const uint WmLeftButtonDoubleClick = 0x0203;
        internal const uint WmRightButtonDown = 0x0204;
        internal const uint WmRightButtonUp = 0x0205;
        internal const uint WmRightButtonDoubleClick = 0x0206;
        internal const uint WmMiddleButtonDown = 0x0207;
        internal const uint WmMiddleButtonUp = 0x0208;
        internal const uint WmMiddleButtonDoubleClick = 0x0209;
        internal const uint WmMouseWheel = 0x020A;
        internal const uint WmMouseHorizontalWheel = 0x020E;
        internal const uint WmMouseLeave = 0x02A3;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate void WriteCallback([In, MarshalAs(UnmanagedType.LPWStr)] string data);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate void ScrollCallback(int viewTop, int viewHeight, int bufferSize);

        internal enum CaretStyle
        {
            BlinkingBlock = 1,
            SteadyBlock = 2,
            BlinkingUnderline = 3,
            SteadyUnderline = 4,
            BlinkingBar = 5,
            SteadyBar = 6
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TerminalTheme
        {
            internal uint DefaultBackground;
            internal uint DefaultForeground;
            internal uint DefaultSelectionBackground;
            internal CaretStyle CursorStyle;

            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U4, SizeConst = 16)]
            internal uint[] ColorTable;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TerminalSizeNative
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint
        {
            internal NativePoint(int x, int y)
            {
                X = x;
                Y = y;
            }

            internal int X;
            internal int Y;
        }

        [DllImport(TerminalControlLibrary, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        internal static extern int CreateTerminal(nint parent, out nint window, out TerminalSafeHandle terminal);

        [DllImport(TerminalControlLibrary, CallingConvention = CallingConvention.StdCall)]
        internal static extern void DestroyTerminal(nint terminal);

        [DllImport(TerminalControlLibrary, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        internal static extern void TerminalRegisterWriteCallback(TerminalSafeHandle terminal, [MarshalAs(UnmanagedType.FunctionPtr)] WriteCallback callback);

        [DllImport(TerminalControlLibrary, CallingConvention = CallingConvention.StdCall)]
        internal static extern void TerminalRegisterScrollCallback(TerminalSafeHandle terminal, [MarshalAs(UnmanagedType.FunctionPtr)] ScrollCallback callback);

        [DllImport(TerminalControlLibrary, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        internal static extern void TerminalSendOutput(TerminalSafeHandle terminal, string data);

        [DllImport(TerminalControlLibrary, CallingConvention = CallingConvention.StdCall)]
        internal static extern void TerminalSendKeyEvent(TerminalSafeHandle terminal, ushort virtualKey, ushort scanCode, ushort flags, [MarshalAs(UnmanagedType.Bool)] bool keyDown);

        [DllImport(TerminalControlLibrary, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        internal static extern void TerminalSendCharEvent(TerminalSafeHandle terminal, char character, ushort scanCode, ushort flags);

        [DllImport(TerminalControlLibrary, CallingConvention = CallingConvention.StdCall)]
        internal static extern int TerminalTriggerResize(TerminalSafeHandle terminal, int width, int height, out TerminalSizeNative dimensions);

        [DllImport(TerminalControlLibrary, CallingConvention = CallingConvention.StdCall)]
        internal static extern int TerminalTriggerResizeWithDimension(TerminalSafeHandle terminal, TerminalSizeNative dimensions, out TerminalSizeNative pixels);

        [DllImport(TerminalControlLibrary, CallingConvention = CallingConvention.StdCall)]
        internal static extern int TerminalCalculateResize(TerminalSafeHandle terminal, int width, int height, out TerminalSizeNative dimensions);

        [DllImport(TerminalControlLibrary, CallingConvention = CallingConvention.StdCall)]
        internal static extern void TerminalDpiChanged(TerminalSafeHandle terminal, int dpi);

        [DllImport(TerminalControlLibrary, CallingConvention = CallingConvention.StdCall)]
        internal static extern void TerminalUserScroll(TerminalSafeHandle terminal, int viewTop);

        [DllImport(TerminalControlLibrary, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.LPWStr)]
        internal static extern string TerminalGetSelection(TerminalSafeHandle terminal);

        [DllImport(TerminalControlLibrary, CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool TerminalIsSelectionActive(TerminalSafeHandle terminal);

        [DllImport(TerminalControlLibrary, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        internal static extern void TerminalSetTheme(TerminalSafeHandle terminal, [MarshalAs(UnmanagedType.Struct)] TerminalTheme theme, string fontFamily, short fontSize, int dpi);

        [DllImport(TerminalControlLibrary, CallingConvention = CallingConvention.StdCall)]
        internal static extern void TerminalSetFocused(TerminalSafeHandle terminal, [MarshalAs(UnmanagedType.Bool)] bool focused);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern nint GetWindowLongPtr(nint window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern nint SetWindowLongPtr(nint window, int index, nint value);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(nint window, int command);

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateWindowEx(uint extendedStyle, string className, string? windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(nint window);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetFocus(nint window);

        [DllImport("user32.dll")]
        internal static extern nint GetFocus();

        [DllImport("user32.dll")]
        internal static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClientToScreen(nint window, ref NativePoint point);

        [DllImport("user32.dll")]
        internal static extern short GetKeyState(int virtualKey);

        [DllImport("user32.dll")]
        internal static extern uint GetDoubleClickTime();

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int valueSize);

        internal static bool IsKeyDown(int virtualKey)
        {
            return (GetKeyState(virtualKey) & 0x8000) != 0;
        }
    }
}
