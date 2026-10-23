using System;
using System.Linq;
using System.Collections.Generic;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

internal static class Program
{
    private static int checks;
    private static void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;}
    private static IEnumerable<UIElement> Walk(UIElement e)
    {
        yield return e;
        if(e is Panel p)foreach(var c in p.Children)foreach(var n in Walk(c))yield return n;
        if(e is ContentControl {Content:UIElement c1})foreach(var n in Walk(c1))yield return n;
        if(e is Border {Child:{} c2})foreach(var n in Walk(c2))yield return n;
    }
    private static T Find<T>(UIElement e,string id)where T:UIElement=>Walk(e).OfType<T>().Single(n=>AutomationProperties.GetAutomationId(n)==id);
    private static void Main()
    {
        var key=new OpenCodeHomeProjectKey("source","/project");
        OpenCodeHomeSession Session(OpenCodeHomeStatus? s)=>new(key,new(){Id="ses_test",Title="Title",Outcome="failed",Time=new(){Updated=100}},s);
        OpenCodeHomeSnapshot Snapshot(OpenCodeHomeStatus? s)=>new(1,new[]{new OpenCodeHomeProject(key,"Project")},new[]{Session(s)});
        var view=new OpenCodeHomeView();var list=Find<ListView>(view,"OpenCodeHomeSessions");
        ListViewItem Row()=>list.Items.OfType<ListViewItem>().Single(i=>i.Tag is OpenCodeHomeSessionRow);
        var cases=new[]{("succeeded",OpenCodeHomeTone.Succeeded,"\uE73E"),("failed",OpenCodeHomeTone.Failed,"\uEA39"),("interrupted",OpenCodeHomeTone.Interrupted,"\uE711")};
        foreach(var (outcome,tone,glyph)in cases)foreach(var unread in new[]{false,true})
        {
            var status=new OpenCodeHomeStatus(false,false,unread,outcome);view.SetSnapshot(Snapshot(status));
            var row=Row();var data=(OpenCodeHomeSessionRow)row.Tag!;
            Check(data.Tone==tone&&data.IsUnread==unread,"outcome and unread are orthogonal real projection outputs");
            Check(Find<FontIcon>(row,"OpenCodeHomeStatus").Glyph==glyph,"actual HomeView keeps legal outcome glyph regardless of unread");
            var dot=Find<Border>(row,"OpenCodeHomeUnread");
            Check(dot.Visibility==(unread?Visibility.Visible:Visibility.Collapsed)&&dot.Width==6&&dot.Height==6&&dot.CornerRadius.Value==3,"actual independent unread solid dot visibility/size");
            Check(dot.Style?.Xml.Contains("{ThemeResource AccentFillColorDefaultBrush}")==true,"dot uses actual shared theme-factory ThemeResource, not frozen RGB");
            Check(AutomationProperties.GetName(row).Contains("未读")==unread,"row accessibility announces unread independently");
        }
        var prior=Row();var priorContent=prior.Content;view.SetSnapshot(Snapshot(new(false,false,true,"interrupted")));
        Check(ReferenceEquals(prior,Row())&&ReferenceEquals(priorContent,Row().Content),"equal status snapshots retain container/content identity");
        view.SetSnapshot(Snapshot(new(false,false,false,"interrupted")));
        Check(ReferenceEquals(prior,Row()),"unread-only updates keep native row identity and actions");
        view.SetSnapshot(Snapshot(new(true,true,true,"succeeded"){QuestionPending=true}));
        Check(((OpenCodeHomeSessionRow)Row().Tag!).Tone==OpenCodeHomeTone.QuestionBlocked,"true question has priority over working without altering status facts");
        var question=Find<FontIcon>(Row(),"OpenCodeHomeStatus");
        Check(question.Glyph=="\uE897"&&question.Style?.Xml.Contains("SystemFillColorCautionBrush")==true,"actual question mark uses Fluent glyph and live caution theme");
        Check(Find<Border>(Row(),"OpenCodeHomeUnread").Visibility==Visibility.Visible,"question and unread dot coexist");
        Check(!Walk(Row()).OfType<ProgressRing>().Any()&&AutomationProperties.GetName(Row()).Contains("等待回答问题"),"question replaces spinner, not an arbitrary warning pretending to be question");
        Check(((OpenCodeHomeSessionRow)Row().Tag!).Session.Status is {Working:true,Outcome:"succeeded",IsUnread:true},"question display does not overwrite working/outcome/unread authority facts");
        Check(((Grid)Row().Content!).ColumnDefinitions[0].Width.Value==34,"status and dot have reserved width independent of question/outcome state");
        foreach(var questionFact in new bool?[]{null,false})
        {
            view.SetSnapshot(Snapshot(new(true,true,true,null){QuestionPending=questionFact}));
            Check(((OpenCodeHomeSessionRow)Row().Tag!).Tone==OpenCodeHomeTone.WorkingNeedsAction&&Find<ProgressRing>(Row(),"OpenCodeHomeStatus").IsActive,"unknown/false question leaves ordinary working/pending semantics");
            Check(Find<Border>(Row(),"OpenCodeHomeUnread").Visibility==Visibility.Visible,"working and old completion unread coexist");
        }
        view.SetSnapshot(Snapshot(new(true,false,true,"succeeded")));
        Check(((OpenCodeHomeSessionRow)Row().Tag!).Tone==OpenCodeHomeTone.Working&&Find<ProgressRing>(Row(),"OpenCodeHomeStatus").IsActive
            &&Find<Border>(Row(),"OpenCodeHomeUnread").Visibility==Visibility.Visible,"plain working ring and old completion unread remain orthogonal");
        view.SetSnapshot(Snapshot(new(false,true,false,null)));
        Check(((OpenCodeHomeSessionRow)Row().Tag!).Tone==OpenCodeHomeTone.NeedsAction&&Find<FontIcon>(Row(),"OpenCodeHomeStatus").Glyph!="\uE897","ordinary permission/pending form is not a question without explicit fact");
        view.SetSnapshot(Snapshot(new(null,null,true,"unexpected")));
        Check(((OpenCodeHomeSessionRow)Row().Tag!).Tone==OpenCodeHomeTone.Unknown&&Find<FontIcon>(Row(),"OpenCodeHomeStatus").Glyph!="\uEA39","unknown status with unread is not synthesized failure or unread-only Tone");
        view.SetSnapshot(Snapshot(null));Check(!((OpenCodeHomeSessionRow)Row().Tag!).IsUnread,"untracked DTO-only legal outcome has no invented unread");
        Check(((OpenCodeHomeSessionRow)Row().Tag!).Tone==OpenCodeHomeTone.Failed,"untracked row can still preserve an actually supplied legal DTO outcome");
        Check(OpenCodeHomeProjection.Classify(Session(null)with{Info=new(){Id="ses_unknown"}})==OpenCodeHomeTone.Unknown,"untracked/no evidence is unknown, not failure");
        var selected=0;view.SessionRequested+=(_,e)=>{Check(e.Session.Info.Id=="ses_test"&&e.Revision==1,"real click echoes owned metadata and revision");selected++;};
        list.RaiseItemClick(Row().Content!);Check(selected==1,"independent badges preserve own-container content click semantics");
        // H4 layout (user-chosen): attention section, Fluent context-menu order, side cards vs narrow chips, real connection line.
        Check(!Walk(view).Any(n=>AutomationProperties.GetAutomationId(n)=="OpenCodeHomeRefresh"),"no refresh button: live events re-project Home");
        Check(Find<Border>(view,"OpenCodeHomeProjectsCard").CornerRadius.Value==8&&Find<Border>(view,"OpenCodeHomeProjectsCard").Style?.Xml.Contains("AcrylicInAppFillColorDefaultBrush")==true,"floating acrylic project card");
        view.SetSnapshot(Snapshot(new(false,false,true,"succeeded")));
        var headers=list.Items.OfType<ListViewItem>().Where(i=>i.Tag is not OpenCodeHomeSessionRow).ToArray();
        Check(headers.Length==1&&AutomationProperties.GetAutomationId(headers[0])=="OpenCodeHomeAttentionHeader"&&((OpenCodeHomeSessionRow)Row().Tag!).NeedsAttention,"finished-but-unread sits under 需要你处理");
        view.SetSnapshot(Snapshot(new(true,false,true,"succeeded")));
        Check(!((OpenCodeHomeSessionRow)Row().Tag!).NeedsAttention&&AutomationProperties.GetAutomationId(list.Items.OfType<ListViewItem>().First())=="OpenCodeHomeDateHeader","still-running session with an old unread is not 需要你处理");
        view.SetSnapshot(Snapshot(new(true,true,false,null){QuestionPending=true}));
        Check(((OpenCodeHomeSessionRow)Row().Tag!).NeedsAttention,"question-blocked sits under 需要你处理");
        var menu=Row().ContextFlyout!;
        var labels=menu.Items.Select(i=>i is MenuFlyoutItem m?m.Text:"-").ToArray();
        Check(string.Join("|",labels)=="修改名称|复制 session ID|-|归档此会话|删除此会话","Fluent menu order with separator before archive/delete");
        view.CanRequestSessionAction=(_,_,a)=>a!=OpenCodeHomeSessionAction.Archive;menu.RaiseOpening();
        Check(menu.Items.OfType<MenuFlyoutItem>().Single(i=>i.Text=="归档此会话").IsEnabled==false&&menu.Items.OfType<MenuFlyoutItem>().Single(i=>i.Text=="删除此会话").Style?.Xml.Contains("SystemFillColorCriticalBrush")==true,"menu items follow the page action gate; delete is critical-coloured");
        view.SetConnection("1.4.2",true,"WSL · NixOS");
        Check(Walk(view).OfType<TextBlock>().Any(t=>t.Text=="v1.4.2 · WSL · NixOS"),"connection line shows real version + identity");
        view.SetConnection(null,false,null);
        Check(Walk(view).OfType<TextBlock>().Any(t=>t.Text=="未连接")&&!Walk(view).OfType<TextBlock>().Any(t=>t.Text.Contains("v1.4.2")),"unknown version/identity is omitted, never invented");
        view.ActualWidth=1600;view.RaiseSizeChanged();
        Check(!view.IsNarrow&&Find<Border>(view,"OpenCodeHomeProjectsCard").Visibility==Visibility.Visible&&Find<ScrollViewer>(view,"OpenCodeHomeProjectChips").Visibility==Visibility.Collapsed,"wide: side cards, no chips");
        view.ActualWidth=1180;view.RaiseSizeChanged();
        Check(view.IsNarrow&&Find<Border>(view,"OpenCodeHomeProjectsCard").Visibility==Visibility.Collapsed&&Find<ScrollViewer>(view,"OpenCodeHomeProjectChips").Visibility==Visibility.Visible,"narrow: cards fold into project chips");
        // Infinite scroll (user m02186): no load-more/refresh button; near-bottom, cursor, in-flight and failure/retry guards.
        Check(!Walk(view).Any(n=>AutomationProperties.GetAutomationId(n)is"OpenCodeHomeLoadMore"or"OpenCodeHomeRefresh"),"no manual load-more or refresh button remains");
        Check(OpenCodeHomeInfiniteScroll.ShouldLoad(1200,600,2300,true,false,false),"within one viewport of the end loads the next page");
        Check(!OpenCodeHomeInfiniteScroll.ShouldLoad(0,600,2300,true,false,false),"far from the end does not load");
        Check(OpenCodeHomeInfiniteScroll.ShouldLoad(0,600,300,true,false,false),"a first page that does not fill the viewport auto-fills");
        Check(!OpenCodeHomeInfiniteScroll.ShouldLoad(1700,600,2300,false,false,false),"no next cursor: never loads");
        Check(!OpenCodeHomeInfiniteScroll.ShouldLoad(1700,600,2300,true,true,false),"request in flight: never loads twice");
        Check(!OpenCodeHomeInfiniteScroll.ShouldLoad(1700,600,2300,true,false,true),"failed attempt waits for an explicit retry");
        Check(!OpenCodeHomeInfiniteScroll.ShouldLoad(0,0,0,true,false,false),"hidden/unmeasured list never loads");
        var loads=0;view.LoadMore+=(_,e)=>loads++;
        OpenCodeHomeSnapshot Paged(long rev,bool more,bool loading=false,bool failed=false)=>new(rev,new[]{new OpenCodeHomeProject(key,"Project")},new[]{Session(null)},loading,null,more,loading,failed);
        view.SetSnapshot(Paged(10,false));view.OnListScrolled(0,600,300);Check(loads==0,"short list without cursor stays put");
        view.SetSnapshot(Paged(11,true));Check(loads==1,"short first page with a cursor auto-fills on the snapshot");
        view.OnListScrolled(0,600,300);view.SetSnapshot(Paged(11,true));Check(loads==1,"no repeat request before the page reports the load");
        view.SetSnapshot(Paged(12,true,loading:true));
        var tail=list.Items.OfType<ListViewItem>().Last();
        Check(AutomationProperties.GetAutomationId(tail)=="OpenCodeHomeLoadingMore"&&Walk(tail).OfType<ProgressRing>().Single().IsActive&&loads==1,"inline ring row while loading; in-flight blocks requests");
        view.SetSnapshot(Paged(13,true,failed:true));view.OnListScrolled(0,600,300);
        tail=list.Items.OfType<ListViewItem>().Last();
        Check(AutomationProperties.GetAutomationId(tail)=="OpenCodeHomeLoadMoreFailed"&&loads==1,"failure shows a quiet retry row and does not auto-retry");
        Check(Walk(tail).OfType<TextBlock>().Any(t=>t.Text=="加载失败"),"failure row copy");
        Find<HyperlinkButton>(tail,"OpenCodeHomeLoadMoreRetry").RaiseClick();Check(loads==2,"重试 is an explicit single request");
        view.SetSnapshot(Paged(14,true,loading:true));view.OnListScrolled(0,600,5000);view.SetSnapshot(Paged(15,true));
        Check(loads==2&&list.Items.OfType<ListViewItem>().All(i=>AutomationProperties.GetAutomationId(i)is not("OpenCodeHomeLoadingMore"or"OpenCodeHomeLoadMoreFailed")),"after the page lands: no tail row, far from the end stays put");
        view.OnListScrolled(4500,600,5000);Check(loads==3,"scrolling near the bottom loads the next page automatically");
        var now=new DateTimeOffset(2026,10,23,12,0,0,TimeSpan.Zero);
        Check(OpenCodeHomeProjection.DateGroup(now.AddDays(-3),now,TimeZoneInfo.Utc)==OpenCodeHomeDateGroup.ThisWeek&&OpenCodeHomeProjection.DateGroup(now.AddDays(-5),now,TimeZoneInfo.Utc)==OpenCodeHomeDateGroup.Earlier,"本周 is the Monday-based calendar week");
        Check(OpenCodeHomeProjection.ShortName("/home/ming/nix-config/")=="nix-config"&&OpenCodeHomeProjection.ShortName("/")=="/"&&OpenCodeHomeProjection.SingleLine("a\r\n b\tc")=="a b c","compact project label and one-line titles keep real text");
        Console.WriteLine($"PASS real Home projection/view/theme factory: {checks} assertions; inert native controls/resources, no rendering/monitor authority claim");
    }
}
