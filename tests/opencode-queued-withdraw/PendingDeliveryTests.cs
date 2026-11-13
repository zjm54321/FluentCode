using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages;
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
            ("visible keyboard/touch steer switch", SteerSwitchControl),
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

    private static async Task<OpenCodePage> DeliveryPage(FakeServer server, OpenCodeClient client, bool owned = true)
    {
        var page = new OpenCodePage(client, Session);
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

    private static async Task SteerSwitchControl()
    {
        var server = new FakeServer();
        server.Inbox.Add(UserItem(Inbox, new string('长', 200), 1000) with { Delivery = "steer" });
        using var client = server.Client();
        var page = await DeliveryPage(server, client);
        var button = Descendants(page.Conversation).OfType<Button>().SingleOrDefault(b => AutomationProperties.GetAutomationId(b) == "OpenCodeAdmittedSteerQueue." + Inbox);
        Check(button is not null, "pending bubble has a native visible/revealable queue button, not just a context menu");
        Check(button!.Content?.ToString() == "改为排队" && button.IsTabStop, "native queue button has a label and keyboard focus");
        var row = page.Conversation.Children.Single();
        Check(button.Opacity == 0 && !button.IsHitTestVisible, "unrevealed action cannot be hit invisibly");
        row.Touch();
        Check(button.Opacity == 1 && button.IsHitTestVisible, "first touch reveals and latches the action");
        row.MouseExit(); row.Focus();
        Check(button.Opacity == 1 && button.IsHitTestVisible, "keyboard focus reveals the action");
        Check(!Ancestors(button).OfType<ContentControl>().Any(parent => parent.MaxHeight == 40), "long-message collapse does not clip the switching control");
        await page.Click(button);
        Check(page.Queue.HasItems && page.Conversation.PendingIds.Count == 0, "visible button routes into real delivery handler");
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
        Check(server.Calls.Count(c => c.StartsWith("PATCH")) == 1 && page.Queue.HasItems, "receipt-owned steer item can switch without snapshot membership or pending-ack list");
        Check(server.Calls.First().StartsWith("GET") && server.Calls.Last().StartsWith("GET"), "receipt switch verifies pending state before PATCH and publishes only the post-read");
        page.Publish(Array.Empty<OpenCodeInboxItem>(), trimAcknowledgements: true);
        server.Calls.Clear(); server.Queries.Clear(); server.Bodies.Clear(); server.Auth.Clear();
        await page.Change(Inbox, "steer");
        Check(page.Conversation.PendingIds.Contains(Inbox), "current receipt still proves ownership after an acknowledgement-list race");
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
        server.Inbox.Add(UserItem(Inbox, "其他客户端", 1000));
        using var client = server.Client();
        var page = await DeliveryPage(server, client, owned: false);
        await page.Change(Inbox, "steer");
        Check(page.Notices.Any(n => n.Contains("不是本客户端发送")) && server.Calls.Count == 0, "foreign item is refused with an ownership explanation and no network calls");
        page.Own(Inbox); page.SetBusy(true);
        var before = page.Notices.Count;
        await page.Change(Inbox, "steer");
        Check(page.Notices.Count > before && server.Calls.Count == 0, "overlapping action gets a busy explanation");
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
