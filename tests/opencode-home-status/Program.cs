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
        Console.WriteLine($"PASS real Home projection/view/theme factory: {checks} assertions; inert native controls/resources, no rendering/monitor authority claim");
    }
}
