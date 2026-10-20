using System.Collections.Generic;
using AIFrontend.Services.Taskbar;

namespace AIFrontend;

// Headless compile host only: supplies the App.OpenCode partial's members used by
// App.Taskbar, without bringing WinUI, notification activation, or network owners in.
// Closing remains mutable and diagnostics are retained, not erased to make compilation pass.
public partial class App
{
    private volatile bool _closing;
    private readonly object _diagnosticSync = new();
    private readonly Queue<(string Code, TaskbarOperationResult? Taskbar)> _diagnostics = new();

    internal bool TestClosing { get => _closing; set => _closing = value; }

    internal IReadOnlyList<(string Code, TaskbarOperationResult? Taskbar)> TestDiagnostics
    {
        get { lock (_diagnosticSync) return _diagnostics.ToArray(); }
    }

    private void RecordDiagnostic(string code, TaskbarOperationResult? taskbar = null)
    {
        lock (_diagnosticSync)
        {
            if (_diagnostics.Count == 128) _diagnostics.Dequeue();
            _diagnostics.Enqueue((code, taskbar));
        }
    }
}
