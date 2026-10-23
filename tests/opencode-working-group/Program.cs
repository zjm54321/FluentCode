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
        Check(typeof(OpenCodeWorkingPhrases).GetField("EnableModelSatire",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.GetRawConstantValue() is true,"satire enabled (user confirmed)");
        Check(OpenCodeWorkingPhrases.Playful.Count is >=10 and <=20,"tasteful general pool size");
        foreach(var sample in new[] { ("proxy/claude-sonnet",OpenCodeWorkingFamily.Claude),("gpt-6",OpenCodeWorkingFamily.OpenAI),("gemini-3",OpenCodeWorkingFamily.Gemini),("gateway/deepseek-v4",OpenCodeWorkingFamily.DeepSeek),("opaque-id",OpenCodeWorkingFamily.Unknown),("notclaude",OpenCodeWorkingFamily.Unknown) })
            Check(OpenCodeWorkingPhrases.Family(sample.Item1)==sample.Item2,"actual model family: "+sample.Item1);
        Check(OpenCodeWorkingPhrases.Family(null)==OpenCodeWorkingFamily.Unknown,"missing executing model stays unknown");
        Check(OpenCodeWorkingPhrases.Satire(OpenCodeWorkingFamily.Claude).SequenceEqual(new[]{"封禁中文中…","封禁中国人中…","检测中国人中…"}),"user copy preserved verbatim");
        Check(OpenCodeWorkingPhrases.Satire(OpenCodeWorkingFamily.DeepSeek).SequenceEqual(new[]{"大肥鱼吃白饭中…","大肥鱼娱乐中…"}),"deepseek copy matches the user's words");
        var picker=new OpenCodeWorkingPhrasePicker(()=>0);
        picker.Start(OpenCodeWorkingActivity.Reasoning,OpenCodeWorkingFamily.Claude,0);
        Check(picker.Current=="思考中…" && picker.Tier==OpenCodeWorkingTier.Standard,"working starts with real standard state");
        for(var t=1;t<8000;t+=10) { picker.UpdateContext(OpenCodeWorkingActivity.Reasoning,OpenCodeWorkingFamily.Claude,t);picker.Tick(t); }
        Check(picker.ChangeCount==1,"stream updates never rotate phrases early");
        picker.Tick(8000);Check(picker.Tier==OpenCodeWorkingTier.Satire && OpenCodeWorkingPhrases.Satire(OpenCodeWorkingFamily.Claude).Contains(picker.Current),"default picker uses the executing family line at the lowest roll");
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
        Check(OpenCodeWorkingIndicator.GearGlyph=="\uE9F5","W1 uses the approved processing gear glyph");
        var toggles=0;var opened=0;
        var group=new OpenCodeToolGroupView("group",(_,_)=>toggles++);
        var summary=new OpenCodeStepRunSummary("处理中…","",0,0,true,null,false,"处理中");
        group.SetWorkingContext(true,false,"claude-sonnet");group.Update(summary,true,true);
        group.ItemsHost.Children.Add(new TextBlock {Text="step one"});
        group.ItemsHost.Children.Add(new TextBlock {Text="step two"});
        LoadTree(group.Element);
        Check(clock.SubscriberCount==baseline+1&&DispatcherQueueTimer.All.Count(t=>t.IsRunning)==1,"group reuses one loaded header timer and shared clock");
        group.Element.Load();group.Element.RaiseUnloaded();
        Check(group.Element.IsLoaded&&DispatcherQueueTimer.All.Count(t=>t.IsRunning)==1,"late Unloaded after reparented Loaded cannot stop the current group timer");
        group.Element.Unload();
        Check(!DispatcherQueueTimer.All.Any(t=>t.IsRunning),"genuine group unload still stops the timer");
        group.Element.Load();
        Check(DispatcherQueueTimer.All.Count(t=>t.IsRunning)==1,"current group load restarts the retained timer");
        var expander=Walk(group.Element).OfType<Expander>().Single();
        var outline=(Border)expander.Parent!;
        Check(outline.BorderThickness==new Thickness(0)&&outline.CornerRadius==new CornerRadius(8)&&outline.Style?.Key=="SubtleFillColorTransparentBrush|","PG4 group has no outline or fill at rest; supersedes PG3 resting ring");
        foreach(var key in new[]{"ExpanderHeaderBackground","ExpanderHeaderBackgroundPointerOver","ExpanderHeaderBackgroundPressed","ExpanderContentBackground","ExpanderContentBorderBrush","ExpanderHeaderBorderBrush","ExpanderHeaderBorderPointerOverBrush","ExpanderHeaderBorderPressedBrush","ExpanderChevronBackground","ExpanderChevronPointerOverBackground","ExpanderChevronPressedBackground","ExpanderChevronBorderBrush","ExpanderChevronBorderPointerOverBrush","ExpanderChevronBorderPressedBrush","ExpanderChevronForeground","ExpanderChevronPointerOverForeground","ExpanderChevronPressedForeground"})
            Check(expander.Resources[key] is Microsoft.UI.Xaml.Media.SolidColorBrush {Color:Microsoft.UI.Colors.Transparent},"PG1 transparent native chrome: "+key);
        Check(expander.Background is {Color:Microsoft.UI.Colors.Transparent}&&expander.BorderThickness==new Thickness(0),"PG1 expanded content explicitly stays transparent and borderless");
        Check((double)expander.Resources["ExpanderChevronButtonSize"]==0&&(double)expander.Resources["ExpanderChevronGlyphSize"]==0&&(Thickness)expander.Resources["ExpanderChevronMargin"]==new Thickness(0),"PG1 removes native boxed chevron and its trailing slot");
        Check((Thickness)expander.Resources["ExpanderHeaderPadding"]==new Thickness(12,0,8,0)&&expander.MinHeight==32,"PG1 restores compact header and smaller old left inset");
        var chevron=Walk((FrameworkElement)expander.Header!).OfType<FontIcon>().Single(icon=>icon.FontSize==10);
        Check(chevron.Opacity==0&&chevron.Margin==new Thickness(4,0,0,0)&&Grid.GetColumn(chevron)==1&&chevron.Parent is Grid {HorizontalAlignment:HorizontalAlignment.Left},"PG1 old plain 10px chevron sits directly after left-aligned title, hidden without hover");
        group.Element.Enter();Check(chevron.Opacity==1,"PG1 group hover reveals old chevron");group.Element.Exit();Check(chevron.Opacity==0,"PG1 pointer exit hides chevron without changing layout");
        expander.Focus(FocusState.Keyboard);Check(chevron.Opacity==1,"PG1 keyboard focus reveals old chevron");expander.Blur();Check(chevron.Opacity==0,"PG1 leaving keyboard focus hides old chevron");
        void CheckGroupHighlight(bool highlighted,string state) => Check(outline.BorderThickness==new Thickness(0)
            && outline.Style?.Key==(highlighted?"SubtleFillColorSecondaryBrush|":"SubtleFillColorTransparentBrush|"),"PG4 "+state+": subtle theme fill only during hover/keyboard focus; no outline");
        CheckGroupHighlight(false,"rest");
        group.Element.Enter();CheckGroupHighlight(true,"pointer entered");
        group.Element.Exit();CheckGroupHighlight(false,"pointer exited");
        group.Element.Enter();group.Element.Cancel();CheckGroupHighlight(false,"pointer cancelled");
        expander.Focus(FocusState.Keyboard);CheckGroupHighlight(true,"keyboard focused");
        group.Element.Enter();group.Element.Exit();CheckGroupHighlight(true,"pointer exits while keyboard focus remains");
        expander.Blur();CheckGroupHighlight(false,"keyboard focus left");
        expander.Focus(FocusState.Pointer);CheckGroupHighlight(false,"pointer focus alone after exit");expander.Blur();
        group.Element.Enter();expander.Focus(FocusState.Keyboard);expander.Blur();CheckGroupHighlight(true,"focus leaves while pointer remains");
        group.Element.Exit();CheckGroupHighlight(false,"all interaction left");
        var stableAnchor=group.DetailAnchor;
        expander.IsExpanded=false;
        Check(group.IsExpanded&&expander.IsExpanded&&toggles==0,"W1 native Expander cannot fold a running turn");
        Check(((FrameworkElement)group.ItemsHost.Parent!).Visibility==Visibility.Visible&&group.ItemsHost.Children.Count==2,"all running turn rows remain visible");
        group.SetCurrentStep("执行 · dotnet test · 运行中");group.Update(summary with {SuccessCount=1},false,true);
        var workingLabel=Walk(group.Element).Single(e=>e.Automation.TryGetValue("Id",out var id)&&id=="OpenCodeWorkingIndicator.group");
        var labelTexts=((Panel)workingLabel).Children.OfType<TextBlock>().ToArray();
        Check(OpenCodeWorkingPhrases.Standard(OpenCodeWorkingActivity.Reasoning).Any(phrase=>OpenCodeWorkingPhrases.Stem(phrase)==labelTexts[0].Text)&&group.IsExpanded,"W1 real reasoning phrase remains the live header even if the timeline requests collapse");
        Check(labelTexts.All(text=>text.FontSize==13),"W1 live group phrase and elapsed use the approved 13px type size");
        Check(labelTexts[0].TextTrimming==TextTrimming.CharacterEllipsis&&labelTexts[1].TextTrimming==TextTrimming.None&&workingLabel is Grid {HorizontalAlignment:HorizontalAlignment.Left},"long step/phrase trims with ellipsis in a left-aligned star cell; elapsed never trims");
        group.Element.Visibility=Visibility.Collapsed;
        Check(clock.SubscriberCount==baseline&&!DispatcherQueueTimer.All.Any(t=>t.IsRunning),"hidden group stops clock and cadence timer");
        group.Element.Visibility=Visibility.Visible;
        group.Update(summary with {IsActive=false,HeaderText="已处理 6 分 7 秒",SuccessCount=29,ErrorCount=3},false,true);
        Check(!group.IsExpanded&&!expander.IsExpanded&&clock.SubscriberCount==baseline&&!DispatcherQueueTimer.All.Any(t=>t.IsRunning),"turn end folds group and stops animation and timer");
        var caption=Walk((FrameworkElement)expander.Header!).OfType<TextBlock>().Single(text=>text.Text=="已处理 6 分 7 秒");
        Check(caption.FontSize==12&&caption.FontWeight==Microsoft.UI.Text.FontWeights.Normal&&chevron.Glyph=="\uE76C","PG1 completed caption is small regular-weight text with the old collapsed chevron");
        var tallies=Walk((FrameworkElement)expander.Header!).OfType<StackPanel>().Where(panel=>panel.Automation.ContainsKey("Id")).ToArray();
        Check(tallies.Length==2&&tallies.All(panel=>panel.Parent is StackPanel tally&&Grid.GetColumn(tally)==2&&tally.Parent==expander.Header)&&tallies.SelectMany(panel=>panel.Children.OfType<TextBlock>()).All(text=>text.FontSize==12&&text.FontWeight==Microsoft.UI.Text.FontWeights.Normal),"PG1 success/error counts remain small at the far-right header column");
        Check(tallies[0].Children.OfType<TextBlock>().Single().Text=="29"&&tallies[1].Children.OfType<TextBlock>().Single().Text=="3","PG1 retains real success and error totals");
        group.Update(summary with {IsActive=false,HeaderText="已处理 6 分 7 秒"},false,true);
        Check(caption.Style?.Key==OpenCodeProcessedSummaryStyle.LabelBrushKey,"B16 successful settled caption retains secondary theme text");
        expander.IsExpanded=true;
        Check(group.IsExpanded&&toggles==1&&group.ItemsHost.Children.Count==2&&chevron.Glyph=="\uE70D","native settled-group expansion reveals retained rows and old down chevron");
        expander.IsExpanded=false;
        Check(!group.IsExpanded&&toggles==2,"native settled-group collapse notifies timeline");
        group.BindSingleLeaf(()=>opened++,null,null,"读取");
        Check(group.ShowsItems&&group.DetailAnchor==group.ItemsHost&&ReferenceEquals(group.DetailAnchor,stableAnchor)&&!Walk(group.Element).OfType<Expander>().Any(),"one completed operation is direct with a stable attached preview anchor across BindSingleLeaf");
        group.Update(summary,true,true);
        Check(group.ShowsItems&&clock.SubscriberCount==baseline,"one running operation also stays direct without a duplicate working header");
        group.BindSingleLeaf(null,null,null);
        group.Update(summary,false,false);
        Check(!Walk(group.Element).OfType<Expander>().Any()&&group.ShowsItems,"active chain follower shows all rows without a repeated header");
        group.Dispose();
        Console.WriteLine($"PASS working group: {checks} assertions; real production logic, controlled native XAML/composition boundary (not pixel/native animation proof)");
    }
    private static void LoadTree(FrameworkElement e)
    { e.Load();if(e is Panel p)foreach(var child in p.Children.OfType<FrameworkElement>())LoadTree(child);if(e is Button {Content:FrameworkElement content})LoadTree(content);if(e is Border {Child:FrameworkElement child2})LoadTree(child2);if(e is Expander exp){if(exp.Header is FrameworkElement h)LoadTree(h);if(exp.Content is FrameworkElement c)LoadTree(c);} }
    private static System.Collections.Generic.IEnumerable<FrameworkElement> Walk(FrameworkElement e)
    { yield return e;if(e is Panel p)foreach(var child in p.Children.OfType<FrameworkElement>())foreach(var item in Walk(child))yield return item;if(e is Button {Content:FrameworkElement content})foreach(var item in Walk(content))yield return item;if(e is Border {Child:FrameworkElement child2})foreach(var item in Walk(child2))yield return item;if(e is Expander exp){if(exp.Header is FrameworkElement h)foreach(var item in Walk(h))yield return item;if(exp.Content is FrameworkElement c)foreach(var item in Walk(c))yield return item;} }
}
