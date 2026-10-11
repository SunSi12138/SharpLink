using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

public partial class StreamFlowGrantGeneratedBridgeTests
{
    private static readonly TimeSpan BridgeTimeout = TimeSpan.FromSeconds(2);
    private const long BridgeRequestId = 73;
    private const ushort BridgeStreamId = 1;

    private sealed class GrantBridgeHarness : IAsyncDisposable
    {
        private readonly SharpLinkRuntimeContext _context;
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly int _window;
        internal RpcSession Session { get; }
        internal StreamFlowController Controller { get; }
        internal StreamFlowController.SendGrantSnapshot Snapshot
            => Controller.CaptureSendGrantSnapshot(BridgeRequestId, BridgeStreamId);

        internal GrantBridgeHarness(int window = 64, int? sendQueueBytes = null)
        {
            _window = window;
            _context = new SharpLinkRuntimeContextBuilder().Configure(options =>
            {
                options.FlowControl.MaxPreCreditSerializedBytes = window;
                if (sendQueueBytes is { } queueBytes)
                    options.FlowControl.MaxSendQueueBytes = queueBytes;
            }).Build(includeGeneratedAssemblyCatalog: false);
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport(
                "b2-generated-bridge", _input.Reader, _output.Writer,
                RpcSessionTestFixture.ServerOptions(_context), completeHandshake: false);
            RpcSessionTestFixture.CompleteHandshake(Session, ProtocolV2Capabilities.FlowControl,
                streamReceiveWindowBytes: window, connectionReceiveWindowBytes: window);
            var field = typeof(RpcSession).GetField("_protocolState", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("RpcSession._protocolState was not found.");
            Controller = ((RpcSessionProtocolState)field.GetValue(Session)!).FlowController
                ?? throw new InvalidOperationException("The test session has no flow controller.");
        }

        internal Task PumpAsync(IAsyncEnumerable<int> stream, IRpcCodec<int> codec,
            CancellationToken cancellationToken = default)
            => new RpcSessionGeneratedServerBridge(Session).PumpOutboundStreamAsync(
                BridgeRequestId, BridgeStreamId, stream, codec, payloadNullable: false,
                contractId: 101, methodId: 202, cancellationToken).AsTask();

        internal void ReturnPeerCredit(int bytes)
            => Session.ApplyWindowUpdate(BridgeRequestId, new ProtocolV2WindowUpdate(BridgeStreamId, checked((uint)bytes)));

        internal void PublishError(SharpLinkErrorCode code = SharpLinkErrorCode.Internal)
            => Session.SendStreamErrorAsync(BridgeRequestId, BridgeStreamId,
                new SharpLinkException(code, "safe generated owner terminal"));

        internal void AssertCredit(long available, long debt, long grant)
        {
            var state = Snapshot;
            Ensure(state.ConnectionCredit == available && state.TotalDebt == debt &&
                   state.TotalUnusedGrant == grant &&
                   state.ConnectionCredit + state.TotalDebt + state.TotalUnusedGrant == _window,
                $"credit must conserve the negotiated window: F={state.ConnectionCredit}, " +
                $"D={state.TotalDebt}, G={state.TotalUnusedGrant}; expected {available}/{debt}/{grant}");
        }

        internal void AssertNoPendingOwner()
        {
            Ensure(Session.PreCreditSerializedBytes == 0 && Session.PreCreditSerializedWaiterCount == 0 &&
                   Snapshot.WaiterCount == 0,
                "the bridge must settle every prepared-byte owner, byte waiter and flow waiter");
        }

        internal void AssertFullyRetired()
        {
            AssertCredit(_window, 0, 0);
            AssertNoPendingOwner();
            Ensure(Snapshot.ActiveStreams == 0 && Snapshot.RetainedStreams == 0,
                "settled EOF/error must leave no active stream or debt tombstone");
        }

        internal async Task<List<BridgeFrame>> ReadFramesAsync()
        {
            await Session.FlushSendQueueAsync().AsTask().WaitAsync(BridgeTimeout);
            var read = await _output.Reader.ReadAsync().AsTask().WaitAsync(BridgeTimeout);
            var remaining = read.Buffer;
            var frames = new List<BridgeFrame>();
            while (ProtocolV2FrameParser.TryReadFrame(ref remaining, Session.RuntimeContext.Protocol,
                       out var header, out var payload))
            {
                Ensure(header.RequestId == (ulong)BridgeRequestId,
                    "every frame must retain its generated invocation request ID");
                frames.Add(new BridgeFrame(header.Type, header.Flags, payload.ToArray()));
            }
            Ensure(remaining.IsEmpty, "the generated bridge must only publish complete Protocol v2 frames");
            _output.Reader.AdvanceTo(read.Buffer.End);
            return frames;
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await _output.Reader.CompleteAsync();
            await _input.Writer.CompleteAsync();
            _context.Dispose();
        }
    }

