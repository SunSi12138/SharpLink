using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Reflection;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public sealed class GeneratedOutputSendPhysicalTailTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeadlineMustRetainHeldRealSizedSendAndItsSnapshot(bool snapshotCleanupFailure)
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "generated-held-sized-send", input.Reader, output.Writer,
            RpcSessionTestFixture.ServerOptions(), completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(session, ProtocolV2Capabilities.FlowControl,
            streamReceiveWindowBytes: 4, connectionReceiveWindowBytes: 4);
        await session.AcquireStreamSendCreditAsync(72, 0, 4, CancellationToken.None);
        var clock = new ManualTimeProvider();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), clock);
        using var codec = new HeldSizedCodec(snapshotCleanupFailure);
        var stream = new PhysicalLifetimeStream();
        Task pump;
        Task? physicalJoin = null;
        using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(session.Id, null, deadline, clock)))
        {
            pump = new RpcSessionGeneratedServerBridge(session).PumpOutboundStreamAsync(
                73, 0, stream, codec, false, 101, 202, CancellationToken.None).AsTask();
        }
        try
        {
            await codec.Sized.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The real sized-send is already awaiting shared connection credit. Its
            // continuation will serialize through the actual session-owned frame writer.
            session.ApplyWindowUpdate(72, new ProtocolV2WindowUpdate(0, 4));
            // This setup-only stream has returned its credit, but a WindowUpdate
            // does not complete an otherwise live send stream.
            session.CompleteSendStream(72, 0);
            await codec.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(1));
            Exception? terminal = null;
            try { await pump.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception error) { terminal = error; }
            Ensure(terminal is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                "logical pump must keep its original deadline while the real send remains physical");
            session.CompleteSendStream(73, 0, terminal); // Same local flow retirement as the real Server owner.
            Ensure(codec.Active == 1 && codec.SnapshotReleases == 0 && stream.DisposeCount == 0,
                "held real serialization must retain its snapshot and precede enumerator disposal");
            physicalJoin = stream.Tail.JoinAsync().AsTask();
            Ensure(!physicalJoin.IsCompleted && !SourceDisposed(stream.Token),
                "send-tail ownership must retain its original linked CTS after logical completion");
        }
        finally
        {
            codec.Release.Set();
            if (physicalJoin is not null) await physicalJoin.WaitAsync(TimeSpan.FromSeconds(5));
            try { await pump.WaitAsync(TimeSpan.FromSeconds(5)); } catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.DeadlineExceeded) { }
        }
        var state = (RpcSessionProtocolState)typeof(RpcSession)
            .GetField("_protocolState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        Ensure(codec.Active == 0 && codec.SnapshotReleases == 1 && stream.DisposeCount == 1 && SourceDisposed(stream.Token),
            "snapshot, physical send and CTS must finish once, including a late cleanup failure");
        var flow = state.FlowController!;
        Console.WriteLine($"PHYSICAL_SEND_EXIT credit={flow.SendConnectionCredit}; active={flow.ActiveSendStreamCount}; retained={flow.RetainedSendStreamCount}; queued={session.QueuedSendBytes}");
        Ensure(flow.SendConnectionCredit == 4,
            $"expired physical send must return credit exactly once, actual {flow.SendConnectionCredit}");
        Ensure(flow.ActiveSendStreamCount == 0 && flow.RetainedSendStreamCount == 0,
            $"both setup and real send streams must retire, active {flow.ActiveSendStreamCount}, retained {flow.RetainedSendStreamCount}");
        Ensure(session.QueuedSendBytes == 0,
            $"expired physical send must never publish its prepared data frame, actual queued bytes {session.QueuedSendBytes}");
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    private static bool SourceDisposed(CancellationToken token)
    {
        var source = typeof(CancellationToken).GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(token)!;
        return (bool)typeof(CancellationTokenSource).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    // A precise protocol component, not a simulated public Module/ServiceLease.
    private sealed class PhysicalLifetimeStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>, IGeneratedOutputPhysicalLifetime
    {
        private bool _moved;
        public GeneratedOutputPhysicalTail Tail { get; } = new();
        public CancellationToken Token;
        public int DisposeCount;
        public int Current => 7;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        { Token = cancellationToken; return this; }
        public ValueTask<bool> MoveNextAsync()
        {
            var hasNext = !_moved;
            _moved = true;
            return ValueTask.FromResult(hasNext);
        }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
        public GeneratedOutputPhysicalTail GetPhysicalTail() => Tail;
    }

    private sealed class HeldSizedCodec(bool cleanupFailure) : IRpcCodec<int>, IRpcSizedCodec<int>, IDisposable
    {
        public TaskCompletionSource Sized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public int Active;
        public int SnapshotReleases;
        public bool CanExactSize => true;
        public bool TryGetEncodedSize(in int value, out int size) { size = 4; return true; }
        public bool TryGetEncodedSize(in int value, out int size, out IRpcSizedCodecSnapshot? snapshot)
        { size = 4; snapshot = new Snapshot(); Sized.TrySetResult(); return true; }
        public void Serialize(in int value, IBufferWriter<byte> buffer) => throw new NotSupportedException("exact-sized path required");
        public int Deserialize(in ReadOnlySequence<byte> buffer) => throw new NotSupportedException();
        public void SerializeSized(in int value, IBufferWriter<byte> buffer, int size, IRpcSizedCodecSnapshot? snapshot)
        {
            Interlocked.Increment(ref Active);
            Entered.TrySetResult();
            try
            {
                Ensure(Release.Wait(TimeSpan.FromSeconds(5)), "held serialization gate did not release");
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.GetSpan(4), value);
                buffer.Advance(4);
            }
            finally { Interlocked.Decrement(ref Active); }
        }
        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
            Interlocked.Increment(ref SnapshotReleases);
            if (cleanupFailure) throw new InvalidOperationException("held send snapshot cleanup failure");
        }
        public void Dispose() => Release.Dispose();
        private sealed class Snapshot : IRpcSizedCodecSnapshot;
    }
}
