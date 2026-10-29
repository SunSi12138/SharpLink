using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SharpLink.UnitTests.Runtime;

public sealed class TypedWindowUpdateTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public void TypedWireMustMatchExistingCodecAtEveryScalarBoundary()
    {
        using var pool = new OutgoingWindowUpdatePool();
        using var encoded = new PooledByteBufferWriter();
        var bytes = new byte[OutgoingWindowUpdate.FrameBytes];
        foreach (var id in new[] { 1L, -1L, long.MinValue, long.MaxValue })
        foreach (var streamId in new ushort[] { 0, ushort.MaxValue })
        foreach (var credit in new[] { 1, int.MaxValue })
        {
            encoded.Clear();
            using (encoded.BeginPacketScope(ProtocolV2FrameType.WindowUpdate, ProtocolV2FrameFlags.None,
                       unchecked((ulong)id)))
                ProtocolV2PayloadCodec.WriteWindowUpdate(encoded, new ProtocolV2WindowUpdate(streamId, (uint)credit));
            var update = pool.Rent(id, streamId, credit);
            try { update.WriteTo(bytes); }
            finally { update.Return(); }
            Ensure(bytes.Length == 21 && bytes.AsSpan().SequenceEqual(encoded.WrittenSpan),
                "typed serialization must be byte-for-byte identical to the existing codec");
            var sequence = new ReadOnlySequence<byte>(bytes);
            Ensure(ProtocolV2FrameParser.TryReadFrame(ref sequence, new SharpLinkProtocolOptions(),
                       out var header, out var payload) && sequence.IsEmpty &&
                   header.Type == ProtocolV2FrameType.WindowUpdate && header.Flags == ProtocolV2FrameFlags.None &&
                   header.RequestId == unchecked((ulong)id), "the ordinary parser must accept the exact typed frame");
            var parsed = ProtocolV2PayloadCodec.ReadWindowUpdate(payload);
            Ensure(parsed.StreamId == streamId && parsed.Credit == (uint)credit, "all scalar bits must survive");
        }
    }

    [Test]
    public async Task TrustedEntryMustRejectInvalidValuesWithoutWeakeningEncodedPacketValidation()
    {
        var fixture = new Fixture();
        try
        {
            Ensure(Capture(() => fixture.Session.SendWindowUpdateCredit(0, 0, 1)) is ArgumentOutOfRangeException &&
                Capture(() => fixture.Session.SendWindowUpdateCredit(1, 0, 0)) is ArgumentOutOfRangeException &&
                Capture(() => fixture.Session.SendWindowUpdateCredit(1, 0, -1)) is ArgumentOutOfRangeException,
                "request ID and canonical positive credit must be checked at the trusted entry");
            var badMagic = fixture.NormalFrame(1);
            badMagic.WrittenSpan[0] = 0;
            Ensure(Capture(() => fixture.Session.SendPacket(badMagic)) is InvalidOperationException,
                "ordinary encoded packets must still reject malformed magic");
            var badLength = fixture.NormalFrame(2);
            BinaryPrimitives.WriteInt32LittleEndian(badLength.WrittenSpan[1..], 0);
            Ensure(Capture(() => fixture.Session.SendPacket(badLength)) is InvalidOperationException,
                "ordinary encoded packets must still reject an inconsistent payload length");
            Ensure(fixture.Session.QueuedSendBytes == 0 && fixture.Context.WindowUpdatePool.RetainedCount == 0 &&
                fixture.Output.FlushCount == 0, "validation failure must not publish or reserve a typed frame");
        }
        finally { await fixture.CleanupAsync(); }
    }

    [Test]
    public async Task TypedCreditsMustRequireFlowControlAndAllowedProtocolPhase()
    {
        var handshaking = new Fixture(handshake: false);
        var noFlow = new Fixture(flowControl: false);
        var ready = new Fixture();
        try
        {
            Ensure(Capture(() => handshaking.Session.SendWindowUpdateCredit(1, 0, 1)) is
                SharpLinkException { Code: SharpLinkErrorCode.ProtocolViolation }, "handshake must reject WU");
            Ensure(Capture(() => noFlow.Session.SendWindowUpdateCredit(1, 0, 1)) is SharpLinkProtocolViolationException,
                "Ready without negotiated flow control must reject WU");
            ready.Session.SendWindowUpdateCredit(1, 0, 1);
            await ready.Output.FirstEntered.Task.WaitAsync(Timeout);
            ready.Session.MarkDraining();
            ready.Session.SendWindowUpdateCredit(-1, ushort.MaxValue, int.MaxValue);
            Ensure(ready.Session.QueuedSendBytes == 42, "Ready and Draining each admit one physical 21-byte frame");
            ready.Output.ReleaseAll();
            await ready.Session.FlushSendQueueAsync().AsTask().WaitAsync(Timeout);
            Ensure(ready.Output.Written.Length == 42 && ready.Context.WindowUpdatePool.RetainedCount == 2,
                "accepted credits must retain two independent ownership leases");
            ready.Session.BeginShutdown();
            Ensure(Capture(() => ready.Session.SendWindowUpdateCredit(2, 0, 1)) is
                SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed }, "Stopping must reject new credit");
            await ready.Session.DisposeAsync().AsTask().WaitAsync(Timeout);
            Ensure(Capture(() => ready.Session.SendWindowUpdateCredit(3, 0, 1)) is
                SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed }, "Terminal must reject new credit");
        }
        finally
        {
            await handshaking.CleanupAsync();
            await noFlow.CleanupAsync();
            await ready.CleanupAsync();
        }
    }

    [Test]
    public async Task TypedEntryMustDefensivelyEnforceSixBytePayloadBoundary()
    {
        var fixture = new Fixture();
        try
        {
            // Legal handshakes require a larger protocol minimum. Inject only the
            // immutable options snapshot to exercise this otherwise unreachable
            // defensive branch; preserve the real negotiated flow controller.
            SetDefensivePayloadLimit(fixture.Session, 5);
            Ensure(Capture(() => fixture.Session.SendWindowUpdateCredit(1, 0, 1)) is
                SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted }, "five bytes cannot hold WU payload");
            Ensure(fixture.Session.QueuedSendBytes == 0, "rejected payload must not reserve bytes");
            SetDefensivePayloadLimit(fixture.Session, 6);
            fixture.Output.ReleaseAll();
            fixture.Session.SendWindowUpdateCredit(1, 0, 1);
            await fixture.Session.FlushSendQueueAsync().AsTask().WaitAsync(Timeout);
            Ensure(fixture.Output.Written.Length == 21, "exactly six payload bytes must be accepted");
        }
        finally { await fixture.CleanupAsync(); }
    }

    [Test]
    public async Task NegotiatedCompressionMustNeverProcessTypedCredit()
    {
        var provider = new RejectCompressionProvider();
        using var context = BuildContext(provider: provider);
        var fixture = new Fixture(context);
        try
        {
            fixture.Output.ReleaseAll();
            fixture.Session.SendWindowUpdateCredit(1, 0, 123);
            await fixture.Session.FlushSendQueueAsync().AsTask().WaitAsync(Timeout);
            Ensure(provider.CompressCount == 0 && fixture.Output.Written.Length == 21 &&
                fixture.Output.Written.Span[6] == 0,
                "even an enabled one-byte compression threshold must leave WU uncompressed");
        }
        finally { await fixture.CleanupAsync(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FirstCreditMustReserveWakeAndRetainNodeUntilFlushOrFault(bool failFlush)
    {
        var fixture = new Fixture(failFirstFlush: failFlush);
        try
        {
            var pool = fixture.Context.WindowUpdatePool;
            fixture.Session.SendWindowUpdateCredit(7, 0, 19);
            await fixture.Output.FirstEntered.Task.WaitAsync(Timeout);
            Ensure(fixture.Session.QueuedSendBytes == 21 && pool.RetainedCount == 0 &&
                fixture.Output.Written.Length == 21, "the first credit must reserve, wake and serialize without waiting for another credit");
            fixture.Output.ReleaseAll();
            if (failFlush)
            {
                var terminal = await fixture.Disconnected.Task.WaitAsync(Timeout);
                Ensure(Contains(terminal, fixture.Output.Failure), "output failure must remain the causal terminal exception");
                Ensure(await CaptureAsync(fixture.Session.DisposeAsync().AsTask()) is null,
                    "a pure output fault adds no owner-cleanup disposal failure");
            }
            else
                await fixture.Session.FlushSendQueueAsync().AsTask().WaitAsync(Timeout);
            Ensure(fixture.Session.QueuedSendBytes == 0 && pool.RetainedCount == 1 &&
                fixture.Output.Written.Length == 21, "flush success and terminal drain each return the node exactly once without replay");
        }
        finally { await fixture.CleanupAsync(); }
    }

    [Test]
    public async Task QueueRejectionMustReturnOnlyUnreservedNodeAndKeepOriginalCreditOwned()
    {
        using var context = BuildContext(queueBytes: 21);
        var fixture = new Fixture(context);
        try
        {
            var pool = context.WindowUpdatePool;
            fixture.Session.SendWindowUpdateCredit(1, 0, 1);
            await fixture.Output.FirstEntered.Task.WaitAsync(Timeout);
            Ensure(Capture(() => fixture.Session.SendWindowUpdateCredit(2, 0, 1)) is
                SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted }, "a second physical frame cannot exceed the byte budget");
            Ensure(fixture.Session.QueuedSendBytes == 21 && pool.RetainedCount == 1,
                "Full returns the unreserved candidate but not the in-flight node");
            fixture.Session.BeginShutdown();
            fixture.Output.ReleaseAll();
            Ensure(await CaptureAsync(fixture.Session.DisposeAsync().AsTask()) is null, "ordinary stop should not invent cleanup failure");
            Ensure(pool.RetainedCount == 2 && fixture.Session.QueuedSendBytes == 0 && fixture.Output.Written.Length == 21,
                "stop releases the remaining reservation and never writes the rejected update");
        }
        finally { await fixture.CleanupAsync(); }
    }

    [Test]
    public async Task ReentrantStopAfterReservationMustReturnUnpublishedTypedNodeOnce()
    {
        var scope = new AsyncLocal<bool> { Value = true };
        var fixture = new Fixture();
        try
        {
            Exception? callbackFailure = null;
            var callbackCount = 0;
            using var listener = ListenForTypedMetric(scope, 21, () =>
            {
                Interlocked.Increment(ref callbackCount);
                try { fixture.Session.BeginShutdown(); }
                catch (Exception exception) { callbackFailure = exception; }
            });
            Ensure(Capture(() => fixture.Session.SendWindowUpdateCredit(1, 0, 1)) is
                SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
                "a stop inside reservation telemetry must reject late queue publication");
            Ensure(await CaptureAsync(fixture.Session.DisposeAsync().AsTask()) is null,
                "reserved-but-unpublished rejection must not create a cleanup fault");
            Ensure(callbackCount == 1 && callbackFailure is null && fixture.Session.QueuedSendBytes == 0 &&
                fixture.Context.WindowUpdatePool.RetainedCount == 1 && fixture.Output.FlushCount == 0 &&
                fixture.Output.Written.IsEmpty,
                "the rejected reservation and node must be released once without physical output");
        }
        finally { await fixture.CleanupAsync(); }
    }

    [Test]
    public async Task MixedThrowingReturnMustNotRecycleReentrantlyRentedTypedNodeTwice()
    {
        var scope = new AsyncLocal<bool> { Value = true };
        var fixture = new Fixture();
        try
        {
            var pool = fixture.Context.WindowUpdatePool;
            var knownNode = pool.Rent(1, 0, 1);
            knownNode.Return();
            var seed = fixture.Session.SendPacketAndFlushAsync(fixture.NormalFrame(1)).AsTask();
            scope.Value = false;
            await fixture.Output.FirstEntered.Task.WaitAsync(Timeout);
            var returnFailure = new InvalidOperationException("mixed encoded owner return");
            var ping = new ThrowingPingWriter(returnFailure);
            fixture.Session.SendWindowUpdateCredit(11, 0, 1);
            fixture.Session.SendPacket(ping);
            fixture.Session.SendWindowUpdateCredit(12, 0, 2);
            fixture.Output.ReleaseFirst();
            await seed.WaitAsync(Timeout);
            await fixture.Output.SecondEntered.Task.WaitAsync(Timeout);
            Exception? callbackFailure = null;
            var reused = false;
            using var listener = ListenForTypedMetric(scope, -21, () =>
            {
                try
                {
                    var returned = pool.Rent(99, 0, 3);
                    reused = ReferenceEquals(returned, knownNode);
                    returned.Return();
                    fixture.Session.SendWindowUpdateCredit(99, 0, 3);
                }
                catch (Exception exception) { callbackFailure = exception; }
            });
            fixture.Output.ReleaseSecond();
            await fixture.Disconnected.Task.WaitAsync(Timeout);
            var cleanup = await CaptureAsync(fixture.Session.DisposeAsync().AsTask());
            fixture.ExpectedCleanupFailure = cleanup;
            Ensure(reused && callbackFailure is null && Contains(cleanup, returnFailure) && ping.ReturnCount == 1,
                "the released typed node must acquire a new queued lease before the mixed owner throws");
            Ensure(fixture.Session.QueuedSendBytes == 0 && pool.RetainedCount == 2 && fixture.Output.FlushCount == 2,
                "old batch slots must not duplicate returned nodes or replay queued credit");
            var first = pool.Rent(1, 0, 1);
            var second = pool.Rent(2, 0, 1);
            try
            {
                Ensure(!ReferenceEquals(first, second) &&
                    (ReferenceEquals(first, knownNode) || ReferenceEquals(second, knownNode)),
                    "the pool must contain two distinct nodes, not duplicate links to a live lease");
            }
            finally { first.Return(); second.Return(); }
        }
        finally { await fixture.CleanupAsync(); }
    }

    [Test]
    public async Task RuntimePoolMustBeSharedBoundedAndDropLateReturnsAfterDisposal()
    {
        using var context = BuildContext();
        var firstSession = new Fixture(context);
        var secondSession = new Fixture(context);
        var pool = context.WindowUpdatePool;
        try
        {
            Ensure(ReferenceEquals(firstSession.Session.RuntimeContext.WindowUpdatePool,
                secondSession.Session.RuntimeContext.WindowUpdatePool), "sessions must not each retain a separate 512-node pool");
            var rented = new OutgoingWindowUpdate[OutgoingWindowUpdatePool.MaximumRetainedNodes + 2];
            for (var index = 0; index < rented.Length; index++) rented[index] = pool.Rent(index + 1, 0, 1);
            foreach (var node in rented) node.Return();
            Ensure(pool.RetainedCount == OutgoingWindowUpdatePool.MaximumRetainedNodes, "runtime-wide idle retention must be bounded");
            var late = pool.Rent(1, 0, 1);
            await firstSession.CleanupAsync();
            await secondSession.CleanupAsync();
            context.Dispose();
            late.Return();
            Ensure(pool.RetainedCount == 0 && Capture(() => pool.Rent(2, 0, 1)) is ObjectDisposedException &&
                Capture(() => _ = context.WindowUpdatePool) is ObjectDisposedException,
                "disposal seals lazy acquisition and late physical returns cannot retain nodes");
        }
        finally
        {
            await firstSession.CleanupAsync();
            await secondSession.CleanupAsync();
        }
    }

    [Test]
    public void TypedTagMustPreserveOwnedFrameSizeAndNormalOwnerContract()
    {
        using var pool = new OutgoingWindowUpdatePool();
        var node = pool.Rent(1, 0, 1);
        try
        {
            var typed = new OwnedFrame(node);
            Ensure(Unsafe.SizeOf<OwnedFrame>() == Unsafe.SizeOf<LegacyOwnedFrame>() && typed.Length == 21 &&
                typed.Owner is null && typed.Memory.IsEmpty && typed.IsProtocolProgress && !typed.ForceFlush &&
                typed.FlushCompletion is null && typed.FailureObserver is null && ReferenceEquals(typed.WindowUpdate, node),
                "typed ownership must reuse the existing union without growing normal queue elements");
            using var writer = new PooledByteBufferWriter();
            var normal = new OwnedFrame(writer, true, null, false);
            Ensure(ReferenceEquals(normal.Owner, writer) && normal.ForceFlush && !normal.IsProtocolProgress,
                "ordinary zero-byte flush markers still own their original writer");
        }
        finally { node.Return(); }
    }

    private static void SetDefensivePayloadLimit(RpcSession session, int bytes)
    {
        var field = typeof(RpcSession).GetField("_protocolState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var current = (RpcSessionProtocolState)field.GetValue(session)!;
        var options = current.Options!;
        field.SetValue(session, new RpcSessionProtocolState(current.Phase,
            new NegotiatedSessionOptions(options.ProtocolMinorVersion, options.Capabilities, bytes,
                options.StreamReceiveWindowBytes, options.ConnectionReceiveWindowBytes, options.CompressionBinding),
            current.FlowController));
    }

    private static MeterListener ListenForTypedMetric(AsyncLocal<bool> scope, long expectedBytes, Action callback)
    {
        var listener = new MeterListener();
        var called = 0;
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "SharpLink" && instrument.Name == "sharplink.send.queue.bytes")
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, bytes, _, _) =>
        {
            if (scope.Value && bytes == expectedBytes && Interlocked.Exchange(ref called, 1) == 0) callback();
        });
        listener.Start();
        return listener;
    }

    private static Exception? Capture(Action action)
    {
        try { action(); return null; }
        catch (Exception exception) { return exception; }
    }

    private static Task<Exception?> CaptureAsync(Task task)
        => ObserveFailureAsync(task).WaitAsync(Timeout);

    private static async Task<Exception?> ObserveFailureAsync(Task task)
    {
        try { await task; return null; }
        catch (Exception exception) { return exception; }
    }

    private static bool Contains(Exception? exception, Exception expected)
    {
        if (ReferenceEquals(exception, expected)) return true;
        if (exception is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions)
                if (Contains(inner, expected)) return true;
        return exception?.InnerException is { } cause && Contains(cause, expected);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static SharpLinkRuntimeContext BuildContext(int queueBytes = 1024, ISharpLinkCompressionProvider? provider = null)
        => new SharpLinkRuntimeContextBuilder().Configure(options =>
        {
            options.FlowControl.MaxSendQueueBytes = queueBytes;
            options.PerformanceProfile = SharpLinkPerformanceProfile.Throughput;
            if (provider is not null) options.Compression.Providers.Add(provider);
        }).Build(includeGeneratedAssemblyCatalog: false);

    private sealed class Fixture
    {
        private readonly Pipe _input = new();
        private readonly bool _ownsContext;
        internal SharpLinkRuntimeContext Context { get; }
        internal RpcSession Session { get; }
        internal GateOutput Output { get; }
        internal Exception? ExpectedCleanupFailure { get; set; }
        internal TaskCompletionSource<Exception?> Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Fixture(SharpLinkRuntimeContext? context = null, bool handshake = true,
            bool flowControl = true, bool failFirstFlush = false)
        {
            _ownsContext = context is null;
            Context = context ?? BuildContext();
            Output = new GateOutput(failFirstFlush);
            var compression = Context.Compression.ProviderBindings.Count != 0;
            var policy = CompressionSendPolicyState.CreateInitial(new SharpLinkCompressionSendPolicy
                { MinimumPayloadBytes = 1, MinimumSavingsBytes = 0, MinimumSavingsRatio = 0 });
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport(Guid.NewGuid().ToString(),
                _input.Reader, Output, new RpcSessionCreationOptions(RpcSessionRole.Client, Context,
                    compressionSendPolicyState: policy), completeHandshake: false);
            Session.OnDisconnected += exception => Disconnected.TrySetResult(exception);
            if (handshake)
                RpcSessionTestFixture.CompleteHandshake(Session,
                    (flowControl ? ProtocolV2Capabilities.FlowControl : ProtocolV2Capabilities.None) |
                    (compression ? ProtocolV2Capabilities.Compression : ProtocolV2Capabilities.None),
                    compressionBinding: compression ? Context.Compression.ProviderBindings[0] : null);
        }

        internal IRpcByteBufferWriter NormalFrame(ulong id)
        {
            var writer = Context.Buffers.Rent();
            using (writer.BeginPacketScope(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, id))
            {
                writer.GetSpan(32)[..32].Clear();
                writer.Advance(32);
            }
            return writer;
        }

        internal async Task CleanupAsync()
        {
            Output.ReleaseAll();
            var failure = await CaptureAsync(Session.DisposeAsync().AsTask());
            await _input.Writer.CompleteAsync();
            if (_ownsContext) Context.Dispose();
            if (failure is not null && !ReferenceEquals(failure, ExpectedCleanupFailure))
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class GateOutput(bool failFirstFlush) : PipeWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        private readonly TaskCompletionSource _firstRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _flushCount;
        internal TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception Failure { get; } = new IOException("typed credit output failure");
        internal ReadOnlyMemory<byte> Written => _buffer.WrittenMemory;
        internal int FlushCount => Volatile.Read(ref _flushCount);
        internal void ReleaseFirst() => _firstRelease.TrySetResult();
        internal void ReleaseSecond() => _secondRelease.TrySetResult();
        internal void ReleaseAll() { ReleaseFirst(); ReleaseSecond(); }
        public override void Advance(int bytes) => _buffer.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => _buffer.GetSpan(sizeHint);
        public override void CancelPendingFlush() => ReleaseAll();
        public override void Complete(Exception? exception = null) => ReleaseAll();
        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref _flushCount);
            if (count == 1)
            {
                FirstEntered.TrySetResult();
                await _firstRelease.Task.WaitAsync(Timeout);
                if (failFirstFlush) throw Failure;
            }
            else if (count == 2)
            {
                SecondEntered.TrySetResult();
                await _secondRelease.Task.WaitAsync(Timeout);
            }
            return new FlushResult(false, false);
        }
    }

    private sealed class ThrowingPingWriter(Exception failure) : IRpcByteBufferWriter
    {
        private readonly PooledByteBufferWriter _inner = CreatePing();
        internal int ReturnCount;
        private static PooledByteBufferWriter CreatePing()
        {
            var writer = new PooledByteBufferWriter();
            using (writer.BeginPacketScope(ProtocolV2FrameType.Ping, ProtocolV2FrameFlags.None, 0))
            {
                writer.GetSpan(8)[..8].Clear();
                writer.Advance(8);
            }
            return writer;
        }
        public int WrittenCount => _inner.WrittenCount;
        public ReadOnlyMemory<byte> WrittenMemory => _inner.WrittenMemory;
        public Span<byte> WrittenSpan => _inner.WrittenSpan;
        public int Capacity => _inner.Capacity;
        public void Advance(int count) => _inner.Advance(count);
        public void Clear() => _inner.Clear();
        public Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
        public void Dispose() { Interlocked.Increment(ref ReturnCount); _inner.Dispose(); throw failure; }
    }

    private sealed class RejectCompressionProvider : ISharpLinkCompressionProvider
    {
        internal int CompressCount;
        public string WireProfile => "test-typed-credit/v1";
        public bool TryCompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output,
            int maxOutputBytes, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CompressCount);
            throw new InvalidOperationException("WindowUpdate reached compression");
        }
        public void Decompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output,
            int maxOutputBytes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private readonly struct LegacyOwnedFrame(IRpcByteBufferWriter owner, bool forceFlush,
        TaskCompletionSource<bool>? completion, bool progress)
    {
        private readonly object? _completionState = completion;
        public IRpcByteBufferWriter Owner { get; } = owner;
        public ReadOnlyMemory<byte> Memory { get; } = owner.WrittenMemory;
        public int Length { get; } = owner.WrittenCount;
        public bool ForceFlush { get; } = forceFlush;
        public bool IsProtocolProgress { get; } = progress;
        public TaskCompletionSource<bool>? FlushCompletion => _completionState as TaskCompletionSource<bool>;
    }
}
