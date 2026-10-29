using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

public readonly record struct PersistentPtySize(
    [property: JsonPropertyName("cols"), JsonRequired] ushort Cols,
    [property: JsonPropertyName("rows"), JsonRequired] ushort Rows);
public readonly record struct PersistentPtyOutput(
    [property: JsonPropertyName("head"), JsonRequired] long Head,
    [property: JsonPropertyName("tail"), JsonRequired] long Tail);
public readonly record struct PersistentPtyCursor(
    [property: JsonPropertyName("x"), JsonRequired] int X,
    [property: JsonPropertyName("y"), JsonRequired] int Y);

public sealed record PersistentPtyInfo(
    [property: JsonPropertyName("id"), JsonRequired] string Id,
    [property: JsonPropertyName("title"), JsonRequired] string Title,
    [property: JsonPropertyName("command"), JsonRequired] string Command,
    [property: JsonPropertyName("args"), JsonRequired] ImmutableArray<string> Args,
    [property: JsonPropertyName("cwd"), JsonRequired] string Cwd,
    [property: JsonPropertyName("status"), JsonRequired] string Status,
    [property: JsonPropertyName("pid"), JsonRequired] long Pid,
    [property: JsonPropertyName("exitCode")] int? ExitCode,
    [property: JsonPropertyName("sessionID"), JsonRequired] string SessionId,
    [property: JsonPropertyName("foregroundProcess"), JsonRequired] string? ForegroundProcess,
    [property: JsonPropertyName("size"), JsonRequired] PersistentPtySize Size,
    [property: JsonPropertyName("output"), JsonRequired] PersistentPtyOutput Output);

public sealed record PersistentPtySnapshot(PersistentPtyInfo Info, string Text,
    ImmutableArray<byte> Checkpoint, PersistentPtyCursor Cursor);
public sealed record PersistentPtyCreateRequest(
    [property: JsonPropertyName("args")] ImmutableArray<string> Args,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("env")] IReadOnlyDictionary<string, string> Env,
    [property: JsonPropertyName("command")] string? Command = null,
    [property: JsonPropertyName("cwd")] string? Cwd = null,
    [property: JsonPropertyName("size")] PersistentPtySize? Size = null);
public sealed record PersistentPtyReadScreen(
    [property: JsonPropertyName("text"), JsonRequired] string Text,
    [property: JsonPropertyName("cols"), JsonRequired] ushort Cols,
    [property: JsonPropertyName("rows"), JsonRequired] ushort Rows,
    [property: JsonPropertyName("cursor"), JsonRequired] PersistentPtyCursor Cursor);
public sealed record PersistentPtyReadResult(
    [property: JsonPropertyName("ptyID"), JsonRequired] string PtyId,
    [property: JsonPropertyName("title"), JsonRequired] string Title,
    [property: JsonPropertyName("cwd"), JsonRequired] string Cwd,
    [property: JsonPropertyName("foregroundProcess"), JsonRequired] string? ForegroundProcess,
    [property: JsonPropertyName("screen"), JsonRequired] PersistentPtyReadScreen Screen);

public enum PersistentPtyRole { Observer, Controller }

public sealed record PersistentPtyReplay(long RequestedOffset, long AvailableOffset, long EndOffset, bool Truncated);
public abstract record PersistentPtyEvent;
public sealed record PersistentPtyAttached(string AttachmentId, int InputProtocol, PersistentPtyInfo Info,
    PersistentPtyRole Role, long Generation, PersistentPtyReplay Replay) : PersistentPtyEvent;
public sealed record PersistentPtyBytes(ImmutableArray<byte> Bytes) : PersistentPtyEvent;
public sealed record PersistentPtyReplayComplete(long EndOffset) : PersistentPtyEvent;
public sealed record PersistentPtyResized(PersistentPtySize Size, long Generation, ImmutableArray<byte> Checkpoint) : PersistentPtyEvent
{
    internal long Revision { get; init; }
}
public sealed record PersistentPtyControllerChanged(string? AttachmentId, long Generation) : PersistentPtyEvent;
public sealed record PersistentPtyExited(int? ExitCode, long FinalOffset) : PersistentPtyEvent;
public sealed record PersistentPtyTitleChanged(string Title) : PersistentPtyEvent;
public sealed record PersistentPtyForegroundChanged(string? Process) : PersistentPtyEvent;

public interface IPersistentPtyConnection : IAsyncDisposable
{
    string PtyId { get; }
    string AttachmentId { get; }
    IAsyncEnumerable<PersistentPtyEvent> ReadEventsAsync(CancellationToken cancellationToken = default);
    ValueTask RequestControlAsync(ushort cols, ushort rows, CancellationToken cancellationToken = default);
    ValueTask SendInputAsync(ReadOnlyMemory<byte> input, ushort cols, ushort rows, CancellationToken cancellationToken = default);
    ValueTask ResizeAsync(ushort cols, ushort rows, CancellationToken cancellationToken = default);
}

/// <summary>Stable, redacted failure code: no peer text, ticket, URI or inner exception.</summary>
public sealed class PersistentPtyException : Exception
{
    public string Code { get; }
    public PersistentPtyException(string code) : base("Persistent terminal operation failed (" + code + ").") => Code = code;
}

internal static class PersistentPtyValidation
{
    internal const long MaxSafeInteger = 9007199254740991;
    internal static void Id(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..") throw new PersistentPtyException("invalid-identity");
    }
    internal static void Size(PersistentPtySize size)
    {
        if (size.Cols == 0 || size.Rows == 0) throw new PersistentPtyException("invalid-geometry");
    }
    internal static long Offset(long value)
    {
        if (value < 0 || value > MaxSafeInteger) throw new PersistentPtyException("invalid-offset");
        return value;
    }
    internal static PersistentPtyInfo Info(PersistentPtyInfo info, string sessionId, string? ptyId = null)
    {
        Id(info.Id);
        if (info.SessionId != sessionId || (ptyId is not null && info.Id != ptyId)) throw new PersistentPtyException("identity-mismatch");
        Size(info.Size); Offset(info.Output.Head); Offset(info.Output.Tail);
        if (info.Output.Head > info.Output.Tail || info.Pid < 0 || info.ExitCode < 0 || info.Title is null || info.Command is null
            || info.Cwd is null || info.Args.IsDefault || info.Status is not ("running" or "exited"))
            throw new PersistentPtyException("invalid-info");
        foreach (var arg in info.Args) if (arg is null) throw new PersistentPtyException("invalid-info");
        return info;
    }
}
