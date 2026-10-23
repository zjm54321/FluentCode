using System;
using System.Linq;
using System.Threading.Tasks;
using AIFrontend.Pages;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

internal static class AgentPresentationTests
{
    internal const string LongName="Orchestrator full display name with an intentionally long descriptive name beyond one hundred DIP";
    private static int checks;
    private static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    private static void IsA(OpenCodePage p,string where)=>Check(p.AgentName==LongName&&p.AgentColor is{R:0x12,G:0x3a,B:0xbc}&&Equals(p.AgentDescription,"description build"),"A exact full name/RGB/description "+where);
    private static MenuFlyoutItem Item(MenuFlyout menu,string text)=>menu.Items.OfType<MenuFlyoutItem>().Single(i=>i.Text==text);
    internal static async Task BaselineProbe(string xaml)
    {
        var p=new OpenCodePage(xaml);p.Load();p.AgentData();p.New();await p.WaitDefaults();IsA(p,"baseline initial handler/default path");
        var menu=await p.AgentMenu();Item(menu,"Other full agent").Press();await p.WaitAgentIdle();
        menu=await p.AgentMenu();p.HoldModels();Item(menu,LongName).Press();await p.WaitModelsStarted();
        Check(p.Preferences?.Agent=="build"&&p.AgentColor is null&&p.AgentName!=LongName,"BASELINE reproduced: actual A-B-A click keeps selected A but loses RGB/full name while models pending");
        p.FailModels();await p.WaitAgentIdle();
        Check(p.AgentEnabled&&p.Preferences?.Agent=="build"&&p.AgentColor is null&&p.AgentName=="未就绪","BASELINE reproduced: failed models leave selected A permanently unpresented after finally, without another user action");
        Console.WriteLine($"PASS R1_BASELINE_REPRO {checks} assertions; baseline production handler/labels/metadata, current source/client/core fixture; no native binary claim");
    }
    internal static async Task Run(string xaml)
    {
        var p=new OpenCodePage(xaml);p.Load();p.AgentData();p.New();await p.WaitDefaults();var a=p.BlankTab!;IsA(p,"after initial actual defaults");
        var menu=await p.AgentMenu();
        Check(menu.Items.Count==2,"real agent menu omits hidden/subagent-only entries");
        var colored=Item(menu,LongName);Check(colored.Foreground is SolidColorBrush{Color:{R:0x12,G:0x3a,B:0xbc}}&&Equals(colored.Attached["Tooltip"],"description build"),"real candidate carries accepted RGB/description");
        Check(colored.Content is TextBlock{Text:LongName,TextWrapping:Microsoft.UI.Xaml.TextWrapping.Wrap,TextTrimming:Microsoft.UI.Xaml.TextTrimming.None,MaxLines:0},"actual Loaded menu name producer wraps complete name, no ellipsis");
        Item(menu,"Other full agent").Press();await p.WaitAgentIdle();
        Check(p.AgentName=="Other full agent"&&p.AgentColor is null&&Equals(p.AgentDescription,"description other"),"real B selection clears local RGB back to theme and keeps B description");
        colored.Press();Check(p.AgentName=="Other full agent"&&p.AgentColor is null,"late A menu closure cannot overwrite a newer B choice");
        menu=await p.AgentMenu();p.HoldModels();Item(menu,LongName).Press();await p.WaitModelsStarted();IsA(p,"immediately while unrelated models still pending");
        Check(!p.AgentEnabled,"pending/disabled state does not erase A local RGB or full name");
        p.HoldConfig();p.ReleaseModels();await p.WaitConfigStarted();IsA(p,"while unrelated config is still pending");
        p.ReleaseConfig();await p.WaitAgentIdle();IsA(p,"after unrelated config failure");
        Check(p.AgentEnabled&&p.PreferenceError is not null,"failure re-enables actual button without throwing away accepted presentation");
        menu=await p.AgentMenu();p.HoldModels();Item(menu,LongName).Press();await p.WaitModelsStarted();p.FailModels();await p.WaitAgentIdle();IsA(p,"after unrelated model failure before shared model metadata installation");
        Check(!p.ModelMetadataReady,"retained agent presentation never marks failed/stale model catalog fresh");
        menu=await p.AgentMenu();Item(menu,LongName).Press();await p.WaitAgentIdle();
        p.New();await p.WaitDefaults();var b=p.BlankTab!;menu=await p.AgentMenu();Item(menu,"Other full agent").Press();await p.WaitAgentIdle();
        p.Select(a);IsA(p,"after independent blank B return");p.Select(b);Check(p.AgentColor is null&&p.AgentName=="Other full agent","B never borrows same-ID/global cached RGB from A");
        p.Select(a);var source=p.SourceInstance;p.LeavePage();p.ReturnPage(source);IsA(p,"after same-source Page return and Home runtime renewal");
        p.HoldModels();var oldLoad=p.ReloadAgentDefaults();await p.WaitModelsStarted();var releaseOld=p.ModelsRelease();
        p.New();await p.WaitDefaults();var newerName=p.AgentName;p.HoldModels();var newLoad=p.ReloadAgentDefaults();await p.WaitModelsStarted();var releaseNew=p.ModelsRelease();
        releaseOld();await oldLoad;
        Check(p.AgentName==newerName&&p.Preferences?.Agent=="build"&&!p.AgentEnabled&&!newLoad.IsCompleted,"late old-slot response/finally cannot change new selection or clear its pending flag");
        releaseNew();await newLoad;
        p.HoldAgents();var oldAgents=p.ReloadAgentDefaults();await p.WaitAgentsStarted();var releaseAgents=p.AgentsRelease();
        p.ReplaceSource();p.AgentData(false);p.New();await p.WaitDefaults();releaseAgents();await oldAgents;
        Check(p.AgentName=="Build"&&p.AgentColor is null,"late old-source colored catalog cannot color a fresh same-ID agent");
        p.ChangeBlankDirectory("/different");await p.WaitAgentIdle();
        Check(p.AgentColor is null&&p.AgentName!=LongName,"different directory never reuses old accepted agent presentation");
        Check(p.HttpMutations==0,"all presentation/selection checks create no config/send mutation");
        AIFrontend.Pages.OpenCode.OpenCodeAgentSelector.CheckActual(p);
        Console.WriteLine($"PASS R1 actual blank agent click/labels/color: {checks} assertions; native brush/template layout remains Gate");
    }
}
