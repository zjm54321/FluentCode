#pragma warning disable CS0169, CS0414, CS0649
// UI/IO shell only. Tab operations, session switch, binding identity and generation comparisons
// are extracted VERBATIM from the current production files. Agent/refresh/ACK transport are boundaries.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
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
    private CancellationTokenSource? _sessionCts;
    private readonly Dictionary<string,ListViewItem> _sessionItems=new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<ListViewItem,OpenCodeSessionInfo> HomeSessionMetadata=new();
    private MessageBinding? _messageBinding;private List<OpenCodeMessage> _olderMessages=new(),_latestMessages=new();private bool _firstMessagesLoading;
    private sealed record SessionReturnTarget(string Id);
    private readonly ViewStub _conversationView=new(),_interactionsView=new();private readonly RecoveryStub _sessionRecoveryPanel=new();
    private bool _sessionProtectionHint,_interactionBusy,_deliveredNoticeShown;private object? _recoveryOrigin;private int _mutationEpoch;
    private readonly HashSet<string> _pendingAckIds=new();private readonly BarStub PermissionBar=new();
    internal readonly TabView SessionTabs=new();internal TabViewItem HomeTab=new();
    internal readonly ListView SessionList=new();private readonly PaneStub SessionPane=new(),InsightsPane=new();
    internal readonly TextBox Composer=new();internal readonly DispatcherQueue DispatcherQueue=new();
    internal string LastLayout="";internal int Invalidations,MessageLoads,Binds,Tracking,Requests,Notices,ReturnClears;
    internal string? Current=>_selectedSessionId;internal bool Home=>_homeActive;internal bool Blank=>_newSessionDraftActive;
    internal TabViewItem? BlankTab=>_newSessionTab;internal bool Creating{set=>_newSessionCreatePending=value;}
    private sealed class PaneStub{internal bool IsPaneOpen;}
    private sealed class ViewStub{internal int Clears;internal void ClearMessages()=>Clears++;internal void ClearAll()=>Clears++;}
    private sealed class BarStub{internal bool IsOpen;}
    private sealed class RecoveryStub{internal void HideConfirm(){}internal void SetBusy(bool busy,string? message){}}
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
        if(connected){_source=new("tabs-source",1,()=>new());_client=_source.CreateClient();}
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
    internal void Select(object item)=>SessionTabs.SelectedItem=item;
    internal void Close(TabViewItem item)=>SessionTabs_TabCloseRequested(SessionTabs,new(){Tab=item});
    internal void CloseOthers(string id)=>CloseOtherSessionTabs(id);
    internal void New()=>OpenNewSessionTab();
    internal void HomeNow()=>ShowHomePage();
    internal bool DragHome(){var args=new DragStartingEventArgs();HomeTab_DragStarting(HomeTab,args);return args.Cancel;}
    internal void DirectRemove(string id)=>RemoveSessionTab(_sessionTabs[id]);
    internal void ForgetMetadata(string id)=>_sessionItems.Remove(id);
    internal void InsertSessionAt(string id,int index)=>CreateSessionTab(id,index);
    internal void ConvertBlank(string id){_convertNewSessionTabTo=id;MirrorSessionSelection(id,true);}
    internal void BackgroundHighlight(string id)
    {var old=_suppressSessionSelection;_suppressSessionSelection=true;try{SessionList.SelectedItem=_sessionItems[id];}finally{_suppressSessionSelection=old;}}
    internal Func<bool> CaptureBindingFence()
    {var s=_source!;var c=_client!;var r=_sourceRevision;var g=_sourceGeneration;var sg=_sessionGeneration;return()=>IsCurrentBinding(s,r,g,c,sg);}
    internal void ReplaceSource(){_source=new("new-source",++_sourceRevision,()=>new());_client=_source.CreateClient();_sourceGeneration++;}
    internal Task SwitchAgain(string id)=>SwitchToSessionAsync(id);
    internal void MoveBeforeHome(TabViewItem item)
    {SessionTabs.SimulateNativeAutoSelect=false;SessionTabs.TabItems.Remove(item);SessionTabs.TabItems.Insert(0,item);SessionTabs.SimulateNativeAutoSelect=true;}
    private void ApplySessionTabState(SessionTabEntry entry)=>entry.Title.Text=_sessionTitles.GetValueOrDefault(entry.SessionId,entry.SessionId);
    private void InvalidateViewAcknowledgement()=>Invalidations++;
    private void QueueHomeProjection()=>Requests++;
    private void ShowMessage(InfoBarSeverity severity,string title,string detail)=>Notices++;
    private void BindComposer()=>Binds++;
    private void ClearSessionReturnContext()=>ReturnClears++;
    private void InstallSessionReturnContext(SessionReturnTarget? target){}
    private void RestartSessionEventSync(string id){}
    private void EnsureConversationImages(){}
    private void ConfigureAgentSelectorForSession(string id)=>OnSessionContextSelected(id);
    partial void OnSessionContextSelected(string id);
    private void ClearAgentSelector(){}
    private void UpdateWaitingText(){}private void UpdateEmptyState(){}private void UpdateComposerButtons(){}private void UpdateInteractionsSection(){}
    private void ResetMessagePaginationState(){}private void RetainMessageWindow(){}private void RestoreMessageWindow(){}
    private void RaiseSessionTracked(string id,OpenCodeSessionTrackReason reason)=>Tracking++;
    private Task RefreshMessagesAsync(bool initial){MessageLoads++;return Task.CompletedTask;}
    private void SetBlankComposerPlacement(bool blank){}private void EnsureBlankPreferences(){}private void WireNewSessionList(){}private void RefreshNewSessionList(){}private void RefreshComposerView(){}
}
