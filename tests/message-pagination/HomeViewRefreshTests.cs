using System;
using System.Linq;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

internal static partial class Program
{
    private static void HomeViewRefreshRegressions(Action<bool, string>? check = null)
    {
        check ??= Check;
        var project = new OpenCodeHomeProjectKey("home-source", "/project/worktree");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        OpenCodeHomeSession Session(string id, double updated, OpenCodeHomeStatus? status = null)
            => new(project, new() { Id = id, Title = id, Location = new() { Directory = project.Directory }, Time = new() { Created = 1, Updated = updated } }, status);
        OpenCodeHomeSnapshot Snapshot(long revision, params OpenCodeHomeSession[] sessions)
            => new(revision, [new(project, "project")], sessions);

        {
            var view = new OpenCodeHomeView();
            var active = Session("ses_running", now, new(Working: true));
            view.TestSnapshot(Snapshot(1, active));
            var owner = view.TestItem("ses_running"); var grid = (Grid)owner.Content!;
            var badges = (Grid)grid.Children[0]; var host = (ContentControl)badges.Children[0];
            var ring = host.Content; var title = grid.Children[1]; var time = (TextBlock)grid.Children[3];
            view.TestSnapshot(Snapshot(2, active with { Info = active.Info with { Title = "live title", Time = new() { Created = 1, Updated = now + 1 } } }));
            check(ReferenceEquals(view.TestItem("ses_running"), owner) && ReferenceEquals(owner.Content, grid)
                && ReferenceEquals(((ContentControl)((Grid)grid.Children[0]).Children[0]).Content, ring)
                && ReferenceEquals(grid.Children[1], title) && ReferenceEquals(grid.Children[3], time),
                "home.row-identity: live DTO refresh retains the container/grid/labels/feedback host and running ring");
            check(((TextBlock)title).Text == "live title" && time.Text == "进行中", "home.row-live-values: retained native labels still update");
            view.TestSnapshot(Snapshot(3, active with { Status = new(Working: false, IsUnread: true, Outcome: "succeeded") { CompletedAt = now } }));
            check(ReferenceEquals(owner.Content, grid) && badges.Children[1].Visibility == Visibility.Visible
                && ((FrameworkElement)host.Content!).Visibility == Visibility.Collapsed && time.Text != "进行中",
                "home.row-terminal: completion updates the same row to one unread dot instead of a frozen ring/label");
        }
        {
            var view = new OpenCodeHomeView();
            var a = Session("ses_A", now); var b = Session("ses_B", now - 1);
            view.TestSnapshot(Snapshot(1, a, b)); view.TestOffset = 80;
            var owner = view.TestItem("ses_B"); owner.Focus(FocusState.Keyboard);
            var top = view.TestTop(owner);
            var attention = b with { Status = new(Working: false, IsUnread: true, Outcome: "succeeded") { CompletedAt = now } };
            view.TestSnapshot(Snapshot(2, a, attention));
            check(ReferenceEquals(view.TestItem("ses_B"), owner) && ReferenceEquals(FocusManager.Current, owner)
                && owner.FocusState == FocusState.Keyboard,
                "home.row-focus: moving a newly completed attention row restores its exact native keyboard owner");
            check(Math.Abs(view.TestTop(owner) - top) < 0.01,
                "home.viewport-anchor: date/attention reordering preserves the first visible container's viewport position");
        }
        {
            var view = new OpenCodeHomeView(); var a = Session("ses_menu", now);
            var expectedRevision = 1L; OpenCodeHomeSessionActionEventArgs? requested = null;
            view.TestActions((revision, session, action) => revision == expectedRevision && session.Project == project && session.Info.Id == "ses_menu",
                args => requested = args);
            view.TestSnapshot(Snapshot(1, a));
            var owner = view.TestItem("ses_menu"); var originalGrid = owner.Content; owner.Focus(FocusState.Keyboard);
            var menu = owner.ContextFlyout!; menu.Show(); var popupFocus = FocusManager.Current;
            var itemCount = view.TestItems;
            expectedRevision = 2;
            var latest = a with { Info = a.Info with { Title = "latest metadata" } };
            view.TestSnapshot(Snapshot(2, latest, Session("ses_new", now + 1)));
            check(menu.IsOpen && view.TestItems == itemCount && ReferenceEquals(originalGrid, owner.Content)
                && ReferenceEquals(FocusManager.Current, popupFocus),
                "home.open-menu: live metadata/badges update without moving/replacing its native owner or stealing popup focus");
            menu.Commands[0]!.Execute();
            check(requested is { Revision: 2 } && requested.Session.Info.Title == "latest metadata",
                "home.menu-revision: an already open command uses current row metadata/revision after cosmetic refresh");
            menu.Hide(); view.DispatcherQueue.Drain();
            check(view.TestItems > itemCount && ReferenceEquals(view.TestItem("ses_menu"), owner),
                "home.menu-close: deferred membership reconciliation catches up using the same owner containers");
        }
        {
            var view = new OpenCodeHomeView(); var root = Session("ses_protected", now, new(Working: true) { QuestionPending = true });
            view.TestSnapshot(Snapshot(1, root));
            check(view.TestProjectWorking(project), "home.project-ring: a question/protection glyph does not erase the independent project working fact");
            var layouts = view.TestLayouts; view.TestCalendarRollover();
            check(view.TestLayouts > layouts, "home.calendar: a local day rollover reprojects calendar/time groups without any server event");
        }
        {
            var view = new OpenCodeHomeView(); var root = Session("ses_move", now);
            view.TestSnapshot(Snapshot(1, root));
            var owner = view.TestItem("ses_move"); var grid = owner.Content;
            var movedProject = new OpenCodeHomeProjectKey(project.SourceId, "/other-project/worktree");
            var moved = root with { Project = movedProject, Info = root.Info with { Location = new() { Directory = movedProject.Directory } } };
            view.TestSnapshot(new(2, [new(project, "project"), new(movedProject, "other")], [moved]));
            check(ReferenceEquals(view.TestItem("ses_move"), owner) && ReferenceEquals(owner.Content, grid)
                && owner.Tag is OpenCodeHomeSessionRow current && current.Session.Project == movedProject,
                "home.moved-row-identity: a root moving directories keeps its source/session container while updating its exact project identity");
        }
        {
            var view = new OpenCodeHomeView(); var root = Session("ses_project", now);
            view.TestSnapshot(Snapshot(1, root)); view.TestSelectProject(project);
            var owner = view.TestProjectItem(project); var grid = owner.Content; owner.Focus(FocusState.Keyboard);
            var added = new OpenCodeHomeProjectKey(project.SourceId, "/added-project/worktree");
            view.TestSnapshot(new(2, [new(project, "updated project"), new(added, "added")], [root]));
            check(ReferenceEquals(view.TestProjectItem(project), owner) && ReferenceEquals(owner.Content, grid)
                && view.TestProjectSelection(project, owner) && ReferenceEquals(FocusManager.Current, owner),
                "home.project-identity: adding/renaming projects retains the selected native project row, ring/count hosts and keyboard focus");
        }
    }
}
