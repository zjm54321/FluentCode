// Connector tests exercise real Chrome, not WinUI activation. The separate production compile uses real WinUI refs.
namespace Microsoft.UI.Xaml { public class FrameworkElement { } }
namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public static DispatcherQueue? GetForCurrentThread() => null;
        public bool HasThreadAccess => false;
        public bool TryEnqueue(System.Action action) { action(); return true; }
    }
}
