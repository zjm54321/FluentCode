// Native controls/dispatcher/collection boundary only. Reordering does not run production policy
// here: notification stack and native temporary selection are explicit test stimuli.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
namespace Windows.Foundation.Collections
{
    public enum CollectionChange{Reset,ItemInserted,ItemRemoved,ItemChanged}
    public interface IVectorChangedEventArgs{CollectionChange CollectionChange{get;}uint Index{get;}}
    public sealed record VectorArgs(CollectionChange CollectionChange,uint Index):IVectorChangedEventArgs;
    public interface IObservableVector<T>:IList<T>{event Action<IObservableVector<T>,IVectorChangedEventArgs>? VectorChanged;}
}
namespace Microsoft.UI.Dispatching
{
    public enum DispatcherQueuePriority{Low}
    public sealed class DispatcherQueue
    {
        private readonly Queue<Action> _queue=new();public int Count=>_queue.Count;public bool Accept=true;public bool HasThreadAccess=>true;
        public bool TryEnqueue(DispatcherQueuePriority priority,Action action){if(!Accept)return false;_queue.Enqueue(action);return true;}
        public bool TryEnqueue(Action action)=>TryEnqueue(DispatcherQueuePriority.Low,action);
        public void Drain(){var n=0;while(_queue.TryDequeue(out var action)){if(++n>100)throw new Exception("queue loop");action();}}
    }
}
namespace Microsoft.UI.Xaml
{
    public enum Visibility{Visible,Collapsed}public enum HorizontalAlignment{Left,Center,Right,Stretch}public enum VerticalAlignment{Top,Center,Bottom,Stretch}
    public enum TextWrapping{NoWrap,Wrap}public enum TextTrimming{None,CharacterEllipsis}public enum FocusState{Programmatic}
    public class Style{}
    public class XamlRoot{public Windows.Foundation.Size Size=new(900,600);}
    public readonly record struct Thickness(double Left,double Top,double Right,double Bottom){public Thickness(double all):this(all,all,all,all){}}
    public readonly record struct CornerRadius(double Value);
    public enum GridUnitType{Auto,Star}public readonly record struct GridLength(double Value,GridUnitType Kind=GridUnitType.Auto){public static GridLength Auto=>new(0);}
    public class RoutedEventArgs:EventArgs{}public class DragStartingEventArgs:EventArgs{public bool Cancel{get;set;}}
    public class DependencyObject{public Dictionary<string,object?> Attached=new();}
    public class UIElement:DependencyObject{public Visibility Visibility{get;set;}}
    public class FrameworkElement:UIElement
    {
        public object Tag{get;set;}=null!;public string Name{get;set;}="";public object? Style{get;set;}
        public double Width{get;set;}public double Height{get;set;}public double MaxWidth{get;set;}public double MinWidth{get;set;}
        public Thickness Padding{get;set;}public Thickness Margin{get;set;}public HorizontalAlignment HorizontalAlignment{get;set;}public VerticalAlignment VerticalAlignment{get;set;}
        public XamlRoot? XamlRoot{get;set;}=new();public event Action<object,RoutedEventArgs>? Loaded;public void RaiseLoaded()=>Loaded?.Invoke(this,new());
    }
    public static class VisualStateManager
    {public static bool GoToState(AIFrontend.Pages.OpenCodePage page,string state,bool transitions){page.LastLayout=state;return true;}}
}
namespace Microsoft.UI.Xaml.Media
{
    using Microsoft.UI.Xaml;using Microsoft.UI.Xaml.Controls;
    public sealed class SolidColorBrush(Windows.UI.Color color){public Windows.UI.Color Color=color;}
    public static class VisualTreeHelper
    {
        private static UIElement[] Children(DependencyObject n)=>n is Panel p?p.Children.ToArray():n is ContentControl{Content:UIElement c}?new[]{c}:Array.Empty<UIElement>();
        public static int GetChildrenCount(DependencyObject n)=>Children(n).Length;public static DependencyObject GetChild(DependencyObject n,int i)=>Children(n)[i];
    }
}
namespace Microsoft.UI.Xaml.Controls.Primitives{public enum FlyoutPlacementMode{Top,Bottom,Left,Right,Full,TopEdgeAlignedLeft,TopEdgeAlignedRight,BottomEdgeAlignedLeft,BottomEdgeAlignedRight,LeftEdgeAlignedTop,LeftEdgeAlignedBottom,RightEdgeAlignedTop,RightEdgeAlignedBottom,Auto}}
namespace Microsoft.UI.Xaml.Shapes{public class Ellipse:Microsoft.UI.Xaml.FrameworkElement{}}
namespace Windows.UI{public struct Color{public byte A,R,G,B;}}
namespace Windows.Foundation{public readonly record struct Size(double Width,double Height);}
namespace Microsoft.UI.Xaml.Markup{public static class XamlReader{public static object Load(string xaml)=>new Microsoft.UI.Xaml.Style();}}
namespace Microsoft.UI.Xaml.Automation
{public static class AutomationProperties{public static void SetAutomationId(Microsoft.UI.Xaml.DependencyObject e,string s)=>e.Attached["Id"]=s;public static void SetName(Microsoft.UI.Xaml.DependencyObject e,string s)=>e.Attached["Name"]=s;public static void SetHelpText(Microsoft.UI.Xaml.DependencyObject e,string s)=>e.Attached["Help"]=s;}}
namespace Microsoft.UI.Xaml.Controls
{
    using Microsoft.UI.Xaml;using Windows.Foundation.Collections;
    public enum Orientation{Horizontal,Vertical}public enum InfoBarSeverity{Informational,Warning,Error}
    public class SelectionChangedEventArgs:EventArgs{}
    public class Control:FrameworkElement{public static readonly object ForegroundProperty=new();public object? Foreground;public int Clears;public void ClearValue(object property){Foreground=null;Clears++;}}
    public class ContentControl:Control{public object? Content{get;set;}public HorizontalAlignment HorizontalContentAlignment{get;set;}}
    public class DropDownButton:Button{}
    public class Panel:FrameworkElement{public List<UIElement> Children{get;}=new();}
    public class ColumnDefinition{public GridLength Width;}public class RowDefinition{public GridLength Height;}
    public class Grid:Panel{public double ColumnSpacing,RowSpacing;public List<ColumnDefinition> ColumnDefinitions=new();public List<RowDefinition> RowDefinitions=new();public static void SetColumn(UIElement e,int n){}public static void SetRow(UIElement e,int n){}public static void SetColumnSpan(UIElement e,int n){}}public class StackPanel:Panel{public Orientation Orientation{get;set;}public double Spacing{get;set;}}
    public class Border:FrameworkElement{public UIElement? Child{get;set;}public CornerRadius CornerRadius{get;set;}public Thickness BorderThickness{get;set;}}
    public class ProgressRing:FrameworkElement{public bool IsActive{get;set;}}public class TextBlock:FrameworkElement{public static readonly object ForegroundProperty=new();public object? Foreground;public void ClearValue(object property)=>Foreground=null;private string _text="";public Action? TextUpdated;public string Text{get=>_text;set{_text=value;TextUpdated?.Invoke();}}public int MaxLines;public bool IsTextSelectionEnabled{get;set;}public TextWrapping TextWrapping{get;set;}public TextTrimming TextTrimming{get;set;}}
    public class Button:ContentControl{public bool IsEnabled{get;set;}=true;public event Action<object,RoutedEventArgs>? Click;public void Press()=>Click?.Invoke(this,new());}
    public class TextBox:FrameworkElement{private string _text="";public Action<string>? OnTextAssigned;public string Text{get=>_text;set{_text=value;OnTextAssigned?.Invoke(value);}}public bool IsReadOnly{get;set;} public int SelectionStart,SelectionLength;public void Select(int start,int length){SelectionStart=start;SelectionLength=length;}public bool Focus(FocusState state)=>true;}
    public class FontIcon:FrameworkElement{public string Glyph{get;set;}="";public double FontSize{get;set;}}
    public class FontIconSource{public string Glyph{get;set;}="";}
    public class MenuFlyout:FrameworkElement{public List<object> Items{get;}=new();public Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode Placement{get;set;}public static Action<MenuFlyout>? Shown;public void ShowAt(FrameworkElement target){foreach(var row in Items.OfType<MenuFlyoutItem>()){row.Content=new TextBlock{Text=row.Text};row.RaiseLoaded();}Shown?.Invoke(this);}}
    public class MenuFlyoutItem:Button{public string Text{get;set;}="";public FontIcon? Icon{get;set;}}public class RadioMenuFlyoutItem:MenuFlyoutItem{public string GroupName{get;set;}="";public bool IsChecked{get;set;}}
    public class MenuFlyoutSeparator{}
    public class ListViewItem:ContentControl{public bool IsEnabled{get;set;}=true;public MenuFlyout? ContextFlyout{get;set;}}
    public sealed class ItemVector:Collection<object>,IObservableVector<object>
    {
        public event Action<IObservableVector<object>,IVectorChangedEventArgs>? VectorChanged;public Action<IVectorChangedEventArgs>? Changed;
        public int NotificationDepth{get;private set;}public bool RecursiveMutation{get;private set;}
        private void Raise(CollectionChange type,int index){NotificationDepth++;try{var a=new VectorArgs(type,(uint)index);VectorChanged?.Invoke(this,a);Changed?.Invoke(a);}finally{NotificationDepth--;}}
        protected override void InsertItem(int i,object item){RecursiveMutation|=NotificationDepth>0;base.InsertItem(i,item);Raise(CollectionChange.ItemInserted,i);}
        protected override void RemoveItem(int i){RecursiveMutation|=NotificationDepth>0;base.RemoveItem(i);Raise(CollectionChange.ItemRemoved,i);}
        protected override void ClearItems(){base.ClearItems();Raise(CollectionChange.Reset,0);}
    }
    public class ListView:FrameworkElement
    {
        public ItemVector Items{get;}=new();private object? _selected;public event Action<object,SelectionChangedEventArgs>? SelectionChanged;
        public object? SelectedItem{get=>_selected;set{if(ReferenceEquals(_selected,value))return;_selected=value;SelectionChanged?.Invoke(this,new());}}
        public void ScrollIntoView(object item){}
    }
    public class TabViewItem:ContentControl{public object? Header{get;set;}public object? IconSource{get;set;}public bool IsClosable{get;set;}=true;public MenuFlyout? ContextFlyout{get;set;}}
    public class TabViewTabCloseRequestedEventArgs{public required TabViewItem Tab{get;init;}}
    public class TabView:FrameworkElement
    {
        public readonly ItemVector TabItems=new();private object? _selected;
        public event Action<object,SelectionChangedEventArgs>? SelectionChanged;public event Action<TabView,IVectorChangedEventArgs>? TabItemsChanged;
        public bool SimulateNativeAutoSelect=true;
        public TabView(){TabItems.Changed+=a=>{TabItemsChanged?.Invoke(this,a);if(SimulateNativeAutoSelect&&(_selected is null||!TabItems.Contains(_selected)))SelectedItem=TabItems.FirstOrDefault();};}
        public object? SelectedItem{get=>_selected;set{if(ReferenceEquals(_selected,value))return;_selected=value;SelectionChanged?.Invoke(this,new());}}
    }
    public static class ToolTipService{public static void SetToolTip(DependencyObject e,object? value)=>e.Attached["Tooltip"]=value;}
}
namespace AIFrontend.Pages.OpenCode
{
    internal static class OpenCodeInteractionUi
    {internal const string BodyStrongStyleKey="BodyStrong";internal const string CaptionSubtleStyleKey="Caption";internal const string AttentionDotStyleKey="Dot";internal static object? TryStyle(string key)=>null;internal static Microsoft.UI.Xaml.Controls.TextBlock StyledText(string text,string style)=>new(){Text=text};}
    internal static class OpenCodeModelLogo{internal static object Label(string? id,string? name)=>name??"";}
    // Native composition boundary for the B13 shimmer: the visual (Canvas/expression animation) is inert here,
    // but clock membership uses the REAL shared OpenCodeWorkingClock so actual Tabs wiring is observable.
    internal sealed class OpenCodeWorkingShimmer:IOpenCodeWorkingClockSubscriber
    {
        internal static OpenCodeWorkingClock Clock=OpenCodeWorkingClock.Shared;internal int Renders;
        internal OpenCodeWorkingShimmer(Microsoft.UI.Xaml.Controls.StackPanel header){}
        internal bool IsWorking{get;private set;}
        internal void SetWorking(bool working){if(IsWorking==working)return;IsWorking=working;if(working)Clock.Join(this);else Clock.Leave(this);}
        void IOpenCodeWorkingClockSubscriber.OnWorkingClockStateChanged(OpenCodeWorkingClock clock)=>Renders++;
    }
    internal static class OpenCodeToolTheme{internal static Microsoft.UI.Xaml.Style? Text(string key,string? basedOnKey=null)=>new();internal static void Apply(Microsoft.UI.Xaml.FrameworkElement e,Microsoft.UI.Xaml.Style? style)=>e.Style=style;}
}
