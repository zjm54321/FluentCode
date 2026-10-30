using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BrowserSelectionFixture;

public sealed partial class App
{
    private static object Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(owner)!;
    private static Task Invoke(OpenCodeBrowserSelection selection, string name, params object[] args)
        => (Task)typeof(OpenCodeBrowserSelection).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(selection,args)!;
    private static async Task Until(Func<bool> predicate)
    { var end=DateTime.UtcNow.AddSeconds(10); while(!predicate()) { if(DateTime.UtcNow>end)throw new TimeoutException("Native selection UI did not settle"); await Task.Delay(20); } }
    private async Task NativeSelectionAsync()
    {
        _grid.Children.Clear();
        var leftIcon=OpenCodeBrowserColorIcon.Create();leftIcon.Visibility=Visibility.Visible;
        var leftContent=new StackPanel{Spacing=12};leftContent.Children.Add(new TextBlock{Text="Controlled left dock"});leftContent.Children.Add(leftIcon);
        _grid.Children.Add(new Border { Width=240,HorizontalAlignment=HorizontalAlignment.Left,Background=new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255,25,55,35)),Child=leftContent });
        _grid.Children.Add(new Border { Width=320,HorizontalAlignment=HorizontalAlignment.Right,Background=new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255,25,35,65)),Child=new TextBlock{Text="Controlled right dock"} });
        var shown = new Grid();shown.Children.Add(_view);
        var overlay = new OpenCodeBrowserOverlay(shown); _grid.Children.Add(overlay);
        var placement=OpenCodeBrowserOverlayLayout.Place(_grid.ActualWidth,_grid.ActualHeight);overlay.SetSurfaceSize(placement.Width,placement.Height);
        var page = new OpenCodeBrowserPage(_view,Cdp);
        var workspace=new OpenCodeBrowserWorkspace { Shown=page };workspace.OverlayChanged+=(_,_)=>{overlay.Bind(workspace);overlay.Visibility=workspace.Shown is null?Visibility.Collapsed:Visibility.Visible;};
        page.Frames.Tree((await Cdp("Page.getFrameTree"))["frameTree"]!.AsObject(),null);
        foreach(var (frame,id) in _contexts)page.Frames.ContextCreated(null,new JsonObject{["id"]=id,["auxData"]=new JsonObject{["frameId"]=frame,["isDefault"]=true}});
        var posted = Array.Empty<BrowserSelectionAttachment>();
        using var selection = new OpenCodeBrowserSelection(overlay,(p,node,generation,frame)=>"fixture-ref-"+node,
            (p,attachments)=>{posted=attachments.ToArray();return Task.FromResult(true);});
        overlay.Bind(workspace);await Task.Delay(100);
        var presenter=(FlyoutPresenter)Field(overlay,"_presenter");
        if(Math.Abs(presenter.ActualWidth-placement.Width)>.5||Math.Abs(presenter.ActualHeight-placement.Height)>.5||Math.Abs(overlay.ContentWidth-(placement.Width-2))>.5)throw new InvalidOperationException("Actual native browser presenter is not full content size: "+presenter.ActualWidth+" / "+placement.Width+" body "+overlay.ContentWidth);
        var chrome=(Grid)presenter.Content;
        var smoke=(Border)overlay.Children[0];
        if(chrome.RowDefinitions.Count!=2||chrome.RowDefinitions[0].Height.Value!=46||Math.Abs(smoke.ActualWidth-_grid.ActualWidth)>.5||Math.Abs(smoke.ActualHeight-_grid.ActualHeight)>.5)throw new InvalidOperationException("Native browser chrome is not one-row/full content coverage");
        foreach(var edge in new[]{5d,_grid.ActualWidth-5})
        {
            var hits=Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(new Windows.Foundation.Point(edge,_grid.ActualHeight/2),_grid);
            if(!ReferenceEquals(hits.FirstOrDefault(),smoke))throw new InvalidOperationException("Actual modal smoke does not intercept a resident side dock");
        }
        _trace.Add(new {check="actual native overlay full-content presentation",rootWidth=_grid.ActualWidth,rootHeight=_grid.ActualHeight,surfaceWidth=presenter.ActualWidth,surfaceHeight=presenter.ActualHeight,contentWidth=overlay.ContentWidth,contentHeight=overlay.ContentHeight,ok=true});
        await Eval("window.__outlines=[]; const nativeStroke=CanvasRenderingContext2D.prototype.strokeRect; CanvasRenderingContext2D.prototype.strokeRect=function(x,y,w,h){window.__outlines.push({x,y,w,h,color:this.strokeStyle});return nativeStroke.call(this,x,y,w,h)}");
        var states=(IDictionary)Field(selection,"_states");
        var state=states[page]!;var list=(OpenCodeBrowserSelectionList)Field(state,"List");
        var tray=(OpenCodeBrowserSelectedList)Field(selection,"_list");
        string ErrorText()=>((TextBlock)Field(tray,"_status")).Text;
        await Invoke(selection,"ActivateAsync",OpenCodeBrowserSelectionTool.Element);
        var rootBox=(await Eval("document.querySelector('#target').getBoundingClientRect().toJSON()"))["result"]!["value"]!;
        foreach(var type in new[]{"mouseMoved","mousePressed","mouseReleased"})await Cdp("Input.dispatchMouseEvent",new(){["type"]=type,["x"]=rootBox["x"]!.GetValue<double>()+20,["y"]=rootBox["y"]!.GetValue<double>()+20,["button"]=type=="mouseMoved"?"none":"left",["clickCount"]=type=="mouseMoved"?0:1});
        await Until(()=>list.Items.Count>0||ErrorText().StartsWith("无法完成操作"));
        if(ErrorText().StartsWith("无法完成操作"))throw new InvalidOperationException("Actual selection controller: "+ErrorText());
        if(list.Items.Single().Elements.Single().Selector!="#target")throw new InvalidOperationException("Native inspect chose wrong element");
        _trace.Add(new { check="actual selection controller root native inspect, image and tray", item=list.Items.Single().Selector, @ref=list.Items.Single().Elements.Single().Ref, ok=true });
        var childFrame=page.Frames.Items.Single(f=>f.ParentId is not null);
        var childContext=page.Frames.Context(childFrame.Id)!.Value;
        var childNode=await Cdp("Runtime.evaluate",new(){["expression"]="document.querySelector('#child')",["contextId"]=childContext});
        var childObject=childNode["result"]!["objectId"]!.ToString();
        var childBackend=(await Cdp("DOM.describeNode",new(){["objectId"]=childObject}))["node"]!["backendNodeId"]!.GetValue<int>();
        await Cdp("Runtime.releaseObject",new(){["objectId"]=childObject});
        var childQuad=(await Cdp("DOM.getBoxModel",new(){["backendNodeId"]=childBackend}))["model"]!["border"]!.AsArray();
        foreach(var type in new[]{"mouseMoved","mousePressed","mouseReleased"})await Cdp("Input.dispatchMouseEvent",new(){["type"]=type,["x"]=childQuad[0]!.GetValue<double>()+10,["y"]=childQuad[1]!.GetValue<double>()+10,["button"]=type=="mouseMoved"?"none":"left",["clickCount"]=type=="mouseMoved"?0:1});
        await Until(()=>list.Items.Any(i=>i.Elements.Any(e=>e.Selector=="#child"))||ErrorText().StartsWith("无法完成操作"));
        if(ErrorText().StartsWith("无法完成操作"))throw new InvalidOperationException("Actual iframe element controller: "+ErrorText());
        var childSelected=list.Items.Single().Elements.Single();
        if(childSelected.FrameId!=childFrame.Id||Math.Abs(childSelected.Box.X-childQuad[0]!.GetValue<double>())>.5||Math.Abs(childSelected.Box.Y-childQuad[1]!.GetValue<double>())>.5)throw new InvalidOperationException("Native iframe ref/geometry ownership mismatch");
        _trace.Add(new{check="actual native inspect of same-process iframe retains frame ref and top-viewport geometry",element=childSelected,ok=true});
        foreach(var type in new[]{"mouseMoved","mousePressed","mouseReleased"})await Cdp("Input.dispatchMouseEvent",new(){["type"]=type,["x"]=5,["y"]=90,["button"]=type=="mouseMoved"?"none":"left",["clickCount"]=type=="mouseMoved"?0:1});
        await Until(()=>list.Items.Any(i=>i.Elements.Any(e=>e.Selector=="body"))||ErrorText().StartsWith("无法完成操作"));
        if(ErrorText().StartsWith("无法完成操作"))throw new InvalidOperationException("Actual oversized element controller: "+ErrorText());
        var oversized=list.Items.Single();
        if(oversized.Image.Length==0||oversized.Box.Y>=0)throw new InvalidOperationException("Oversized/offscreen element did not produce a safe visible crop");
        _trace.Add(new{check="oversized scrolled body selection clamps screenshot to visible viewport without invalid clip",box=oversized.Box,imageBytes=oversized.Image.Length,ok=true});
        list.Clear(); await Invoke(selection,"ActivateAsync",OpenCodeBrowserSelectionTool.Region);
        var before=(await Eval("({w:innerWidth,h:innerHeight,x:scrollX,y:scrollY,box:document.querySelector('#target').getBoundingClientRect().toJSON()})"))["result"]!["value"]!;
        var b=before["box"]!;var x=b["x"]!.GetValue<double>();var y=b["y"]!.GetValue<double>();
        await Cdp("Input.dispatchMouseEvent",new(){["type"]="mousePressed",["x"]=x-8,["y"]=y-8,["button"]="left",["clickCount"]=1});
        await Cdp("Input.dispatchMouseEvent",new(){["type"]="mouseMoved",["x"]=x+178,["y"]=y+56,["button"]="left",["buttons"]=1});
        await Cdp("Input.dispatchMouseEvent",new(){["type"]="mouseReleased",["x"]=x+178,["y"]=y+56,["button"]="left",["clickCount"]=1});
        await Until(()=>list.Items.Count>0||ErrorText().StartsWith("无法完成操作"));
        if(ErrorText().StartsWith("无法完成操作"))throw new InvalidOperationException("Actual region controller: "+ErrorText());
        await Task.Delay(100);
        var after=(await Eval("({w:innerWidth,h:innerHeight,x:scrollX,y:scrollY,box:document.querySelector('#target').getBoundingClientRect().toJSON()})"))["result"]!["value"]!;
        if(before.ToJsonString()!=after.ToJsonString())throw new InvalidOperationException("Opening annotation tray changed actual page viewport/target: "+before+" -> "+after);
        if(!list.Items.Single().Elements.Any(e=>e.Selector=="#target"))throw new InvalidOperationException("Native region omitted intended element");
        var nativeItems=(StackPanel)Field(tray,"_items");
        var note=((StackPanel)((Border)nativeItems.Children[0]).Child).Children.OfType<TextBox>().Single(); note.Text="Controlled region note";
        try { await NativeScreenshot.SaveAsync(_window!,Path.Combine(Program.Evidence,"native-region-panel-open.png")); }
        catch(Exception ex)
        {
            File.WriteAllText(Path.Combine(Program.Evidence,"native-screen-unavailable.txt"),ex.ToString());
            // WinUI raster evidence deliberately does not claim to capture the WebView's separate surface.
            await NativeScreenshot.SaveUiOnlyAsync(_window!,Path.Combine(Program.Evidence,"native-region-panel-ui-only.png"));
        }
        // Always keep a WinUI raster too: PrintWindow returns blank frames while the workstation is locked.
        await NativeScreenshot.SaveUiOnlyAsync(_window!,Path.Combine(Program.Evidence,"native-region-panel-open-xaml.png"));
        await Eval("window.__outlines=[];scrollBy(0,73)"); await Task.Delay(100);
        var scrolled=(await Eval("({x:scrollX,y:scrollY,box:document.querySelector('#target').getBoundingClientRect().toJSON(),outlines:window.__outlines})"))["result"]!["value"]!;
        var browserImage=await Cdp("Page.captureScreenshot",new(){["format"]="png"});
        File.WriteAllBytes(Path.Combine(Program.Evidence,"browser-region-after-scroll.png"),Convert.FromBase64String(browserImage["data"]!.ToString()));
        var region=list.Items.Single().Box;var expectedY=region.Y+before["y"]!.GetValue<double>()-scrolled["y"]!.GetValue<double>();
        if(!scrolled["outlines"]!.AsArray().Any(o=>Math.Abs(o!["y"]!.GetValue<double>()-expectedY)<.5&&Math.Abs(o["w"]!.GetValue<double>()-region.Width)<.5))throw new InvalidOperationException("Region outline lost its document anchor after scroll");
        // Return to the original scroll to verify the close/open action itself changes nothing.
        await Eval("scrollTo("+before["x"]+","+before["y"]+")");await Task.Delay(70);
        var hide=((Grid)tray.Children[0]).Children.OfType<Button>().Single();
        var hidePeer=new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(hide);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)hidePeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await Until(()=>tray.Visibility==Visibility.Collapsed);await Task.Delay(70);
        var closed=(await Eval("({w:innerWidth,h:innerHeight,x:scrollX,y:scrollY,box:document.querySelector('#target').getBoundingClientRect().toJSON()})"))["result"]!["value"]!;
        if(before.ToJsonString()!=closed.ToJsonString())throw new InvalidOperationException("Closing annotation tray changed actual page viewport");
        try { await NativeScreenshot.SaveAsync(_window!,Path.Combine(Program.Evidence,"native-region-panel-closed.png")); }
        catch(Exception ex) { File.WriteAllText(Path.Combine(Program.Evidence,"native-closed-screen-unavailable.txt"),ex.ToString()); await NativeScreenshot.SaveUiOnlyAsync(_window!,Path.Combine(Program.Evidence,"native-region-panel-closed-ui-only.png")); }
        await NativeScreenshot.SaveUiOnlyAsync(_window!,Path.Combine(Program.Evidence,"native-region-panel-closed-xaml.png"));
        var selectedElement=list.Items.Single().Elements.Single(e=>e.Selector=="#target");
        tray.Render(list);await Invoke(selection,"AddToConversationAsync");
        if(!posted.Any(a=>a.Details.Contains("Controlled region note")&&a.Details.Contains("fixture-ref-")))throw new InvalidOperationException("Posted annotation lost original selected ref or note");
        var details=JsonNode.Parse(posted.Single(a=>a.Details.Contains("Controlled region note")).Details)!;
        if(details["coordinateSpace"]?.ToString()!="viewport-at-capture"||Math.Abs(details["documentBox"]!["y"]!.GetValue<double>()-region.Y-before["y"]!.GetValue<double>())>.5)throw new InvalidOperationException("Posted region coordinates lost original document/viewport metadata");
        File.WriteAllText(Path.Combine(Program.Evidence,"posted-annotations.json"),JsonSerializer.Serialize(posted.Select(a=>new{a.Name,a.Details,a.Context,imageBytes=a.Image.Length}),new JsonSerializerOptions{WriteIndented=true}));
        _trace.Add(new { check="actual native region drag -> annotation open/close -> scroll -> note -> attachment",before,after,closed,scrolled,element=selectedElement,ok=true });
        await Invoke(selection,"ActivateAsync",OpenCodeBrowserSelectionTool.Region);
        childQuad=(await Cdp("DOM.getBoxModel",new(){["backendNodeId"]=childBackend}))["model"]!["border"]!.AsArray();
        var childX=childQuad[0]!.GetValue<double>();var childY=childQuad[1]!.GetValue<double>();
        foreach(var (type,px,py) in new[]{("mousePressed",childX-3,childY-3),("mouseMoved",childQuad[4]!.GetValue<double>()+3,childQuad[5]!.GetValue<double>()+3),("mouseReleased",childQuad[4]!.GetValue<double>()+3,childQuad[5]!.GetValue<double>()+3)})
            await Cdp("Input.dispatchMouseEvent",new(){["type"]=type,["x"]=px,["y"]=py,["button"]="left",["buttons"]=type=="mouseReleased"?0:1,["clickCount"]=type=="mouseMoved"?0:1});
        await Until(()=>list.Items.Count>0||ErrorText().StartsWith("无法完成操作"));
        if(ErrorText().StartsWith("无法完成操作"))throw new InvalidOperationException("Actual iframe region query: "+ErrorText());
        if(!list.Items.Single().Elements.Any(e=>e.Selector=="#child"&&e.FrameId==childFrame.Id))throw new InvalidOperationException("Region query resolved the iframe selector in the wrong document");
        _trace.Add(new{check="native iframe region selection queries the frame document object without frontend nodeId",element=list.Items.Single().Elements.Single(e=>e.Selector=="#child"),ok=true});
        var genericIcon=OpenCodeBrowserColorIcon.Create();
        if(genericIcon is not FontIcon { Glyph: "\uE774", FontSize: 18 } coloredGlobe || coloredGlobe.FontFamily.Source!="Segoe Fluent Icons" || genericIcon.Width!=18||genericIcon.Height!=18)
            throw new InvalidOperationException("Colored browser icon must retain the exact existing native globe glyph");
        _trace.Add(new{check="colored browser icon keeps the exact native Segoe Fluent E774 globe glyph, not an engine brand",ok=true});
        var actions=(StackPanel)Field(overlay,"_actions");var close=(Button)actions.Children[^1];
        var closePeer=new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(close);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)closePeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await Until(()=>workspace.Shown is null&&overlay.Page is null&&overlay.Visibility==Visibility.Collapsed);
        try { await NativeScreenshot.SaveAsync(_window!,Path.Combine(Program.Evidence,"native-generic-open-icon.png")); }
        catch(Exception ex) { File.WriteAllText(Path.Combine(Program.Evidence,"native-icon-screen-unavailable.txt"),ex.ToString()); await NativeScreenshot.SaveUiOnlyAsync(_window!,Path.Combine(Program.Evidence,"native-generic-open-icon-ui-only.png")); }
        await NativeScreenshot.SaveUiOnlyAsync(_window!,Path.Combine(Program.Evidence,"native-generic-open-icon-xaml.png"));
        workspace.Shown=page;overlay.Visibility=Visibility.Visible;overlay.Bind(workspace);await Task.Delay(70);
        if(Math.Abs(presenter.ActualWidth-placement.Width)>.5||Math.Abs(overlay.ContentWidth-(placement.Width-2))>.5)throw new InvalidOperationException("Reopened browser returned to a partial panel");
        _trace.Add(new{check="native WinUI close button invocation and full-content reopen, one toolbar row",nativeCloseControl=close.GetType().FullName,ok=true});
        page.Closed=true;
    }
}
