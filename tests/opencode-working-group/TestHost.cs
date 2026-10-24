// Native XAML/composition boundary only. Production group, indicator, phrase picker, brand helper and clock are linked.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public enum HorizontalAlignment { Left, Center, Stretch }
    public enum VerticalAlignment { Center, Stretch }
    public enum TextWrapping { NoWrap }
    public enum TextTrimming { None, CharacterEllipsis }
    public enum FocusState { Unfocused, Pointer, Keyboard, Programmatic }
    public enum GridUnitType { Star }
    public record struct GridLength(double Value, GridUnitType Type = default) { public static GridLength Auto => new(0); }
    public record struct Thickness(double Left, double Top, double Right, double Bottom) { public Thickness(double all):this(all,all,all,all) {} }
    public record struct CornerRadius(double TopLeft, double TopRight, double BottomRight, double BottomLeft)
    { public CornerRadius(double all):this(all,all,all,all) {} }
    public class Style { public string? Key; }
    public class ResourceDictionary : Dictionary<string,object> { public Dictionary<string,object> ThemeDictionaries = new(); }
    public class RoutedEventArgs { public object? OriginalSource; }
    public class DependencyObject { }
    public class UIElement : DependencyObject
    {
        public static readonly object VisibilityProperty = new();
        private readonly Dictionary<long, Action<DependencyObject, object>> _callbacks = new();
        private long _token; private Visibility _visibility;
        public Visibility Visibility { get => _visibility; set { if (_visibility == value) return; _visibility=value; foreach(var c in _callbacks.Values.ToArray()) c(this, VisibilityProperty); } }
        public long RegisterPropertyChangedCallback(object property, Action<DependencyObject, object> callback) { _callbacks[++_token]=callback; return _token; }
        public void UnregisterPropertyChangedCallback(object property, long token) => _callbacks.Remove(token);
        public UIElement? Parent;
        public double Opacity { get; set; } = 1;
        public bool IsHitTestVisible { get; set; } = true;
        public Dictionary<string,string> Automation = new();
    }
    public class FrameworkElement : UIElement
    {
        public double Width, Height, MinWidth, MinHeight;
        public Thickness Margin, Padding, BorderThickness;
        public HorizontalAlignment HorizontalAlignment, HorizontalContentAlignment;
        public VerticalAlignment VerticalAlignment, VerticalContentAlignment;
        public Style? Style;
        public ResourceDictionary Resources = new();
        public event Action<object,RoutedEventArgs>? GotFocus, LostFocus;
        public void Focus(FocusState state) { if(this is Control c)c.FocusState=state; GotFocus?.Invoke(this,new(){OriginalSource=this}); }
        public void Blur() { if(this is Control c)c.FocusState=FocusState.Unfocused; LostFocus?.Invoke(this,new(){OriginalSource=this}); }
        public event Action<object,object>? Loaded, Unloaded, PointerEntered, PointerMoved, PointerExited, PointerCanceled;
        public bool IsLoaded { get; private set; }
        public void Load() { IsLoaded=true; Loaded?.Invoke(this,new()); }
        public void Unload() { IsLoaded=false; RaiseUnloaded(); }
        public void RaiseUnloaded() => Unloaded?.Invoke(this,new());
        public void Enter()=>PointerEntered?.Invoke(this,new()); public void Exit()=>PointerExited?.Invoke(this,new()); public void Cancel()=>PointerCanceled?.Invoke(this,new());
        public void Pointer() { PointerEntered?.Invoke(this,new()); PointerMoved?.Invoke(this,new()); PointerExited?.Invoke(this,new()); PointerCanceled?.Invoke(this,new()); }
    }
}
namespace Microsoft.UI.Xaml.Controls
{
    public class Control : FrameworkElement { public FocusState FocusState; public CornerRadius CornerRadius; public Microsoft.UI.Xaml.Media.SolidColorBrush? Background; }
    public enum Orientation { Horizontal }
    public class Children(UIElement owner) : System.Collections.ObjectModel.Collection<UIElement>
    { protected override void InsertItem(int i, UIElement e) { base.InsertItem(i,e); e.Parent=owner; } }
    public class Panel : FrameworkElement { public Children Children { get; } public Panel() => Children=new(this); }
    public class StackPanel : Panel { public Orientation Orientation; public double Spacing; }
    public class Grid : Panel { public double ColumnSpacing; public List<ColumnDefinition> ColumnDefinitions = new(); private static readonly Dictionary<UIElement,int> Columns=new(); public static void SetColumn(UIElement e,int column)=>Columns[e]=column; public static int GetColumn(UIElement e)=>Columns.TryGetValue(e,out var column)?column:0; }
    public class ColumnDefinition { public GridLength Width; }
    public class Border : FrameworkElement { public CornerRadius CornerRadius; private UIElement? _child; public UIElement? Child { get=>_child; set { _child=value; if(value is not null)value.Parent=this; } } }
    public class TextBlock : FrameworkElement { public string Text { get; set; } = ""; public double FontSize; public int FontWeight; public TextWrapping TextWrapping; public TextTrimming TextTrimming; }
    public class FontIcon : FrameworkElement { public string Glyph=""; public double FontSize; }
    public class ProgressRing : FrameworkElement { public bool IsActive; }
    public class Button : Control
    { private object? _content; public object? Content { get=>_content; set { _content=value; if(value is UIElement e)e.Parent=this; } } public event Action<object,object>? Click; public void Press()=>Click?.Invoke(this,new()); }
    public class Expander : Control
    {
        private bool _expanded;
        private object? _content, _header;
        public object? Content { get=>_content; set { if (_content is UIElement old) old.Parent=null; _content=value; if(value is UIElement e)e.Parent=this; } }
        public object? Header { get=>_header; set { _header=value; if(value is UIElement e)e.Parent=this; } }
        public bool IsExpanded { get=>_expanded; set { if(_expanded==value)return; _expanded=value; if(value)Expanding?.Invoke(this,new());else Collapsed?.Invoke(this,new()); } }
        public event Action<object,object>? Expanding, Collapsed;
    }
    public static class ToolTipService { public static void SetToolTip(DependencyObject e,object value) {} }
}
namespace Microsoft.UI.Xaml.Automation.Peers { public enum AccessibilityView { Raw } }
namespace Microsoft.UI.Xaml.Automation
{
    public static class AutomationProperties
    {
        public static void SetAutomationId(UIElement e,string value)=>e.Automation["Id"]=value;
        public static void SetName(UIElement e,string value)=>e.Automation["Name"]=value;
        public static void SetHelpText(UIElement e,string value)=>e.Automation["Help"]=value;
        public static void SetAccessibilityView(UIElement e,Peers.AccessibilityView value) {}
    }
}
namespace Microsoft.UI { public static class Colors { public const int Transparent=0; } }
namespace Microsoft.UI.Text { public static class FontWeights { public const int Normal=400; } }
namespace Microsoft.UI.Xaml.Media
{
    public class SolidColorBrush(int color) { public int Color=color; public string? ThemeKey; }
    public static class VisualTreeHelper { public static DependencyObject? GetParent(DependencyObject e)=>(e as UIElement)?.Parent; }
}
namespace Microsoft.UI.Xaml.Markup
{
    public static class XamlReader
    {
        public static object Load(string xaml)
        {
            var element = System.Xml.Linq.XElement.Parse(xaml);
            if(element.Name.LocalName=="ResourceDictionary")
            {
                var resources=new ResourceDictionary();
                foreach(var child in element.Elements())
                    resources[(string)child.Attribute(System.Xml.Linq.XName.Get("Key","http://schemas.microsoft.com/winfx/2006/xaml"))!]=Load(child.ToString());
                return resources;
            }
            var color=element.Attribute("Color")?.Value;
            if(element.Name.LocalName!="SolidColorBrush" || color is not ("{ThemeResource SubtleFillColorSecondary}" or "{ThemeResource SystemColorButtonFaceColor}"))
                throw new InvalidOperationException("Unexpected resource XAML in working-group boundary");
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(-1) {ThemeKey=color[15..^1]};
        }
    }
}
namespace Microsoft.UI.Dispatching
{
    public class DispatcherQueue
    { public static DispatcherQueue GetForCurrentThread()=>new(); public DispatcherQueueTimer CreateTimer()=>new(); }
    public class DispatcherQueueTimer
    {
        public static readonly List<DispatcherQueueTimer> All = new();
        public DispatcherQueueTimer()=>All.Add(this);
        public bool IsRunning; public TimeSpan Interval; public bool IsRepeating;
        public event Action<object,object>? Tick;
        public void Start()=>IsRunning=true; public void Stop()=>IsRunning=false; public void Fire() { if(IsRunning)Tick?.Invoke(this,new()); }
    }
}
namespace Microsoft.UI.Composition
{
    public class CompositionPropertySet { }
    public class ExpressionAnimation(string expression)
    { public string Expression=expression; public void SetReferenceParameter(string key,CompositionPropertySet value) {} public void SetScalarParameter(string key,float value) {} }
    public class Compositor { public ExpressionAnimation CreateExpressionAnimation(string text)=>new(text); }
    public class Visual
    { public Compositor Compositor = new(); public Vector3 CenterPoint; public float RotationAngleInDegrees, Opacity=1; public Dictionary<string,ExpressionAnimation> Animations = new(); public void StartAnimation(string key,ExpressionAnimation value)=>Animations[key]=value; public void StopAnimation(string key)=>Animations.Remove(key); }
}
namespace Microsoft.UI.Xaml.Hosting
{
    public static class ElementCompositionPreview
    { private static readonly Dictionary<UIElement,Microsoft.UI.Composition.Visual> Visuals=new(); public static Microsoft.UI.Composition.Visual GetElementVisual(UIElement e) { if(!Visuals.TryGetValue(e,out var v))Visuals[e]=v=new();return v; } }
}
namespace AIFrontend.Pages.OpenCode
{
    internal static class OpenCodeWorkingClockComposition { internal static void EnsureInitialized() {} internal static Microsoft.UI.Composition.CompositionPropertySet PhaseSet(Microsoft.UI.Composition.Compositor c)=>new(); }
    internal static class OpenCodeToolTheme
    {
        internal const string Accent="accent",TextSecondary="secondary",Success="success",Critical="critical",Divider="divider";
        internal static Style Icon(string key)=>new(){Key=key}; internal static Style Text(string key,string? basedOn=null)=>new(){Key=key}; internal static Style Border(string key,string? fallback=null)=>new(){Key=key+"|"+fallback}; internal static void Apply(FrameworkElement e,Style? style)=>e.Style=style;
    }
    internal static class OpenCodeInteractionUi { internal const string CaptionStyleKey="caption"; internal static TextBlock StyledText(string text,string key)=>new(){Text=text}; internal static Style? TryStyle(string key)=>null; }
    internal static class OpenCodeToolDetailPopup { internal static OpenCodeToolDetailPopupHost Shared=new(); }
    internal class OpenCodeToolDetailPopupHost { internal void TrackPointer(FrameworkElement e,object args) {} }
}