    private readonly record struct BridgeFrame(ProtocolV2FrameType Type, ProtocolV2FrameFlags Flags, byte[] Payload);

    private static void AssertFrames(List<BridgeFrame> frames, int[] values, bool errorTerminal)
    {
        Ensure(frames.Count == values.Length + 1,
            "only accepted items and the unique owner/success terminal may be present on the wire");
        for (var index = 0; index < values.Length; index++)
        {
            var frame = frames[index];
            Ensure(frame.Type == ProtocolV2FrameType.StreamData && frame.Flags == ProtocolV2FrameFlags.None &&
                   frame.Payload.Length == sizeof(ushort) + sizeof(int) &&
                   BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload) == BridgeStreamId,
                "each accepted four-byte item must retain its stream ID and wire order");
            for (var offset = sizeof(ushort); offset < frame.Payload.Length; offset++)
                Ensure(frame.Payload[offset] == (byte)values[index], "serialized payload bytes must be unchanged");
        }
        var terminal = frames[^1];
        Ensure(terminal.Type == ProtocolV2FrameType.StreamComplete &&
               ((terminal.Flags & ProtocolV2FrameFlags.Error) != 0) == errorTerminal &&
               BinaryPrimitives.ReadUInt16LittleEndian(terminal.Payload) == BridgeStreamId,
            "exactly one terminal with the selected outcome must follow all accepted items");
    }

    private static async Task<Exception> CaptureFailureAsync(Task task)
    {
        try
        {
            await task.WaitAsync(BridgeTimeout);
        }
        catch (Exception exception) when (exception is not TimeoutException)
        {
            return exception;
        }
        throw new InvalidOperationException("Expected the generated bridge operation to fail.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            Ensure(Stopwatch.GetElapsedTime(started) < BridgeTimeout, message);
            await Task.Delay(1);
        }
    }

    private static async Task ObserveFixtureTaskAsync(Task task)
    {
        try { await task.WaitAsync(BridgeTimeout); }
        catch (Exception exception) when (exception is not TimeoutException) { }
    }

    private static bool IsInactive(IRpcByteBufferWriter? writer)
    {
        if (writer is null)
            return false;
        try { _ = writer.WrittenCount; return false; }
        catch (ObjectDisposedException) { return true; }
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Generated grant bridge assertion failed: {message}.");
    }

