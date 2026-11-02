// Inert native boundaries for the real FormCard + QuestionOverlay. These do not emulate TSF.
using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
namespace Microsoft.UI.Xaml.Controls
{
    public sealed class TextBox : Control
    {
        public string PlaceholderText { get; set; } = string.Empty;
        public TextWrapping TextWrapping { get; set; }
        public bool AcceptsReturn { get; set; }
        public int SelectionStart { get; set; }
        public int SelectionLength { get; set; }
        private string _text = string.Empty;
        internal int TextWrites;
        public string Text { get => _text; set { TextWrites++; _text = value; TextChanged?.Invoke(this, EventArgs.Empty); } }
        public event EventHandler? TextChanged;
    }
    public sealed class RadioButtons : Control
    {
        public readonly List<object> Items = new();
        private int _selectedIndex = -1;
        public int SelectedIndex { get => _selectedIndex; set { if (_selectedIndex == value) return; _selectedIndex = value; SelectionChanged?.Invoke(this, EventArgs.Empty); } }
        public event EventHandler? SelectionChanged;
    }
    public class CheckBox : ContentControl
    {
        public Thickness Padding { get; set; }
        private bool? _checked;
        public bool? IsChecked { get => _checked; set { if (_checked == value) return; _checked = value; if (value == true) Checked?.Invoke(this, EventArgs.Empty); else Unchecked?.Invoke(this, EventArgs.Empty); } }
        public event EventHandler? Checked, Unchecked;
    }
    public sealed class RadioButton : CheckBox { }
}
namespace FluentCode.Pages
{
    // Only native/page boundaries: SyncQuestionOverlay itself is extracted from production.
    public sealed partial class OpenCodePage
    {
        private readonly FluentCode.Pages.OpenCode.OpenCodeQuestionOverlay _questionOverlay = new();
        private readonly FormList _interactionsView = new();
        private readonly PermissionFocus _permissionOverlay = new();
        private bool _pageActive = true, _isComposing = false, _composerCoveredByPermission = false, _composerCoveredByQuestion;
        private bool _suppressComposerFocus = false;
        private readonly Microsoft.UI.Xaml.Controls.TextBox Composer = new();
        private readonly Microsoft.UI.Xaml.Controls.Grid QuestionOverlayHost = new();
        private bool ComposerCoveredByOverlay => _composerCoveredByPermission || _composerCoveredByQuestion;
        private Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; } = new();
        private void UpdateComposerButtons() { }
        internal FluentCode.Pages.OpenCode.OpenCodeQuestionOverlay ShowQuestion(FluentCode.Pages.OpenCode.OpenCodeFormCard card)
        {
            _interactionsView.FormCards.Add(card);
            Composer.Focus(FocusState.Programmatic);
            SyncQuestionOverlay(false);
            return _questionOverlay;
        }
        private sealed class FormList
        {
            internal readonly List<FluentCode.Pages.OpenCode.OpenCodeFormCard> FormCards = new();
        }
        private sealed class PermissionFocus { internal void FocusCurrent() { } }
    }
}
namespace Microsoft.UI.Xaml.Input
{
    public delegate void KeyEventHandler(object sender, KeyRoutedEventArgs args);
    public sealed class KeyRoutedEventArgs : EventArgs
    {
        public Windows.System.VirtualKey Key { get; init; }
        public bool Handled { get; set; }
    }
}
namespace Windows.System { public enum VirtualKey { Enter, Escape, Control, ProcessKey } }
namespace Windows.UI.Core { [Flags] public enum CoreVirtualKeyStates { None = 0, Down = 1 } }
namespace Microsoft.UI.Input
{
    public static class InputKeyboardSource
    {
        public static Windows.UI.Core.CoreVirtualKeyStates GetKeyStateForCurrentThread(Windows.System.VirtualKey key) => Windows.UI.Core.CoreVirtualKeyStates.None;
    }
}
