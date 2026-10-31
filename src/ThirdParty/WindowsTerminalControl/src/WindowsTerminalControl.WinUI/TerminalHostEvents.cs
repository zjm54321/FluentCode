namespace WindowsTerminalControl.WinUI;

/// <summary>Local scrollback position: first visible row, visible rows and total retained rows.</summary>
public readonly record struct TerminalScrollState(int ViewTop, int ViewHeight, int BufferSize)
{
    /// <summary>Largest valid <see cref="ViewTop"/>.</summary>
    public int MaximumTop => System.Math.Max(0, BufferSize - ViewHeight);

    /// <summary>Whether the viewport shows the live bottom of the buffer.</summary>
    public bool AtBottom => ViewTop >= MaximumTop;
}

/// <summary>Primary-button press on the native terminal window, before the renderer handles it.</summary>
public sealed class TerminalClickEventArgs(int clickCount) : System.EventArgs
{
    /// <summary>1 for a single press, 2 for the second press of a system double-click without dragging.</summary>
    public int ClickCount { get; } = clickCount;

    /// <summary>Set to keep the press (and its release) from reaching the native renderer.</summary>
    public bool Handled { get; set; }
}