    private sealed class ImmediateValues(params int[] values) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private int _index = -1;
        internal CancellationToken Token { get; private set; }
        public int Current => values[_index];
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            return this;
        }
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(++_index < values.Length);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NonCooperativeValues(int[] values, bool blockEof)
        : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private readonly TaskCompletionSource<bool> _pendingMoveNext = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _pendingDispose = NewSignal();
        private int _index = -1, _disposeRequested;
        internal TaskCompletionSource WaitingForMoveNext { get; } = NewSignal();
        internal CancellationToken Token { get; private set; }
        internal bool DisposeRequested => Volatile.Read(ref _disposeRequested) != 0;
        internal bool DisposeCompleted => _pendingDispose.Task.IsCompleted;
        public int Current => values[_index];
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            return this;
        }
        public ValueTask<bool> MoveNextAsync()
        {
            if (++_index < values.Length)
                return ValueTask.FromResult(true);
            if (!blockEof)
                return ValueTask.FromResult(false);
            WaitingForMoveNext.TrySetResult();
            return new ValueTask<bool>(_pendingMoveNext.Task);
        }
        public ValueTask DisposeAsync()
        {
            Volatile.Write(ref _disposeRequested, 1);
            return new ValueTask(_pendingDispose.Task);
        }
        internal void Release()
        {
            _pendingMoveNext.TrySetResult(false);
            _pendingDispose.TrySetResult();
        }
    }

    private sealed class GrantProbeCodec(Func<int, bool>? canSize = null, Func<int, int>? encodedSize = null)
        : IRpcCodec<int>, IRpcSizedCodec<int>
    {
        private int _sizedCalls, _unsizedCalls, _releaseCalls;
        private IRpcByteBufferWriter? _lastWriter;
        internal int SizedCalls => Volatile.Read(ref _sizedCalls);
        internal int UnsizedCalls => Volatile.Read(ref _unsizedCalls);
        internal int ReleaseCalls => Volatile.Read(ref _releaseCalls);
        internal IRpcByteBufferWriter? LastWriter => Volatile.Read(ref _lastWriter);
        internal Action<int>? BeforeSize { get; init; }
        internal Action<int, bool>? BeforeSerialize { get; init; }
        internal Action<int, bool>? AfterSerialize { get; init; }
        internal Exception? ReleaseFailure { get; init; }
        public bool CanExactSize => true;
        public bool TryGetEncodedSize(in int value, out int size)
        {
            size = encodedSize?.Invoke(value) ?? sizeof(int);
            return canSize?.Invoke(value) ?? true;
        }
        public bool TryGetEncodedSize(in int value, out int size, out IRpcSizedCodecSnapshot? snapshot)
        {
            BeforeSize?.Invoke(value);
            var known = TryGetEncodedSize(value, out size);
            snapshot = known ? new ItemSnapshot(value, size) : null;
            return known;
        }
        public void SerializeSized(in int value, IBufferWriter<byte> buffer, int size,
            IRpcSizedCodecSnapshot? snapshot)
        {
            Ensure(snapshot is ItemSnapshot item && item.Value == value && item.Bytes == size && item.Released == 0,
                "the item must receive its original, unreleased sized snapshot");
            Interlocked.Increment(ref _sizedCalls);
            SerializeCore(value, buffer, size, sized: true);
        }
        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            Interlocked.Increment(ref _unsizedCalls);
            SerializeCore(value, buffer, encodedSize?.Invoke(value) ?? sizeof(int), sized: false);
        }
        private void SerializeCore(int value, IBufferWriter<byte> buffer, int bytes, bool sized)
        {
            Volatile.Write(ref _lastWriter, (IRpcByteBufferWriter)buffer);
            BeforeSerialize?.Invoke(value, sized);
            buffer.GetSpan(bytes)[..bytes].Fill((byte)value);
            buffer.Advance(bytes);
            AfterSerialize?.Invoke(value, sized);
        }
        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
            Ensure(snapshot is ItemSnapshot, "only a captured snapshot may be released");
            var item = (ItemSnapshot)snapshot!;
            var releases = Interlocked.Increment(ref item.Released);
            Interlocked.Increment(ref _releaseCalls);
            Ensure(releases == 1, "each item snapshot must have exactly one release owner");
            if (ReleaseFailure is { } failure)
                throw failure;
        }
        public int Deserialize(in ReadOnlySequence<byte> buffer) => buffer.FirstSpan[0];
        private sealed class ItemSnapshot(int value, int bytes) : IRpcSizedCodecSnapshot
        {
            internal int Value { get; } = value;
            internal int Bytes { get; } = bytes;
            internal int Released;
        }
    }
}
