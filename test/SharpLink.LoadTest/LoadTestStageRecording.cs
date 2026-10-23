using SharpLink.LoadTestBase;

namespace SharpLink.LoadTest;

// One concurrency stage owns these buffers across warmup and measurement.
// Warmup uses a separate recording-off state and never writes measurement samples.
internal sealed class LoadTestStageRecording
{
    internal LoadTestStageRecording(LoadTestOptions options, int concurrency, bool isWarmup = false)
    {
        Mode = isWarmup ? LatencyRecordingMode.Off : options.RecordingMode;
        FormalRecorder = LatencyRecordingPolicy.CreatesFormalRecorder(Mode)
            ? new StageLatencyRecorder(concurrency, options.MaximumRecordedOperations)
            : null;
        DiagnosticHistogram = LatencyRecordingPolicy.CreatesDiagnosticRecorder(Mode)
            ? new LatencyHistogram()
            : null;
        Realtime = LatencyRecordingPolicy.StartsRealtimeReporter(Mode)
            ? new RealtimeLatencyState()
            : null;
        TailObserverRecorder = options.TailObserver && !isWarmup
            ? new StageLatencyRecorder(1, options.TailObserverMaximumRecordedOperations)
            : null;
    }

    internal LatencyRecordingMode Mode { get; }
    internal StageLatencyRecorder? FormalRecorder { get; }
    internal LatencyHistogram? DiagnosticHistogram { get; }
    internal RealtimeLatencyState? Realtime { get; }
    internal StageLatencyRecorder? TailObserverRecorder { get; }
}
