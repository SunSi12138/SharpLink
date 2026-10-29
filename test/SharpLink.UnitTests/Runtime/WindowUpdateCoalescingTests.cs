using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.IO.Pipelines;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel]
public partial class WindowUpdateCoalescingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task HundredSelectedUpdatesKeepOwnersUntilOnePhysicalFlush()
    {
        await using var fixture = new Fixture();
        var parked = await fixture.ParkAsync();
        var owners = new List<CountingOwner>();
        for (var index = 0; index < 100; index++)
        {
            var owner = new CountingOwner(Update(1, 1, 4));
            owners.Add(owner);
            fixture.Enqueue(new OwnedFrame(owner, false, null, true,
                isCoalescibleWindowUpdate: true));
        }
        Ensure(fixture.Session.QueuedSendBytes == parked.Bytes.Length + 2100,
            "all original reservations must remain charged before the drain");
        parked.Succeed();
        var batch = await fixture.Writer.NextAsync();
        Ensure(batch.Bytes.AsSpan().SequenceEqual(Update(1, 1, 400)),
            "100 adjacent updates must encode one original-protocol frame totaling 400");
        Ensure(fixture.Writer.FlushCount == 2 && fixture.Session.QueuedSendBytes == 2100,
            "compaction must neither add a flush nor refund original queue charges early");
        foreach (var owner in owners)
            Ensure(owner.WrittenCount == 21 && owner.ReturnCount == 0,
                "every original owner must remain live during physical flush");
        batch.Succeed();
        await WaitUntilAsync(() => fixture.Session.QueuedSendBytes == 0);
        foreach (var owner in owners)
        {
            EnsureReturned(owner);
            Ensure(owner.ReturnCount == 1, "every original owner must return exactly once");
        }
        Ensure(fixture.PendingCandidateCount == 0, "release must reset batch-only eligibility");
    }

    [Test]
    public async Task OnlyInternalCreditEmitterOptsInAndSingletonStillFlushesImmediately()
    {
        await using var fixture = new Fixture();
        fixture.Session.SendWindowUpdate(1, 1, 4);
        var singleton = await fixture.Writer.NextAsync();
        Ensure(singleton.Bytes.AsSpan().SequenceEqual(Update(1, 1, 4)),
            "one update must reach FlushAsync without another consume, queue item, or timer");
        fixture.Session.SendWindowUpdate(2, 1, 4);
        fixture.Session.SendWindowUpdate(2, 1, 4);
        fixture.Session.SendPacket(fixture.CreateOwner(Update(2, 1, 4)));
        fixture.Session.SendWindowUpdate(2, 1, 4);
        fixture.Session.SendWindowUpdate(2, 1, 4);
        singleton.Succeed();
        var batch = await fixture.Writer.NextAsync();
        var expected = Join(Update(2, 1, 8), Update(2, 1, 4), Update(2, 1, 8));
        Ensure(batch.Bytes.AsSpan().SequenceEqual(expected),
            "canonical arbitrary packets must stay raw and separate trusted runs");
        batch.Succeed();
        await WaitUntilAsync(() => fixture.Session.QueuedSendBytes == 0);
    }

    [Test]
    [Arguments("key")]
    [Arguments("stream")]
    [Arguments("type")]
    [Arguments("fence")]
    [Arguments("force")]
    [Arguments("waiter")]
    [Arguments("observer")]
    [Arguments("untrusted")]
    [Arguments("flags")]
    [Arguments("zero-id")]
    [Arguments("zero-credit")]
    [Arguments("large-credit")]
    [Arguments("short")]
    [Arguments("long")]
    [Arguments("payload-length")]
    [Arguments("magic")]
    [Arguments("multiple-frames")]
    public async Task EveryBoundaryAndMalformedOriginalStaysUnchanged(string boundary)
    {
        await using var fixture = new Fixture();
        var parked = await fixture.ParkAsync();
        var middle = Update(1, 1, 9);
        var force = false;
        TaskCompletionSource<bool>? waiter = null;
        var observer = new FailureObserver();
        IRequestEmissionFailureObserver? failureObserver = null;
        var trusted = true;
        switch (boundary)
        {
            case "key": middle = Update(2, 1, 9); break;
            case "stream": middle = Update(1, 2, 9); break;
            case "type": middle = Ping(); break;
            case "fence": middle = []; break;
            case "force": force = true; break;
            case "waiter": waiter = new(TaskCreationOptions.RunContinuationsAsynchronously); break;
            case "observer": failureObserver = observer; break;
            case "untrusted": trusted = false; break;
            case "flags": middle[6] = (byte)ProtocolV2FrameFlags.Error; break;
            case "zero-id": middle.AsSpan(7, 8).Clear(); break;
            case "zero-credit": middle.AsSpan(17, 4).Clear(); break;
            case "large-credit": BinaryPrimitives.WriteUInt32LittleEndian(middle.AsSpan(17), 0x80000000); break;
            case "short": middle = middle[..^1]; break;
            case "long": middle = Join(middle, new byte[1]); break;
            case "payload-length": middle[1] = 5; break;
            case "magic": middle[0] = 0; break;
            case "multiple-frames": middle = Join(middle, Update(1, 1, 10)); break;
            default: throw new ArgumentOutOfRangeException(nameof(boundary));
        }
        fixture.EnqueueUpdate(1);
        fixture.EnqueueUpdate(2);
        var middleOwner = fixture.CreateOwner(middle);
        fixture.Enqueue(new OwnedFrame(middleOwner, force, waiter, true, failureObserver, trusted));
        fixture.EnqueueUpdate(3);
        fixture.EnqueueUpdate(4);
        parked.Succeed();
        var batch = await fixture.Writer.NextAsync();
        Ensure(batch.Bytes.AsSpan().SequenceEqual(Join(Update(1, 1, 3), middle, Update(1, 1, 7))),
            $"{boundary} must remain an unchanged boundary, not disappear or throw in the encoder");
        Ensure(waiter is null || !waiter.Task.IsCompleted, "flush waiter must await physical completion");
        Ensure(observer.Count == 0, "successful serialization must not invoke failure observers");
        batch.Succeed();
        await WaitUntilAsync(() => fixture.Session.QueuedSendBytes == 0);
        Ensure(waiter is null || waiter.Task.IsCompletedSuccessfully, "original waiter must complete on flush");
        EnsureReturned(middleOwner);
    }

    [Test]
    public async Task MaximumCreditStartsAnotherGroupInsteadOfOverflowing()
    {
        await using var fixture = new Fixture();
        var parked = await fixture.ParkAsync();
        fixture.EnqueueUpdate(int.MaxValue - 1);
        fixture.EnqueueUpdate(1);
        fixture.EnqueueUpdate(1);
        fixture.EnqueueUpdate(2);
        parked.Succeed();
        var batch = await fixture.Writer.NextAsync();
        Ensure(batch.Bytes.AsSpan().SequenceEqual(Join(Update(1, 1, int.MaxValue), Update(1, 1, 3))),
            "Int32.MaxValue is valid and the following positive credit must start a new group");
        batch.Succeed();
    }

    [Test]
    public async Task LowLatencyStillFlushesEveryOriginalUpdate()
    {
        await using var fixture = new Fixture(SharpLinkPerformanceProfile.LowLatency);
        var parked = await fixture.ParkAsync();
        for (var index = 0; index < 3; index++)
            fixture.Session.SendWindowUpdate(1, 1, 4);
        parked.Succeed();
        for (var index = 0; index < 3; index++)
        {
            var batch = await fixture.Writer.NextAsync();
            Ensure(batch.Bytes.AsSpan().SequenceEqual(Update(1, 1, 4)),
                "LowLatency must retain its existing per-frame physical flush");
            batch.Succeed();
        }
        await WaitUntilAsync(() => fixture.Session.QueuedSendBytes == 0);
        Ensure(fixture.Writer.FlushCount == 4 && fixture.PendingCandidateCount == 0,
            "singleton releases must reset the candidate count each time");
    }

    [Test]
    public async Task ProgressBurstBudgetStillCountsOriginalFrames()
    {
        await using var fixture = new Fixture();
        var parked = await fixture.ParkAsync();
        for (var index = 0; index < 300; index++)
            fixture.Session.SendWindowUpdate(1, 1, 1);
        var normal = Frame(ProtocolV2FrameType.Response, 2, new byte[1]);
        parked.Succeed();
        var first = await fixture.Writer.NextAsync();
        Ensure(first.Bytes.AsSpan().SequenceEqual(Update(1, 1, 256)),
            "the first progress burst must stop after 256 original frames, not 256 encoded groups");
        // The parked initial ping's loop-top flush precedes its normal turn. Queue this
        // normal frame only while the actual 256-update burst is physically held.
        fixture.Session.SendPacket(fixture.CreateOwner(normal));
        first.Succeed();
        var second = await fixture.Writer.NextAsync();
        Ensure(second.Bytes.AsSpan().SequenceEqual(normal),
            "normal traffic must be served before the remaining progress burst");
        second.Succeed();
        var third = await fixture.Writer.NextAsync();
        Ensure(third.Bytes.AsSpan().SequenceEqual(Update(1, 1, 44)), "the remaining originals must retain their credit");
        third.Succeed();
    }

    [Test]
    public async Task FailedPhysicalFlushNeverReplaysAndReleasesEveryOriginal()
    {
        await using var fixture = new Fixture();
        var parked = await fixture.ParkAsync();
        var owners = new List<IRpcByteBufferWriter>();
        for (var index = 0; index < 3; index++)
        {
            var owner = fixture.CreateOwner(Update(1, 1, 4));
            owners.Add(owner);
            fixture.Enqueue(new OwnedFrame(owner, false, null, true, isCoalescibleWindowUpdate: true));
        }
        parked.Succeed();
        var batch = await fixture.Writer.NextAsync();
        Ensure(batch.Bytes.AsSpan().SequenceEqual(Update(1, 1, 12)), "the transport may already have observed compact bytes");
        batch.Fail(new IOException("injected failure after transport accepted a prefix"));
        await WaitUntilAsync(() => fixture.Session.QueuedSendBytes == 0 && !fixture.Session.IsConnected);
        foreach (var owner in owners)
            EnsureReturned(owner);
        Ensure(fixture.Writer.FlushCount == 2, "a failed partial write must not replay either compact or original bytes");
        Ensure(fixture.PendingCandidateCount == 0, "terminal release must reset candidate metadata");
    }

    [Test]
    public async Task SentByteTelemetryCountsCompactBytesNotOriginalReservations()
    {
        long sent = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == "sharplink.transport.bytes.sent")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref sent, value));
        listener.Start();
        await using var fixture = new Fixture();
        var parked = await fixture.ParkAsync();
        fixture.EnqueueUpdate(1);
        fixture.EnqueueUpdate(2);
        fixture.EnqueueUpdate(3);
        parked.Succeed();
        var batch = await fixture.Writer.NextAsync();
        Ensure(Interlocked.Read(ref sent) == parked.Bytes.Length + 21,
            "sent-byte telemetry must describe actual encoded transport bytes");
        Ensure(fixture.Session.QueuedSendBytes == 63, "reservation telemetry/accounting must still retain all originals");
        batch.Succeed();
    }

    [Test]
    public async Task OwnedFrameSizeMatchesTheExactBaselineLayout()
    {
        Console.WriteLine($"OwnedFrame baseline={Unsafe.SizeOf<BaselineOwnedFrame>()}, candidate={Unsafe.SizeOf<OwnedFrame>()}");
        Ensure(Unsafe.SizeOf<OwnedFrame>() == Unsafe.SizeOf<BaselineOwnedFrame>(),
            $"OwnedFrame must not grow: baseline={Unsafe.SizeOf<BaselineOwnedFrame>()}, candidate={Unsafe.SizeOf<OwnedFrame>()}");
        await Task.CompletedTask;
    }

    // Field types/order mirror baseline 3d76a3a8's actual OwnedFrame declaration, including
    // auto-property backing fields. This is a target-runtime layout check, not an estimate.
    private readonly struct BaselineOwnedFrame
    {
        private readonly object? _completionState;
        internal BaselineOwnedFrame(IRpcByteBufferWriter owner)
        {
            _completionState = null;
            Owner = owner;
            Memory = owner.WrittenMemory;
            Length = owner.WrittenCount;
            ForceFlush = false;
            IsProtocolProgress = true;
        }
        public IRpcByteBufferWriter Owner { get; }
        public ReadOnlyMemory<byte> Memory { get; }
        public int Length { get; }
        public bool ForceFlush { get; }
        public bool IsProtocolProgress { get; }
        public object? CompletionState => _completionState;
    }

    private sealed class FailureObserver : IRequestEmissionFailureObserver
    {
        internal int Count;
        public void OnRequestEmissionFailure(long requestId, Exception exception) => Count++;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly SharpLinkRuntimeContext _context;
        internal GateWriter Writer { get; } = new();
        internal RpcSession Session { get; }
        private object Pump => typeof(RpcSession).GetField("_pump", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Session) ?? throw new Exception("pump not started");
        internal int PendingCandidateCount => (int)Pump.GetType()
            .GetField("_pendingCoalescibleWindowUpdateCount", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Pump)!;
        internal RpcSessionFlushPolicyState Policy => (RpcSessionFlushPolicyState)Pump.GetType()
            .GetField("_flushPolicyState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Pump)!;

        internal Fixture(SharpLinkPerformanceProfile profile = SharpLinkPerformanceProfile.Balanced)
        {
            _context = new SharpLinkRuntimeContextBuilder()
                .Configure(options => options.PerformanceProfile = profile)
                .Build(includeGeneratedAssemblyCatalog: false);
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport(
                "window-update-coalescing", _input.Reader, Writer,
                RpcSessionTestFixture.ClientOptions(_context));
        }

        internal async Task<FlushCall> ParkAsync()
        {
            Session.SendPacket(CreateOwner(Ping()));
            return await Writer.NextAsync();
        }

        internal IRpcByteBufferWriter CreateOwner(byte[] bytes)
        {
            var owner = _context.Buffers.Rent();
            bytes.CopyTo(owner.GetSpan(bytes.Length));
            owner.Advance(bytes.Length);
            return owner;
        }

        internal void EnqueueUpdate(int credit)
            => Enqueue(new OwnedFrame(CreateOwner(Update(1, 1, credit)), false, null, true,
                isCoalescibleWindowUpdate: true));

        internal void Enqueue(OwnedFrame frame)
        {
            var result = Pump.GetType().GetMethod("TryEnqueue", BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(Pump, [frame]);
            Ensure(result?.ToString() == "Accepted", "the test frame must acquire normal pump ownership");
        }

        public async ValueTask DisposeAsync()
        {
            Writer.ReleaseAll();
            await Session.DisposeAsync();
            await _input.Writer.CompleteAsync();
            _context.Dispose();
        }
    }

    private sealed class FlushCall(byte[] bytes)
    {
        internal byte[] Bytes { get; } = bytes;
        internal TaskCompletionSource<FlushResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Succeed() => Completion.TrySetResult(new FlushResult(false, false));
        internal void Fail(Exception exception) => Completion.TrySetException(exception);
    }

    private sealed class GateWriter : PipeWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        private readonly Channel<FlushCall> _calls = Channel.CreateUnbounded<FlushCall>();
        private readonly ConcurrentQueue<FlushCall> _all = new();
        private int _released;
        private int _flushCount;
        internal int FlushCount => Volatile.Read(ref _flushCount);
        internal async Task<FlushCall> NextAsync() => await _calls.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        public override void Advance(int bytes) => _buffer.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => _buffer.GetSpan(sizeHint);
        public override void CancelPendingFlush() { }
        public override void Complete(Exception? exception = null) { }
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            var call = new FlushCall(_buffer.WrittenSpan.ToArray());
            _buffer.Clear();
            Interlocked.Increment(ref _flushCount);
            _all.Enqueue(call);
            _calls.Writer.TryWrite(call);
            if (Volatile.Read(ref _released) != 0)
                call.Succeed();
            return new ValueTask<FlushResult>(call.Completion.Task);
        }
        internal void ReleaseAll()
        {
            Volatile.Write(ref _released, 1);
            foreach (var call in _all)
                call.Succeed();
        }
    }

    private static byte[] Update(ulong requestId, ushort streamId, int credit)
    {
        var payload = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, streamId);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(2), credit);
        return Frame(ProtocolV2FrameType.WindowUpdate, requestId, payload);
    }

    private static byte[] Ping() => Frame(ProtocolV2FrameType.Ping, 0, new byte[8]);

    private static byte[] Frame(ProtocolV2FrameType type, ulong requestId, byte[] payload)
    {
        var bytes = new byte[ProtocolV2Constants.HeaderBytes + payload.Length];
        bytes[0] = ProtocolV2Constants.Magic;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), payload.Length);
        bytes[5] = (byte)type;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(7), requestId);
        payload.CopyTo(bytes.AsSpan(ProtocolV2Constants.HeaderBytes));
        return bytes;
    }

    private static byte[] Join(params byte[][] frames) => frames.SelectMany(static frame => frame).ToArray();

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!predicate())
            await Task.Delay(1, timeout.Token);
    }

    private static void EnsureReturned(IRpcByteBufferWriter owner)
    {
        try { _ = owner.WrittenCount; }
        catch (ObjectDisposedException) { return; }
        throw new Exception("the original owner was not returned after physical completion");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
