using System;

namespace FluentCode.Services.OpenCode;

/// <summary>“连接其他设备”里链接框的文字属于哪种情况。</summary>
public enum OpenCodePairingLinkEditKind
{
    /// <summary>与生成的链接相同。</summary>
    Generated,

    /// <summary>用户改过，仍是带当前配对码的有效链接。</summary>
    Edited,

    /// <summary>框是空的。</summary>
    Empty,

    /// <summary>不是解析器接受的配对链接。</summary>
    Invalid,

    /// <summary>格式对，但配对码不是当前这一个：扫了也连不上。</summary>
    StaleCode,
}

/// <summary>链接框当前文字的判定结果。含配对码，<see cref="ToString"/> 已脱敏。</summary>
public sealed class OpenCodePairingLinkEditState
{
    internal OpenCodePairingLinkEditState(OpenCodePairingLinkEditKind kind, bool isEdited, string status, string qrNote,
        string? link = null, string? address = null, bool isLoopback = false)
    {
        Kind = kind;
        IsEdited = isEdited;
        Status = status;
        QrNote = qrNote;
        Link = link;
        Address = address;
        IsLoopback = isLoopback;
    }

    public OpenCodePairingLinkEditKind Kind { get; }

    /// <summary>二维码、复制与打开使用的文字：就是框里的内容（去掉首尾空白）。不能用时为 null。</summary>
    public string? Link { get; }

    /// <summary>用户写的地址部分（<c>/auth/connect/</c> 之前，原样保留）；链接不能用时为 null。</summary>
    public string? Address { get; }

    /// <summary>回环地址只有这台电脑能打开：链接可用，但不显示二维码。</summary>
    public bool IsLoopback { get; }

    /// <summary>框里的内容与生成的链接不同（此时显示“恢复原链接”）。</summary>
    public bool IsEdited { get; }

    /// <summary>链接不能用：不显示二维码，也不能复制或打开。</summary>
    public bool IsProblem => Link is null;

    public bool ShowQr => Link is not null && !IsLoopback;

    /// <summary>链接框下方的一句话。固定文案，不含配对码。</summary>
    public string Status { get; }

    /// <summary>不显示二维码时，写在二维码位置的原因；显示二维码时为空。</summary>
    public string QrNote { get; }

    public override string ToString() => "OpenCodePairingLinkEditState { [redacted] }";
}

/// <summary>
/// 配对码更换（重新生成、过期）时要带到新链接上的用户修改：
/// <see cref="Address"/> 是用户写的地址（不含配对码）；<see cref="Text"/> 是无法合并时原样保留的文字（可能含旧配对码）。
/// </summary>
public sealed class OpenCodePairingLinkCarry
{
    private OpenCodePairingLinkCarry(string? address, string? text)
    {
        Address = address;
        Text = text;
    }

    public static OpenCodePairingLinkCarry None { get; } = new(null, null);

    public string? Address { get; }
    public string? Text { get; }
    public bool IsNone => Address is null && Text is null;

    internal static OpenCodePairingLinkCarry OfAddress(string address) => new(address, null);
    internal static OpenCodePairingLinkCarry OfText(string text) => new(null, text);

    public override string ToString() => "OpenCodePairingLinkCarry { [redacted] }";
}

/// <summary>
/// 可编辑链接框的规则（纯逻辑，不依赖界面）：
/// 二维码编码的就是框里的文字；只有解析器接受、并且带着当前配对码的链接才算可用；
/// 换配对码时，只改过地址的链接把地址带到新链接上，其余情况原样保留并提示。
/// </summary>
public static class OpenCodePairingLinkEdit
{
    /// <summary>链接长度上限：远超任何真实地址，同时保证二维码不会密到扫不出来。</summary>
    public const int MaxLength = 512;

    private const string ExchangePath = "/auth/connect/";

