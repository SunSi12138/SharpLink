using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks.Sources;
using SharpLink.Runtime;

namespace Issue739Aot;

internal static class ReaderCases
{
    private const string PinnedSource = "eb99fe887cf2129d9b88441245ca0a4a6406b6c2";

    internal static void Run(string[] args)
    {
        if (args.Length != 6)
            throw new ArgumentException("case operations warmup sample output expected-runtime");
        string kind = args[0];
        int operations = int.Parse(args[1]), warmup = int.Parse(args[2]);
        var (wrapped, incomplete, burstWidth) = kind switch
        {
            "reader-wrapped-sync" => (true, false, 1),
            "reader-direct-sync" => (false, false, 1),
            "reader-wrapped-incomplete" => (true, true, 1),
            "reader-direct-incomplete" => (false, true, 1),
            "reader-wrapped-incomplete-burst32" => (true, true, 32),
            "reader-direct-incomplete-burst32" => (false, true, 32),
            _ => throw new ArgumentException("Unknown reader control case")
        };
        if (operations < 1 || operations > 1048576 || warmup < 1 || warmup > 65536
            || operations % burstWidth != 0 || warmup % burstWidth != 0)
            throw new ArgumentException("Bounded counts divisible by burstWidth required");
        if (Environment.Version.ToString() != args[5])
            throw new InvalidOperationException("Runtime version mismatch");
        string sourceSha = Environment.GetEnvironmentVariable("ISSUE739_SOURCE_SHA") ?? "UNVERIFIED";
        if (sourceSha != PinnedSource)
            throw new InvalidOperationException("Source baseline mismatch");
        if (SynchronizationContext.Current is not null || ExecutionContext.IsFlowSuppressed())
            throw new InvalidOperationException("Reader controls require the ordinary console execution context");

        // All objects, source cores, results, buffers and outstanding-read storage exist before warmup.
        var driver = new ReaderDriver(wrapped, incomplete, burstWidth);
        driver.Run(warmup);
        driver.Verify(warmup, default);
        Counts warmupCounts = driver.CaptureCounts();
        _ = GC.GetTotalAllocatedBytes(precise: true);
        _ = GC.GetAllocatedBytesForCurrentThread();
        _ = Stopwatch.GetTimestamp();
        Thread.Sleep(1000);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        int threadStart = Environment.CurrentManagedThreadId;
        long currentStart = GC.GetAllocatedBytesForCurrentThread();
        long totalStart = GC.GetTotalAllocatedBytes(precise: true);
        long ticksStart = Stopwatch.GetTimestamp();
        driver.Run(operations);
        long ticksEnd = Stopwatch.GetTimestamp();
        long totalEnd = GC.GetTotalAllocatedBytes(precise: true);
        long currentEnd = GC.GetAllocatedBytesForCurrentThread();
        int threadEnd = Environment.CurrentManagedThreadId;

        Counts measured = driver.Verify(operations, warmupCounts);
        if (threadStart != threadEnd)
            throw new InvalidOperationException("Measurement changed threads");
        ThreadPool.GetMinThreads(out int minimumWorkers, out int minimumIo);

        // Explicit JSON writing avoids reflection-dependent serialization. Reporting and hashes are outside the interval.
        using var output = File.Create(args[4]);
        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteNumber("schemaVersion", 1);
        json.WriteString("kind", kind);
        json.WriteString("sample", args[3]);
        json.WriteNumber("processId", Environment.ProcessId);
        json.WriteString("sourceSha", sourceSha);
        json.WriteString("runtime", Environment.Version.ToString());
        json.WriteString("expectedRuntime", args[5]);
        json.WriteString("framework", RuntimeInformation.FrameworkDescription);
        json.WriteString("architecture", RuntimeInformation.ProcessArchitecture.ToString());
        json.WriteString("os", RuntimeInformation.OSDescription);
        json.WriteNumber("processorCount", Environment.ProcessorCount);
        json.WriteBoolean("serverGc", System.Runtime.GCSettings.IsServerGC);
        json.WriteNumber("minimumWorkers", minimumWorkers);
        json.WriteNumber("minimumIo", minimumIo);
        json.WriteString("tieredPgo", "N/A: NativeAOT");
        json.WriteString("tieredCompilation", "N/A: NativeAOT");
        json.WriteString("readyToRun", "N/A: NativeAOT");
        json.WriteNumber("operations", operations);
        json.WriteNumber("warmup", warmup);
        json.WriteNumber("burstWidth", burstWidth);
        json.WriteNumber("bursts", operations / burstWidth);
        json.WriteBoolean("wrapped", wrapped);
        json.WriteBoolean("forcedIncomplete", incomplete);
        WriteCounts(json, measured);
        json.WriteNumber("expectedContinuationRegistrations", wrapped && incomplete ? operations : 0);
        json.WriteNumber("bytes", totalEnd - totalStart);
        json.WriteNumber("bytesPerOperation", (totalEnd - totalStart) / (double)operations);
        json.WriteNumber("currentThreadBytes", currentEnd - currentStart);
        json.WriteNumber("currentThreadBytesPerOperation", (currentEnd - currentStart) / (double)operations);
        json.WriteNumber("threadStart", threadStart);
        json.WriteNumber("threadEnd", threadEnd);
        json.WriteNumber("ticksStart", ticksStart);
        json.WriteNumber("ticksEnd", ticksEnd);
        json.WriteNumber("stopwatchFrequency", Stopwatch.Frequency);
        json.WriteNumber("elapsedSeconds", (ticksEnd - ticksStart) / (double)Stopwatch.Frequency);
        json.WriteBoolean("precise", true);
        json.WriteBoolean("driverIncluded", true);
        json.WriteBoolean("subtractionApplied", false);
        json.WriteBoolean("sameThreadVerified", true);
        json.WriteBoolean("immediateCompletionVerified", true);
        NativeIdentity.Write(json);
        json.WriteString("interpretation", "actual-reader source-shape microcontrol only; no transport multiplicity, end-to-end owner closure, cancellation or fault claim");
        json.WriteStartObject("details");
        json.WriteString("driver", "precreated readers, ManualResetValueTaskSourceCore<ReadResult>, buffers, ReadResults and ValueTask array; no per-read TaskCompletionSource, AsTask, task dispatch or waiting");
        json.WriteString("schedule", "start every read in a burst; verify incomplete when requested; complete every source inline; require outer synchronous success; consume once and AdvanceTo end; reuse only after advance");
        json.WriteString("completionPolicy", "RunContinuationsAsynchronously=false; same-thread immediate completion is required in both variants; incompatible scheduling fails the sample, not a production correctness verdict");
        json.WriteString("lifetime", "one outstanding operation per reader/source; source Reset follows prior outer GetResult and AdvanceTo; short version tokens may wrap only after prior tokens have been consumed");
        json.WriteString("fixedOffsets", "raw gross process/current-thread intervals retain measurement and loop overhead; setup, warmup, verification, hashes and JSON excluded; no fixed or direct-control subtraction");
        json.WriteString("warmup", "same selected serial/burst driver and source instances; one-second scheduling pause (no NativeAOT tiering) followed by full GC before measured interval; warmup counters retained and measurement uses deltas");
        json.WriteString("scope", burstWidth == 1
            ? "one reusable reader/source; warm serial read shape only; no simultaneous pending burst or full transport semantics"
            : "32 distinct precreated readers/sources outstanding together; bounded warm-pool burst shape only; not one connection at concurrency32 or unbounded pool pressure");
        json.WriteEndObject();
        json.WriteStartObject("warmupCounts");
        WriteCounts(json, warmupCounts);
        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static void WriteCounts(Utf8JsonWriter json, Counts counts)
    {
        json.WriteNumber("checks", counts.Checks);
        json.WriteNumber("completed", counts.Completed);
        json.WriteNumber("startedIncomplete", counts.StartedIncomplete);
        json.WriteNumber("reads", counts.Reads);
        json.WriteNumber("resets", counts.Resets);
        json.WriteNumber("sourceCompletions", counts.SourceCompletions);
        json.WriteNumber("sourceGetResults", counts.SourceGetResults);
        json.WriteNumber("advances", counts.Advances);
        json.WriteNumber("continuationRegistrations", counts.ContinuationRegistrations);
        json.WriteNumber("tokenWraps", counts.TokenWraps);
    }

    private readonly record struct Counts(long Checks, long Completed, long StartedIncomplete,
        long Reads, long Resets, long SourceCompletions, long SourceGetResults, long Advances,
        long ContinuationRegistrations, long TokenWraps)
    {
        public static Counts operator -(Counts a, Counts b) => new(
            a.Checks - b.Checks, a.Completed - b.Completed, a.StartedIncomplete - b.StartedIncomplete,
            a.Reads - b.Reads, a.Resets - b.Resets, a.SourceCompletions - b.SourceCompletions,
            a.SourceGetResults - b.SourceGetResults, a.Advances - b.Advances,
            a.ContinuationRegistrations - b.ContinuationRegistrations, a.TokenWraps - b.TokenWraps);
    }

    private sealed class ReaderDriver
    {
        private readonly bool _wrapped;
        private readonly bool _incomplete;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly ControlledReader[] _sources;
        private readonly PipeReader[] _readers;
        private readonly ValueTask<ReadResult>[] _outstanding;
        private long _checks, _completed, _startedIncomplete;

        internal ReaderDriver(bool wrapped, bool incomplete, int burstWidth)
        {
            _wrapped = wrapped;
            _incomplete = incomplete;
            _sources = new ControlledReader[burstWidth];
            _readers = new PipeReader[burstWidth];
            _outstanding = new ValueTask<ReadResult>[burstWidth];
            for (int slot = 0; slot < burstWidth; slot++)
            {
                var source = new ControlledReader(incomplete);
                _sources[slot] = source;
                _readers[slot] = wrapped ? new ReadOwnershipPipeReader(source) : source;
            }
        }

        internal void Run(int count)
        {
            CheckThread();
            for (int offset = 0; offset < count; offset += _sources.Length)
            {
                for (int slot = 0; slot < _sources.Length; slot++)
                {
                    _outstanding[slot] = _readers[slot].ReadAsync();
                    if (_incomplete)
                    {
                        if (_outstanding[slot].IsCompleted || !_sources[slot].IsPending)
                            throw new InvalidOperationException("Read was not initially incomplete");
                        _startedIncomplete++;
                    }
                    else if (!_outstanding[slot].IsCompletedSuccessfully)
                        throw new InvalidOperationException("Synchronous read was not completed successfully");
                }
                if (_incomplete)
                {
                    for (int slot = 0; slot < _sources.Length; slot++)
                    {
                        _sources[slot].CompletePending();
                        // Never add a variant-only wait or scheduling adaptation here.
                        if (!_outstanding[slot].IsCompletedSuccessfully)
                            throw new InvalidOperationException("Unsupported control scheduling: source completion did not complete outer read inline");
                    }
                }
                for (int slot = 0; slot < _sources.Length; slot++)
                {
                    ReadResult result = _outstanding[slot].GetAwaiter().GetResult();
                    if (result.IsCanceled || result.IsCompleted || result.Buffer.Length != 1
                        || result.Buffer.FirstSpan[0] != 42)
                        throw new InvalidOperationException("Read result mismatch");
                    _completed++;
                    _readers[slot].AdvanceTo(result.Buffer.End, result.Buffer.End);
                    _outstanding[slot] = default;
                    _checks++;
                }
                CheckThread();
            }
        }

        internal Counts CaptureCounts()
        {
            long reads = 0, resets = 0, sourceCompletions = 0, sourceGetResults = 0, advances = 0;
            long continuationRegistrations = 0, tokenWraps = 0;
            foreach (ControlledReader source in _sources)
            {
                reads += source.Reads;
                resets += source.Resets;
                sourceCompletions += source.SourceCompletions;
                sourceGetResults += source.SourceGetResults;
                advances += source.Advances;
                continuationRegistrations += source.ContinuationRegistrations;
                tokenWraps += source.TokenWraps;
            }
            return new Counts(_checks, _completed, _startedIncomplete, reads, resets,
                sourceCompletions, sourceGetResults, advances, continuationRegistrations, tokenWraps);
        }

        internal Counts Verify(int count, Counts before)
        {
            CheckThread();
            foreach (ControlledReader source in _sources)
                source.VerifyIdle();
            Counts delta = CaptureCounts() - before;
            if (delta.Checks != count || delta.Completed != count || delta.Reads != count
                || delta.Resets != count || delta.SourceCompletions != count
                || delta.SourceGetResults != count || delta.Advances != count
                || delta.StartedIncomplete != (_incomplete ? count : 0)
                || delta.ContinuationRegistrations != (_wrapped && _incomplete ? count : 0))
                throw new InvalidOperationException("Exact read lifecycle count mismatch; control shape unsupported");
            return delta;
        }

        private void CheckThread()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Reader driver changed threads");
        }
    }

