using System;
using System.Linq;
using AIFrontend.Pages.OpenCode;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

internal static class Program
{
    private static int checks;
    private static void Check(bool ok,string name) { if(!ok)throw new Exception(name);checks++; }
    private static void Main()
    {
        Check(typeof(OpenCodeWorkingPhrases).GetField("EnableModelSatire",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.GetRawConstantValue() is false,"satire const disabled by default");
        Check(OpenCodeWorkingPhrases.Playful.Count is >=10 and <=20,"tasteful general pool size");
        foreach(var sample in new[] { ("proxy/claude-sonnet",OpenCodeWorkingFamily.Claude),("gpt-6",OpenCodeWorkingFamily.OpenAI),("gemini-3",OpenCodeWorkingFamily.Gemini),("gateway/deepseek-v4",OpenCodeWorkingFamily.DeepSeek),("opaque-id",OpenCodeWorkingFamily.Unknown),("notclaude",OpenCodeWorkingFamily.Unknown) })
            Check(OpenCodeWorkingPhrases.Family(sample.Item1)==sample.Item2,"actual model family: "+sample.Item1);
        Check(OpenCodeWorkingPhrases.Family(null)==OpenCodeWorkingFamily.Unknown,"missing executing model stays unknown");
        Check(OpenCodeWorkingPhrases.Satire(OpenCodeWorkingFamily.Claude).SequenceEqual(new[]{"封禁中文中…","封禁中国人中…","检测中国人中…"}),"review-only copy preserved verbatim");
        var picker=new OpenCodeWorkingPhrasePicker(()=>0);
        picker.Start(OpenCodeWorkingActivity.Reasoning,OpenCodeWorkingFamily.Claude,0);
        Check(picker.Current=="思考中…" && picker.Tier==OpenCodeWorkingTier.Standard,"working starts with real standard state");
        for(var t=1;t<8000;t+=10) { picker.UpdateContext(OpenCodeWorkingActivity.Reasoning,OpenCodeWorkingFamily.Claude,t);picker.Tick(t); }
        Check(picker.ChangeCount==1,"stream updates never rotate phrases early");
        picker.Tick(8000);Check(picker.Tier==OpenCodeWorkingTier.Playful,"default picker never enters satire even at lowest roll");
        Check(!picker.Tick(15999),"next cadence scheduled from last rotation");
        picker.Stop();Check(!picker.Tick(90000),"stopped picker does no work");
        var late=new OpenCodeWorkingPhrasePicker(()=>0.999999);late.Start(OpenCodeWorkingActivity.General,OpenCodeWorkingFamily.Unknown,0);
        Check(!late.Tick(11999)&&late.Tick(12000),"cadence upper bound twelve seconds");
        var unknown=new OpenCodeWorkingPhrasePicker(()=>0,satireEnabled:true);unknown.Start(OpenCodeWorkingActivity.General,OpenCodeWorkingFamily.Unknown,0);unknown.Tick(8000);
        Check(unknown.Tier!=OpenCodeWorkingTier.Satire,"unknown family always general even if review flag enabled");
        var reviewed=new OpenCodeWorkingPhrasePicker(()=>0,satireEnabled:true);reviewed.Start(OpenCodeWorkingActivity.General,OpenCodeWorkingFamily.DeepSeek,0);reviewed.Tick(8000);
        Check(reviewed.Current=="大肥鱼吃白饭中…","matching review-only pool selected only with explicit test opt-in");
        reviewed.UpdateContext(OpenCodeWorkingActivity.General,OpenCodeWorkingFamily.Unknown,8001);
        Check(reviewed.Tier==OpenCodeWorkingTier.Standard,"model change immediately retires stale family copy");
        Check(OpenCodeWorkingMotion.GearAngle(.5)==180&&new[]{0d,.25,.5,.75}.Select(OpenCodeWorkingMotion.LitDots).SequenceEqual(new[]{0,1,2,3}),"gear rotation and discrete dot phases");
        var clock=OpenCodeWorkingClock.Shared;var baseline=clock.SubscriberCount;
        var ancestor=new StackPanel();var indicator=new OpenCodeWorkingIndicator("test");ancestor.Children.Add(indicator.Label);indicator.SetActive(true);
        Check(clock.SubscriberCount==baseline,"unloaded indicator cannot join clock");
        indicator.Label.Load();Check(clock.SubscriberCount==baseline+1,"loaded working indicator subscribes");
        var visual=ElementCompositionPreview.GetElementVisual(indicator.Gear);
        Check(visual.Animations["RotationAngleInDegrees"].Expression==OpenCodeWorkingMotion.GearExpression,"gear expression references shared phase");
        clock.SetMotionEnabled(false);Check(visual.Animations.Count==0&&visual.RotationAngleInDegrees==0&&!clock.IsRunning,"reduced motion stops and resets gear");
        clock.SetMotionEnabled(true);Check(visual.Animations.Count==1,"motion setting resumes joined indicator");
        ancestor.Visibility=Visibility.Collapsed;Check(clock.SubscriberCount==baseline&&visual.Animations.Count==0,"hidden ancestor immediately unsubscribes");
        ancestor.Visibility=Visibility.Visible;Check(clock.SubscriberCount==baseline+1,"visible working ancestor resumes");
        indicator.Label.Unload();Check(clock.SubscriberCount==baseline,"unload unsubscribes");indicator.Label.Load();indicator.SetActive(false);
        Check(clock.SubscriberCount==baseline,"Done unsubscribes");indicator.Dispose();
        var toggles=0;var opened=0;
        var group=new OpenCodeToolGroupView("group",(_,_)=>toggles++);
        var summary=new OpenCodeStepRunSummary("处理中…","",0,0,true,null,false,"处理中");
        group.SetWorkingContext(true,false,"claude-sonnet");group.Update(summary,true,true);
        group.BindSingleLeaf(()=>opened++,null,null,"读取");
        group.ItemsHost.Children.Add(new TextBlock {Text="step one"});
        group.ItemsHost.Children.Add(new TextBlock {Text="step two"});
        LoadTree(group.Element);
        Check(clock.SubscriberCount==baseline+1&&DispatcherQueueTimer.All.Count(t=>t.IsRunning)==1,"group reuses one loaded header timer and shared clock");
        group.SetCurrentStep("读取 · file-a.cs · 运行中");
        ((Button)group.DetailAnchor).Press();
        Check(!group.IsExpanded&&toggles==1&&opened==0,"active single-leaf header collapses rather than opening detail");
        Check(((FrameworkElement)group.ItemsHost.Parent!).Visibility==Visibility.Collapsed&&group.ItemsHost.Children.Count==2,"collapse hides rows without removing them");
        group.SetCurrentStep("执行 · dotnet test · 运行中");group.Update(summary with {SuccessCount=1},false,true);
        var workingLabel=Walk(group.Element).Single(e=>e.Automation.TryGetValue("Id",out var id)&&id=="OpenCodeWorkingIndicator.group");
        var labelTexts=((Panel)workingLabel).Children.OfType<TextBlock>().ToArray();
        Check(labelTexts[0].Text=="执行 · dotnet test · 运行中"&&labelTexts[1].Visibility==Visibility.Collapsed,"collapsed native label updates real step and omits decorative phrase/elapsed");
        Check(labelTexts[0].TextTrimming==TextTrimming.CharacterEllipsis&&labelTexts[1].TextTrimming==TextTrimming.None&&workingLabel is Grid {HorizontalAlignment:HorizontalAlignment.Left},"long step/phrase trims with ellipsis in a left-aligned star cell; elapsed never trims");
        var header=(Button)group.DetailAnchor;var chevron=Walk(group.Element).OfType<FontIcon>().Single(i=>i.Glyph is "\uE76C" or "\uE70D");
        header.Focus(FocusState.Pointer);Check(chevron.Opacity==0,"pointer-click focus alone keeps chevron reserved but hidden");
        header.Focus(FocusState.Keyboard);Check(chevron.Opacity==1,"keyboard focus reveals the expand chevron");
        header.Blur();Check(chevron.Opacity==0,"losing focus hides the chevron again (layout space kept)");
        Check(group.DetailAnchor.Automation["Name"].Contains("dotnet test",StringComparison.Ordinal)&&clock.SubscriberCount==baseline+1,"collapsed accessibility reports latest real step while gear keeps working");
        ((Button)group.DetailAnchor).Press();
        Check(group.IsExpanded&&toggles==2&&((FrameworkElement)group.ItemsHost.Parent!).Visibility==Visibility.Visible&&group.ItemsHost.Children.Count==2,"native re-expand reveals intact rows");
        group.Element.Visibility=Visibility.Collapsed;
        Check(clock.SubscriberCount==baseline&&!DispatcherQueueTimer.All.Any(t=>t.IsRunning),"hidden group stops clock and cadence timer");
        group.Element.Visibility=Visibility.Visible;
        group.Update(summary with {IsActive=false},false,true);
        Check(clock.SubscriberCount==baseline&&!DispatcherQueueTimer.All.Any(t=>t.IsRunning),"Done group stops animation and timer");group.Dispose();
        Console.WriteLine($"PASS working group: {checks} assertions; real production logic, controlled native XAML/composition boundary (not pixel/native animation proof)");
    }
    private static void LoadTree(FrameworkElement e)
    { e.Load();if(e is Panel p)foreach(var child in p.Children.OfType<FrameworkElement>())LoadTree(child);if(e is Button {Content:FrameworkElement content})LoadTree(content);if(e is Border {Child:FrameworkElement child2})LoadTree(child2); }
    private static System.Collections.Generic.IEnumerable<FrameworkElement> Walk(FrameworkElement e)
    { yield return e;if(e is Panel p)foreach(var child in p.Children.OfType<FrameworkElement>())foreach(var item in Walk(child))yield return item;if(e is Button {Content:FrameworkElement content})foreach(var item in Walk(content))yield return item;if(e is Border {Child:FrameworkElement child2})foreach(var item in Walk(child2))yield return item; }
}
