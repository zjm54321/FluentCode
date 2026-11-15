using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

internal static partial class Program
{
    private static async Task PendingDeliveryRegressions()
    {
        var failures = new List<string>();
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("pending inbox round trip", DeliveryRoundTrip),
            ("always-visible steer switch beside the bubble", SteerSwitchControl),
            ("another client's message switches both ways", ForeignDeliverySwitch),
            ("right-click entries of a pending bubble", SteerMenuEntries),
            ("always-visible steer switch in the queue row", QueueSwitchControl),
            ("right-click entries of a queued row", QueueMenuEntries),
            ("always-visible withdraw icon in the queue row", QueueWithdrawControl),
            ("queue header shows link labels, not addresses", QueueHeaderLinks),
            ("touch reveal helper", TouchRevealHelper),
            ("receipt-projected steer switch", ReceiptProjectedSwitch),
            ("empty inbox retires uncertainty", EmptyInboxRetiresUncertainty),
            ("unchanged 204 is visibly unconfirmed", UnchangedDeliveryIsUnconfirmed),
            ("404 reconciles unavailable item", DeliveryNotFound),
            ("409 reports refusal without retry", DeliveryConflict),
            ("no silent guard drops", DeliveryGuardFeedback),
            ("delivery binding/source fences", DeliveryBindingFences),
        })
        {
            try { await run(); Console.WriteLine("PASS delivery regression: " + name); }
            catch (Exception error) { failures.Add(name); Console.WriteLine("FAIL delivery regression: " + name + ": " + error.Message); }
        }
        Check(failures.Count == 0, "pending-delivery regressions failed: " + string.Join(", ", failures));
    }

    // owned: false = nothing was sent, admitted or acknowledged by this page (another client's message, or this
    // client's own after a restart or session switch). width = transcript width the view reports before any row exists.
    private static async Task<OpenCodePage> DeliveryPage(FakeServer server, OpenCodeClient client, bool owned = true, double width = 0)
    {
        var page = new OpenCodePage(client, Session);
        page.Conversation.ActualWidth = width;
        if (owned) page.Own(Inbox);
        page.Publish(await client.GetInboxAsync(Session, CancellationToken.None));
        server.Calls.Clear(); server.Queries.Clear(); server.Bodies.Clear(); server.Auth.Clear();
        return page;
    }

    private static async Task DeliveryRoundTrip()
    {
        var server = new FakeServer();
        server.Inbox.Add(UserItem(Inbox, "反复切换", 1000));
        using var client = server.Client();
        var page = await DeliveryPage(server, client);
        foreach (var delivery in new[] { "steer", "queue", "steer" })
        {
            await page.Change(Inbox, delivery);
            Check(page.InboxSnapshot?.Single().Delivery == delivery, "UI publishes re-read mode " + delivery);
            Check(page.UncertainChanges == 0 && page.CanChange(page.InboxSnapshot!.Single()), "confirmed mode allows the next switch");
            Check(page.Queue.HasItems == (delivery == "queue") && page.Conversation.PendingIds.Contains(Inbox) == (delivery == "steer"), "same id moves between queue row and steer bubble");
        }
        Check(server.Calls.Count(c => c.StartsWith("PATCH")) == 3, "one PATCH per explicit switch");
        Check(server.Bodies.Where(b => b is not null).SequenceEqual(new[] { "{\"delivery\":\"steer\"}", "{\"delivery\":\"queue\"}", "{\"delivery\":\"steer\"}" }), "both delivery directions use the exact API contract");
    }

    private static Button? SteerSwitch(OpenCodePage page) => Descendants(page.Conversation).OfType<Button>()
        .SingleOrDefault(b => AutomationProperties.GetAutomationId(b) == "OpenCodeAdmittedSteerQueue." + Inbox);
    private static Button? QueueButton(OpenCodePage page, string prefix) => Descendants(page.Queue).OfType<Button>()
        .SingleOrDefault(b => AutomationProperties.GetAutomationId(b) == prefix + Inbox);
    private static int Patches(FakeServer server) => server.Calls.Count(c => c.StartsWith("PATCH"));

    private static async Task SteerSwitchControl()
    {
        var text = new string('长', 200);
        var server = new FakeServer();
        server.Inbox.Add(UserItem(Inbox, text, 1000) with { Delivery = "steer" });
        using var client = server.Client();
        var page = await DeliveryPage(server, client, width: 1000);
        var row = (ContentControl)page.Conversation.Children.Single();
        var layout = (Grid)row.Content!;
        var button = SteerSwitch(page);
        Check(button is not null, "pending bubble has a native queue button, not just a context menu");
        Check(button!.Content is FontIcon { Glyph: "\uE823", FontSize: 16 } && button.Width == 32 && button.Height == 32
            && button.Padding == new Thickness(0) && button.Style?.Key == "SubtleButtonStyle",
            "icon only: the composer's queue clock at its 32px size, in the stock borderless subtle-button style");
        Check(button.AutomationName == "改为排队：" + text && button.ToolTip as string == "改为排队",
            "tooltip is the plain action name; the Narrator name adds which message it moves");
        Check(!Descendants(row).OfType<TextBlock>().Any(t => t.Text is "改为排队" or "插入当前回合"),
            "no caption or text button is left under the bubble");
        var bubble = (FrameworkElement)layout.Children[1];
        Check(layout.Children.Count == 2 && ReferenceEquals(layout.Children[0], button)
            && Grid.GetColumn(button) == 0 && Grid.GetColumn(bubble) == 1
            && layout.ColumnDefinitions.Select(c => c.Width).SequenceEqual(new[] { new GridLength(1, GridUnitType.Star), GridLength.Auto })
            && button.HorizontalAlignment == HorizontalAlignment.Right && button.VerticalAlignment == VerticalAlignment.Top,
            "the switch takes the free space left of the bubble, in the bubble's own row: nothing is stacked below it");
        Check(bubble.MaxWidth == 720, "the bubble starts at its usual 72% of the transcript, so the auto column never measures a long message on one line");
        Check(button.IsTabStop && button.Visibility == Visibility.Visible && button.Opacity == 1 && button.IsHitTestVisible,
            "visible and keyboard reachable without hover, focus or touch");
        row.Touch(); row.MouseExit(); row.Focus(); row.Blur();
        Check(button.Visibility == Visibility.Visible && button.Opacity == 1 && button.IsHitTestVisible, "pointer and focus changes neither hide nor reveal it");
        Check(!Ancestors(button).OfType<ContentControl>().Any(parent => parent.MaxHeight == 40), "long-message collapse does not clip the switching control");
        page.Queue.IsWithdrawBusy = true;
        Check(!button.IsEnabled && button.Visibility == Visibility.Visible, "a running inbox action disables the switch in place");
        page.Queue.IsWithdrawBusy = false;
        Check(button.IsEnabled, "the switch is usable again once the action ends");
        await page.Click(button);
        Check(page.Queue.HasItems && page.Conversation.PendingIds.Count == 0, "visible button routes into real delivery handler");

        var unsized = new FakeServer { PatchChangesDelivery = false };
        unsized.Inbox.Add(UserItem(Inbox, "未确认", 1000) with { Delivery = "steer" });
        using var unsizedClient = unsized.Client();
        var early = await DeliveryPage(unsized, unsizedClient);
        var earlyLayout = (Grid)((ContentControl)early.Conversation.Children.Single()).Content!;
        Check(((FrameworkElement)earlyLayout.Children[1]).MaxWidth is > 0 and < 480, "a transcript that was never laid out still gives the bubble a finite starting width");
        await early.Click(SteerSwitch(early)!);
        Check(early.UncertainChanges == 1 && early.Conversation.PendingIds.Contains(Inbox)
            && SteerSwitch(early)!.Visibility == Visibility.Collapsed && earlyLayout.Children.Count == 2,
            "an unconfirmed switch hides only the button; the bubble keeps its place in the row");
    }

    private static async Task ForeignDeliverySwitch()
    {
        var server = new FakeServer();
        server.Inbox.Add(UserItem(Inbox, "其他客户端排队", 1000));
        server.Inbox.Add(new Item("msg_synthetic", "synthetic", "{}", 900));
        using var client = server.Client();
        var page = await DeliveryPage(server, client, owned: false);
        var listed = page.InboxSnapshot!.Single(item => item.Id == Inbox);
        Check(page.CanChange(listed) && !page.CanWithdraw(listed), "a server-listed user input is switchable with no local send record; withdrawal stays with its sender");
        Check(!page.CanChange(page.InboxSnapshot!.Single(item => item.Id == "msg_synthetic")), "server-generated inbox work is still not switchable");
        var toSteer = QueueButton(page, "OpenCodePendingDelivery.");
        Check(toSteer is not null && QueueButton(page, "OpenCodeQueuedPromptWithdraw.") is null, "another client's queued row offers the switch and no 撤回");
        await page.Click(toSteer!);
        Check(Patches(server) == 1 && server.Bodies.Last(b => b is not null) == "{\"delivery\":\"steer\"}"
            && page.Conversation.PendingIds.Contains(Inbox) && !page.Queue.HasItems,
            "queued by another client -> 插入当前回合 with one PATCH, confirmed by the re-read");
        Check(server.Calls.First().StartsWith("GET") && server.Calls.Last().StartsWith("GET"), "the server's inbox is read before and after the PATCH");
        var toQueue = SteerSwitch(page);
        Check(toQueue is { IsEnabled: true } && toQueue.Visibility == Visibility.Visible, "another client's pending bubble offers the queue button");
        await page.Click(toQueue!);
        Check(Patches(server) == 2 && server.Bodies.Last(b => b is not null) == "{\"delivery\":\"queue\"}"
            && page.Queue.HasItems && page.Conversation.PendingIds.Count == 0,
            "steered by another client -> 排队 with one PATCH, confirmed by the re-read");
        Check(page.Notices.Count == 0 && page.UncertainChanges == 0, "no refusal, warning or leftover uncertainty for a message this client did not send");
        await page.Change("msg_synthetic", "steer");
        Check(Patches(server) == 2 && page.Notices.Any(n => n.Contains("不是用户输入")), "a non-user item is refused after the fresh read, without a PATCH");
    }

    private static async Task SteerMenuEntries()
    {
        var server = new FakeServer();
        server.Inbox.Add(UserItem(Inbox, "右键切换", 1000) with { Delivery = "steer" });
        using var client = server.Client();
        var foreign = await DeliveryPage(server, client, owned: false);
        var menu = foreign.Conversation.Children.Single().ContextFlyout as OpenCodeMessageMenuProbe;
        Check(menu is not null && ReferenceEquals(menu, foreign.Conversation.TextMenu(Inbox)) && menu.Text() == "右键切换",
            "right-click on the bubble and on its text open the one menu, which still copies the message");
        var entries = menu!.Actions().ToArray();
        Check(entries.Length == 1 && entries[0] is { Label: "改为排队", Glyph: "\uE823", Enabled: true }
            && entries[0].AutomationId == "OpenCodeAdmittedSteerDelivery." + Inbox,
            "another client's pending message: the menu offers 改为排队 under the button's icon, and no 撤回 it could not honour");
        await foreign.Run(entries[0].Run);
        Check(Patches(server) == 1 && foreign.Queue.HasItems && foreign.Conversation.PendingIds.Count == 0, "the menu entry runs the same delivery handler as the button");

        var ownServer = new FakeServer();
        ownServer.Inbox.Add(UserItem(Inbox, "本页发送", 1000) with { Delivery = "steer" });
        using var ownClient = ownServer.Client();
        var own = await DeliveryPage(ownServer, ownClient);
        var ownMenu = (OpenCodeMessageMenuProbe)own.Conversation.Children.Single().ContextFlyout!;
        Check(ownMenu.Actions().Select(e => e.Label + ":" + e.Enabled).SequenceEqual(new[] { "改为排队:True", "撤回:True" }),
            "this page's own pending message keeps both entries");
        own.Queue.IsWithdrawBusy = true;
        Check(ownMenu.Actions().Select(e => e.Label + ":" + e.Enabled).SequenceEqual(new[] { "改为排队:False", "撤回:False" }),
            "entries read the busy state when the menu opens");
    }

    private static Grid QueueRow(OpenCodePage page, string id) => Descendants(page.Queue).OfType<Grid>()
        .Single(g => AutomationProperties.GetAutomationId(g) == "OpenCodeQueuedPrompt." + id);
    private static bool Shown(Button button) => button.Visibility == Visibility.Visible && button.Opacity == 1 && button.IsHitTestVisible;

    private static async Task QueueSwitchControl()
    {
        var text = "排队中的第一条";
        var server = new FakeServer();
        server.Inbox.Add(UserItem(Inbox, text, 1000));
        server.Inbox.Add(UserItem("msg_other", "排队中的第二条", 1100));
        using var client = server.Client();
        var page = await DeliveryPage(server, client);
        var row = QueueRow(page, Inbox);
        var button = QueueButton(page, "OpenCodePendingDelivery.");
        Check(button is not null && ReferenceEquals(button!.Parent, row), "a queued row keeps its switch under the existing AutomationId");
        Check(button!.Content is FontIcon { Glyph: "\uE724", FontSize: 16 } && button.Width == 32 && button.Height == 32
            && button.Padding == new Thickness(0) && button.Style?.Key == "SubtleButtonStyle",
            "icon only: the composer's send glyph, in the same 32px borderless subtle button as the bubble's queue clock");
        var clock = "\uE823";
        Check(((FontIcon)button.Content!).Glyph != clock, "the two directions use different icons");
        Check(button.AutomationName == "改为插入当前回合：" + text && button.ToolTip as string == "改为插入当前回合",
            "tooltip is the plain action name; the Narrator name adds which queued message it moves");
        var second = Descendants(page.Queue).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "OpenCodePendingDelivery.msg_other");
        Check(second.AutomationName == "改为插入当前回合：排队中的第二条" && second.AutomationName != button.AutomationName,
            "several queued entries have distinguishable names");
        Check(!Descendants(row).OfType<TextBlock>().Any(t => t.Text is "改为插入当前回合" or "当前回合结束后发送")
            && !Descendants(row).OfType<Button>().Any(b => b.Content is string label && label.Contains("插入")),
            "no text button or caption is left for the switch");
        Check(Grid.GetColumn(button) == 3 && row.ColumnDefinitions[3].Width == new GridLength(32)
            && row.ColumnDefinitions[1].Width == new GridLength(1, GridUnitType.Star) && row.ColumnDefinitions.Count == 4
            && button.VerticalAlignment == VerticalAlignment.Center,
            "the switch has a fixed 32px slot at the end of the row, centred on it");
        Check(button.IsTabStop && Shown(button), "visible and keyboard reachable without hover, focus or touch");
        row.Touch(); row.MouseExit(); row.Focus(); row.Blur();
        Check(Shown(button), "pointer and focus changes neither hide nor reveal the switch");
        var requests = 0;
        page.Queue.DeliveryChangeRequested += (_, _) => requests++;
        page.Queue.IsWithdrawBusy = true;
        Check(!button.IsEnabled && !second.IsEnabled && Shown(button), "a running inbox action disables every switch in place");
        button.RaiseClick();
        Check(requests == 0, "a disabled switch requests nothing");
        page.Queue.IsWithdrawBusy = false;
        Check(button.IsEnabled && second.IsEnabled, "the switches are usable again once the action ends");
        await page.Click(button);
        Check(Patches(server) == 1 && server.Bodies.Last(b => b is not null) == "{\"delivery\":\"steer\"}"
            && requests == 1 && page.Conversation.PendingIds.SequenceEqual(new[] { Inbox }) && page.Queue.HasItems,
            "the icon routes this entry, and only this entry, into the real delivery handler");
        Check(Descendants(page.Queue).OfType<Button>().Count(b => AutomationProperties.GetAutomationId(b).StartsWith("OpenCodePendingDelivery.")) == 1,
            "the other entry stays queued with its own switch");

        var attachmentOnly = new FakeServer();
        attachmentOnly.Inbox.Add(UserItem(Inbox, "", 1000));
        using var attachmentClient = attachmentOnly.Client();
        var bare = await DeliveryPage(attachmentOnly, attachmentClient);
        Check(QueueButton(bare, "OpenCodePendingDelivery.")?.AutomationName == "改为插入当前回合：附件消息", "an entry without text is still named");

        var stuck = new FakeServer { PatchChangesDelivery = false };
        stuck.Inbox.Add(UserItem(Inbox, "未确认", 1000));
        using var stuckClient = stuck.Client();
        var unconfirmed = await DeliveryPage(stuck, stuckClient);
        var before = QueueRow(unconfirmed, Inbox);
        var widths = before.ColumnDefinitions.Select(c => c.Width).ToArray();
        await unconfirmed.Click(QueueButton(unconfirmed, "OpenCodePendingDelivery.")!);
        var after = QueueRow(unconfirmed, Inbox);
        Check(unconfirmed.UncertainChanges == 1 && QueueButton(unconfirmed, "OpenCodePendingDelivery.") is null,
            "an unconfirmed switch removes the button: the page's server-state rule decides, not the view");
        Check(after.ColumnDefinitions.Select(c => c.Width).SequenceEqual(widths)
            && !after.Children.Any(child => Grid.GetColumn(child) == 3),
            "the slot stays reserved and empty, so the message keeps its width when the switch goes away");
    }

    private static async Task QueueWithdrawControl()
    {
        var text = "本页发出";
        var server = new FakeServer();
        server.Inbox.Add(UserItem(Inbox, text, 1000));
        server.Inbox.Add(UserItem("msg_other", "其他客户端发出", 1100));
        using var client = server.Client();
        var page = await DeliveryPage(server, client);
        var row = QueueRow(page, Inbox);
        var withdraw = QueueButton(page, "OpenCodeQueuedPromptWithdraw.");
        var change = QueueButton(page, "OpenCodePendingDelivery.")!;
        Check(withdraw is not null && ReferenceEquals(page.Queue.WithdrawButton(Inbox), withdraw) && ReferenceEquals(withdraw!.Parent, row),
            "this page's own entry keeps 撤回 under the existing AutomationId, where the page looks it up for its feedback");
        Check(withdraw!.Content is FontIcon { Glyph: "\uE7A7", FontSize: 16 } && withdraw.Width == 32 && withdraw.Height == 32
            && withdraw.Padding == new Thickness(0) && withdraw.Style?.Key == "SubtleButtonStyle",
            "icon only: the app's 撤回 arrow in the same 32px borderless button as the switch");
        Check(withdraw.AutomationName == "撤回待投递消息：" + text && withdraw.ToolTip as string == "撤回", "tooltip and Narrator name are unchanged");
        Check(!Descendants(row).OfType<Button>().Any(b => b.Content is string) && !Descendants(row).OfType<TextBlock>().Any(t => t.Text == "撤回"),
            "no worded button is left in the row");
        Check(Grid.GetColumn(withdraw) == 2 && Grid.GetColumn(change) == 3 && row.ColumnSpacing == 4
            && row.ColumnDefinitions.Select(c => c.Width).SequenceEqual(new[] { new GridLength(24), new GridLength(1, GridUnitType.Star), new GridLength(32), new GridLength(32) })
            && withdraw.VerticalAlignment == VerticalAlignment.Center && change.VerticalAlignment == VerticalAlignment.Center,
            "number, message, 撤回, switch: the two icons sit side by side in fixed slots, centred on the row");
        Check(withdraw.IsTabStop && Shown(withdraw), "visible and reachable at rest: there is no hidden action for a finger to hit");
        row.Touch(); row.MouseExit(); row.Focus(); row.Blur();
        Check(Shown(withdraw) && Shown(change), "pointer and focus changes neither hide nor reveal the row's actions");

        var other = QueueRow(page, "msg_other");
        Check(!Descendants(other).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b).StartsWith("OpenCodeQueuedPromptWithdraw."))
            && !other.Children.Any(child => Grid.GetColumn(child) == 2)
            && Grid.GetColumn(Descendants(other).OfType<Button>().Single()) == 3,
            "another client's entry gets no 撤回: that slot is empty and its switch still ends the row");
        Check(other.ColumnDefinitions.Select(c => c.Width).SequenceEqual(row.ColumnDefinitions.Select(c => c.Width)),
            "rows with and without 撤回 share one column layout, so the icons line up down the list");

        var requested = new List<string>();
        page.Queue.WithdrawRequested += (_, id) => requested.Add(id);
        page.Queue.IsWithdrawBusy = true;
        Check(!withdraw.IsEnabled && Shown(withdraw), "a running inbox action disables 撤回 in place");
        withdraw.RaiseClick();
        Check(requested.Count == 0, "a disabled 撤回 requests nothing");
        page.Queue.IsWithdrawBusy = false;
        withdraw.RaiseClick();
        Check(withdraw.IsEnabled && requested.SequenceEqual(new[] { Inbox }), "a click asks the page to withdraw exactly this entry");

        var menu = (OpenCodeMessageMenuProbe)row.ContextFlyout!;
        var entries = menu.Actions().ToArray();
        Check(entries.Select(e => e.Label + ":" + e.Glyph + ":" + e.Enabled).SequenceEqual(new[] { "改为插入当前回合:\uE724:True", "撤回:\uE7A7:True" })
            && entries[1].AutomationId == "OpenCodeQueuedPromptMenuWithdraw." + Inbox,
            "this page's own entry: the menu offers both actions under the buttons' icons");
        Check(((OpenCodeMessageMenuProbe)other.ContextFlyout!).Actions().Select(e => e.Label).SequenceEqual(new[] { "改为插入当前回合" }),
            "another client's entry: no 撤回 in the menu either");
        page.Queue.IsWithdrawBusy = true;
        Check(menu.Actions().All(e => !e.Enabled), "both entries read the busy state when the menu opens");
        entries[1].Run();
        Check(requested.Count == 1, "an entry of a menu opened earlier does nothing while an inbox action runs");
        page.Queue.IsWithdrawBusy = false;
        entries[1].Run();
        Check(requested.SequenceEqual(new[] { Inbox, Inbox }), "the menu entry raises the same withdraw request as the button");
        page.Publish(Array.Empty<OpenCodeInboxItem>());
        entries[1].Run();
        Check(requested.Count == 2 && menu.Actions().All(e => !e.Enabled), "a menu left over from a removed row is inert");
    }

    private static TextBlock QueueHeader(OpenCodePage page) => (TextBlock)Descendants(page.Queue).OfType<Expander>().Single().Header!;

    // The header previews the next message as one line of plain text, and the row below it shows chips. A link must
    // read the same in both: the header names it by the label its chip shows, and never spells out the address.
    private static async Task QueueHeaderLinks()
    {
        static string Label(string url) => OpenCodeLinkChipParser.Parse(url)!.Label; // what the row's chip shows for this address
        const string pull = "https://github.com/sst/opencode/pull/4821", generic = "https://blog.example.dev/posts/2026/rich-paste";
        const string prefix = "排队 1 条 · 下一条：";
        Check(Label(pull) == "opencode #4821" && Label(generic) == "blog.example.dev/…/rich-paste", "the labels the chips show for these addresses");
        foreach (var (text, expected, note) in new[]
        {
            ("看这个" + pull + "然后合并", "看这个 " + Label(pull) + " 然后合并", "Chinese text touching the link on both sides"),
            (pull, Label(pull), "a link alone"),
            ("看 " + pull + " 谢谢", "看 " + Label(pull) + " 谢谢", "a link that already has spaces around it"),
            ("（" + pull + "）。", "（" + Label(pull) + "）。", "punctuation around the link"),
            ("对比" + pull + "和" + generic, "对比 " + Label(pull) + " 和 " + Label(generic), "two links"),
            ("看 [文档](" + generic + ")", "看 文档 (" + Label(generic) + ")", "a Markdown link"),
            ("看https://user:hunter2@example.com/x?token=s3cr3t", "看 example.com/x", "a link that carries a password and a token"),
        })
        {
            var server = new FakeServer();
            server.Inbox.Add(UserItem(Inbox, text, 1000));
            using var client = server.Client();
            var page = await DeliveryPage(server, client);
            var header = QueueHeader(page);
            Check(header.Text == prefix + expected, note + ": the header shows \"" + header.Text + "\"");
            Check(!header.Text.Contains("://", StringComparison.Ordinal), note + ": no address is left in the header");
            Check(Descendants(QueueRow(page, Inbox)).OfType<TextBlock>().Any(t => t.Text == text),
                note + ": the row is still given the message as written (its renderer makes the chips)");
            Check(header.TextWrapping == TextWrapping.NoWrap && header.MaxLines == 1 && header.TextTrimming == TextTrimming.CharacterEllipsis,
                note + ": the header stays one line that ends in an ellipsis when it does not fit");
        }

        // Where the row keeps an address as text, the header keeps the same text: it never invents a label.
        foreach (var (text, note) in new[]
        {
            ("abc" + pull, "an address glued to a Latin word"),
            ("`" + pull + "`", "an address in inline code"),
            ("没有链接的消息", "a message without a link"),
        })
        {
            var server = new FakeServer();
            server.Inbox.Add(UserItem(Inbox, text, 1000));
            using var client = server.Client();
            Check(QueueHeader(await DeliveryPage(server, client)).Text == prefix + text, note + " is shown as written");
        }

        var queue = new FakeServer();
        queue.Inbox.Add(UserItem(Inbox, "先看" + pull, 1000));
        queue.Inbox.Add(UserItem("msg_other", "再看" + generic, 1100));
        using var queueClient = queue.Client();
        var two = await DeliveryPage(queue, queueClient);
        var title = QueueHeader(two);
        Check(title.Text == "排队 2 条 · 下一条：先看 " + Label(pull), "the header counts every queued message and previews the first: " + title.Text);
        queue.Inbox.RemoveAt(0);
        two.Publish(await queueClient.GetInboxAsync(Session, CancellationToken.None));
        Check(ReferenceEquals(QueueHeader(two), title) && title.Text == prefix + "再看 " + Label(generic),
            "when the first message leaves, the same header previews the next one: " + title.Text);
        two.Publish(Array.Empty<OpenCodeInboxItem>());
        Check(title.Text.Length == 0 && !two.Queue.HasItems, "an empty queue has no header text");

        var bare = new FakeServer();
        bare.Inbox.Add(UserItem(Inbox, "", 1000));
        using var bareClient = bare.Client();
        Check(QueueHeader(await DeliveryPage(bare, bareClient)).Text == prefix + "（仅附件）", "a message without text keeps its placeholder");
    }

    // The queue row no longer hides anything, but other views still do (browser strip, tool rows): keep the shared
    // helper's contract covered here, where its real source is compiled.
    private static Task TouchRevealHelper()
    {
        var host = new Grid();
        var states = new List<bool>();
        OpenCodeTouchInput.WireReveal(host, states.Add);
        Check(states.SequenceEqual(new[] { false }), "hover-revealed actions start hidden");
        host.MouseEnter();
        Check(states[^1], "mouse hover reveals");
        host.MouseExit();
        Check(!states[^1], "mouse exit hides");
        host.Touch();
        Check(states[^1], "the first touch reveals, so a finger never has to hit an invisible target");
        host.TouchExit();
        Check(states[^1], "lifting the finger keeps the actions");
        host.MouseExit();
        Check(!states[^1], "a later mouse exit returns to hover-only");
        host.Focus();
        Check(states[^1], "keyboard focus reveals");
        host.Blur();
        Check(!states[^1], "losing focus hides");
        return Task.CompletedTask;
    }

    private static async Task QueueMenuEntries()
    {
        var server = new FakeServer();
        server.Inbox.Add(UserItem(Inbox, "右键插入", 1000));
        using var client = server.Client();
        var foreign = await DeliveryPage(server, client, owned: false);
        var row = QueueRow(foreign, Inbox);
        var menu = row.ContextFlyout as OpenCodeMessageMenuProbe;
        Check(menu is not null && menu.Text() == "右键插入", "right-click on a queued row opens the shared message menu, which copies the message");
        Check(ReferenceEquals(Descendants(row).OfType<TextBlock>().Single(t => t.Text == "右键插入").ContextFlyout, menu),
            "right-click on the row's text opens the same menu");
        var entries = menu!.Actions().ToArray();
        Check(entries.Length == 1 && entries[0] is { Label: "改为插入当前回合", Glyph: "\uE724", Enabled: true }
            && entries[0].AutomationId == "OpenCodeQueuedPromptMenuDelivery." + Inbox,
            "the menu offers 改为插入当前回合 under the button's icon");
        var requests = 0;
        foreign.Queue.DeliveryChangeRequested += (_, _) => requests++;
        foreign.Queue.IsWithdrawBusy = true;
        Check(menu.Actions().Single() is { Enabled: false }, "the entry reads the busy state when the menu opens, like the button");
        entries[0].Run();
        Check(requests == 0, "an entry of a menu opened earlier does nothing while an inbox action runs");
        foreign.Queue.IsWithdrawBusy = false;
        await foreign.Run(entries[0].Run);
        Check(requests == 1 && Patches(server) == 1 && foreign.Conversation.PendingIds.Contains(Inbox) && !foreign.Queue.HasItems,
            "the menu entry runs the same delivery handler as the button");
        Check(menu.Actions().Single() is { Enabled: false }, "a menu left over from a replaced row is inert");
        entries[0].Run();
        Check(requests == 1, "and its entry requests nothing");

        var stuck = new FakeServer { PatchChangesDelivery = false };
        stuck.Inbox.Add(UserItem(Inbox, "未确认", 1000));
        using var stuckClient = stuck.Client();
        var blocked = await DeliveryPage(stuck, stuckClient);
        await blocked.Change(Inbox, "steer");
        var blockedMenu = (OpenCodeMessageMenuProbe)QueueRow(blocked, Inbox).ContextFlyout!;
        Check(QueueButton(blocked, "OpenCodePendingDelivery.") is null
            && !blockedMenu.Actions().Any(e => e.Label == "改为插入当前回合"),
            "an entry that cannot be switched offers the action neither as a button nor in the menu");
    }

    private static async Task ReceiptProjectedSwitch()
    {
        var server = new FakeServer();
        server.Inbox.Add(UserItem(Inbox, "收据先于列表", 1000) with { Delivery = "steer" });
        using var client = server.Client();
        var page = new OpenCodePage(client, Session);
        page.Admit(new() { Id = Inbox, SessionId = Session, Type = "user", Delivery = "steer", Payload = new() { Text = "收据先于列表" } });
        Check(page.Conversation.PendingIds.Contains(Inbox) && page.InboxSnapshot is null, "receipt projects a pending bubble before any inbox snapshot");
        await page.Change(Inbox, "queue");
        Check(server.Calls.Count(c => c.StartsWith("PATCH")) == 1 && page.Queue.HasItems, "a receipt-projected steer item can switch before any inbox snapshot lists it");
        Check(server.Calls.First().StartsWith("GET") && server.Calls.Last().StartsWith("GET"), "receipt switch verifies pending state before PATCH and publishes only the post-read");
        page.Publish(Array.Empty<OpenCodeInboxItem>(), trimAcknowledgements: true);
        server.Calls.Clear(); server.Queries.Clear(); server.Bodies.Clear(); server.Auth.Clear();
        await page.Change(Inbox, "steer");
        Check(page.Conversation.PendingIds.Contains(Inbox), "a racing empty snapshot does not block the switch: the fresh server read decides");
    }

    private static async Task EmptyInboxRetiresUncertainty()
    {
        var server = new FakeServer { Fail = c => c.StartsWith("PATCH") };
        server.Inbox.Add(UserItem(Inbox, "结果未知", 1000));
        using var client = server.Client();
        var page = await DeliveryPage(server, client);
        await page.Change(Inbox, "steer");
        Check(page.UncertainChanges == 1, "unconfirmed mutation remains blocked");
        page.Publish(Array.Empty<OpenCodeInboxItem>());
        Check(page.UncertainChanges == 0, "confirmed empty inbox is evidence, not a null/unread snapshot");
        Check(!page.Queue.HasItems && page.Conversation.PendingIds.Count == 0, "resolved absence leaves no actionable pending UI");
    }

    private static async Task UnchangedDeliveryIsUnconfirmed()
    {
        var server = new FakeServer { PatchChangesDelivery = false };
        server.Inbox.Add(UserItem(Inbox, "204 不是确认", 1000));
        using var client = server.Client();
        var page = await DeliveryPage(server, client);
        await page.Change(Inbox, "steer");
        Check(page.InboxSnapshot?.Single().Delivery == "queue" && page.UncertainChanges == 1, "204 never optimistically edits the mode");
        Check(page.Notices.Any(n => n.Contains("投递方式尚未确认")), "unchanged post-read warns instead of silently blocking reverse switches");
        var before = page.Notices.Count;
        await page.Change(Inbox, "steer");
        Check(page.Notices.Count > before && server.Calls.Count(c => c.StartsWith("PATCH")) == 1, "blocked repeated gesture explains uncertainty and does not retry");
        page.Publish(new[] { page.InboxSnapshot!.Single() with { Delivery = "steer" } });
        Check(page.UncertainChanges == 0 && page.CanChange(page.InboxSnapshot!.Single()), "later server confirmation restores switching");
    }

    private static async Task DeliveryNotFound()
    {
        var server = new FakeServer { PatchStatus = HttpStatusCode.NotFound, OnPatch = s => s.Inbox.Clear() };
        server.Inbox.Add(UserItem(Inbox, "已经不可用", 1000) with { Delivery = "steer" });
        using var client = server.Client();
        var page = await DeliveryPage(server, client);
        await page.Change(Inbox, "queue");
        Check(page.Notices.Any(n => n.Contains("不可用")), "404 identifies unavailable session/item rather than a generic refusal");
        Check(page.Conversation.PendingIds.Count == 0 && !page.Queue.HasItems && page.UncertainChanges == 0, "404 refresh removes unavailable controls");
        Check(server.Calls.Count(c => c.StartsWith("PATCH")) == 1 && server.Calls.Last().StartsWith("GET"), "404 re-reads, never retries PATCH");
    }

    private static async Task DeliveryConflict()
    {
        var server = new FakeServer { PatchStatus = HttpStatusCode.Conflict };
        server.Inbox.Add(UserItem(Inbox, "服务端冲突", 1000));
        using var client = server.Client();
        var page = await DeliveryPage(server, client);
        await page.Change(Inbox, "steer");
        Check(page.Notices.Any(n => n.Contains("服务端拒绝")), "409 plainly reports refusal");
        Check(page.InboxSnapshot?.Single().Delivery == "queue" && page.UncertainChanges == 0, "explicit rejection retains only the re-read actual mode");
        Check(server.Calls.Count(c => c.StartsWith("PATCH")) == 1 && server.Calls.Last().StartsWith("GET"), "409 re-reads without mutation retry");
    }

    private static async Task DeliveryGuardFeedback()
    {
        var server = new FakeServer();
        server.Inbox.Add(UserItem(Inbox, "防护反馈", 1000));
        using var client = server.Client();
        var page = await DeliveryPage(server, client);
        page.SetBusy(true);
        await page.Change(Inbox, "steer");
        Check(page.Notices.Count == 1 && server.Calls.Count == 0, "overlapping action gets a busy explanation");
        page.SetBusy(false); server.Inbox.Clear();
        await page.Change(Inbox, "steer");
        Check(page.Notices.Any(n => n.Contains("已不在待投递列表")) && !server.Calls.Any(c => c.StartsWith("PATCH")), "fresh absence explains delivery/removal without a futile PATCH");
    }

    private static async Task DeliveryBindingFences()
    {
        foreach (var boundary in new[] { "source", "session" })
        {
            var server = new FakeServer();
            server.Inbox.Add(UserItem(Inbox, "旧来源", 1000));
            using var client = server.Client();
            var page = await DeliveryPage(server, client);
            server.OnInboxRead = _ => { if (boundary == "source") page.Revoke(); else page.ReplaceSession("ses_other"); };
            await page.Change(Inbox, "steer");
            Check(server.Calls.Count(c => c.StartsWith("PATCH")) == 0, boundary + " changed after preflight starts no mutation");
            Check(page.Notices.Count == 0, boundary + " change publishes no old-page notices");
        }
    }

    private static IEnumerable<UIElement> Descendants(UIElement element)
    {
        yield return element;
        var children = element switch
        {
            Panel panel => panel.Children.Cast<UIElement>(),
            ContentControl { Content: UIElement content } => new[] { content },
            ListView list => list.Items.OfType<UIElement>(),
            _ => Array.Empty<UIElement>(),
        };
        foreach (var child in children) foreach (var nested in Descendants(child)) yield return nested;
    }
    private static IEnumerable<UIElement> Ancestors(UIElement element)
    {
        for (var parent = element.Parent; parent is not null; parent = parent.Parent) yield return parent;
    }
}
