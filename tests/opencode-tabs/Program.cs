using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using AIFrontend.Pages;
using Microsoft.UI.Xaml.Controls;
internal static class Program
{
    private static int checks;private static string xaml="";
    private static void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);}
    private static OpenCodePage Page(bool connected=true){var p=new OpenCodePage(xaml,connected);p.Load();return p;}
    private static void OnlyHome(OpenCodePage p,string name)
    {Check(p.SessionTabs.TabItems.Count==1&&ReferenceEquals(p.SessionTabs.TabItems[0],p.HomeTab)&&ReferenceEquals(p.SessionTabs.SelectedItem,p.HomeTab)&&p.Home&&!p.Blank,name);}
    private static void Main(string[] args)
    {
        xaml=Path.Combine(args[0],"src/AIFrontend/Pages/OpenCodePage.xaml");
        if(Environment.GetEnvironmentVariable("AIF_R1_BASELINE_PROBE")=="1"){AgentPresentationTests.BaselineProbe(xaml).GetAwaiter().GetResult();return;}
        var cold=new OpenCodePage(xaml,false);
        Check(cold.SessionTabs.TabItems.Count==1&&ReferenceEquals(cold.SessionTabs.SelectedItem,cold.HomeTab)&&cold.LastLayout=="HomeLayout","Home exists/selected before Loaded, without connection or first resize");
        Check(cold.HomeTab.Header is FontIcon{Glyph:"\uE80F"}&&!cold.HomeTab.IsClosable&&cold.HomeTab.Tag is not string&&cold.HomeTab.MinWidth==48&&cold.HomeTab.MaxWidth==48,"real icon-only pinned item shape and opaque identity from actual XAML");
        cold.Load();cold.Load();OnlyHome(cold,"Loaded idempotently retains one real Home and Home layout");
        Check(cold.LastLayout=="HomeLayout"&&cold.MessageLoads==0&&cold.Tracking==0,"disconnected Home initialization does not activate a session");
        cold.Close(cold.HomeTab);OnlyHome(cold,"native close/middle/keyboard-equivalent Home close request is rejected");Check(cold.DragHome(),"actual Home UIElement DragStarting cancels originating drag");

        var p=Page();p.AddSession("a");p.AddSession("b");p.Select(p.Session("a"));
        Check(p.Current=="a"&&!p.Home&&p.LastLayout=="SessionLayout"&&p.MessageLoads==1,$"real Tab selection routes through actual SwitchToSessionAsync and native list handler (current={p.Current},home={p.Home},layout={p.LastLayout},loads={p.MessageLoads},notices={p.Notices})");
        p.Text("keep draft");var fence=p.CaptureBindingFence();var loads=p.MessageLoads;p.HomeNow();
        Check(p.Home&&ReferenceEquals(p.SessionTabs.SelectedItem,p.HomeTab)&&p.Current=="a"&&!p.Draft.IsActive,"Home selects collection item and suspends composer while retaining real session binding");
        Check(fence(),"Home does not corrupt source/session binding ownership fields");
        p.Select(p.Session("a"));Check(!p.Home&&p.MessageLoads==loads&&p.Current=="a"&&p.Composer.Text=="keep draft","same accepted current session restores its slot from Home without unnecessary history read");
        p.Select(p.Session("b"));Check(!fence()&&p.Current=="b","actual switch invalidates captured session-generation fence");
        var oldA=p.Session("a");var sourceFence=p.CaptureBindingFence();p.ReplaceSource();Check(!sourceFence(),"actual binding comparison rejects previous source/revision/client owner");
        p.AddSession("b");loads=p.MessageLoads;p.SwitchAgain("b").GetAwaiter().GetResult();Check(p.MessageLoads==loads+1,"same session ID under replaced source cannot bypass actual IsSameMessageBinding guards");
        p.HomeNow();p.New();Check(p.Blank&&!p.Home&&ReferenceEquals(p.SessionTabs.SelectedItem,p.BlankTab),"Home to singleton blank uses real unbound layout/selection");
        var blank=p.BlankTab;p.New();var secondBlank=p.BlankTab;Check(!ReferenceEquals(secondBlank,blank)&&p.BlankCount==2,"each plus allocates a stable independent native blank");
        p.HomeNow();p.Close(oldA);Check(p.Home&&ReferenceEquals(p.SessionTabs.SelectedItem,p.HomeTab),"inactive retired actor close on Home does not steal selection into preserved hidden blank");
        p.Select(blank!);p.Creating=true;p.Close(blank!);Check(ReferenceEquals(p.BlankTab,blank),"pending blank create blocks its own native close");p.Creating=false;p.HomeNow();
        p.Close(blank!);Check(p.Home&&p.BlankTab is null&&ReferenceEquals(p.SessionTabs.SelectedItem,p.HomeTab),"inactive blank close preserves Home selection");
        p.Close(secondBlank!);Check(p.Home&&!p.Draft.IsActive,"closing both hidden blanks leaves Home suspended, not a carried cross-slot editor");

        foreach(var order in new[]{"blank-first","real-first"})
        {
            var q=Page();q.AddSession("a");q.Select(q.Session("a"));q.New();var b=q.BlankTab!;
            if(order=="blank-first"){q.Close(b);Check(q.Current=="a"&&!q.Home,"active blank close activates adjacent real session");q.Close(q.Session("a"));}
            else{q.Close(q.Session("a"));Check(q.Blank&&!q.Home,"inactive real close leaves active blank");q.Close(b);}
            q.DispatcherQueue.Drain();OnlyHome(q,"all closable closed falls to Home without delayed auto-blank: "+order);
            Check(q.DispatcherQueue.Count==0&&q.BlankTab is null,"no queued blank resurrection");
        }
        var last=Page();last.AddSession("last");last.Select(last.Session("last"));last.Close(last.Session("last"));OnlyHome(last,"last real tab closes directly to Home");
        var onlyBlank=Page();onlyBlank.New();onlyBlank.Close(onlyBlank.BlankTab!);OnlyHome(onlyBlank,"last blank tab closes directly to Home");
        var rollback=Page();rollback.AddSession("a");rollback.AddSession("missing");rollback.Select(rollback.Session("a"));rollback.ForgetMetadata("missing");rollback.Select(rollback.Session("missing"));
        Check(rollback.Current=="a"&&ReferenceEquals(rollback.SessionTabs.SelectedItem,rollback.Session("a")),"missing-target activation rolls back to real current tab");
        rollback.Close(rollback.Session("a"));Check(rollback.Home&&ReferenceEquals(rollback.SessionTabs.SelectedItem,rollback.HomeTab),"rejected adjacent activation after current close falls to real Home layout, not selected-Home/old-content mismatch");
        var bg=Page();bg.AddSession("a");bg.BackgroundHighlight("a");Check(bg.Home&&bg.Current is null&&ReferenceEquals(bg.SessionTabs.SelectedItem,bg.HomeTab),"background catalog update while Home cannot navigate or activate tab");
        bg.DirectRemove("a");OnlyHome(bg,"HomeActions low-level real session removal preserves actual Home");
        var other=Page();other.AddSession("a");other.AddSession("b");var closedOther=other.Session("b");other.New();other.HomeNow();other.CloseOthers("a");
        Check(other.SessionTabs.TabItems.Contains(other.HomeTab)&&other.SessionTabs.TabItems.Contains(other.BlankTab!)&&!other.SessionTabs.TabItems.Contains(closedOther),"existing close-other-session semantics preserve Home and blank, do not invent CloseAll");
        var convert=Page();convert.New();convert.ConvertBlank("converted");Check(convert.BlankTab is null&&ReferenceEquals(convert.SessionTabs.TabItems[0],convert.HomeTab)&&convert.SessionTabs.TabItems.IndexOf(convert.Session("converted"))>=1,"blank-to-real replacement keeps Home first");
        var insert=Page();insert.InsertSessionAt("insert",0);Check(ReferenceEquals(insert.SessionTabs.TabItems[0],insert.HomeTab),"normal explicit insert is clamped after Home");

        var drag=Page();drag.AddSession("a");drag.AddSession("b");drag.AddSession("c");drag.Select(drag.Session("a"));drag.MoveBeforeHome(drag.Session("c"));
        Check(!ReferenceEquals(drag.SessionTabs.TabItems[0],drag.HomeTab)&&drag.DispatcherQueue.Count==1&&!drag.SessionTabs.TabItems.RecursiveMutation,"collection notification only coalesces deferred normalization, no recursive vector edits");
        drag.Select(drag.Session("b"));var selected=drag.SessionTabs.SelectedItem;loads=drag.MessageLoads;drag.DispatcherQueue.Drain();
        Check(ReferenceEquals(drag.SessionTabs.TabItems[0],drag.HomeTab)&&ReferenceEquals(drag.SessionTabs.SelectedItem,selected)&&drag.MessageLoads==loads,"normalization keeps execution-time new selected object and does not trigger switch");
        Check(drag.SessionTabs.TabItems.OfType<TabViewItem>().Where(i=>i.Tag is string).Select(i=>(string)i.Tag).SequenceEqual(new[]{"c","a","b"}),"normalization preserves all other relative tab order");
        drag.MoveBeforeHome(drag.Session("a"));drag.UnloadFixture();var sequence=drag.SessionTabs.TabItems.ToArray();drag.DispatcherQueue.Drain();Check(sequence.SequenceEqual(drag.SessionTabs.TabItems),"unloaded queued callback cannot mutate old page");
        drag.ReloadGeneration();drag.DispatcherQueue.Drain();Check(ReferenceEquals(drag.SessionTabs.TabItems[0],drag.HomeTab),"reload retries pinning under the current lifetime");
        drag.MoveBeforeHome(drag.Session("c"));drag.ReloadGeneration();drag.Select(drag.Session("a"));selected=drag.SessionTabs.SelectedItem;loads=drag.MessageLoads;drag.DispatcherQueue.Drain();
        Check(ReferenceEquals(drag.SessionTabs.TabItems[0],drag.HomeTab)&&ReferenceEquals(drag.SessionTabs.SelectedItem,selected)&&loads==drag.MessageLoads,"old lifetime callback reschedules under current lifetime without stealing newer selection");
        drag.DispatcherQueue.Accept=false;drag.MoveBeforeHome(drag.Session("b"));Check(drag.DispatcherQueue.Count==0,"enqueue failure is not misreported as normalization");drag.DispatcherQueue.Accept=true;drag.ReloadGeneration();drag.DispatcherQueue.Drain();Check(ReferenceEquals(drag.SessionTabs.TabItems[0],drag.HomeTab),"enqueue failure releases queued guard for later retry");
        var missingHome=Page();missingHome.AddSession("a");missingHome.MoveBeforeHome(missingHome.Session("a"));missingHome.SessionTabs.TabItems.Remove(missingHome.HomeTab);missingHome.DispatcherQueue.Drain();Check(!missingHome.SessionTabs.TabItems.Contains(missingHome.HomeTab),"deferred notification does not manufacture a duplicate Home during temporary native removal");

        var doc=XDocument.Load(xaml);XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation";XNamespace x="http://schemas.microsoft.com/winfx/2006/xaml";
        var tabs=doc.Descendants(ns+"TabView").Single();Check(!tabs.Elements(ns+"TabView.TabStripHeader").Any()&&!doc.Descendants().Any(e=>(string?)e.Attribute(x+"Name")=="NoOpenTabsPanel"),"no external fake Home or no-open shell remains");
        Check((string?)tabs.Attribute("Height") is null&&(string?)tabs.Attribute("CanDragTabs")=="False"&&(string?)tabs.Attribute("CanReorderTabs")=="True"&&(string?)tabs.Attribute("AllowDropTabs")=="True","native strip height untouched; same-collection reorder enabled without drag-out");
        Console.WriteLine($"PASS A6 actual Tab collection/selection/close + actual Switch/binding: {checks} assertions; native controls/IO boundaries inert, no native layout/keyboard/drag claim");
        WorkingClockTests.Run();
        RetirementTests.Run(xaml);
        var work=Page();work.AddSession("w1");work.AddSession("w2");var shared=AIFrontend.Pages.OpenCode.OpenCodeWorkingClock.Shared;var baseCount=shared.SubscriberCount;
        work.Shimmer("w1").SetWorking(true);work.Shimmer("w2").SetWorking(true);Check(shared.SubscriberCount==baseCount+2&&shared.IsRunning,"B13 actual tab entries join the shared working clock");
        var w1Renders=work.Shimmer("w1").Renders;work.Close(work.Session("w2"));Check(shared.SubscriberCount==baseCount+1&&shared.IsRunning&&work.Shimmer("w1").Renders==w1Renders,"B13 actual tab close leaves the clock without touching other working tabs");
        work.DirectRemove("w1");Check(shared.SubscriberCount==baseCount&&!shared.IsRunning,"B13 removing the last working tab stops the shared clock");
        DraftSlotPageTests.Run(xaml).GetAwaiter().GetResult();
        OpenCodePage.NavigationLifecycleTest(xaml);
        DirectorySelectorTests.Run(xaml).GetAwaiter().GetResult();
    }
}
