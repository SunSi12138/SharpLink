using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks.Sources;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace Issue739SendPump;

internal static class Program
{
    internal const string Source = "eb99fe887cf2129d9b88441245ca0a4a6406b6c2";

    private static void Main(string[] args)
    {
        if (args.Length != 6) throw new ArgumentException("case cycles warmup sample output expected-runtime");
        string kind = args[0];
        int cycles = int.Parse(args[1]), warmup = int.Parse(args[2]);
        if (cycles is < 1 or > 1048576 || warmup is < 1 or > 65536)
            throw new ArgumentException("Bounded positive counts required");
        Require(Environment.Version.ToString() == args[5], "Runtime version mismatch");
        Require(Environment.GetEnvironmentVariable("ISSUE739_SOURCE_SHA") == Source, "Source identity mismatch");
        Require(SynchronizationContext.Current is null && !ExecutionContext.IsFlowSuppressed(), "Ordinary console context required");
        var field = typeof(PooledByteBufferWriter).GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance);
        Require(field is not null && field.FieldType == typeof(int), "Source-pinned lease observer field mismatch");
        using var driver = new Driver(kind);
        driver.Run(warmup);
        var warmupCounts = driver.Capture();
        using var process = Process.GetCurrentProcess();
        _ = process.TotalProcessorTime;
        _ = GC.GetTotalAllocatedBytes(precise: true);
        _ = Stopwatch.GetTimestamp();
        Thread.Sleep(1000);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        int threadStart = Environment.CurrentManagedThreadId;
        var cpuStart = process.TotalProcessorTime;
        int gen0Start = GC.CollectionCount(0);
        long bytesStart = GC.GetTotalAllocatedBytes(precise: true);
        long start = Stopwatch.GetTimestamp();
        driver.Run(cycles);
        long end = Stopwatch.GetTimestamp();
        long bytesEnd = GC.GetTotalAllocatedBytes(precise: true);
        int gen0End = GC.CollectionCount(0);
        var cpuEnd = process.TotalProcessorTime;
        int threadEnd = Environment.CurrentManagedThreadId;
        var counts = driver.Capture() - warmupCounts;
        Require(threadStart == threadEnd, "Controller changed threads");
        Require(counts.Cycles == cycles && counts.Frames == (long)cycles * driver.FramesPerCycle,
            "Measured lifecycle denominator mismatch");
        double seconds = (end - start) / (double)Stopwatch.Frequency;
        string executable = typeof(Program).Assembly.Location, runtimeAssembly = typeof(RpcSession).Assembly.Location;
        string corelib = typeof(object).Assembly.Location;
        ThreadPool.GetMinThreads(out int workers, out int io);
        var row = new
        {
            schemaVersion = 1, kind, sample = args[3], processId = Environment.ProcessId, sourceSha = Source,
            runtime = Environment.Version.ToString(), expectedRuntime = args[5], framework = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), os = RuntimeInformation.OSDescription,
            processorCount = Environment.ProcessorCount, serverGc = System.Runtime.GCSettings.IsServerGC,
            minimumWorkers = workers, minimumIo = io,
            tieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            readyToRun = Environment.GetEnvironmentVariable("DOTNET_ReadyToRun"),
            cycles, warmup, framesPerCycle = driver.FramesPerCycle, frames = counts.Frames,
            frameBytes = driver.FrameBytes, queueCapacity = driver.Capacity, fixtureOnly = driver.FixtureOnly,
            forceFlushCompletionContract = driver.ForceFlush, controlledIncompleteFlush = driver.Gated,
            profile = "Balanced", explicitTimedBatch = false, callerCancellationToken = "None",
            driverPolicy = "synchronous polling controller; no caller async continuation registration; not ordinary async API caller cost",
            completionPolicy = "controlled source allows inline completion; release after core.OnCompleted returned, accepted registration observed and registration-inflight zero",
            idleWaitObservation = "PreParked/PostParked count idle-wait publication observations, not fully unwound callback stacks; residual idle-registration bookkeeping may cross the measurement boundary",
            leaseObserver = "read-only source-pinned UnsafeAccessor PooledByteBufferWriter._active; exact lease refs observed 1 then 0 before subsequent rents; not Return-call count or resource reclamation",
            counts, warmupCounts, writerPendingEnd = driver.WriterPendingEnd,
            writerRegistrationInFlightEnd = driver.WriterRegistrationInFlightEnd, writerConsumedEnd = driver.WriterConsumedEnd,
            maximumRegistrationInFlight = driver.MaximumRegistrationInFlight,
            bytes = bytesEnd - bytesStart, bytesPerCycle = (bytesEnd - bytesStart) / (double)cycles,
            bytesPerFrame = (bytesEnd - bytesStart) / (double)counts.Frames,
            ticksStart = start, ticksEnd = end, stopwatchFrequency = Stopwatch.Frequency, elapsedSeconds = seconds,
            nanosecondsPerCycle = seconds * 1e9 / cycles, nanosecondsPerFrame = seconds * 1e9 / counts.Frames,
            cpuSeconds = (cpuEnd - cpuStart).TotalSeconds, gen0Collections = gen0End - gen0Start,
            threadStart, threadEnd, precise = true, driverIncluded = true, subtractionApplied = false,
            executableSha256 = Hash(executable), runtimeAssemblySha256 = Hash(runtimeAssembly),
            corelibSha256 = Hash(corelib), corelibPath = corelib,
            interpretation = "controlled normal Response send-pump shape only; force-flush contract separately labeled; fixture gross bytes retained; no default RPC ownership, multiplicity, cancellation/fault, socket/SHM or NativeAOT claim"
        };
        File.WriteAllText(args[4], JsonSerializer.Serialize(row, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{kind}: {cycles} cycles, {counts.Frames} frames, {counts.Writer.PendingFlushes} pending flushes, {counts.SecondIncomplete} incomplete admissions, {seconds:F6}s; lifecycle verified");
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_active")]
    internal static extern ref int LeaseActive(PooledByteBufferWriter writer);
}

internal readonly record struct WriterCounts(long Flushes, long PendingFlushes, long Resets, long RegistrationEntries,
    long RegistrationsAccepted, long RegistrationsReturned, long Releases, long GetResults, long Bytes, long Frames,
    long SyncFlushes, long TokenWraps)
{
    public static WriterCounts operator -(WriterCounts a, WriterCounts b) => new(a.Flushes-b.Flushes,
        a.PendingFlushes-b.PendingFlushes,a.Resets-b.Resets,a.RegistrationEntries-b.RegistrationEntries,
        a.RegistrationsAccepted-b.RegistrationsAccepted,a.RegistrationsReturned-b.RegistrationsReturned,
        a.Releases-b.Releases,a.GetResults-b.GetResults,a.Bytes-b.Bytes,a.Frames-b.Frames,
        a.SyncFlushes-b.SyncFlushes,a.TokenWraps-b.TokenWraps);
}

internal readonly record struct Counts(long Cycles, long Frames, long LeaseActiveObservations, long LeaseReturnedObservations,
    long PreParked, long PostParked, long FirstAdmissionsCompleted, long SecondCompleted, long SecondIncomplete,
    long ForceCompletionConsumed, long HeldQueueChecks, WriterCounts Writer)
{
    public static Counts operator -(Counts a, Counts b) => new(a.Cycles-b.Cycles,a.Frames-b.Frames,
        a.LeaseActiveObservations-b.LeaseActiveObservations,a.LeaseReturnedObservations-b.LeaseReturnedObservations,
        a.PreParked-b.PreParked,a.PostParked-b.PostParked,a.FirstAdmissionsCompleted-b.FirstAdmissionsCompleted,
        a.SecondCompleted-b.SecondCompleted,a.SecondIncomplete-b.SecondIncomplete,a.ForceCompletionConsumed-b.ForceCompletionConsumed,
        a.HeldQueueChecks-b.HeldQueueChecks,a.Writer-b.Writer);
}

internal sealed class Driver : IDisposable
{
    private readonly SharpLinkRuntimeContext _context;
    private readonly RpcSession? _session;
    private readonly Pipe? _input;
    private readonly ControlledWriter _writer;
    private readonly bool _capacityIncomplete;
    private long _cycles, _frames, _active, _returned, _preParked, _postParked, _firstCompleted,
        _secondCompleted, _secondIncomplete, _forceConsumed, _heldChecks;
    private static readonly Action<object?> FixtureContinuation = static _ => { };
    public bool FixtureOnly { get; }
    public bool Gated { get; }
    public bool ForceFlush { get; }
    public int FramesPerCycle { get; }
    public int FrameBytes { get; }
    public int Capacity { get; }
    public int WriterPendingEnd => _writer.Pending;
    public int WriterRegistrationInFlightEnd => _writer.RegistrationInFlight;
    public int WriterConsumedEnd => _writer.Consumed;
    public int MaximumRegistrationInFlight => _writer.MaximumRegistrationInFlight;

    public Driver(string kind)
    {
        (FixtureOnly, Gated, ForceFlush, FramesPerCycle, _capacityIncomplete) = kind switch
        {
            "pump-idle-sync" => (false,false,false,1,false),
            "pump-force-sync" => (false,false,true,1,false),
            "pump-flush-gated" => (false,true,false,1,false),
            "pump-capacity-completed" => (false,true,false,2,false),
            "pump-capacity-incomplete" => (false,true,false,2,true),
            "fixture-sync" => (true,false,false,1,false),
            "fixture-gated" => (true,true,false,1,false),
            "fixture-pair" => (true,true,false,2,false),
            _ => throw new ArgumentException("Unknown send-pump case")
        };
        FrameBytes = ProtocolV2Constants.HeaderBytes;
        Capacity = (_capacityIncomplete ? 1 : 2) * FrameBytes;
        Program.Require(Capacity < 32768 && FrameBytes <= Capacity, "No reserve/oversize admission allowed");
        _context = new SharpLinkRuntimeContextBuilder().Configure(options =>
        {
            options.PerformanceProfile = SharpLinkPerformanceProfile.Balanced;
            options.FlowControl.MaxSendQueueBytes = Capacity;
        }).Build(includeGeneratedAssemblyCatalog: false);
        var packet = _context.Buffers.Rent();
        packet.WritePacket(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, 1);
        Program.Require(packet.WrittenCount == FrameBytes, "Serialized frame length mismatch");
        _writer = new ControlledWriter(packet.WrittenMemory.ToArray());
        _context.Buffers.Return(packet);
        if (!FixtureOnly)
        {
            _input = new Pipe();
            _session = new RpcSession(new Connection(_input.Reader, _writer), new RpcSessionCreationOptions(RpcSessionRole.Client, _context));
            Program.Require(_session.TryCompleteHandshake(new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.None, _context.Protocol.MaxFramePayloadBytes, _context.FlowControl.StreamReceiveWindowBytes,
                _context.FlowControl.ConnectionReceiveWindowBytes)), "Handshake failed");
            // Start lazy pump with a real normal frame. This setup cycle is entirely outside all warmup/measurement counters.
            var first = Rent();
            Consume(_session.SendPacketWithBackpressureAsync(first, CancellationToken.None));
            WaitParked(); CheckLease(first, false);
            _writer.ClearSetupCounts();
            _frames = _active = _returned = 0;
        }
    }

    public Counts Capture() => new(_cycles,_frames,_active,_returned,_preParked,_postParked,_firstCompleted,
        _secondCompleted,_secondIncomplete,_forceConsumed,_heldChecks,_writer.Capture());

    public void Run(int cycles)
    {
        try
        {
            for (int i = 0; i < cycles; i++) RunCycle();
        }
        catch (Exception error)
        {
            // Preserve the original lifecycle failure even if teardown also fails.
            Console.Error.WriteLine("Lifecycle failure before teardown: " + error);
            throw;
        }
    }

    private void RunCycle()
    {
        if (_session is not null) { WaitParked(); _preParked++; }
        var before = _writer.Capture();
        _writer.BeginCycle(Gated);
        var first = Rent();
        ValueTask firstSend = default;
        ValueTask<FlushResult> firstFixtureFlush = default;
        if (FixtureOnly)
        {
            firstFixtureFlush = CopyAndFlush(first);
            if (Gated)
            {
                Program.Require(!firstFixtureFlush.IsCompleted, "Fixture flush did not start incomplete");
                firstFixtureFlush.GetAwaiter().UnsafeOnCompleted(FixtureSignal);
            }
        }
        else
        {
            firstSend = ForceFlush ? _session!.SendPacketAndFlushAsync(first, CancellationToken.None)
                : _session!.SendPacketWithBackpressureAsync(first, CancellationToken.None);
            if (!ForceFlush)
            {
                Program.Require(firstSend.IsCompletedSuccessfully, "First admission was not synchronous");
                Consume(firstSend); _firstCompleted++;
            }
        }
        IRpcByteBufferWriter? second = null;
        ValueTask secondSend = default;
        if (Gated)
        {
            _writer.WaitRegistered();
            CheckLease(first, true);
            if (_session is not null)
            {
                Program.Require(_session.QueuedSendBytes == FrameBytes, "Held first reservation mismatch"); _heldChecks++;
            }
            if (FramesPerCycle == 2)
            {
                second = Rent();
                if (!FixtureOnly)
                {
                    secondSend = _session!.SendPacketWithBackpressureAsync(second, CancellationToken.None);
                    if (_capacityIncomplete)
                    {
                        Program.Require(!secondSend.IsCompleted && _session.QueuedSendBytes == FrameBytes,
                            "Second admission did not remain incomplete at capacity F"); _secondIncomplete++;
                    }
                    else
                    {
                        Program.Require(secondSend.IsCompletedSuccessfully && _session.QueuedSendBytes == 2L * FrameBytes,
                            "Second admission did not complete at capacity 2F");
                        Consume(secondSend); _secondCompleted++;
                    }
                    _heldChecks++;
                }
                CheckLease(second, true);
            }
            _writer.Release();
        }
        if (FixtureOnly)
        {
            Consume(firstFixtureFlush); _context.Buffers.Return(first);
            if (second is not null) { Consume(CopyAndFlush(second)); _context.Buffers.Return(second); }
        }
        else
        {
            if (ForceFlush) { Consume(firstSend); _forceConsumed++; }
            if (_capacityIncomplete) Consume(secondSend);
            WaitParked(); _postParked++;
        }
        CheckLease(first, false);
        if (second is not null) CheckLease(second, false);
        _writer.VerifyCycle(before, FramesPerCycle, Gated);
        _cycles++;
    }

    // Cached delegate. No closure, task dispatch or caller state machine is created per fixture cycle.
    private static readonly Action FixtureSignal = static () => FixtureContinuation(null);

    private IRpcByteBufferWriter Rent()
    {
        var packet = _context.Buffers.Rent();
        packet.WritePacket(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, 1);
        Program.Require(packet.WrittenCount == FrameBytes, "Frame serialization changed");
        CheckLease(packet, true); _frames++;
        return packet;
    }

    private void CheckLease(IRpcByteBufferWriter packet, bool active)
    {
        Program.Require(packet is PooledByteBufferWriter, "Unexpected frame writer type");
        Program.Require(Volatile.Read(ref Program.LeaseActive((PooledByteBufferWriter)packet)) == (active ? 1 : 0),
            "Exact frame lease state mismatch");
        if (active) _active++; else _returned++;
    }

    private ValueTask<FlushResult> CopyAndFlush(IRpcByteBufferWriter packet)
    {
        packet.WrittenMemory.Span.CopyTo(_writer.GetSpan(FrameBytes));
        _writer.Advance(FrameBytes);
        return _writer.FlushAsync(CancellationToken.None);
    }

    private void WaitParked()
    {
        long deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        int spins = 0;
        while (!_session!.HasPendingSendPumpIdleWait || _session.QueuedSendBytes != 0)
            WaitStep(ref spins, deadline);
    }

    internal static void WaitStep(ref int spins, long deadline)
    {
        Thread.SpinWait(32);
        if ((++spins & 255) == 0)
        {
            if (Stopwatch.GetTimestamp() >= deadline) throw new TimeoutException("Bounded five-second lifecycle wait expired");
            Thread.Yield();
        }
    }

    private static void Consume(ValueTask task)
    {
        long deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        int spins = 0;
        while (!task.IsCompleted) WaitStep(ref spins, deadline);
        task.GetAwaiter().GetResult();
    }

    private static void Consume(ValueTask<FlushResult> task)
    {
        long deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        int spins = 0;
        while (!task.IsCompleted) WaitStep(ref spins, deadline);
        var result = task.GetAwaiter().GetResult();
        Program.Require(!result.IsCanceled && !result.IsCompleted, "Unexpected flush result");
    }

    public void Dispose()
    {
        if (_session is not null) Consume(_session.DisposeAsync());
        if (_input is not null) Consume(_input.Writer.CompleteAsync());
        _writer.Complete();
        _context.Dispose();
    }

    private sealed class Connection(PipeReader input, PipeWriter output) : ITransportConnection
    {
        public string Id => "issue739-controlled-sendpump";
        public PipeReader Input => input;
        public PipeWriter Output => output;
        public EndPoint? LocalEndPoint => null;
        public EndPoint? RemoteEndPoint => null;
        public ValueTask DisposeAsync() { output.Complete(); input.Complete(); return ValueTask.CompletedTask; }
    }
}

