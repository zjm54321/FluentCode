using System;
using System.Linq;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

internal static class VisibleIdle
{
    private static int checks;
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
    private static OpenCodeMessage Idle(string? id, double created) => new() { Id = id, Type = "idle", Time = new() { Created = created }, Outcome = "succeeded" };
    private sealed class Fixture
    {
        internal readonly Border Root = new();
        internal readonly ScrollView Scroll = new();
        internal readonly ScrollContentPresenter Presenter = new();
        internal readonly OpenCodeConversationView View = new();
        internal readonly XamlRoot Xaml = new();
        internal int Events;
        internal Fixture(params OpenCodeMessage[] messages)
        {
            Root.Child = Scroll; Scroll.Content = Presenter; Presenter.Content = View;
            View.IdleViewportInvalidated += (_, _) => Events++;
            View.ApplyMessages(messages);
            Prepare(Root); Layout();
        }
        internal void Prepare(UIElement element)
        {
            if (element is FrameworkElement f)
            {
                f.XamlRoot = Xaml; f.IsLoaded = true; f.ActualWidth = 500; f.ActualHeight = 500;
                f.TestTransform = _ => new GeneralTransform(0);
            }
            if (element is Panel panel) foreach (var child in panel.Children) Prepare(child);
            if (element is Border { Child: { } child1 }) Prepare(child1);
            if (element is ContentControl { Content: UIElement child2 }) Prepare(child2);
        }
        internal TextBlock Label(int row = 0) => ((Grid)View.Children[row]).Children.OfType<TextBlock>().Single();
        internal void Place(int row, double y, double height = 20, double x = 20, double width = 150)
        {
            var label = Label(row); label.ActualWidth = width; label.ActualHeight = height;
            label.TestTransform = _ => new GeneralTransform(y, x);
        }
        internal void Layout() => View.RaiseLayoutUpdatedForTest();
        internal OpenCodeIdleViewportSnapshot Capture() => View.CaptureIdleViewport(Scroll);
    }
    internal static void Run()
    {
        var f = new Fixture(Idle("msg_old", 10), Idle("msg_new", 20)); f.Place(0, 100); f.Place(1, 600);
        Check(f.Capture().Candidate is { MessageId: "msg_old", Watermark: 10 }, "newer rendered but out-of-viewport idle cannot enlarge visible covered watermark");
        f.Place(1, 200); Check(f.Capture().Candidate is { MessageId: "msg_new", Watermark: 20 }, "only actually fully visible legal highest idle is chosen");
        var snap = f.Capture(); Check(snap == f.Capture(), "stable query is immutable/equal and does not advance render stamp");
        f.Place(1, 210); Check(snap != f.Capture(), "geometry change changes snapshot without incrementing content epoch");
        Check(snap.RenderVersion == f.Capture().RenderVersion, "layout-only changes do not create perpetual render-version churn");
        f.Place(0, 600); f.Place(1, 490);
        Check(f.Capture().Candidate is null, "one clipped label edge is insufficient even when divider row is rendered");
        f.Place(1, -1); Check(f.Capture().Candidate is null, "partially above viewport is insufficient");
        f.Place(1, 100, x: 490); Check(f.Capture().Candidate is null, "horizontal partial visibility is insufficient");
        f.Place(1, 100); f.Scroll.Clip = new RectangleGeometry { Rect = new(0, 0, 500, 110) };
        Check(f.Capture().Candidate is null, "explicit ScrollViewer clip intersects effective viewport");
        f.Scroll.Clip = null; f.Root.Clip = new RectangleGeometry { Rect = new(0, 0, 500, 110) };
        Check(f.Capture().Candidate is null, "ancestor clip is enforced, not only scroll bounds");
        f.Root.Clip = new RectangleGeometry { Rect = new(0,0,500,500), Transform = new Transform() };
        Check(f.Capture().Candidate is null, "unproven clip transform fails closed"); f.Root.Clip = null;
        f.Label(1).Clip = new RectangleGeometry { Rect = new(0,0,150,10) };
        Check(f.Capture().Candidate is null,"readable label's own partial clip is insufficient"); f.Label(1).Clip=null;
        f.Presenter.ActualHeight = 110; Check(f.Capture().Candidate is null, "native template content viewport can be smaller than scroll chrome"); f.Presenter.ActualHeight = 500;
        f.Root.ActualHeight = 110; Check(f.Capture().Candidate is null, "ancestor implicit layout bounds are conservatively clipped"); f.Root.ActualHeight = 500;
        f.Root.CornerRadius = new CornerRadius(200); Check(f.Capture().Candidate is null,"rounded Border implicit clipping uses a safe inner rectangle, not only bounding box"); f.Root.CornerRadius=default;
        f.Root.Visibility = Visibility.Collapsed; Check(f.Capture().Candidate is null, "hidden ancestor prevents proof"); f.Root.Visibility = Visibility.Visible;
        f.Label(1).Visibility = Visibility.Collapsed; Check(f.Capture().Candidate is null, "hidden idle readable label prevents proof"); f.Label(1).Visibility = Visibility.Visible;
        f.View.Opacity = 0; Check(f.Capture().Candidate is null, "transparent ancestor prevents proof"); f.View.Opacity = 1;
        f.Label(1).IsLoaded = false; Check(f.Capture().Candidate is null, "unloaded label prevents proof"); f.Label(1).IsLoaded = true;
        f.Presenter.IsLoaded = false; Check(f.Capture().Candidate is null, "unloaded ancestor prevents proof"); f.Presenter.IsLoaded = true;
        f.Label(1).XamlRoot = new(); Check(f.Capture().Candidate is null, "different label XamlRoot prevents proof"); f.Label(1).XamlRoot = f.Xaml;
        f.Root.XamlRoot = new(); Check(f.Capture().Candidate is null, "different ancestor XamlRoot prevents proof"); f.Root.XamlRoot = f.Xaml;
        f.Label(1).ActualHeight = 0; Check(f.Capture().Candidate is null, "zero-size label prevents proof"); f.Place(1,100);
        f.Scroll.ActualWidth = double.NaN; Check(f.Capture().Candidate is null, "nonfinite viewport prevents proof"); f.Scroll.ActualWidth = 500;
        f.Label(1).TestTransform = _ => throw new InvalidOperationException("detached test transform"); Check(f.Capture().Candidate is null, "transform failure is absence of proof");
        f.Label(1).TestTransform = _ => new GeneralTransform(100,20, skew:0.2); Check(f.Capture().Candidate is null, "skew/rotated bounding rectangle never overclaims clipping coverage");
        f.Place(1,100); f.Presenter.Content = null;
        Check(f.Capture().Candidate is null, "current entry outside specified scroll tree is not owned"); f.Presenter.Content=f.View;
        Check(f.Capture().Candidate is not null, "same actual owner tree can restore proof");
        var foreign = new ScrollView { XamlRoot=f.Xaml, IsLoaded=true };
        Check(f.View.CaptureIdleViewport(foreign).Candidate is null, "same-root unrelated scroll cannot prove another view's anchor");
        f.Presenter.Content=null; f.Scroll.Content=f.View;
        Check(f.Capture().Candidate is null, "missing actual ScrollContentPresenter template fails closed");
        foreach (var bad in new[] { -1d, double.NaN, double.PositiveInfinity })
        {
            var badTime = new Fixture(Idle("msg_bad",bad)); badTime.Place(0,100);
            Check(badTime.Capture().Candidate is null,"invalid time never yields proof");
        }
        var zero=new Fixture(Idle("msg_zero",0)); zero.Place(0,100); Check(zero.Capture().Candidate?.Watermark==0,"finite zero watermark is legal");
        var anonymous=new Fixture(Idle(null,10)); anonymous.Place(0,100); Check(anonymous.Capture().Candidate is null,"anonymous fallback key is not an anchor ID");
        var duplicate=new Fixture(Idle("msg_dup",10),Idle("msg_dup",20)); duplicate.Place(0,100); duplicate.Place(1,200);
        Check(duplicate.Capture().Candidate is null,"both duplicate IDs excluded instead of assigning ambiguous ownership");
        var mixed=new Fixture(Idle("msg_dup",10),new() {Id="msg_dup",Type="user",Text="duplicate"}); mixed.Place(0,100);
        Check(mixed.Capture().Candidate is null,"ID duplicated by nonidle also invalidates idle ownership");
        var update=new Fixture(Idle("msg_update",10)); update.Place(0,100); var oldLabel=update.Label(); var oldStamp=update.Capture().RenderVersion;
        update.View.ApplyMessages(new[]{Idle("msg_update",30)});
        Check(update.Capture().Candidate is null && update.View.RenderVersion>oldStamp,"Apply invalidates old proof before layout, including in-place reuse");
        Check(ReferenceEquals(oldLabel,update.Label()),"actual idle label is reused, not replaced by proof machinery");
        update.Layout(); Check(update.Capture().Candidate?.Watermark==30,"reused label carries new exact idle.Time.Created, not stale watermark");
        var events=update.Events; update.View.ApplyMessages(Array.Empty<OpenCodeMessage>()); update.Layout();
        Check(update.Capture().Candidate is null && update.Events>events,"projected/revert-removed idle loses proof and emits invalidation");
        update.View.ApplyMessages(new[]{Idle("msg_update",40)}); update.Prepare(update.Root);update.Place(0,100);update.Layout();
        var clearStamp=update.View.RenderVersion; events=update.Events;update.View.ClearMessages();
        Check(update.Capture().Candidate is null && update.View.RenderVersion>clearStamp && update.Events>events,"Clear always invalidates generation and removes anchors");
        var failure=new Fixture(Idle("msg_before_failure",50));failure.Place(0,100);
        try{failure.View.ApplyMessages(new BrokenWindow());}catch(InvalidOperationException){}
        failure.Layout();Check(failure.Capture().Candidate is null,"partially failed Apply cannot revive an old idle as a completed snapshot");
        failure.View.ApplyMessages(new[]{Idle("msg_before_failure",60)});failure.Layout();
        Check(failure.Capture().Candidate?.Watermark==60,"next successful snapshot can recover proof after failed Apply");
        var owner = new Fixture(Idle("msg_owner",70));owner.Place(0,100);var ownerStamp=owner.View.RenderVersion;
        owner.View.SetCurrentTurnActivity(true);Check(owner.View.RenderVersion==ownerStamp&&owner.Capture().Candidate is not null,"activity metadata without assistant presentation does not demand a nonexistent layout pass");
        owner.Layout();ownerStamp=owner.View.RenderVersion;owner.View.SetCurrentTurnActivity(true);
        Check(owner.View.RenderVersion==ownerStamp&&owner.Capture().Candidate is not null,"unchanged turn presentation does not churn epoch");
        ConversationViewTestProbe.Reset();
        var assistant=System.Text.Json.JsonSerializer.Deserialize<OpenCodeMessage>("""
            {"id":"msg_assistant","type":"assistant","content":[
              {"type":"reasoning","text":"PLAN"},
              {"id":"call_read","type":"tool","name":"read","state":{"status":"completed","input":{"path":"a.cs"}}},
              {"type":"text","text":"DONE"}],"time":{"created":1,"completed":2}}
            """,OpenCodeJson.Options)!;
        var grouped=new Fixture(assistant,Idle("msg_independent_idle",80));grouped.Place(1,100);
        var group=ConversationViewTestProbe.Records.Where(r=>r.Operation=="Construct").Select(r=>r.Subject).OfType<OpenCodeToolGroupView>().Single();
        var groupStamp=grouped.View.RenderVersion;group.ToggleForTest();
        Check(grouped.View.RenderVersion>groupStamp&&grouped.Capture().Candidate is null,"actual historical group toggle invalidates presentation version before fresh layout");
        grouped.Layout();Check(grouped.Capture().Candidate?.MessageId=="msg_independent_idle","idle readable label remains independently provable outside assistant grouping");
        ConversationViewTestProbe.Reset();
        var working = new OpenCodeConversationView();
        var executing = System.Text.Json.JsonSerializer.Deserialize<OpenCodeMessage>("""
            {"id":"msg_work","type":"assistant","model":{"providerID":"gateway","id":"proxy/claude-sonnet"},"time":{"created":1},"content":[{"id":"r_work","type":"reasoning","text":"thinking"}]}
            """,OpenCodeJson.Options)!;
        working.ApplyMessages(new[]{executing});
        var activeGroup=ConversationViewTestProbe.Records.Where(r=>r.Operation=="Construct").Select(r=>r.Subject).OfType<OpenCodeToolGroupView>().Single();
        Check(activeGroup.ExecutingModelId=="proxy/claude-sonnet"&&activeGroup.WorkingReasoning,"working context comes from executing assistant modelID and real reasoning");
        var next=executing with {Id="msg_next",Model=new(){ProviderId="openai",Id="opaque"},Content=new()};
        working.ApplyMessages(new[]{executing,next});
        Check(activeGroup.ExecutingModelId=="opaque","empty newest executing assistant never borrows previous model or infers brand from provider");
        working.ApplyMessages(new[]{executing,next with {Model=null}});
        Check(activeGroup.ExecutingModelId is null,"missing actual execution model remains unknown rather than using previous step");
        working.ClearMessages();
        ConversationViewTestProbe.Reset();
        OpenCodeMessage StreamingSteps(int count) => System.Text.Json.JsonSerializer.Deserialize<OpenCodeMessage>(
            System.Text.Json.JsonSerializer.Serialize(new { id="msg_steps",type="assistant",time=new {created=1700000000000d},
                content=Enumerable.Range(0,count).Select(i=>new {id="call_"+i,type="tool",name="read",state=new {status=i==count-1?"running":"completed",input=new {path="file-"+i+".cs"}}}).ToArray() }),OpenCodeJson.Options)!;
        var stepsView=new OpenCodeConversationView();stepsView.ApplyMessages(new[]{StreamingSteps(2)});
        var stepsGroup=ConversationViewTestProbe.Records.Where(r=>r.Operation=="Construct").Select(r=>r.Subject).OfType<OpenCodeToolGroupView>().Single();
        Check(stepsGroup.IsExpanded,"new working group defaults expanded");stepsGroup.ToggleForTest();
        Check(!stepsGroup.IsExpanded,"manual working collapse is accepted");
        for(var i=3;i<=20;i++)stepsView.ApplyMessages(new[]{StreamingSteps(i)});
        Check(!stepsGroup.IsExpanded&&stepsGroup.CurrentStep!.Contains("file-19.cs",StringComparison.Ordinal),"streaming preserves collapse while latest real step keeps updating");
        Check(stepsGroup.ItemsHost.Children.Count==2,"collapsed streaming does not materialize new hidden steps");
        stepsGroup.ToggleForTest();
        Check(stepsGroup.IsExpanded&&stepsGroup.ItemsHost.Children.Count==20,"re-expanding retains every step");
        stepsView.ApplyMessages(new[]{StreamingSteps(21)});stepsView.SetCurrentTurnActivity(false);
        Check(stepsGroup.IsExpanded,"manual expansion persists through updates and terminal transition for group lifetime");
        stepsView.ClearMessages();stepsView.SetCurrentTurnActivity(null);stepsView.ApplyMessages(new[]{StreamingSteps(2)});
        var resetGroup=ConversationViewTestProbe.Records.Where(r=>r.Operation=="Construct").Select(r=>r.Subject).OfType<OpenCodeToolGroupView>().Last();
        Check(resetGroup.IsExpanded&&!ReferenceEquals(resetGroup,stepsGroup),"new group lifetime resets manual choice");stepsView.ClearMessages();
        Console.WriteLine($"PASS visible-idle actual ConversationView/query: {checks} assertions; controlled WinUI tree/transforms only, NOT native geometry/ACK proof");
    }
    private sealed class BrokenWindow:System.Collections.Generic.IReadOnlyList<OpenCodeMessage>
    {
        public int Count=>1;public OpenCodeMessage this[int i]=>throw new InvalidOperationException("synthetic incomplete Apply");
        public System.Collections.Generic.IEnumerator<OpenCodeMessage> GetEnumerator()=>throw new InvalidOperationException("synthetic incomplete Apply");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
    }
}
