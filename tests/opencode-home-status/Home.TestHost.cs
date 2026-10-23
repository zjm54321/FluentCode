// Inert native control/resource boundary, no Home classification/reconciliation algorithm.
using System;
using System.Collections.Generic;
using System.Linq;
namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public enum HorizontalAlignment { Left, Center, Right, Stretch }
    public enum VerticalAlignment { Top, Center, Bottom, Stretch }
    public enum TextWrapping { NoWrap, Wrap }
    public enum TextTrimming { None, CharacterEllipsis }
    public enum GridUnitType { Pixel, Auto, Star }
    public readonly record struct GridLength(double Value, GridUnitType Type = GridUnitType.Pixel) { public static GridLength Auto => new(0,GridUnitType.Auto); }
    public readonly record struct Thickness(double Left, double Top, double Right, double Bottom)
    { public Thickness(double all):this(all,all,all,all){} }
    public readonly record struct CornerRadius(double Value);
    public class DependencyObject { internal Dictionary<string,object?> Attached = new(); }
    public class UIElement:DependencyObject { public Visibility Visibility {get;set;} public double Opacity{get;set;}=1; public object? Shadow{get;set;} public System.Numerics.Vector3 Translation{get;set;} }
    public class FrameworkElement:UIElement
    {
        public object Tag {get;set;}=null!; public Style? Style {get;set;}
        public double Width{get;set;} public double Height{get;set;} public double ActualWidth{get;set;}=1000; public double ActualHeight{get;set;}=800;
        public Thickness Margin{get;set;} public double MaxHeight{get;set;}
        public double MinWidth{get;set;} public double MinHeight{get;set;}
        public HorizontalAlignment HorizontalAlignment{get;set;} public VerticalAlignment VerticalAlignment{get;set;}
        public Thickness Padding{get;set;}
        public event EventHandler? SizeChanged; public event EventHandler? Loaded;
        public void RaiseLoaded()=>Loaded?.Invoke(this,EventArgs.Empty);
        public void RaiseSizeChanged()=>SizeChanged?.Invoke(this,EventArgs.Empty);
    }
    public class Style {public Style? BasedOn{get;set;} public string Xml{get;set;}="";}
    public class Application {public static Application? Current{get;set;} public Dictionary<string,object> Resources {get;}=new();}
}
namespace Microsoft.UI.Xaml.Media {public class Brush{} public class ThemeShadow{}}
namespace Microsoft.UI.Xaml.Documents {}
namespace Microsoft.UI.Xaml.Controls.Primitives {}
namespace Microsoft.UI.Xaml.Markup
{
    public static class XamlReader {public static object Load(string xml)=>new Microsoft.UI.Xaml.Style{Xml=xml};}
}
namespace Microsoft.UI.Xaml.Automation
{
    public static class AutomationProperties
    {
        public static void SetAutomationId(Microsoft.UI.Xaml.DependencyObject e,string s)=>e.Attached["Id"]=s;
        public static string GetAutomationId(Microsoft.UI.Xaml.DependencyObject e)=>e.Attached.GetValueOrDefault("Id") as string??"";
        public static void SetName(Microsoft.UI.Xaml.DependencyObject e,string s)=>e.Attached["Name"]=s;
        public static string GetName(Microsoft.UI.Xaml.DependencyObject e)=>e.Attached.GetValueOrDefault("Name") as string??"";
    }
}
namespace Microsoft.UI.Xaml.Controls
{
    using Microsoft.UI.Xaml;
    public enum Orientation {Vertical,Horizontal}
    public enum ListViewSelectionMode {None,Single}
    public enum ScrollMode{Disabled,Enabled} public enum ScrollBarVisibility{Disabled,Hidden}
    public class Panel:FrameworkElement {public List<UIElement> Children{get;}=new();}
    public class StackPanel:Panel {public Orientation Orientation{get;set;} public double Spacing{get;set;}}
    public class ColumnDefinition {public GridLength Width{get;set;} public double MaxWidth{get;set;}}
    public class RowDefinition {public GridLength Height{get;set;}}
    public class Grid:Panel
    {
        public double ColumnSpacing{get;set;} public double RowSpacing{get;set;}
        public List<ColumnDefinition> ColumnDefinitions{get;}=new();public List<RowDefinition> RowDefinitions{get;}=new();
        public static void SetColumn(FrameworkElement e,int i)=>e.Attached["Column"]=i;
        public static void SetRow(FrameworkElement e,int i)=>e.Attached["Row"]=i;
    }
    public class ContentControl:FrameworkElement
    {
        public object? Content{get;set;}public HorizontalAlignment HorizontalContentAlignment{get;set;}
        public VerticalAlignment VerticalContentAlignment{get;set;}
    }
    public class UserControl:ContentControl{}
    public class Border:FrameworkElement {public UIElement? Child{get;set;}public CornerRadius CornerRadius{get;set;}public Thickness BorderThickness{get;set;}}
    public class TextBlock:FrameworkElement
    {
        public string Text{get;set;}="";public TextWrapping TextWrapping{get;set;}public TextTrimming TextTrimming{get;set;}
        public bool IsTextSelectionEnabled{get;set;}public double FontSize{get;set;}
    }
    public class TextBox:FrameworkElement
    {
        private string _text="";public string Text{get=>_text;set{_text=value;TextChanged?.Invoke(this,EventArgs.Empty);}}
        public string PlaceholderText{get;set;}="";public event EventHandler? TextChanged;
    }
    public class FontIcon:FrameworkElement {public string Glyph{get;set;}="";public double FontSize{get;set;}}
    public class ProgressRing:FrameworkElement {public bool IsActive{get;set;}}
    public class Button:ContentControl
    {public bool IsEnabled{get;set;}=true; public CornerRadius CornerRadius{get;set;} public event EventHandler? Click;public void RaiseClick()=>Click?.Invoke(this,EventArgs.Empty);}
    public class HyperlinkButton:Button{}
    public class ListViewItem:ContentControl
    {public bool IsHitTestVisible{get;set;}=true;public bool IsTabStop{get;set;}=true;public MenuFlyout? ContextFlyout{get;set;}}
    public class ItemClickEventArgs:EventArgs {public object? ClickedItem{get;set;}}
    public class ListView:FrameworkElement
    {
        public ListViewSelectionMode SelectionMode{get;set;} public bool IsItemClickEnabled{get;set;}
        public List<object> Items{get;}=new();private object? _selected;
        public object? SelectedItem{get=>_selected;set{_selected=value;SelectionChanged?.Invoke(this,EventArgs.Empty);}}
        public event EventHandler? SelectionChanged;public event EventHandler<ItemClickEventArgs>? ItemClick;
        public object? ContainerFromItem(object? value)=>Items.OfType<ListViewItem>().FirstOrDefault(i=>ReferenceEquals(i,value)||ReferenceEquals(i.Content,value));
        public void RaiseItemClick(object content)=>ItemClick?.Invoke(this,new(){ClickedItem=content});
    }
    public class MenuFlyoutItemBase:FrameworkElement{} public class MenuFlyoutSeparator:MenuFlyoutItemBase{}
    public class MenuFlyout:FrameworkElement {public List<MenuFlyoutItemBase> Items{get;}=new();public event EventHandler? Opening;public void RaiseOpening()=>Opening?.Invoke(this,EventArgs.Empty);}
    public class MenuFlyoutItem:MenuFlyoutItemBase{public string Text{get;set;}="";public bool IsEnabled{get;set;}=true;public FontIcon? Icon{get;set;}public event EventHandler? Click;public void RaiseClick()=>Click?.Invoke(this,EventArgs.Empty);}
    public class ScrollViewer:ContentControl
    {public static void SetHorizontalScrollMode(DependencyObject e,ScrollMode m){}public static void SetHorizontalScrollBarVisibility(DependencyObject e,ScrollBarVisibility v){}
     public static void SetVerticalScrollMode(DependencyObject e,ScrollMode m){}public static void SetVerticalScrollBarVisibility(DependencyObject e,ScrollBarVisibility v){}}
    public static class ToolTipService {public static void SetToolTip(DependencyObject e,object? value)=>e.Attached["Tooltip"]=value;}
}
