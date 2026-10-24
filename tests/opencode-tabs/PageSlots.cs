using System;
using System.Linq;
using System.Threading.Tasks;
using AIFrontend.Pages;
using Microsoft.UI.Xaml.Controls;

internal static class DraftSlotPageTests
{
    private static int checks;
    private static void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);}
    internal static async Task Run(string xaml)
    {
        var p=new OpenCodePage(xaml);p.Load();p.New();var a=p.BlankTab!;var aid=p.BlankId;
        await p.WaitDefaults();
        Check(p.Preferences is {Agent:"build",Model.Id:"base",Model.Variant:"medium"},"blank A independently reads existing configured/agent defaults: "+p.PreferenceError);
        await p.Command("1234");await p.File("a.txt",new byte[]{1,2,3});p.ChooseModel("custom","xhigh");
        p.Composer.Select(2,2);var aToken=p.Draft.Token;var aFile=p.Draft.Attachments.Single().Id;
        p.DefaultVariant="low";p.New();var b=p.BlankTab!;var bid=p.BlankId;
        await p.WaitDefaults();
        Check(!ReferenceEquals(a,b)&&aid!=bid&&p.Draft.Text==""&&p.Draft.Attachments.Count==0&&p.Draft.SelectedCommand is null,"plus creates B empty without A text/files/command");
        Check(p.Preferences is {Agent:"build",Model.Id:"base",Model.Variant:"low"},"B defaults are freshly initialized, not A custom model/variant override");
        p.Text("5678");await p.File("b.txt",new byte[]{4,5});var bFile=p.Draft.Attachments.Single().Id;
        p.Select(a);
        Check(p.Draft.Text=="1234"&&p.Draft.SelectedCommand=="review"&&p.Draft.Attachments.Single().Id==aFile,"real Page/native A restore retains own body/command/file ID");
        Check(p.Preferences is{Model.Id:"custom",Model.Variant:"xhigh"}&&p.Preferences.BlankSlotId==aid,"A restores its own chosen model/variant/stable blank ID");
        Check(p.Composer.SelectionStart==2&&p.Composer.SelectionLength==2,"selection restored/clamped after real guarded clean activation");
        Check(p.Draft.Token.Draft==aToken.Draft&&p.Draft.Token.Scope!=aToken.Scope&&p.Draft.Token.Edit>aToken.Edit,"stable data Draft identity is distinct from fresh activation Scope/Edit");
        Check(p.Controller.TryApplyEditorInput(aToken,"OLD",false)is null&&p.Draft.Text=="1234","old A full edit token rejected after A-B-A");
        p.Select(b);Check(p.Draft.Text=="5678"&&p.Draft.Attachments.Single().Id==bFile&&p.Preferences?.Model?.Variant=="low","B restore is independent of A files/variant");
        Check(p.FileOpens==2,"restores do not reopen attachment streams");
        var savedB=p.Draft.Token;p.HomeNow();Check(!p.Draft.IsActive&&!p.Blank&&p.Home&&p.Draft.Attachments.Count==0,"Home null-triple suspension drops active payload and blank input authority");
        p.Select(b);Check(p.Draft.Text=="5678"&&p.Draft.Attachments.Single().Id==bFile&&p.Draft.Token.Scope!=savedB.Scope,"return from Home explicitly activates saved slot with new authority");
        p.Composing=true;p.Text("5678候选");var count=p.BlankCount;var selected=p.SessionTabs.SelectedItem;
        p.New();p.HomeNow();p.Close(b);
        Check(p.BlankCount==count&&ReferenceEquals(p.SessionTabs.SelectedItem,selected)&&p.Composer.Text=="5678候选","IME rejects ordinary plus/Home/active-close, no input loss or queued navigation");
        p.Composing=false;p.FlushInput();Check(p.Draft.Text=="5678候选"&&p.BlankCount==count,"composition end acknowledges only old same-owner pending input, not navigation");
        p.Select(a);var aBeforeClose=p.Draft.Token;p.Close(b);
        Check(p.Draft.Token==aBeforeClose&&p.Draft.Attachments.Single().Id==aFile&&p.BlankCount==1,"inactive close releases only B without disturbing A token/data");
        p.New();Check(p.Draft.Text==""&&p.Draft.Attachments.Count==0,"a later new blank is fresh, not the closed B slot");
        Check(p.HttpMutations==0,"slot restore/default initialization performs no send/config mutation");

        var existing=new OpenCodePage(xaml);existing.Load();existing.AddSession("same");existing.Select(existing.Session("same"));existing.Text("dir-one");
        await existing.Directory("same","/other");Check(existing.Draft.Text=="","same session ID in another real directory gets independent slot");
        existing.Text("dir-two");await existing.Directory("same","/project");Check(existing.Draft.Text=="dir-one","exact original directory restores only its saved data");
        await existing.Directory("same","/other");Check(existing.Draft.Text=="dir-two"&&existing.HttpMutations==0,"other directory data remains independent without config POST");
        existing.Composer.Select(1,3);existing.Composing=true;existing.Text("visible unconfirmed");var oldToken=existing.Draft.Token;
        existing.ReplaceSource();Check(existing.Recovery&&!existing.Draft.IsActive&&existing.Composer.IsReadOnly&&existing.Composer.Text=="visible unconfirmed","forced account epoch loss preserves actual visible IME text read-only and suspends core");
        existing.Composing=false;existing.FlushInput();Check(existing.Recovery&&existing.Composer.Text=="visible unconfirmed"&&!existing.Draft.IsActive,"composition end cannot replay recovery into a new token");
        existing.New();Check(existing.Recovery&&existing.BlankCount==0,"unresolved recovery blocks new composer activation/navigation");
        existing.DiscardRecovery();existing.New();Check(!existing.Recovery&&existing.Draft.IsActive&&existing.Draft.Text==""&&existing.Draft.Token!=oldToken,"explicit discard releases old authority/data before activating fresh account blank");
        Check(existing.Controller.TryApplyEditorInput(oldToken,"old account",false)is null,"old epoch full token never becomes current under new account");

        var unknown=new OpenCodePage(xaml);unknown.Load();unknown.AddSession("unknown");unknown.UnknownDirectory("unknown");unknown.Select(unknown.Session("unknown"));
        Check(!unknown.Draft.IsActive&&unknown.Composer.IsReadOnly&&unknown.Composer.Text=="","unknown existing directory cannot edit/restore or guess Home directory");
        unknown.ConfirmDirectory("/confirmed");unknown.Text("confirmed");Check(unknown.Draft.IsActive&&unknown.Draft.Text=="confirmed","existing authoritative-directory confirmation bridge activates real slot without new API request");

        var tombstone=new OpenCodePage(xaml);tombstone.Load();tombstone.New();await tombstone.WaitDefaults();var blank=tombstone.BlankTab!;var stable=tombstone.BlankId;
        tombstone.Text("once");tombstone.MarkCreateUnknown();Check(!tombstone.MayCreate,"unknown create blocks stable blank");
        tombstone.Text("edited");Check(!tombstone.MayCreate,"text editing does not bypass stable unknown create");
        tombstone.New();tombstone.Select(blank);Check(tombstone.BlankId==stable&&!tombstone.MayCreate,"activation ABA cannot bypass stable unknown create");
        tombstone.ExplicitDiscard();Check(tombstone.BlankId==stable&&!tombstone.MayCreate,"controller DraftId reset cannot bypass stable blank tombstone");
        await CreationCases(xaml);
        await CallbackCases(xaml);
        await RetiredCases(xaml);
        await PageLifetimeCases(xaml);
        await AgentPresentationTests.Run(xaml);
        Console.WriteLine($"PASS A7 real Page/native/core slot integration: {checks} assertions; native editor/window/IO boundaries explicit, no native IME/geometry claim");
    }

    private static async Task CreationCases(string xaml)
    {
        var inactive=new OpenCodePage(xaml);inactive.Load();inactive.New();await inactive.WaitDefaults();
        var a=inactive.BlankTab!;var stable=inactive.BlankId!.Value;inactive.Text("A1234");await inactive.File("a.txt",new byte[]{1});
        inactive.ChooseModel("custom","xhigh");inactive.HoldCreate();var create=inactive.Create();await inactive.WaitCreateStarted();
        Check(inactive.CreatePosts==1&&inactive.CreateBody!.Contains("xhigh")&&inactive.CreateBody.Contains("/project"),"actual configured create POST captures original stable blank model/variant/real directory");
        inactive.New();await inactive.WaitDefaults();var b=inactive.BlankTab!;inactive.Text("B5678");await inactive.File("b.txt",new byte[]{2});var bToken=inactive.Draft.Token;
        inactive.CompleteCreate("ses_background");await create;
        Check(ReferenceEquals(inactive.SessionTabs.SelectedItem,b)&&inactive.Draft.Token==bToken&&inactive.Draft.Text=="B5678","inactive accepted create cannot select, edit or clear B");
        Check(!inactive.SessionTabs.TabItems.Contains(a)&&inactive.SessionTabs.TabItems.Contains(inactive.Session("ses_background"))&&inactive.SubmissionRequests==0,"accepted create converts only captured original native blank, no automatic inactive send");
        inactive.Select(inactive.Session("ses_background"));Check(inactive.Draft.Text=="A1234"&&inactive.Draft.Attachments.Single().Name=="a.txt","converted existing slot explicitly restores original A pure data");
        Check(inactive.CreatePosts==1&&inactive.HasCreateTombstone(inactive.Endpoint,stable),"conversion/activation retains stable creation receipt and never re-POSTs create");

        var current=new OpenCodePage(xaml);current.Load();current.New();await current.WaitDefaults();current.Text("one requested input");
        current.HoldCreate();var own=current.Create();await current.WaitCreateStarted();current.CompleteCreate("ses_current");await own;
        Check(current.Current=="ses_current"&&current.Draft.Text=="one requested input"&&current.SubmissionRequests==1,"still-current exact original create performs one fresh ordinary submission handoff");
        Check(!current.Blank&&current.BlankCount==0&&current.CreatePosts==1,"current accepted blank converts without an extra draft or create");

        var edited=new OpenCodePage(xaml);edited.Load();edited.New();await edited.WaitDefaults();edited.Text("original");
        edited.HoldCreate();var changed=edited.Create();await edited.WaitCreateStarted();edited.Text("new edit while create waits");edited.CompleteCreate("ses_edited");await changed;
        Check(edited.Current=="ses_edited"&&edited.Draft.Text=="new edit while create waits"&&edited.SubmissionRequests==0,"late accepted create retains new edit and cannot arm original-token auto-send");

        var composing=new OpenCodePage(xaml);composing.Load();composing.New();await composing.WaitDefaults();composing.Text("confirmed");
        composing.HoldCreate();var ime=composing.Create();await composing.WaitCreateStarted();composing.Composing=true;composing.Text("visible candidate");
        composing.CompleteCreate("ses_ime");await ime;
        Check(composing.Current=="ses_ime"&&composing.Recovery&&!composing.Draft.IsActive&&composing.Composer.IsReadOnly
            &&composing.Composer.Text=="visible candidate"&&composing.SubmissionRequests==0,"accepted forced session transition with IME retains visible readonly recovery and never sends");
        composing.Composing=false;composing.FlushInput();Check(composing.Recovery&&composing.Composer.Text=="visible candidate","composition-end does not adopt accepted session token");
        composing.DiscardRecovery();Check(composing.Home&&!composing.Draft.IsActive&&composing.Composer.Text=="","explicit recovery discard returns to empty authority-free Home");
        composing.Select(composing.Session("ses_ime"));Check(composing.Draft.IsActive&&composing.Draft.Text=="","only a new explicit session selection activates the clean accepted session");

        var unknown=new OpenCodePage(xaml);unknown.Load();unknown.New();await unknown.WaitDefaults();unknown.Text("once");
        var oldBlank=unknown.BlankTab!;var oldId=unknown.BlankId!.Value;var endpoint=unknown.Endpoint;
        unknown.HoldCreate();var lost=unknown.Create();await unknown.WaitCreateStarted();unknown.FailCreate();await lost;
        Check(unknown.CreatePosts==1&&!unknown.MayCreate&&unknown.HasCreateTombstone(endpoint,oldId),"real unknown transport leaves stable blank tombstone");
        unknown.Text("changed");await unknown.Create();Check(unknown.CreatePosts==1,"editing after real unknown cannot retry create");
        unknown.New();await unknown.WaitDefaults();unknown.Select(oldBlank);await unknown.Create();Check(unknown.CreatePosts==1,"switch ABA after real unknown cannot retry create");
        unknown.ExplicitDiscard();await unknown.Create();Check(unknown.CreatePosts==1,"fresh controller DraftId cannot bypass real stable create tombstone");
        unknown.New();unknown.Close(oldBlank);Check(unknown.HasCreateTombstone(endpoint,oldId),"closing origin releases payload but keeps unknown create tombstone");
        unknown.ReplaceSource();Check(unknown.HasCreateTombstone(endpoint,oldId)&&unknown.CreatePosts==1,"source epoch rotation never erases old stable create tombstone or resends");
    }

    private static async Task CallbackCases(string xaml)
    {
        var defaults=new OpenCodePage(xaml);defaults.Load();defaults.HoldModels();defaults.New();await defaults.WaitModelsStarted();
        defaults.Text("typed while defaults load");var edited=defaults.Draft.Token;defaults.ReleaseModels();await defaults.WaitDefaults();
        Check(defaults.Preferences is{Agent:"build",Model.Id:"base"}&&defaults.Draft.Token==edited&&defaults.Draft.Text=="typed while defaults load",
            "captured same-activation/version background defaults initialize without stranding normal typing or borrowing text authority");
        var chip=new OpenCodePage(xaml);chip.Load();chip.New();await chip.WaitDefaults();var a=chip.BlankTab!;
        chip.Text("A");await chip.File("same-slot.txt",new byte[]{9});var file=chip.Draft.Attachments.Single().Id;
        var oldRemove=chip.CaptureAttachmentRemoval();chip.New();await chip.WaitDefaults();chip.Select(a);
        var newRemove=chip.CaptureAttachmentRemoval();oldRemove();
        Check(chip.Draft.Attachments.Single().Id==file,"actual old attachment chip closure rejects A-B-A despite restored identical file GUID");
        chip.Text("same activation new edit");newRemove();
        Check(chip.Draft.Attachments.Count==0&&chip.Draft.Text=="same activation new edit","actual fresh remove gesture on reused same-activation chip takes current Edit after ownership check");

        var variant=new OpenCodePage(xaml);variant.Load();variant.New();await variant.WaitDefaults();var origin=variant.BlankTab!;
        await variant.ChooseVariant("high");variant.HoldModels();var late=variant.ChooseVariant("xhigh");await variant.WaitModelsStarted();
        variant.New();await variant.WaitDefaults();variant.Select(origin);var activation=variant.Draft.Token;
        variant.ReleaseModels();await late;
        Check(variant.Draft.Token==activation&&variant.Preferences?.Model?.Variant=="high","actual late variant catalog completion cannot mutate same stable A after activation ABA");
        variant.OfferXhigh=false;await variant.ChooseVariant("xhigh");
        Check(variant.Preferences?.Model?.Variant=="high"&&variant.PreferenceError is not null,"fresh withdrawn variant rejected without changing wire preference");
        await variant.ChooseVariant("极高");Check(variant.Preferences?.Model?.Variant=="high","translated display label is not accepted as an opaque variant ID");
        await variant.ChooseVariant(null);Check(variant.Preferences?.Model?.Variant is null,"actual default action clears only current slot Variant to null");
        Check(chip.HttpMutations==0&&variant.HttpMutations==0,"UI removals/selection/restoration/callback rejection never send or POST server configuration");
    }

    private static async Task RetiredCases(string xaml)
    {
        var p=new OpenCodePage(xaml);p.Load();p.AddSession("same");p.RenameSession("same","Old account title");var old=p.Session("same");
        var oldClose=(MenuFlyoutItem)((MenuFlyout)old.ContextFlyout!).Items[0];
        var oldOthers=(MenuFlyoutItem)((MenuFlyout)old.ContextFlyout!).Items[1];
        var oldLocate=(MenuFlyoutItem)((MenuFlyout)old.ContextFlyout!).Items[3];
        p.Select(old);p.Text("old session");p.Composer.Select(2,4);await p.File("old.txt",new byte[]{1});
        p.New();await p.WaitDefaults();var blank=p.BlankTab!;var blankId=p.BlankId!.Value;var endpoint=p.Endpoint;
        p.Text("old blank");p.Composer.Select(1,3);p.MarkCreateUnknown();p.HomeNow();p.ReplaceSource(true);
        Check(p.SavedData(old)&&p.SavedData(blank)&&!p.Recovery,"clean Home source replacement retains inactive old data without manufacturing a recovery");
        p.AddSession("same");p.RenameSession("same","New account title");var fresh=p.Session("same");
        Check(!ReferenceEquals(fresh,old)&&p.SessionTabs.TabItems.Contains(old)&&p.SessionTabs.TabItems.Contains(fresh),"same endpoint/account revision and server ID create distinct native actors");
        Check(((StackPanel)old.Header!).Children.OfType<TextBlock>().Single().Text=="Old account title"
            &&((StackPanel)fresh.Header!).Children.OfType<TextBlock>().Single().Text=="New account title","actual new-source title-cache decoration updates only new actor, not retired frozen title");
        p.Select(fresh);p.Text("new account");await p.File("new.txt",new byte[]{2});var newFile=p.Draft.Attachments.Single().Id;
        var requests=p.HttpMutations;var loads=p.MessageLoads;p.Select(blank);
        Check(p.Recovery&&!p.Draft.IsActive&&p.Composer.IsReadOnly&&p.Composer.Text=="old blank"&&ReferenceEquals(p.RecoveryTab,blank),"different inactive old blank is shown only by real guarded readonly presenter");
        Check(p.Composer.SelectionStart==1&&p.Composer.SelectionLength==3,"retired readonly selection restored after guarded actual text readback");
        Check(p.HttpMutations==requests&&p.MessageLoads==loads&&p.Draft.Attachments.Count==0,"retired preview issues no backend request or opaque attachment activation");
        p.Select(fresh);Check(ReferenceEquals(p.SessionTabs.SelectedItem,blank)&&p.Composer.Text=="old blank"&&!p.Draft.IsActive,"unresolved readonly preview blocks normal current-account selection");
        oldOthers.Press();oldLocate.Press();Check(p.SessionTabs.TabItems.Contains(fresh)&&p.Recovery&&p.MessageLoads==loads,"stale retired close-others/locate closures cannot act on new account IDs");
        oldClose.Press();Check(!p.SessionTabs.TabItems.Contains(old)&&p.SessionTabs.TabItems.Contains(fresh)&&ReferenceEquals(p.RecoveryTab,blank),"captured old close removes only old exact actor, not new source same-ID tab or held blank");
        oldClose.Press();p.Close(old);Check(p.SessionTabs.TabItems.Contains(fresh)&&p.Recovery,"repeated stale menu/native close requests cannot resolve fresh same-ID actor");
        p.Close(blank);Check(!p.Recovery&&p.Home&&!p.SessionTabs.TabItems.Contains(blank)&&p.HasCreateTombstone(endpoint,blankId),"explicit readonly close releases exactly its native payload while retaining create tombstone");
        p.Select(fresh);Check(p.Draft.Text=="new account"&&p.Draft.Attachments.Single().Id==newFile,"current-account outgoing data survives retired preview/close unchanged");

        var q=new OpenCodePage(xaml);q.Load();q.AddSession("same");var a=q.Session("same");q.Select(a);q.Text("A readonly");q.Composer.Select(2,5);
        q.New();await q.WaitDefaults();var other=q.BlankTab!;q.Text("other old slot");q.HomeNow();q.ReplaceSource();q.AddSession("same");var b=q.Session("same");
        q.Select(b);q.Text("B confirmed");q.Composing=true;q.Text("B pending IME");q.Select(a);
        Check(!q.Recovery&&ReferenceEquals(q.SessionTabs.SelectedItem,b)&&q.Composer.Text=="B pending IME"&&q.Draft.IsActive,"ordinary retired selection cannot bypass composing outgoing Prepare");
        q.Composing=false;q.FlushInput();q.Select(a);
        Check(q.Recovery&&ReferenceEquals(q.RecoveryTab,a)&&q.Composer.Text=="A readonly"&&!q.Draft.IsActive,"retired session preview is readonly and never activates its old binding");
        Check(q.Composer.SelectionStart==2&&q.Composer.SelectionLength==5,"retired existing-session saved selection restored");
        q.ReplaceSource(true);q.AddSession("same");var c=q.Session("same");q.Select(c);
        Check(q.Recovery&&ReferenceEquals(q.RecoveryTab,a)&&ReferenceEquals(q.SessionTabs.SelectedItem,a)&&q.Composer.Text=="A readonly"&&!q.Draft.IsActive,$"another source replacement while A readonly does not relabel recovery as B/C or auto-adopt (held={q.Recovery},owner={ReferenceEquals(q.RecoveryTab,a)},selected={ReferenceEquals(q.SessionTabs.SelectedItem,a)},text={q.Composer.Text},active={q.Draft.IsActive})");
        q.DiscardRecovery();Check(q.Home&&!q.Recovery&&!q.SessionTabs.TabItems.Contains(a)&&q.SessionTabs.TabItems.Contains(b)&&q.SessionTabs.TabItems.Contains(c)&&q.SavedData(other),"explicit A discard releases only A, keeps independent retired B/blank and new C actor");
        q.Select(other);Check(q.Recovery&&q.Composer.Text=="other old slot"&&!q.Draft.IsActive,"other inactive old-epoch data remains independently recoverable after A discard");
        q.Close(other);q.Select(c);Check(q.Draft.IsActive&&q.Draft.Text==""&&q.HttpMutations==0,"only new explicit C selection obtains fresh empty authority after readonly resolution");

        var r=new OpenCodePage(xaml);r.Load();r.AddSession("same");var actor=r.Session("same");r.Select(actor);r.Text("reentrant old text");r.Composer.Select(4,5);r.HomeNow();r.ReplaceSource();
        r.Composer.OnTextAssigned=text=>{if(text!="reentrant old text")return;r.Composer.OnTextAssigned=null;r.Composer.Select(0,0);r.ReplaceSource(true);};
        r.Select(actor);
        Check(r.Recovery&&ReferenceEquals(r.RecoveryTab,actor)&&r.Composer.Text=="reentrant old text"&&!r.Draft.IsActive,"source reentry during native readonly assignment preserves original recovery actor without authority");
        Check(r.Composer.SelectionStart==0&&r.Composer.SelectionLength==0,"superseded Page presenter cannot restore old selection after source/recovery actor changed");
        Check(r.HttpMutations==0,"reentrant readonly presentation performs no backend mutation");
    }

    private static async Task PageLifetimeCases(string xaml)
    {
        // These call the ACTUAL OnPageUnloaded, ApplyCurrentSource and InvalidateForSourceChange
        // entries plus real Page helpers/Core/Sync; only App property/native events and GET completion are boundaries.
        var p=new OpenCodePage(xaml);p.Load();p.New();await p.WaitDefaults();var a=p.BlankTab!;
        p.Text("A clean");await p.File("A.txt",new byte[]{3});p.ChooseModel("custom","xhigh");p.Composer.Select(2,3);
        var file=p.Draft.Attachments.Single().Id;var epoch=p.SourceEpoch;var source=p.SourceInstance;
        p.DefaultVariant="low";p.New();await p.WaitDefaults();var b=p.BlankTab!;p.Text("B clean");await p.File("B.txt",new byte[]{4});
        p.Select(a);var token=p.Draft.Token;var oldChip=p.CaptureAttachmentRemoval();var oldCallback=p.CaptureBindingFence();
        p.HoldModels();var lateVariant=p.ChooseVariant("low");await p.WaitModelsStarted();var unloadWrites=0;
        p.Composer.OnTextAssigned=_=>unloadWrites++;p.LeavePage(childUnloadedFirst:true);p.Composer.OnTextAssigned=null;
        Check(unloadWrites==0,"already-unloaded child clean capture never flushes/reconciles or writes the native editor during Page unload");
        Check(!p.Recovery&&!p.Draft.IsActive&&p.SourceEpoch==epoch&&ReferenceEquals(p.SessionTabs.SelectedItem,a),"ordinary actual unload clean-captures A and suspends without epoch retirement or forced discard barrier");
        p.ReturnPage(source);
        p.ReleaseModels();await lateVariant;
        Check(p.SourceEpoch==epoch&&p.Draft.IsActive&&!p.Recovery&&ReferenceEquals(p.SessionTabs.SelectedItem,a)&&p.BlankTab==a,"actual same-reference/revision Apply reinstall retains native actor/epoch and restores clean blank");
        Check(p.Draft.Text=="A clean"&&p.Draft.Attachments.Single().Id==file&&p.Composer.SelectionStart==2&&p.Composer.SelectionLength==3,"same-source reload restores A pure text/file identity and clamped selection");
        Check(p.Preferences is{Agent:"build",Model.Id:"custom",Model.Variant:"xhigh"},"same-source new Home runtime preserves chosen profile/model/variant instead of inheriting or stranding old runtime prefs");
        Check(p.Draft.Token.Draft==token.Draft&&p.Draft.Token.Scope!=token.Scope&&!oldCallback()&&p.Controller.TryApplyEditorInput(token,"stale",false)is null,"Page/client generations and full token are fresh even when source epoch/data Draft stay stable");
        Check(p.Controller.CaptureDraft(token)is null&&p.Preferences?.Model?.Variant=="xhigh","old-page send capture and actual delayed variant picker are rejected after same-source fresh activation");
        oldChip();Check(p.Draft.Attachments.Single().Id==file,"old-page native picker/chip callback cannot act on restored same file ID");
        p.Select(b);Check(p.Draft.Text=="B clean"&&p.Draft.Attachments.Single().Name=="B.txt"&&p.Preferences?.Model?.Variant=="low","inactive B independently survives same-source page reinstall");
        Check(p.FileOpens==2&&p.HttpMutations==0,"page reinstall neither reopens attachments nor posts configuration/create/send");

        var metadata=new OpenCodePage(xaml);metadata.Load();metadata.New();await metadata.WaitDefaults();metadata.Text("clean editor");
        await metadata.File("not-rendered-yet.txt",new byte[]{8},reconcile:false);var metadataSource=metadata.SourceInstance;
        metadata.LeavePage(childUnloadedFirst:true);metadata.ReturnPage(metadataSource);
        Check(!metadata.Recovery&&metadata.Draft.IsActive&&metadata.Draft.Text=="clean editor"&&metadata.Draft.Attachments.Single().Name=="not-rendered-yet.txt","child-first unload captures current core attachment data before queued render without reusing its old editor Edit baseline as authority");

        var alias=new OpenCodePage(xaml);alias.Load();alias.New();await alias.WaitDefaults();var aliasSource=alias.SourceInstance;
        alias.Composer.OnTextAssigned=text=>{if(text.Contains('\r'))alias.Composer.Text=text.Replace("\r\n","\n");};
        alias.Controller.TryApplyEditorInput(alias.Draft.Token,"a\r\nb",false);alias.FlushInput();
        alias.LeavePage(childUnloadedFirst:true);alias.ReturnPage(aliasSource);
        Check(!alias.Recovery&&alias.Draft.IsActive&&alias.Draft.Text=="a\r\nb"&&alias.Composer.Text=="a\nb","already acknowledged native newline alias remains clean on child-first unload; exact token permits its guarded actual readback without a fake input acknowledgment");

        var session=new OpenCodePage(xaml);session.Load();session.AddSession("same");var actor=session.Session("same");session.Select(actor);
        session.Text("session clean");await session.File("session.txt",new byte[]{7});session.Composer.Select(1,4);
        var sessionSource=session.SourceInstance;var sessionEpoch=session.SourceEpoch;var sessionToken=session.Draft.Token;
        session.LeavePage();session.ReturnPage(sessionSource);
        Check(session.SourceEpoch==sessionEpoch&&ReferenceEquals(session.Session("same"),actor)&&session.Current=="same"&&!session.Recovery&&session.Draft.IsActive,"same-source navigation retains accepted directory and reactivates the selected session draft under fresh page capabilities");
        await session.Directory("same","/project");
        Check(session.Draft.IsActive&&session.Draft.Text=="session clean"&&session.Draft.Attachments.Single().Name=="session.txt"&&session.Composer.SelectionStart==1&&session.Composer.SelectionLength==4,"same accepted directory selection retains restored text/file/caret without another destructive switch");
        Check(session.Draft.Token.Scope!=sessionToken.Scope&&session.Controller.TryApplyEditorInput(sessionToken,"old page",false)is null,"existing-session old Page activation cannot become current again");

        session.LeavePage();sessionSource.Revoke();var replacement=session.NewSource(sessionSource.Identity,sessionSource.Revision+1);session.ReturnPage(replacement);
        Check(session.SourceEpoch!=sessionEpoch&&!session.Draft.IsActive&&session.SavedData(actor)&&session.SessionTabs.TabItems.Contains(actor),"revocation/replacement during page absence retires old native/data epoch even with same endpoint and server ID");
        session.AddSession("same");var newActor=session.Session("same");Check(!ReferenceEquals(actor,newActor),"off-page replacement same server ID cannot reuse old native actor");
        session.Select(newActor);session.Select(actor);Check(session.Recovery&&session.Composer.Text=="session clean"&&session.Composer.IsReadOnly&&!session.Draft.IsActive,"off-page retired A is only guarded readonly recovery, not automatic binding adoption");

        var ime=new OpenCodePage(xaml);ime.Load();ime.New();await ime.WaitDefaults();ime.Text("confirmed");var imeSource=ime.SourceInstance;var imeEpoch=ime.SourceEpoch;
        ime.Composing=true;ime.RecordOnly("visible IME text");ime.LeavePage(childUnloadedFirst:true);ime.ReturnPage(imeSource);
        Check(ime.Recovery&&ime.Composer.Text=="visible IME text"&&ime.Composer.IsReadOnly&&!ime.Draft.IsActive&&ime.SourceEpoch==imeEpoch,"same-source IME unload/reload retains actual visible readonly recovery without retiring account epoch");
        ime.Composing=false;ime.FlushInput();Check(ime.Recovery&&ime.Composer.Text=="visible IME text"&&!ime.Draft.IsActive,"composition end after same-source reload does not replay held input");

        var held=new OpenCodePage(xaml);held.Load();held.AddSession("held");held.Select(held.Session("held"));held.Text("confirmed");var heldSource=held.SourceInstance;
        held.Composing=true;held.RecordOnly("visible held input");held.Controller.TryApplyEditorInput(held.Draft.Token,"different confirmed edit",false);held.Composing=false;
        held.LeavePage();held.ReturnPage(heldSource);
        Check(held.Recovery&&held.Composer.Text=="visible held input"&&held.Composer.IsReadOnly&&!held.Draft.IsActive,$"noncomposing stale pending input that cannot Prepare still uses forced readonly unload recovery (recovery={held.Recovery},text={held.Composer.Text},readonly={held.Composer.IsReadOnly},active={held.Draft.IsActive})");

        var missing=new OpenCodePage(xaml);missing.Load();missing.New();await missing.WaitDefaults();var missingActor=missing.BlankTab!;missing.Text("old data");var missingEpoch=missing.SourceEpoch;
        missing.LeavePage();missing.ReturnPage(null);
        Check(missing.SourceEpoch!=missingEpoch&&!missing.Draft.IsActive&&missing.SavedData(missingActor),"null App source after unload is a real invalidation, not null/null installed-client no-op");
        var revoked=new OpenCodePage(xaml);revoked.Load();revoked.New();await revoked.WaitDefaults();revoked.Text("revoked data");var revokedSource=revoked.SourceInstance;var revokedEpoch=revoked.SourceEpoch;
        revoked.LeavePage();revokedSource.Revoke();revoked.ReturnPage(revokedSource);
        Check(revoked.SourceEpoch!=revokedEpoch&&!revoked.Draft.IsActive,"same object/revision is insufficient when the actual App source was revoked while away");
        var alive=new OpenCodePage(xaml);alive.Load();alive.New();await alive.WaitDefaults();alive.Text("old live data");var liveSource=alive.SourceInstance;var liveEpoch=alive.SourceEpoch;
        alive.LeavePage();alive.ReturnPage(alive.NewSource(liveSource.Identity,liveSource.Revision));
        Check(!liveSource.IsRevoked&&alive.SourceEpoch!=liveEpoch&&!alive.Draft.IsActive,"new source reference alone rotates epoch even when old source remains live with equal endpoint identity and revision");
    }
}