internal sealed class ControlledWriter(byte[] expectedFrame) : PipeWriter, IValueTaskSource<FlushResult>
{
    private readonly byte[] _buffer = new byte[4096];
    private ManualResetValueTaskSourceCore<FlushResult> _core = new() { RunContinuationsAsynchronously = false };
    private int _written, _gateNext, _pending, _registrationInFlight, _registrationReturned, _consumed = 1;
    private int _maximumRegistrationInFlight;
    public int Pending => Volatile.Read(ref _pending);
    public int RegistrationInFlight => Volatile.Read(ref _registrationInFlight);
    public int Consumed => Volatile.Read(ref _consumed);
    public int MaximumRegistrationInFlight => _maximumRegistrationInFlight;
    private long _flushes, _pendingFlushes, _resets, _entries, _accepted, _returned, _releases, _getResults,
        _bytes, _frames, _sync, _wraps;
    public WriterCounts Capture() => new(_flushes,_pendingFlushes,_resets,_entries,_accepted,_returned,_releases,
        _getResults,_bytes,_frames,_sync,_wraps);

    public void ClearSetupCounts()
    {
        Program.Require(_pending == 0 && _written == 0 && _registrationInFlight == 0, "Setup writer not quiescent");
        _flushes = _pendingFlushes = _resets = _entries = _accepted = _returned = _releases = _getResults = _bytes = _frames = _sync = _wraps = 0;
    }

