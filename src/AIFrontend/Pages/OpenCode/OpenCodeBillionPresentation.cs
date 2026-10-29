using System;
using System.Collections.Generic;
using System.Globalization;
using AIFrontend.Services.Billion;

namespace AIFrontend.Pages.OpenCode;

internal static class OpenCodeBillionPresentation
{
    internal static string BestContext(BillionSessionStats stats) => Number(stats.ContextBestTokens) + (stats.ContextBestKind switch
    {
        "usage" => "（实际用量）", "estimate" => "（估算）", "upper" => "（上界）", _ => "（来源未知）",
    });
    internal static string Number(long? value) => value is >= 0 ? value.Value.ToString("N0", CultureInfo.InvariantCulture) : "未知";
    private static string Number(double? value, bool signed = false) => value is { } number && double.IsFinite(number) && (signed || number >= 0)
        ? number.ToString("#,0.##", CultureInfo.InvariantCulture) : "未知";
    private static string Percent(double? value) => value is >= 0 and <= 100 && double.IsFinite(value.Value)
        ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) + "%" : "未知";
    internal static IReadOnlyList<(string, string)> LedgerRows(BillionCacheLedger ledger) => new (string, string)[]
    {
        ("账本请求数", Number(ledger.Requests)), ("账本输入 tokens", Number(ledger.Input)),
        ("账本缓存 tokens", Number(ledger.Cached)),
        ("未缓存输入 tokens", ledger.Input is >= 0 && ledger.Cached is >= 0 && ledger.Input >= ledger.Cached ? Number(ledger.Input - ledger.Cached) : "未知"),
        ("账本输出 tokens", Number(ledger.Output)), ("账本命中率", Percent(ledger.HitPct)),
        ("新增内容未命中", Number(ledger.NewContent)), ("压缩重读未命中", Number(ledger.CompRepay)),
        ("稳定前缀未命中", Number(ledger.TtlRepay)), ("折叠次数", Number(ledger.Folds)),
        ("累计节省 tokens（估算）", Number(ledger.GrossSaved)), ("重读开销 tokens", Number(ledger.RepayCost)),
        ("摘要开销 tokens", Number(ledger.SummaryCost)), ("净节省 tokens（估算）", Number(ledger.NetTokens, signed: true)),
        ("已回本折叠", Number(ledger.PaidBackCount)), ("未回本折叠", Number(ledger.NotPaidBackCount)),
        ("尚未观测的折叠", Number(ledger.UnobservedCount)),
    };
    internal const string LedgerNote = "仅当前会话的 token 账本，不是金额。收益为估算，净节省可以为负。稳定前缀未命中也可能来自客户端重写，不能单独归因为 TTL 过期。未知项不按零计算。";
    internal static Uri? DashboardUri(OpenCodeSessionInsightsSnapshot snapshot, OpenCodeBillionContext context)
    {
        if (snapshot.BillionContext != context || !snapshot.BillionConfirmed || snapshot.BillionOriginState != OpenCodeBillionOriginState.Confirmed
            || snapshot.Billion is not { Health.Available: true, Phase: OpenCodeBillionPhase.Loaded or OpenCodeBillionPhase.NoRecord } billion
            || billion.SessionId != context.Binding.SessionId || string.IsNullOrWhiteSpace(context.Binding.SessionId)
            || !BillionContextClient.TryNormalizeOrigin(snapshot.BillionOrigin, out var origin) || origin != billion.Origin) return null;
        return new Uri(origin + "/__bili/#/session/" + Uri.EscapeDataString(context.Binding.SessionId));
    }
}