    private sealed class ControlledReader : PipeReader, IValueTaskSource<ReadResult>
    {
        private readonly bool _incomplete;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly ReadResult _result = new(new ReadOnlySequence<byte>(new byte[] { 42 }), false, false);
        private ManualResetValueTaskSourceCore<ReadResult> _core = new() { RunContinuationsAsynchronously = false };
        private bool _active, _completionSet, _consumed;
        private short _token;
        internal long Reads, Resets, SourceCompletions, SourceGetResults, Advances, ContinuationRegistrations, TokenWraps;

        internal ControlledReader(bool incomplete) => _incomplete = incomplete;

        internal bool IsPending
        {
            get
            {
                CheckThread();
                return _active && !_completionSet && _core.GetStatus(_token) == ValueTaskSourceStatus.Pending;
            }
        }

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            VerifyIdle();
            if (cancellationToken.CanBeCanceled)
                throw new InvalidOperationException("Cancellation is outside this control shape");
            _core.Reset();
            _token = _core.Version;
            if (_token == 0)
                TokenWraps++;
            _active = true;
            _completionSet = _consumed = false;
            Reads++;
            Resets++;
            if (!_incomplete)
                CompletePending();
            return new ValueTask<ReadResult>(this, _token);
        }

        internal void CompletePending()
        {
            CheckThread();
            if (!_active || _completionSet || _consumed)
                throw new InvalidOperationException("Source completion outside its single active lifetime");
            _completionSet = true;
            SourceCompletions++;
            _core.SetResult(_result);
            CheckThread();
        }

