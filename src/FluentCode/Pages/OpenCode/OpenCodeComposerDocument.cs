using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

internal enum OpenCodeComposerInlineKind { Mention, Link, Image }
internal abstract record OpenCodeComposerRun;
internal sealed record OpenCodeComposerTextRun(string Text) : OpenCodeComposerRun;
internal sealed record OpenCodeComposerInlineToken(Guid Id, OpenCodeComposerInlineKind Kind,
    string Value, string Label, bool IsSkill = false, Guid? AttachmentId = null, int Number = 0,
    string? Color = null) : OpenCodeComposerRun;
internal readonly record struct OpenCodeComposerSelection(int Anchor, int Head)
{
    internal int Start => Math.Min(Anchor, Head);
    internal int End => Math.Max(Anchor, Head);
    internal OpenCodeComposerSelection Clamp(int length) => new(Math.Clamp(Anchor, 0, length), Math.Clamp(Head, 0, length));
}

/// <summary>Immutable authority. Each token occupies one UTF-16 model/native position; send offsets are separate.</summary>
internal sealed class OpenCodeComposerDocument
{
    internal const char ObjectCharacter = '\ufffc';
    internal static OpenCodeComposerDocument Empty { get; } = new(Array.Empty<OpenCodeComposerRun>(), 0);
    internal IReadOnlyList<OpenCodeComposerRun> Runs { get; }
    internal string Story { get; }
    internal int Length => Story.Length;
    internal int ImageHighWater { get; }
    internal IReadOnlyList<(int Position, OpenCodeComposerInlineToken Token)> Objects { get; }
    private readonly (string Text, OpenCodePromptContext[] Contexts) _serialized;
    internal string RawText => _serialized.Text;
    internal IReadOnlyList<OpenCodePromptContext> RawContexts { get; }

    private OpenCodeComposerDocument(IEnumerable<OpenCodeComposerRun> runs, int highWater)
    {
        var normalized = new List<OpenCodeComposerRun>();
        var story = new StringBuilder();
        var objects = new List<(int, OpenCodeComposerInlineToken)>();
        var ids = new HashSet<Guid>();
        foreach (var run in runs)
        {
            if (run is OpenCodeComposerTextRun text)
            {
                var value = Normalize(text.Text).Replace(ObjectCharacter.ToString(), string.Empty, StringComparison.Ordinal);
                if (value.Length == 0) continue;
                if (normalized.LastOrDefault() is OpenCodeComposerTextRun previous)
                    normalized[^1] = new OpenCodeComposerTextRun(previous.Text + value);
                else normalized.Add(new OpenCodeComposerTextRun(value));
                story.Append(value);
            }
            else if (run is OpenCodeComposerInlineToken token)
            {
                if (token.Id == Guid.Empty || !ids.Add(token.Id)) throw new ArgumentException("Duplicate or empty composer token identity.");
                objects.Add((story.Length, token));
                normalized.Add(token);
                story.Append(ObjectCharacter);
                if (token.Kind == OpenCodeComposerInlineKind.Image) highWater = Math.Max(highWater, token.Number);
            }
        }
        Runs = Array.AsReadOnly(normalized.ToArray());
        Story = story.ToString();
        Objects = Array.AsReadOnly(objects.ToArray());
        ImageHighWater = highWater;
        _serialized = SerializeRuns(Runs);
        RawContexts = Array.AsReadOnly(_serialized.Contexts);
    }

    internal static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    internal static OpenCodeComposerDocument Text(string text) => Empty.Replace(0, 0, text);
    internal OpenCodeComposerDocument WithHighWater(int value) => value <= ImageHighWater ? this : new(Runs, value);
    internal OpenCodeComposerDocument Replace(int start, int length, string text)
        => Replace(start, length, new OpenCodeComposerRun[] { new OpenCodeComposerTextRun(text) });
    internal OpenCodeComposerDocument Replace(int start, int length, IEnumerable<OpenCodeComposerRun> insertion)
    {
        if (start < 0 || length < 0 || start > Length - length || SplitsSurrogate(Story, start) || SplitsSurrogate(Story, start + length))
            throw new ArgumentOutOfRangeException(nameof(start));
        return new(Slice(0, start).Concat(insertion).Concat(Slice(start + length, Length - start - length)), ImageHighWater);
    }
    internal OpenCodeComposerDocument InsertToken(int start, int length, OpenCodeComposerInlineToken token)
        => Replace(start, length, new OpenCodeComposerRun[] { token });

    private IEnumerable<OpenCodeComposerRun> Slice(int start, int length)
    {
        var end = start + length;
        var offset = 0;
        foreach (var run in Runs)
        {
            var count = run is OpenCodeComposerTextRun text ? text.Text.Length : 1;
            var left = Math.Max(start, offset);
            var right = Math.Min(end, offset + count);
            if (left < right)
                yield return run is OpenCodeComposerTextRun value
                    ? new OpenCodeComposerTextRun(value.Text.Substring(left - offset, right - left)) : run;
            offset += count;
        }
    }

