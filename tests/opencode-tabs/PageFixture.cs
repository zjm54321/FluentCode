#pragma warning disable CS0169, CS0414, CS0649, CS0067
// UI/IO shell only. Tab operations, session switch, binding identity and generation comparisons
// are extracted VERBATIM from the current production files. Agent/refresh/ACK transport are boundaries.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using System.IO;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;
public sealed partial class OpenCodePage
{
    private readonly Dictionary<string,SessionTabEntry> _sessionTabs=new(StringComparer.Ordinal);
    private readonly Dictionary<string,string> _sessionTitles=new(StringComparer.Ordinal);
    private bool _sessionTabsInitialized,_suppressTabSelection;
    private string? _activeTabSessionId,_closedBoundSessionId;private int _closedBoundGeneration;
    private static readonly object HomeSessionTabTag=new();private bool _homeNormalizeQueued,_homeNormalizeApplying;
    private static readonly object NewSessionTabTag=new();private TabViewItem? _newSessionTab;
    private string? _blankWasActiveFor,_convertNewSessionTabTo;private bool _activatingSessionTab;
    private bool _newSessionDraftActive,_newSessionCreatePending,_homeActive=true,_pageActive;
    private int _pageGeneration=1,_sessionGeneration,_sourceGeneration=1;private long _sourceRevision=1;
    private string? _selectedSessionId;private bool _sessionMissing,_suppressSessionSelection;
    private OpenCodePageSource? _source;private OpenCodeClient? _client;
    private OpenCodeComposerController? _composerController;
    private OpenCodeComposerView? _composerView;
    private OpenCodeComposerBinding? _composerBinding;
    private bool _isComposing,_isSending,_composerPickerPending,_nativeDialogPending;
    private object? _nativeActionOwner,_nativeTextCandidate,_expandedComposerSelection;
    private OpenCodeComposerToken? _newSessionHandoffToken,_newSessionCreatedToken;
    private string? _newSessionCreatedId; private OpenCodeComposerBinding? _newSessionCreatedBinding;
    private Guid? _newSessionOperation; private long _newSessionCreateEpoch,_blankPickerEpoch;
    private bool _blankPickerPending,_blankModelChosen;
    private OpenCodeBlankDraftPreferences? _blankPreferences;
    private IReadOnlyList<OpenCodeAgentInfo> _blankAgents=Array.Empty<OpenCodeAgentInfo>();
    private IReadOnlyList<OpenCodeModelInfo> _blankModels=Array.Empty<OpenCodeModelInfo>();
    private string? _blankMetadataRuntime,_blankMetadataDirectory,_blankPreferenceError;
    private Guid _blankMetadataDraft,_blankMetadataSlotId;
    private string _homeRuntimeSource="tabs-home";private string? _homeDefaultDirectory="/project";
    private CancellationTokenSource? _homeCancellation=new();
    private readonly Dictionary<(string SourceIdentity,Guid BlankSlotId),string?> _blankCreationResults=new();
    private readonly Dictionary<string,OpenCodeHomeProject> _homeProjects=new();
    private sealed class HomeStub{internal OpenCodeHomeProjectKey? SelectedProject;}
    private readonly HomeStub _homeView=new();
    private CancellationTokenSource? _sessionCts;private CancellationTokenSource? _pageCts=new();
    private readonly Button NewSessionButton=new();
    private enum ConnectionState{Connected,Unknown}
    private void SetConnectionState(ConnectionState state){}
    private void RefreshSessionStateUi(){}
    private bool HasPendingNativeAction()=>_nativeActionOwner is not null||_nativeDialogPending;
    internal int SubmissionRequests;
    private void RequestComposerSubmission()=>SubmissionRequests++;
    private readonly Dictionary<string,ListViewItem> _sessionItems=new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<ListViewItem,OpenCodeSessionInfo> HomeSessionMetadata=new();
    private MessageBinding? _messageBinding;private List<OpenCodeMessage> _olderMessages=new(),_latestMessages=new();private bool _firstMessagesLoading;
    private sealed record SessionReturnTarget(string Id);
    private readonly ViewStub _conversationView=new(),_interactionsView=new();private readonly RecoveryStub _sessionRecoveryPanel=new();
    private bool _sessionProtectionHint,_interactionBusy,_deliveredNoticeShown;private object? _recoveryOrigin;private int _mutationEpoch;
    private readonly HashSet<string> _pendingAckIds=new();private readonly BarStub PermissionBar=new(),MessageBar=new();
    private bool _hostWindowVisible;
    private readonly Dictionary<string,string> _lastIdleBySession=new();private readonly HashSet<string> _workingSessions=new();
    private sealed class TimerBoundary{internal void Stop(){}}private TimerBoundary? _pollTimer;
    private event EventHandler? TrustedEndpointIdentityChanged;
    private void OpenCodePage_TrustedEndpointIdentityChanged(object? sender,EventArgs e){}
    private void UpdateViewAcknowledgementVisibility(bool visible){}private void DetachViewAcknowledgementEvents(){}
    private void UnsubscribeAppSourceChanged(){}private void DetachConversationEvents(){}private void StopEventSync(){}
    private static OpenCodePageSource? _fixtureAppSource;
    private static OpenCodePageSource? ReadAppSource()=>_fixtureAppSource; // App property boundary only; actual Apply/install decision is source-linked.
    private void ShowWorkspace(){}
    private Task RefreshSessionsAsync()=>Task.CompletedTask; // No synthetic directory: tests explicitly deliver real directory confirmation through actual Switch.
    private Task TryDrainNavigationQueueAsync()=>Task.CompletedTask;
    private void StartEventSync(){}
    private void ResetSessionPaginationState(){}
    private void ResetHomeSource(){_homeCancellation?.Cancel();_homeCancellation?.Dispose();_homeCancellation=null;_homeRuntimeSource=Guid.NewGuid().ToString("N");_blankPickerEpoch++;_blankPickerPending=false;}
    private void BeginHomeSource(){_homeCancellation=new();}
    internal readonly TabView SessionTabs=new();internal TabViewItem HomeTab=new();
    internal readonly ListView SessionList=new();private readonly PaneStub SessionPane=new(),InsightsPane=new();
    internal readonly TextBox Composer=new();internal readonly DispatcherQueue DispatcherQueue=new();
    internal string LastLayout="";internal int Invalidations,MessageLoads,Binds,Tracking,Requests,Notices,ReturnClears;
    internal string? Current=>_selectedSessionId;internal bool Home=>_homeActive;internal bool Blank=>_newSessionDraftActive;
    internal OpenCodeComposerController Controller=>_composerController!;
    internal OpenCodeComposerSnapshot Draft=>_composerController!.Snapshot;
    internal int BlankCount=>_blankTabs.Count;
    internal Guid? BlankId=>_activeBlankSlotId;
    internal int SlotCount=>_draftSlots.Count;
    internal long Activation=>_draftActivationVersion;
    internal int FileOpens;
    internal bool Recovery=>_draftRecoveryPending;
    private readonly DropDownButton BlankAgentButton=new(){MaxWidth=260},BlankStrengthButton=new(),BlankProjectButton=new();private readonly Button BlankModelButton=new();private readonly TextBlock BlankPreferencesHint=new();
    internal string AgentName=>BlankAgentButton.Content is TextBlock text?text.Text:BlankAgentButton.Content as string??"";
    internal Windows.UI.Color? AgentColor=>(BlankAgentButton.Foreground as Microsoft.UI.Xaml.Media.SolidColorBrush)?.Color;
    internal object? AgentDescription=>BlankAgentButton.Attached.GetValueOrDefault("Tooltip");
    internal bool AgentEnabled=>BlankAgentButton.IsEnabled;
    internal bool ModelMetadataReady=>_blankPreferences is{} preferences&&HasBlankMetadata(preferences);
    internal void AgentData(bool enabled=true)=>_http.AgentMode=enabled;
    internal void FailConfig(bool value)=>_http.FailConfig=value;
    private TaskCompletionSource? _agentIdle;
    internal Task WaitAgentIdle(){if(!_blankPickerPending)return Task.CompletedTask;_agentIdle??=new(TaskCreationOptions.RunContinuationsAsynchronously);return _agentIdle.Task.WaitAsync(TimeSpan.FromSeconds(5));}
    internal Task ReloadAgentDefaults()=>LoadBlankMetadataAsync(_blankPreferences!,Draft.Token);
    internal Action ModelsRelease(){var signal=_http.HeldModels!;var result=EmptyHttp.Json(_http.Catalog());return()=>signal.TrySetResult(result);}
    internal void FailModels()=>_http.HeldModels!.TrySetException(new HttpRequestException("synthetic model failure"));
    internal void HoldAgents(){_http.AgentsGate=new(TaskCreationOptions.RunContinuationsAsynchronously);_http.AgentsStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);}
    internal Task WaitAgentsStarted()=>_http.AgentsStarted!.Task.WaitAsync(TimeSpan.FromSeconds(5));
    internal Action AgentsRelease(){var signal=_http.PendingAgentsGate!;var result=EmptyHttp.Json(_http.LastAgentsJson!);return()=>signal.TrySetResult(result);}
    internal void HoldConfig(){_http.ConfigGate=new(TaskCreationOptions.RunContinuationsAsynchronously);_http.ConfigStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);}
    internal Task WaitConfigStarted()=>_http.ConfigStarted!.Task.WaitAsync(TimeSpan.FromSeconds(5));
    internal void ReleaseConfig()=>_http.PendingConfigGate!.TrySetException(new HttpRequestException("synthetic delayed config failure"));
    internal void ChangeBlankDirectory(string directory)=>ChangeBlankSlotDirectory(directory);
    internal async Task<MenuFlyout> AgentMenu()
    {
        var shown=new TaskCompletionSource<MenuFlyout>(TaskCreationOptions.RunContinuationsAsynchronously);
        MenuFlyout.Shown=menu=>shown.TrySetResult(menu);BlankAgentButton_Click(this,new());
        return await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    internal Guid SourceEpoch=>_draftSourceEpoch;
    internal OpenCodePageSource SourceInstance=>_source!;
    internal OpenCodePageSource NewSource(string identity,long revision)=>new(identity,revision,CreateClient);
    internal void LeavePage(bool childUnloadedFirst=false){if(childUnloadedFirst)_composerView!.NativeUnloaded();OnPageUnloaded(this,new());ResetHomeSource();}
    internal void ReturnPage(OpenCodePageSource? source)
    {
        _fixtureAppSource=source;_pageActive=true;_pageGeneration++;_pageCts=new();_composerView!.NativeLoaded();BindComposer();ApplyCurrentSource();
    }
    internal object? RecoveryTab=>_draftRecoveryTab;
    internal bool SavedData(TabViewItem item)=>TryGetRetiredDraftTab(item,out var slot)&&slot?.Data is not null;
    internal bool Composing{set=>_isComposing=value;}
    internal void Text(string text){Composer.Text=text;_composerView!.Observe();}
    internal void RecordOnly(string text){Composer.Text=text;_composerView!.RecordOnly();}
    internal void FlushInput()=>_composerView!.PrepareClientTextApplication();
    internal Task Directory(string id,string directory)=>SwitchToSessionAsync(id,directory:directory);
    internal void UnknownDirectory(string id){var item=_sessionItems[id];HomeSessionMetadata.Remove(item);HomeSessionMetadata.Add(item,new(){Id=id});}
    internal void ConfirmDirectory(string directory){_messageBinding=_messageBinding! with{Directory=directory};BindComposer();}
    internal async Task File(string name,byte[] bytes,bool reconcile=true)
    {
        var pick=Controller.BeginPick(Draft.Token)??throw new Exception("No real file ticket");
        await Controller.AddFilesAsync(pick,new[]{new OpenCodeComposerFileSource(name,null,_=>{FileOpens++;return Task.FromResult<Stream>(new MemoryStream(bytes));})});
        if(reconcile)FlushInput();
    }
    internal async Task Command(string text)
    {
        Text("/review "+text);var lifetime=Controller.BeginCatalogLifetime();
        await Controller.LoadCatalogAsync(Draft.Token,lifetime);
        if(!Controller.TrySelectCommand(Draft.Token,Draft.CatalogHandle!,new OpenCodeComposerCommandIdentity.Server("review")))throw new Exception("No real command choice");
        FlushInput();
    }
    internal void ExplicitDiscard(){Controller.DiscardDraft(Draft.Token);_composerView!.Discard();EnsureBlankPreferences();}
    internal void MarkCreateUnknown(){_blankCreationResults[(_source!.Identity,BlankId!.Value)]=null;}
    internal bool MayCreate=>CaptureBlankPreferences(Draft.Token)is not null;
    internal void DiscardRecovery()=>DiscardDraftSlotRecovery();
    internal OpenCodeBlankDraftPreferences? Preferences=>_blankPreferences;
    internal string? PreferenceError=>_blankPreferenceError;
    private readonly Dictionary<Guid,TaskCompletionSource> _defaultsReady=new();
    internal Task WaitDefaults()
    {
        if(_activeDraftSlot?.DefaultsInitialized==true||_blankPreferenceError is not null)return Task.CompletedTask;
        var id=_activeBlankSlotId!.Value;
        if(!_defaultsReady.TryGetValue(id,out var signal))_defaultsReady[id]=signal=new(TaskCreationOptions.RunContinuationsAsynchronously);
        return signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    internal void ChooseModel(string id,string variant){_blankPreferences=_blankPreferences! with{Model=new(){Id=id,ProviderId="p",Variant=variant},Version=_blankPreferences!.Version+1};_blankModelChosen=true;}
    internal Task ChooseVariant(string? variant)=>SetBlankVariantAsync(_blankPreferences!,Draft.Token,variant);
    internal string DefaultVariant{set=>_http.DefaultVariant=value;}
    internal int HttpMutations=>_http.Mutations;
    internal int CreatePosts=>_http.CreatePosts;
    internal Task WaitCreateStarted()=>_http.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    internal string? CreateBody=>_http.LastCreateBody;
    internal Task Create()=>CreateSessionAndSubmitAsync(Draft.Token);
    internal Action CaptureAttachmentRemoval()=>_composerView!.Removal();
    internal void HoldCreate()=>_http.CreateResponse=new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void CompleteCreate(string id)=>_http.CreateResponse!.TrySetResult(EmptyHttp.Json("{\"data\":{\"id\":\""+id+"\",\"location\":{\"directory\":\"/project\"}}}"));
    internal void FailCreate()=>_http.CreateResponse!.TrySetException(new HttpRequestException("synthetic unknown transport"));
    internal void HoldModels(){_http.HoldNextModels=true;_http.ModelsStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);_http.HeldModels=new(TaskCreationOptions.RunContinuationsAsynchronously);}
    internal Task WaitModelsStarted()=>_http.ModelsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    internal void ReleaseModels()=>_http.HeldModels!.TrySetResult(EmptyHttp.Json(_http.Catalog()));
    internal bool OfferXhigh{set=>_http.IncludeXhigh=value;}
    internal bool HasCreateTombstone(string endpoint,Guid id)=>_blankCreationResults.ContainsKey((endpoint,id));
    internal string Endpoint=>_source!.Identity;
    internal TabViewItem? BlankTab=>_newSessionTab;internal bool Creating{set{_newSessionCreatePending=value;if(_activeDraftSlot is{} s){s.CreateOperation=value?Guid.NewGuid():null;s.CreatedSessionId=null;}}}
    private sealed class PaneStub{internal bool IsPaneOpen;}
    private sealed class ViewStub{internal int Clears;internal void ClearMessages()=>Clears++;internal void ClearAll()=>Clears++;}
    private sealed class BarStub{internal bool IsOpen;}
    private sealed class RecoveryStub{internal void HideConfirm(){}internal void HideEntry(){}internal void SetBusy(bool busy,string? message){}}
    internal OpenCodePage(string xaml,bool connected=true)
    {
        var doc=XDocument.Load(xaml);XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation";XNamespace x="http://schemas.microsoft.com/winfx/2006/xaml";
        var xml=doc.Descendants(ns+"TabViewItem").Single(e=>(string?)e.Attribute(x+"Name")=="HomeTab");
        // XAML/native creation boundary: construct declared item shape BEFORE first measure/load.
        HomeTab.IsClosable=bool.Parse((string)xml.Attribute("IsClosable")!);
        HomeTab.MinWidth=double.Parse((string)xml.Attribute("MinWidth")!,System.Globalization.CultureInfo.InvariantCulture);
        HomeTab.MaxWidth=double.Parse((string)xml.Attribute("MaxWidth")!,System.Globalization.CultureInfo.InvariantCulture);
        HomeTab.Header=new FontIcon{Glyph=(string)xml.Descendants(ns+"FontIcon").Single().Attribute("Glyph")!};
        SessionTabs.TabItems.Add(HomeTab);SessionTabs.SelectedItem=HomeTab;
        SessionTabs.SelectionChanged+=SessionTabs_SelectionChanged;SessionTabs.TabItemsChanged+=SessionTabs_TabItemsChanged;
        SessionList.SelectionChanged+=SessionList_SelectionChanged;
        if(connected){_source=new("tabs-source",1,CreateClient);_client=_source.CreateClient();EnsureDraftSourceEpoch(_source);}
        _fixtureAppSource=_source;
        _composerController=new(OwnsComposerBinding);
        _composerView=new(GetComposerSnapshot,Composer,()=>_isComposing,(token,text,carry)=>_composerController.TryApplyEditorInput(token,text,carry));
        _composerView.AttachmentRemoved+=(_,e)=>_composerController.RemoveAttachment(e.Token,e.Id);
        _composerController.SnapshotChanged+=OnComposerSnapshotChanged;
        _homeProjects["/project"]=new(new(_homeRuntimeSource,"/project"),"Project");
        BlankPreferencesHint.TextUpdated=OnBlankLabelsRefreshed;
        InitializePinnedHomeTab();
    }
    internal void Load(){_pageActive=true;SessionTabs_Loaded(SessionTabs,new());}
    internal void ReloadGeneration(){_pageActive=false;_pageGeneration++;_pageActive=true;SessionTabs_Loaded(SessionTabs,new());}
    internal void UnloadFixture()=>_pageActive=false;
    internal void AddSession(string id)
    {
        var item=new ListViewItem{Tag=id,Content=new StackPanel{Children={new TextBlock{Text=id},new TextBlock{Text=id}}}};
        _sessionItems[id]=item;HomeSessionMetadata.Add(item,new(){Id=id,Location=new(){Directory="/project"}});SessionList.Items.Add(item);
        CreateSessionTab(id);
    }
    internal TabViewItem Session(string id)=>_sessionTabs[id].Item;
    internal void RenameSession(string id,string title)
    {
        var row=_sessionItems[id];((StackPanel)row.Content!).Children.OfType<TextBlock>().First().Text=title;
        DecorateSessionItem(row); // Actual current-source title-cache/tab decoration, not a copied retirement algorithm.
    }
    internal void Select(object item)=>SessionTabs.SelectedItem=item;
    internal void Close(TabViewItem item)=>SessionTabs_TabCloseRequested(SessionTabs,new(){Tab=item});
    internal void CloseOthers(string id)=>CloseOtherSessionTabs(id);
    internal void New()=>OpenNewSessionTab();
    internal void HomeNow()=>ShowHomePage();
    internal bool DragHome(){var args=new DragStartingEventArgs();HomeTab_DragStarting(HomeTab,args);return args.Cancel;}
    internal void DirectRemove(string id)=>RemoveSessionTab(_sessionTabs[id]);
    internal OpenCodeWorkingShimmer Shimmer(string id)=>_sessionTabs[id].Shimmer;
    internal void ForgetMetadata(string id)=>_sessionItems.Remove(id);
    internal void InsertSessionAt(string id,int index)=>CreateSessionTab(id,index);
    internal void ConvertBlank(string id){RecordCreatedBlankSlot(_source!,_blankPreferences!,new(){Id=id,Location=new(){Directory="/project"}});ConvertCreatedBlankTab(_activeBlankSlotId!.Value,id,true);}
    internal void BackgroundHighlight(string id)
    {var old=_suppressSessionSelection;_suppressSessionSelection=true;try{SessionList.SelectedItem=_sessionItems[id];}finally{_suppressSessionSelection=old;}}
    internal Func<bool> CaptureBindingFence()
    {var s=_source!;var c=_client!;var r=_sourceRevision;var g=_sourceGeneration;var sg=_sessionGeneration;return()=>IsCurrentBinding(s,r,g,c,sg);}
    internal void ReplaceSource(bool sameIdentity=false)
    {
        var identity=sameIdentity?_source!.Identity:"new-source";InvalidateDraftSourceEpoch();
        // Source/client transport boundary: the production invalidation also clears current list/binding.
        _source=new(identity,++_sourceRevision,CreateClient);_client=_source.CreateClient();_sourceGeneration++;
        _selectedSessionId=null;_messageBinding=null;_sessionMissing=false;_sessionItems.Clear();SessionList.Items.Clear();
        var suppress=_suppressSessionSelection;_suppressSessionSelection=true;
        try{SessionList.SelectedItem=null;}finally{_suppressSessionSelection=suppress;}
    }
    internal Task SwitchAgain(string id)=>SwitchToSessionAsync(id);
    internal void MoveBeforeHome(TabViewItem item)
    {SessionTabs.SimulateNativeAutoSelect=false;SessionTabs.TabItems.Remove(item);SessionTabs.TabItems.Insert(0,item);SessionTabs.SimulateNativeAutoSelect=true;}
    private void ApplySessionTabState(SessionTabEntry entry)=>entry.Title.Text=_sessionTitles.GetValueOrDefault(entry.SessionId,entry.SessionId);
    private void InvalidateViewAcknowledgement()=>Invalidations++;
    private void QueueHomeProjection()=>Requests++;
    private void ShowMessage(InfoBarSeverity severity,string title,string detail)=>Notices++;
    private void ShowSourceWaiting(){} // Connection panel native visibility boundary; no Source/Slot authority.
    private void ClearSessionReturnContext()=>ReturnClears++;
    private void InstallSessionReturnContext(SessionReturnTarget? target){}
    private void RestartSessionEventSync(string id){}
    private void EnsureConversationImages(){}
    private void ConfigureAgentSelectorForSession(string id)=>OnSessionContextSelected(id);
    partial void OnSessionContextSelected(string id);
    partial void OnNativeSessionCreating(string id);
    partial void OnNativeSessionCreated(string id,OpenCodeComposerBinding? binding,OpenCodeComposerToken? token);
    private void ClearAgentSelector(){}
    private void UpdateWaitingText(){}private void UpdateEmptyState(){}private void UpdateComposerButtons(){}private void UpdateInteractionsSection(){}
    private void ResetMessagePaginationState(){}private void RetainMessageWindow(){}private void RestoreMessageWindow(){}
    private void RaiseSessionTracked(string id,OpenCodeSessionTrackReason reason)=>Tracking++;
    private Task RefreshMessagesAsync(bool initial){MessageLoads++;return Task.CompletedTask;}
    private void SetBlankComposerPlacement(bool blank){}private void WireNewSessionList(){}private void RefreshNewSessionList(){}
    private void OnBlankLabelsRefreshed(){if(!_blankPickerPending){_agentIdle?.TrySetResult();_agentIdle=null;if(_activeBlankSlotId is{} id&&_defaultsReady.TryGetValue(id,out var ready))ready.TrySetResult();}}
    private void InsertSessionItemFirst(OpenCodeSessionInfo info)
    {
        if(info.Id is not{} id||_sessionItems.ContainsKey(id))return;
        var item=new ListViewItem{Tag=id,Content=new StackPanel{Children={new TextBlock{Text=info.Title??id}}}};
        _sessionItems[id]=item;HomeSessionMetadata.Add(item,info);SessionList.Items.Add(item);
    }
    private void RefreshComposerView()=>_composerView?.RequestRender();
    private void CheckNativeEditorOwnership(){}private void InvalidateNativeActionIfBindingLost(){}
    private void InvalidateLocalSubmissionOwner()=>_isSending=false;
    private void SelectSessionTabSilentlyForFixture()=>SelectSessionTabSilently(_activeTabSessionId);
    private readonly EmptyHttp _http=new();
    private OpenCodeClient CreateClient()=>new(new HttpClient(_http,false),new(){BaseUrl="https://offline.invalid",AuthenticationMode=OpenCodeAuthenticationMode.None},true);
    private sealed class EmptyHttp:HttpMessageHandler
    {
        internal string DefaultVariant="medium";internal int Mutations,CreatePosts;internal string? LastCreateBody;
        internal TaskCompletionSource<HttpResponseMessage>? CreateResponse;
        internal readonly TaskCompletionSource CreateStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldNextModels,IncludeXhigh=true;
        internal bool AgentMode,FailConfig;
        internal TaskCompletionSource<HttpResponseMessage>? AgentsGate,ConfigGate,PendingAgentsGate,PendingConfigGate;
        internal TaskCompletionSource? AgentsStarted,ConfigStarted;internal string? LastAgentsJson;
        internal TaskCompletionSource ModelsStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<HttpResponseMessage>? HeldModels;
        internal string Catalog()=>"{\"location\":{\"directory\":\"/project\"},\"data\":["+Model("base")+","+Model("custom")+"]}";
        internal static HttpResponseMessage Json(string json)=>new(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")};
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(request.Method!=HttpMethod.Get)Mutations++;
            if(request.Method==HttpMethod.Post&&request.RequestUri!.AbsolutePath=="/api/session")
            {
                CreatePosts++;LastCreateBody=await request.Content!.ReadAsStringAsync(ct);
                CreateStarted.TrySetResult();
                return CreateResponse is{} pending?await pending.Task:Json("{\"data\":{\"id\":\"ses_created\",\"location\":{\"directory\":\"/project\"}}}");
            }
            if(request.Method==HttpMethod.Get&&request.RequestUri!.AbsolutePath=="/api/model"&&HoldNextModels)
            {HoldNextModels=false;ModelsStarted.TrySetResult();return await HeldModels!.Task;}
            var location="\"location\":{\"directory\":\"/project\"},";
            var json=request.RequestUri!.AbsolutePath switch
            {
                "/api/agent"=>AgentMode?"{"+location+"\"data\":["+Agent("build",AgentPresentationTests.LongName,"#123aBc","primary",false)+","+Agent("other","Other full agent",null,"all",false)+","+Agent("hidden","Hidden",null,"primary",true)+","+Agent("sub","Sub",null,"subagent",false)+"]}":"{"+location+"\"data\":[{\"id\":\"build\",\"name\":\"Build\",\"request\":{},\"permissions\":{},\"hidden\":false,\"mode\":\"primary\",\"model\":{\"id\":\"base\",\"providerID\":\"p\",\"variant\":\""+DefaultVariant+"\"}}]}",
                "/api/model"=>Catalog(),
                "/api/config"=>FailConfig?throw new HttpRequestException("synthetic config failure"):"[{\"type\":\"document\",\"info\":{\"default_agent\":\"build\"}}]",
                "/api/command"=>"{\"data\":[{\"name\":\"review\",\"description\":\"Review\"}]}",
                _=>"{\"data\":[]}"
            };
            if(request.RequestUri!.AbsolutePath=="/api/agent"&&AgentsGate is{} agents){LastAgentsJson=json;AgentsGate=null;PendingAgentsGate=agents;AgentsStarted!.TrySetResult();return await agents.Task;}
            if(request.RequestUri!.AbsolutePath=="/api/config"&&ConfigGate is{} config){ConfigGate=null;PendingConfigGate=config;ConfigStarted!.TrySetResult();return await config.Task;}
            return Json(json);
        }
        private string Model(string id)
        {var json="{\"id\":\""+id+"\",\"providerID\":\"p\",\"name\":\""+id+"\",\"enabled\":true,\"status\":\"active\",\"variants\":[{\"id\":\"low\"},{\"id\":\"medium\"},{\"id\":\"high\"},{\"id\":\"xhigh\"}]}";return IncludeXhigh?json:json.Replace(",{\"id\":\"xhigh\"}","");}
        private static string Agent(string id,string name,string? color,string mode,bool hidden)=>"{\"id\":\""+id+"\",\"name\":\""+name+"\",\"description\":\"description "+id+"\",\"request\":{},\"permissions\":{},\"hidden\":"+(hidden?"true":"false")+",\"mode\":\""+mode+"\""+(color is null?"":",\"color\":\""+color+"\"")+",\"model\":{\"id\":\"base\",\"providerID\":\"p\",\"variant\":\"medium\"}}";
    }
}
