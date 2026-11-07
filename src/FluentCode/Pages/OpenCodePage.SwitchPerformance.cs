using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private sealed class SessionSwitchPerformance
    {
        internal readonly long Started = Stopwatch.GetTimestamp();
        internal readonly TaskCompletionSource<bool> Frame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal required int Generation;
        internal required int PageGeneration;
        internal required long SourceRevision;
        internal required long FullApplies;
        internal required long Layouts;
        internal required long FastApplies;
        internal required long UnchangedApplies;
        internal double PreludeMs = -1, RetainedMs = -1, RestMs = -1, FirstFrameMs = -1;
        internal bool FrameQueued;
    }

    private SessionSwitchPerformance? _sessionSwitchPerformance;
    private Task TranscriptFirstFrame => _sessionSwitchPerformance?.Frame.Task ?? Task.CompletedTask;

    private async Task WaitForTranscriptFirstFrameAsync()
    {
        Task frame;
        do { frame = TranscriptFirstFrame; await frame.ConfigureAwait(true); }
        while (!ReferenceEquals(frame, TranscriptFirstFrame));
    }

    private void RunAfterTranscriptFirstFrame(Action action)
    {
        var queued = false;
        QueueAfterTranscriptFirstFrame(action, ref queued);
        if (!queued) action();
    }

    partial void QueueAfterTranscriptFirstFrame(Action action, ref bool queued);

    private SessionSwitchPerformance BeginSessionSwitchPerformance()
    {
        _sessionSwitchPerformance?.Frame.TrySetResult(false);
        var sample = new SessionSwitchPerformance
        {
            Generation = _sessionGeneration + 1, PageGeneration = _pageGeneration, SourceRevision = _sourceRevision,
            FullApplies = _conversationView.FullApplyCount, Layouts = _conversationView.UpdateLayoutCount,
            FastApplies = _conversationView.FastApplyCount, UnchangedApplies = _conversationView.UnchangedApplyCount,
        };
        _sessionSwitchPerformance = sample;
        _conversationView.BeginSessionSwitchLayout();
        return sample;
    }

    private bool IsCurrentSwitchPerformance(SessionSwitchPerformance sample)
        => ReferenceEquals(sample, _sessionSwitchPerformance) && sample.Generation == _sessionGeneration
        && sample.PageGeneration == _pageGeneration && sample.SourceRevision == _sourceRevision && _pageActive;

    private void RecordSessionSwitchPrelude(SessionSwitchPerformance sample)
    {
        if (!IsCurrentSwitchPerformance(sample)) return;
        sample.PreludeMs = Stopwatch.GetElapsedTime(sample.Started).TotalMilliseconds;
        WriteSessionSwitchPerformance(sample, "prelude");
    }

    private void RecordRetainedSwitchApply(long started)
    {
        if (_sessionSwitchPerformance is { } sample && IsCurrentSwitchPerformance(sample))
            sample.RetainedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private void RecordRestSwitchApply(long started)
    {
        if (_sessionSwitchPerformance is not { RestMs: < 0 } sample || !IsCurrentSwitchPerformance(sample)) return;
        sample.RestMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        WriteSessionSwitchPerformance(sample, "rest");
    }

    private void QueueSessionSwitchFirstFrame()
    {
        if (_sessionSwitchPerformance is not { FrameQueued: false } sample || !IsCurrentSwitchPerformance(sample)) return;
        sample.FrameQueued = true;
        void AcceptedFrame()
        {
            if (IsCurrentSwitchPerformance(sample))
            {
                _conversationView.EndSessionSwitchLayout();
                sample.FirstFrameMs = Stopwatch.GetElapsedTime(sample.Started).TotalMilliseconds;
                WriteSessionSwitchPerformance(sample, "first-frame");
            }
            sample.Frame.TrySetResult(true);
        }
        var queued = false;
        QueueTranscriptFirstFrame(AcceptedFrame, ref queued);
        if (!queued) AcceptedFrame(); // Inert/native-unavailable hosts have no rendering event.
    }

    partial void QueueTranscriptFirstFrame(Action acceptedFrame, ref bool queued);

    private void WriteSessionSwitchPerformance(SessionSwitchPerformance sample, string stage)
        => Debug.WriteLine($"OpenCode.Switch generation={sample.Generation} stage={stage} prelude_ms={sample.PreludeMs:F2} retained_apply_ms={sample.RetainedMs:F2} rest_apply_ms={sample.RestMs:F2} first_frame_ms={sample.FirstFrameMs:F2} full_applies={_conversationView.FullApplyCount - sample.FullApplies} fast_applies={_conversationView.FastApplyCount - sample.FastApplies} unchanged_applies={_conversationView.UnchangedApplyCount - sample.UnchangedApplies} forced_layouts={_conversationView.UpdateLayoutCount - sample.Layouts}");
}