    /// <summary>
    /// 判定链接框的内容。<paramref name="generated"/> 是当前生成的链接，<paramref name="code"/> 是当前配对码；
    /// <paramref name="renewed"/> 表示这段文字是换配对码时原样留下的（只影响文案）。
    /// </summary>
    public static OpenCodePairingLinkEditState Evaluate(string? text, string? generated, string? code, bool renewed = false)
    {
        var value = text?.Trim() ?? string.Empty;
        var edited = !string.Equals(value, generated ?? string.Empty, StringComparison.Ordinal);
        if (value.Length == 0)
            return Problem(OpenCodePairingLinkEditKind.Empty, edited, "链接是空的。填上链接，或点“恢复原链接”。");
        if (value.Length > MaxLength)
            return Problem(OpenCodePairingLinkEditKind.Invalid, edited, "链接太长。只改地址部分，其余保持原样。");
        if (!TrySplit(value, out var address, out var linkCode, out var loopback))
        {
            return Problem(OpenCodePairingLinkEditKind.Invalid, edited, renewed
                ? "配对码已重新生成；框里的内容还不是有效链接。"
                : "链接格式不对。只改地址部分，其余保持原样。");
        }

        if (string.IsNullOrEmpty(code) || !string.Equals(linkCode, code, StringComparison.Ordinal))
        {
            return Problem(OpenCodePairingLinkEditKind.StaleCode, edited, renewed
                ? "配对码已重新生成，框里还是旧链接，连不上。"
                : "配对码被改动了，这个链接连不上。只改地址部分。");
        }

        return new OpenCodePairingLinkEditState(
            edited ? OpenCodePairingLinkEditKind.Edited : OpenCodePairingLinkEditKind.Generated,
            edited,
            edited ? "已改用你填的链接；能否访问需要自己确认。" : "可以直接改链接里的地址，二维码会跟着变。",
            loopback ? "这个地址只有这台电脑能打开，所以不显示二维码。" : string.Empty,
            value, address, loopback);
    }

    /// <summary>
    /// 配对码即将作废时调用：没改过 → <see cref="OpenCodePairingLinkCarry.None"/>；只改了地址 → 记下地址；
    /// 其余（改坏了、配对码对不上）→ 原样记下文字。空框视为没改。
    /// </summary>
    public static OpenCodePairingLinkCarry Capture(string? text, string? generated, string? code)
    {
        var state = Evaluate(text, generated, code);
        if (!state.IsEdited || state.Kind == OpenCodePairingLinkEditKind.Empty) return OpenCodePairingLinkCarry.None;
        return state.Address is { } address ? OpenCodePairingLinkCarry.OfAddress(address) : OpenCodePairingLinkCarry.OfText(text ?? string.Empty);
    }

    /// <summary>
    /// 新配对码到手后链接框应显示的文字。记下的地址拼上新配对码并重新复核；复核不过就退回生成的链接。
    /// <paramref name="kept"/> 为 true 表示原样留下了旧文字，界面要说明配对码已重新生成。
    /// </summary>
    public static string Apply(OpenCodePairingLinkCarry carry, string generated, string code, out bool kept)
    {
        ArgumentNullException.ThrowIfNull(carry);
        ArgumentNullException.ThrowIfNull(generated);
        kept = false;
        if (carry.Address is { } address)
        {
            var link = address + ExchangePath + code;
            return OpenCodePairingLinks.IsValidCode(code) && link.Length <= MaxLength && TrySplit(link, out _, out var linkCode, out _)
                && string.Equals(linkCode, code, StringComparison.Ordinal)
                ? link
                : generated;
        }

        if (carry.Text is { Length: > 0 } text)
        {
            kept = true;
            return text;
        }

        return generated;
    }

    /// <summary>用现有解析器复核，再按原文拆成“地址”和“配对码”。解析器保证路径恰好是 /auth/connect/ 加一段配对码。</summary>
    private static bool TrySplit(string link, out string address, out string code, out bool loopback)
    {
        address = string.Empty;
        code = string.Empty;
        loopback = false;
        OpenCodePairingLink parsed;
        try { parsed = OpenCodePairingService.Parse(link); }
        catch (OpenCodeConfigurationException) { return false; }
        var slash = link.LastIndexOf('/');
        var start = slash + 1 - ExchangePath.Length;
        if (start <= 0 || string.CompareOrdinal(link, start, ExchangePath, 0, ExchangePath.Length) != 0) return false;
        address = link[..start];
        code = link[(slash + 1)..];
        loopback = OpenCodePairingLinks.IsLoopbackOrigin(parsed.BaseUrl);
        return code.Length > 0;
    }

    private static OpenCodePairingLinkEditState Problem(OpenCodePairingLinkEditKind kind, bool edited, string status)
        => new(kind, edited, status, "链接不能用，所以不显示二维码。");
}
