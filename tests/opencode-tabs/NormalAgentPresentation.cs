#pragma warning disable CS0169,CS0414
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;
using AIFrontend.Services.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace AIFrontend.Pages.OpenCode;

internal sealed partial class OpenCodeAgentSelector : StackPanel
{
    private readonly List<OpenCodeAgentPickerRow> _rows=new();
    private readonly TextBlock _currentText=new(),_modelText=new(),_statusText=new();
    private readonly ProgressRing _progress=new();private readonly StackPanel _recoveryEntryPanel=new();
    private AgentSwitchOrigin? _currentOrigin;private OpenCodeClient? _client;private CancellationTokenSource? _contextCts;
    private Guid? _retainedUnknownOperationId;private bool _busy,_localSubmissionPending;private string? _currentAgentId,CurrentModelDisplay,_pendingAgentId;
    private string? CurrentAgentId=>_pendingAgentId??_currentAgentId;
    private string? CurrentAgentDisplay=>CurrentAgentId is null?null:DisplayName(CurrentAgentId);
    private bool HasPendingSelection=>_pendingAgentId is not null;
    private OpenCodeModelRef? CurrentModel;
    private OpenCodeSessionInfo? CurrentSessionInfo;
    private const string UnknownAgentText="未知智能体";
    private void UpdateControls(){}private void SetStatus(string s){}private Task ReloadAsync(AgentSwitchOrigin o,bool surfaceProtection)=>Task.CompletedTask;
    private void HideRecoveryConfirm(){}private void SyncRecoveryBar(){}private void UpdateStatusVisibility(){}

    internal static void CheckActual(AIFrontend.Pages.OpenCodePage p)
    {
        var checks=0;void Check(bool ok,string label){checks++;if(!ok)throw new Exception(label);}
        var selector=new OpenCodeAgentSelector();OpenCodeAgentPresentation.WrapName(selector._currentText);
        var data=new OpenCodeAgentInfo[]{new(){Id="A",Name=AgentPresentationTests.LongName,Color="#abcdef",Description="A full description"},new(){Id="B",Name="B full name",Description="B description"},new(){Id="H",Name="Hidden",Hidden=true},new(){Id="S",Name="Sub",Mode="subagent"}};
        selector.Configure(p.SourceInstance,p.SourceInstance.Revision,"ses_test",1);
        selector.ApplyAgents(data,"A");
        selector.CurrentModel = new() { Id = "gpt-real", ProviderId = "provider", Variant = "opaque/V2" };
        selector.CurrentSessionInfo = new() { Id="ses_test", Agent="A", Model=selector.CurrentModel };
        Check(selector._rows.Count==2&&selector._rows[0].Color=="#abcdef","actual normal projection keeps Color, omits hidden/subagent");
        Check(selector._currentText.Text==AgentPresentationTests.LongName&&selector._currentText.Foreground is SolidColorBrush{Color:{R:0xab,G:0xcd,B:0xef}},"normal actual current name and RGB retained");
        Check(Equals(selector._currentText.Attached["Tooltip"],"A full description")&&Equals(selector._currentText.Attached["Help"],"A full description"),"normal current description retained");
        var row=OpenCodeAgentPickerDialog.ActualRow(selector._rows[0]);var grid=(Grid)row.Content!;var text=grid.Children[1] as TextBlock;
        Check(text is{Text:AgentPresentationTests.LongName,TextWrapping:TextWrapping.NoWrap,TextTrimming:TextTrimming.CharacterEllipsis}
            && grid.Children[0] is FontIcon{Glyph:OpenCodeAgentIcons.DefaultGlyph},"compact native agent row has only icon/name; unknown agents use default glyph");
        var origin=selector._currentOrigin!;var mutations=p.HttpMutations;
        selector.SelectPendingAgent(origin,"B");
        Check(selector.CurrentAgentId=="B"&&selector._currentAgentId=="A"&&selector.HasPendingSelection&&p.HttpMutations==mutations,"AS3 selecting changes UI only, with no server mutation");
        selector.ApplyAgents(data,"A");
        Check(selector.CurrentAgentId=="B"&&selector._currentText.Text=="B full name","server display reload does not erase pending next-message choice");
        selector.SelectPendingAgent(origin,"S");Check(selector.CurrentAgentId=="B","subagent-only rows cannot become pending choices");
        selector.Configure(p.SourceInstance,p.SourceInstance.Revision,"ses_test",2);
        Check(selector.CurrentModel is { Id: "gpt-real", Variant: "opaque/V2" }, "same-session generation keeps the same observed model used by sidebar/status");
        Check(selector.CurrentSessionInfo is {Id:"ses_test",Agent:"A"},"same-session generation keeps exact observed session metadata");
        Check(selector.CurrentAgentId=="B"&&selector.HasPendingSelection&&selector._currentText.Text=="B full name","same-session generation retains pending next-message agent");
        selector.SelectPendingAgent(origin,"A");Check(selector.CurrentAgentId=="B","stale picker origin cannot replace pending choice");
        selector.ApplyAgents(data,"B");Check(selector._currentText.Text=="B full name"&&selector._currentText.Foreground is null,"normal B removes old A RGB");
        Check(!selector.HasPendingSelection,"only observing selected agent on server consumes pending choice");
        selector.CurrentSessionInfo = new() {Id="ses_test",ParentId="ses_parent",Agent="B"};
        selector.SelectPendingAgent(selector._currentOrigin!,"A");
        Check(!selector.HasPendingSelection&&selector.CurrentAgentId=="B"&&selector.SwitchBlockedReason()=="子智能体会话仅供查看。","SA2 child session blocks picker selection, not merely button visuals");
        selector.ApplyAgents(data,"A");Check(selector._currentText.Foreground is SolidColorBrush{Color:{B:0xef}},"normal A-B-A reapplies custom RGB");
        selector.ResetUi();Check(selector._currentText.Text==""&&selector._currentText.Foreground is null&&Equals(selector._currentText.Attached["Help"],""),"real ResetUi clears previous identity/color/description together");
        Check(selector.CurrentModel is null, "reset clears observed sidebar/status model, never carries it into another session");
        Check(selector.CurrentSessionInfo is null,"reset clears child/root metadata with observed identity");
        selector.Configure(p.SourceInstance,p.SourceInstance.Revision,"ses_other",3);Check(selector._rows.Count==0&&selector._currentText.Foreground is null,"different actual session origin never keeps old rows/RGB");
        foreach(var invalid in new string?[]{null,"accent","#abc","#abcdef00","bad","# 12345","#12345 "}){OpenCodeAgentPresentation.Apply(selector._currentText,"#123456");OpenCodeAgentPresentation.Apply(selector._currentText,invalid);Check(selector._currentText.Foreground is null,"invalid/unset has no local brush, dynamic style owns accent");}
        Console.WriteLine($"PASS R1 actual normal ApplyAgents/RowItem/Configure/Reset: {checks} assertions; network reload and native theme/measure are boundaries");
    }
}
internal static partial class OpenCodeAgentPickerDialog
{internal static ListViewItem ActualRow(OpenCodeAgentPickerRow row)=>RowItem(row,true);}