    internal (string Text, OpenCodePromptContext[] Contexts) Serialize(int start = 0, int? length = null)
        => start == 0 && (length is null || length == Length)
            ? (_serialized.Text, _serialized.Contexts.ToArray()) : SerializeRuns(Slice(start, length ?? Length - start));
    private static (string Text, OpenCodePromptContext[] Contexts) SerializeRuns(IEnumerable<OpenCodeComposerRun> runs)
    {
        var text = new StringBuilder();
        var contexts = new List<OpenCodePromptContext>();
        foreach (var run in runs)
        {
            if (run is OpenCodeComposerTextRun plain) text.Append(plain.Text);
            else if (run is OpenCodeComposerInlineToken token)
            {
                if (token.Kind == OpenCodeComposerInlineKind.Mention)
                    contexts.Add(new(token.Value, token.IsSkill, text.Length));
                text.Append(token.Kind switch
                {
                    OpenCodeComposerInlineKind.Mention => "@" + token.Value,
                    OpenCodeComposerInlineKind.Image => OpenCodeComposerChips.ImageToken(token.Number),
                    _ => token.Value,
                });
            }
        }
        return (text.ToString(), contexts.ToArray());
    }

    internal bool SameContent(OpenCodeComposerDocument other) => Story == other.Story && Runs.SequenceEqual(other.Runs);
    internal int TextOffset(int native)
    {
        var shift = 0;
        foreach (var item in Objects)
            if (item.Position < native) shift += Raw(item.Token).Length - 1;
        return Math.Clamp(native, 0, Length) + shift;
    }
    internal int NativeOffset(int textOffset, bool end = false)
    {
        var shift = 0;
        foreach (var item in Objects)
        {
            var start = item.Position + shift;
            var length = Raw(item.Token).Length;
            if (textOffset <= start) break;
            if (textOffset < start + length) return item.Position + (end ? 1 : 0);
            shift += length - 1;
        }
        return Math.Clamp(textOffset - shift, 0, Length);
    }
    internal static string Raw(OpenCodeComposerInlineToken token) => token.Kind switch
    {
        OpenCodeComposerInlineKind.Mention => "@" + token.Value,
        OpenCodeComposerInlineKind.Image => OpenCodeComposerChips.ImageToken(token.Number),
        _ => token.Value,
    };
    internal static bool SplitsSurrogate(string value, int position)
        => position > 0 && position < value.Length && char.IsHighSurrogate(value[position - 1]) && char.IsLowSurrogate(value[position]);
}

/// <summary>An editor-derived receipt, not a string-diff guess. Unchanged prefix/suffix must match exactly.</summary>
internal readonly record struct OpenCodeComposerEditReceipt(int Start, int Removed, string Inserted,
    OpenCodeComposerSelection Before, OpenCodeComposerSelection After)
{
    internal OpenCodeComposerSelection SanitizedAfter => new(SanitizedOffset(After.Anchor), SanitizedOffset(After.Head));
    private int SanitizedOffset(int position)
    {
        var limit = Math.Clamp(position - Start, 0, Inserted.Length);
        var removed = 0;
        for (var index = 0; index < limit; index++)
            if (Inserted[index] == OpenCodeComposerDocument.ObjectCharacter) removed++;
        return position - removed;
    }
    internal static bool TryCreate(string oldStory, string newStory, OpenCodeComposerSelection before,
        OpenCodeComposerSelection after, out OpenCodeComposerEditReceipt receipt)
    {
        receipt = default;
        if (before.Start < 0 || before.End > oldStory.Length || after.Start < 0 || after.End > newStory.Length) return false;
        var caret = after.Head;
        var prefix = 0;
        while (prefix < oldStory.Length && prefix < newStory.Length && oldStory[prefix] == newStory[prefix]) prefix++;
        var start = Math.Min(Math.Min(before.Start, caret), prefix);
        var inserted = caret - start;
        var removed = oldStory.Length - newStory.Length + inserted;
        if (inserted < 0 || removed < 0 || start > oldStory.Length - removed || start + removed < before.End
            || OpenCodeComposerDocument.SplitsSurrogate(oldStory, start)
            || OpenCodeComposerDocument.SplitsSurrogate(oldStory, start + removed)
            || OpenCodeComposerDocument.SplitsSurrogate(newStory, caret)
            || !oldStory.AsSpan(0, start).SequenceEqual(newStory.AsSpan(0, start))
            || !oldStory.AsSpan(start + removed).SequenceEqual(newStory.AsSpan(caret))) return false;
        receipt = new(start, removed, newStory.Substring(start, inserted), before, after);
        return true;
    }

    internal bool TryApply(OpenCodeComposerDocument before, string actualStory, out OpenCodeComposerDocument document)
    {
        document = before;
        if (Start < 0 || Removed < 0 || Start > before.Length - Removed
            || OpenCodeComposerDocument.SplitsSurrogate(before.Story, Start)
            || OpenCodeComposerDocument.SplitsSurrogate(before.Story, Start + Removed)
            || before.Story.Remove(Start, Removed).Insert(Start, Inserted) != actualStory) return false;
        document = before.Replace(Start, Removed, Inserted);
        // Surviving objects retain EXACT IDs; inserted foreign objects are stripped, never assigned an old ordinal.
        var start = Start;
        var end = Start + Removed;
        var expected = before.Objects.Where(item => item.Position < start || item.Position >= end).Select(item => item.Token.Id);
        return document.Objects.Select(item => item.Token.Id).SequenceEqual(expected);
    }
}

internal sealed record OpenCodeComposerDocumentObservation(OpenCodeComposerDocument Before,
    OpenCodeComposerDocument Document, OpenCodeComposerSelection Selection, bool IsValid = true);
