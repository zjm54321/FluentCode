using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages;

// Design B2: one list of terminals (persistent PTYs + agent commands) shared by the right-panel strip and the
// overlay side list. Lifecycle of each kind stays in OpenCodePage.PersistentPty.cs / OpenCodePage.TerminalView.cs.
public sealed partial class OpenCodePage
{
    private void InitializeTerminalSwitcher()
    {
        _persistentPtyInventoryView.CloseRequested += () => CloseTerminalOverlay();
        _persistentPtyInventoryView.ShellRequested += OpenTerminalView;
        _persistentPtyInventoryView.EndRequested += EndTerminalEntry;
    }

    /// <summary>× on a strip chip or list row: a running PTY is confirmed in the overlay header; an exited one is removed.</summary>
    private void EndTerminalEntry(OpenCodeTerminalEntry entry)
    {
        if (entry.Kind != OpenCodeTerminalEntryKind.Pty || _persistentInventoryOwner is not { } inventory) return;
        if (entry.State is OpenCodeTerminalState.Done or OpenCodeTerminalState.Failed)
        {
            if (_persistentAttachmentOwner is { } shown && shown.Context.PtyId == entry.Id) { shown.View.RequestEnd(); return; }
            _ = RemoveExitedPersistentPtyAsync(inventory, entry.Id);
            return;
        }
        if (_persistentAttachmentOwner?.Context.PtyId != entry.Id) OpenPersistentPty(inventory, entry.Id);
        if (_persistentAttachmentOwner is { } owner && owner.Context.PtyId == entry.Id) owner.View.RequestEnd();
    }

    private async Task RemoveExitedPersistentPtyAsync(PersistentInventoryOwner owner, string ptyId)
    {
        if (!IsPersistentInventoryCurrent(owner.Origin)) return;
        try
        {
            using var client = owner.Origin.Source.CreateClient();
            await client.RemovePersistentPtyAsync(owner.Origin.SessionId, ptyId, owner.Origin.Source.RevocationToken);
        }
        catch (PersistentPtyException error) when (error.Code == "not-found") { }
        catch (Exception) { if (IsPersistentInventoryCurrent(owner.Origin)) _persistentPtyInventoryView.SetCreationState(false, "移除终端失败"); return; }
        if (!IsPersistentInventoryCurrent(owner.Origin)) return;
        owner.Inventory.AcceptRemoved(ptyId);
        PaintPersistentInventory(owner);
        QueuePersistentPtyInventoryRefresh();
    }

    /// <summary>Running background agent commands, plus the agent command currently shown in the overlay.</summary>
    private void SyncShellTerminalEntries()
    {
        var entries = new List<OpenCodeTerminalEntry>();
        foreach (var shell in _treeShells.Values.Where(shell => shell.Id != _terminalState.ShellId && shell.Kind == OpenCodeTreeShellKind.Background && shell.Status == OpenCodeTreeStatus.Running))
            entries.Add(new(OpenCodeTerminalEntryKind.Shell, shell.Id, shell.Command, "后台命令", shell.Cwd, OpenCodeTerminalState.Running, "运行中", false));
        if (_terminalState.ShellId is { } id && entries.All(entry => entry.Id != id))
        {
            var state = _terminalState.Running ? OpenCodeTerminalState.Running : _terminalState.Failed ? OpenCodeTerminalState.Failed : OpenCodeTerminalState.Done;
            entries.Add(new(OpenCodeTerminalEntryKind.Shell, id, _terminalState.Command, "智能体命令", _terminalState.Cwd, state, _terminalState.StatusText, false));
        }
        _persistentPtyInventoryView.SetShellEntries(entries);
    }
}