    public void BeginCycle(bool gate)
    {
        Program.Require(Volatile.Read(ref _pending) == 0 && Volatile.Read(ref _consumed) == 1 &&
            Volatile.Read(ref _registrationInFlight) == 0 && _written == 0, "Source reused before prior consumption/registration return");
        Volatile.Write(ref _registrationReturned, 0);
        Volatile.Write(ref _gateNext, gate ? 1 : 0);
    }

    public override void Advance(int bytes)
    {
        Program.Require(bytes >= 0 && bytes <= _buffer.Length - _written, "Advance outside preallocated writer");
        _written += bytes;
    }

    public override Memory<byte> GetMemory(int sizeHint = 0) { EnsureCapacity(sizeHint); return _buffer.AsMemory(_written); }
    public override Span<byte> GetSpan(int sizeHint = 0) { EnsureCapacity(sizeHint); return _buffer.AsSpan(_written); }
    private void EnsureCapacity(int sizeHint) => Program.Require(sizeHint >= 0 && Math.Max(1,sizeHint) <= _buffer.Length-_written,
        "Preallocated writer capacity exceeded");
    public override void CancelPendingFlush() => throw new InvalidOperationException("Unexpected cancel path");
    public override void Complete(Exception? exception = null)
    {
        Program.Require(exception is null && Volatile.Read(ref _pending) == 0, "Unexpected writer teardown");
    }

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Program.Require(_written == expectedFrame.Length && _buffer.AsSpan(0,_written).SequenceEqual(expectedFrame),
            "Flush must contain exactly one fixed normal Response frame");
        _bytes += _written; _frames++; _written = 0; _flushes++;
        if (Interlocked.Exchange(ref _gateNext, 0) == 0)
        {
            _sync++;
            return ValueTask.FromResult(new FlushResult(false,false));
        }
        Program.Require(Volatile.Read(ref _consumed) == 1 && Volatile.Read(ref _registrationInFlight) == 0 && _pending == 0,
            "Reset before prior source lifetime ended");
        short previous = _core.Version;
        _core.Reset(); _resets++;
        if (_core.Version < previous) _wraps++;
        Volatile.Write(ref _consumed, 0); Volatile.Write(ref _pending, 1); _pendingFlushes++;
        return new ValueTask<FlushResult>(this, _core.Version);
    }

    public void WaitRegistered()
    {
        long deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        int spins = 0;
        while (Volatile.Read(ref _registrationReturned) != 1 || Volatile.Read(ref _registrationInFlight) != 0)
            Driver.WaitStep(ref spins, deadline);
        Program.Require(Volatile.Read(ref _pending) == 1 && _core.GetStatus(_core.Version) == ValueTaskSourceStatus.Pending,
            "Gate was completed before accepted registration returned");
    }

    public void Release()
    {
        Program.Require(Volatile.Read(ref _registrationReturned) == 1 && Volatile.Read(ref _registrationInFlight) == 0 &&
            Interlocked.CompareExchange(ref _pending, 0, 1) == 1, "Release without returned accepted registration");
        _releases++;
        _core.SetResult(new FlushResult(false,false));
    }

    public FlushResult GetResult(short token)
    {
        var result = _core.GetResult(token);
        Program.Require(Interlocked.Exchange(ref _consumed, 1) == 0, "Source consumed more than once");
        _getResults++;
        return result;
    }
    public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        Program.Require(Interlocked.Increment(ref _registrationInFlight) == 1 && Volatile.Read(ref _registrationReturned) == 0,
            "Duplicate or overlapping source registration");
        _entries++;
        _maximumRegistrationInFlight = 1;
        _core.OnCompleted(continuation,state,token,flags);
        _accepted++;
        // Publish only AFTER the actual reusable core accepted and returned from registration.
        // The wrapper has no core/continuation access after the following bookkeeping.
        _returned++;
        Interlocked.Decrement(ref _registrationInFlight);
        Volatile.Write(ref _registrationReturned,1);
    }

    public void VerifyCycle(WriterCounts before, int frames, bool gated)
    {
        WriterCounts d = Capture()-before;
        int g = gated ? 1 : 0;
        Program.Require(d.Flushes == frames && d.Frames == frames && d.Bytes == (long)frames * expectedFrame.Length &&
            d.SyncFlushes == frames-g && d.PendingFlushes == g && d.Resets == g && d.RegistrationEntries == g &&
            d.RegistrationsAccepted == g && d.RegistrationsReturned == g && d.Releases == g && d.GetResults == g &&
            Volatile.Read(ref _pending) == 0 && Volatile.Read(ref _consumed) == 1 &&
            Volatile.Read(ref _registrationInFlight) == 0 && _written == 0,
            "Exact per-cycle writer lifecycle mismatch");
    }
}