        public ReadResult GetResult(short token)
        {
            CheckToken(token);
            if (!_completionSet || _consumed)
                throw new InvalidOperationException("Source result consumed before completion or more than once");
            ReadResult result = _core.GetResult(token);
            _consumed = true;
            SourceGetResults++;
            return result;
        }

        public ValueTaskSourceStatus GetStatus(short token)
        {
            CheckToken(token);
            return _core.GetStatus(token);
        }

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            CheckToken(token);
            if (_completionSet || _consumed)
                throw new InvalidOperationException("Unexpected continuation registration after source completion");
            ContinuationRegistrations++;
            _core.OnCompleted(continuation, state, token, flags);
        }

        public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            CheckThread();
            if (!_active || !_completionSet || !_consumed
                || !consumed.Equals(_result.Buffer.End) || !examined.Equals(_result.Buffer.End))
                throw new InvalidOperationException("Advance must follow exactly one result consumption and consume the full buffer");
            Advances++;
            _active = false;
        }

        internal void VerifyIdle()
        {
            CheckThread();
            if (_active)
                throw new InvalidOperationException("Reset or verification before prior GetResult and AdvanceTo");
        }

        private void CheckToken(short token)
        {
            CheckThread();
            if (!_active || token != _token)
                throw new InvalidOperationException("ValueTask source token lifetime mismatch");
        }

        private void CheckThread()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Source or continuation changed threads");
        }

        public override bool TryRead(out ReadResult result) => throw new NotSupportedException("Control uses ReadAsync only");
        public override void CancelPendingRead() => throw new NotSupportedException("Cancellation is outside this control shape");
        public override void Complete(Exception? exception = null) => throw new NotSupportedException("Reader shutdown is outside this control shape");
    }
}
