using System.Buffers.Binary;
using System.IO.Pipelines;

namespace SharpLink.UnitTests.Runtime;

public sealed class RpcPublicationRetirementTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task RejectedHeadPublishesDispositionBeforeIndependentObserverAndWaiter()
    {
        await using var fixture = new Fixture();
        var session = fixture.Session;
        fixture.SendBlockingRequest(900);
        await fixture.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
        var binding = new TestRpcBindingPublication(session);
        var group = new TestRpcStreamPublication(binding, 101);
        SendRelease(binding);
        using var resume = new ManualResetEventSlim();
        var observer = new BlockingObserver(group, resume);
        var head = Head(group);
        head = session.PrepareOutboundFrame(head, CancellationToken.None);
        Ensure(session.TryEnqueuePreparedFrame(head, true, CancellationToken.None, observer,
            out var emission, out var rejection, group, RpcFramePublicationRole.RequestHead),
            "the late head must reach the real normal queue before its consumer rejection");
        Ensure(rejection is null, "queue acceptance is distinct from delivery");
        SendChunk(group);
        var fence = session.CompleteStreamPublicationAsync(group).AsTask();
        try
        {
            fixture.Output.Release();
            await observer.Entered.Task.WaitAsync(Timeout);
            Ensure(group.HeadDisposition == RpcStreamHeadDisposition.Rejected,
                "the consumer must set Rejected before calling the full-ID observer");
            Ensure(!emission.IsCompleted && !fence.IsCompleted,
                "independent emission and physical fence cannot finish before blocked cleanup");
        }
        finally { resume.Set(); }
        var failure = await Failure(emission.AsTask());
        await fence.WaitAsync(Timeout);
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition } &&
            observer.Count == 1 && observer.RequestId == 101,
            "both independent completion obligations must observe the exact rejected generation");
        var ids = await fixture.ReadRequestIdsAsync();
        Ensure(ids.Count == 2 && ids[0] == 900 && ids[1] == 0,
            "neither rejected head nor its queued chunk may escape to transport");
        Ensure(session.QueuedSendBytes == 0, "all physical frame bytes must return before fence completion");
    }

    [Test]
    public async Task ReleaseDoesNotRejectAnAlreadyAdmittedGroupsLaterChunks()
    {
        await using var fixture = new Fixture();
        var session = fixture.Session;
        fixture.SendBlockingRequest(900);
        await fixture.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
        var binding = new TestRpcBindingPublication(session);
        var admitted = new TestRpcStreamPublication(binding, 101);
        var rejected = new TestRpcStreamPublication(binding, 102);
        admitted.SendHead();
        SendRelease(binding);
        rejected.SendHead();
        SendChunk(admitted);
        SendChunk(rejected);
        var firstFence = session.CompleteStreamPublicationAsync(admitted).AsTask();
        var secondFence = session.CompleteStreamPublicationAsync(rejected).AsTask();
        fixture.Output.Release();
        await Task.WhenAll(firstFence, secondFence).WaitAsync(Timeout);
        var ids = await fixture.ReadRequestIdsAsync();
        Ensure(ids.Count == 4 && ids[0] == 900 && ids[1] == 101 && ids[2] == 0 && ids[3] == 101,
            "same candidate release must suppress only the later head and its own chunks");
        Ensure(admitted.HeadDisposition == RpcStreamHeadDisposition.Admitted &&
            rejected.HeadDisposition == RpcStreamHeadDisposition.Rejected,
            "each exact full-ID group retains its own immutable consumer decision");
    }

    [Test]
    public async Task CleanupFailureStillDrainsLaterOwnersAndFaultsPhysicalFence()
    {
        var fixture = new Fixture();
        var session = fixture.Session;
        var headOwner = new TrackingWriter(throwOnReturn: true);
        var chunkOwner = new TrackingWriter(throwOnReturn: false);
        var binding = new TestRpcBindingPublication(session);
        var group = new TestRpcStreamPublication(binding, 103);
        Exception? failure;
        try
        {
            fixture.SendBlockingRequest(900);
            await fixture.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
            SendRelease(binding);
            WriteHead(headOwner, group);
            session.SendPacket(headOwner, publication: group, publicationRole: RpcFramePublicationRole.RequestHead);
            WriteChunk(chunkOwner, group);
            session.SendPacket(chunkOwner, publication: group, publicationRole: RpcFramePublicationRole.StreamData);
            var fence = session.CompleteStreamPublicationAsync(group).AsTask();
            fixture.Output.Release();
            failure = await Failure(fence);
            Ensure(failure is not null && Contains(failure, "injected frame return failure"),
                "fence failure must retain preceding cleanup failure, not merely logical Close");
            Ensure(headOwner.ReturnCount == 1 && chunkOwner.ReturnCount == 1 && session.QueuedSendBytes == 0,
                "a throwing earlier return cannot skip or double-return any later accepted owner");
        }
        finally
        {
            fixture.Output.Release();
            // The same physical cleanup failure may be reported again by the explicit stop join.
            try { await fixture.DisposeAsync(); }
            catch (Exception cleanup) when (Contains(cleanup, "injected frame return failure")) { }
        }
    }

    [Test]
    public async Task ThrowingObserverCannotSkipIndependentWaiterAndLaterFrameCleanup()
    {
        var fixture = new Fixture();
        var group = new TestRpcStreamPublication(fixture.Session, 107);
        var later = new TrackingWriter(throwOnReturn: false);
        var observer = new ThrowingObserver();
        try
        {
            fixture.SendBlockingRequest(900);
            await fixture.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
            SendRelease(group.Binding);
            var head = fixture.Session.PrepareOutboundFrame(Head(group), CancellationToken.None);
            Ensure(fixture.Session.TryEnqueuePreparedFrame(head, true, CancellationToken.None, observer,
                out var emission, out _, group, RpcFramePublicationRole.RequestHead), "head accepted before rejection");
            WriteChunk(later, group);
            fixture.Session.SendPacket(later, publication: group, publicationRole: RpcFramePublicationRole.StreamData);
            var fence = fixture.Session.CompleteStreamPublicationAsync(group).AsTask();
            fixture.Output.Release();
            var emissionFailure = await Failure(emission.AsTask());
            var fenceFailure = await Failure(fence);
            Ensure(emissionFailure is not null && Contains(emissionFailure, "injected observer failure") &&
                fenceFailure is not null && Contains(fenceFailure, "injected observer failure"),
                "observer failure must settle the independent waiter and physical fence, preserving cause");
            Ensure(observer.Count == 1 && later.ReturnCount == 1 && fixture.Session.QueuedSendBytes == 0,
                "observer failure must not skip later accepted frame ownership");
        }
        finally
        {
            fixture.Output.Release();
            try { await fixture.DisposeAsync(); }
            catch (Exception cleanup) when (Contains(cleanup, "injected observer failure")) { }
        }
    }

    [Test]
    public async Task SameTokenInDifferentSessionsHasIndependentConsumerAuthority()
    {
        await using var first = new Fixture();
        await using var second = new Fixture();
        var firstBinding = new TestRpcBindingPublication(first.Session);
        var secondBinding = new TestRpcBindingPublication(second.Session);
        var group = new TestRpcStreamPublication(secondBinding, 104);
        SendRelease(firstBinding);
        first.Output.Release();
        await first.Session.FlushSendQueueAsync().AsTask().WaitAsync(Timeout);
        group.SendHead();
        SendChunk(group);
        var fence = second.Session.CompleteStreamPublicationAsync(group).AsTask();
        second.Output.Release();
        await fence.WaitAsync(Timeout);
        Ensure(firstBinding.ReleaseClaimed && !secondBinding.ReleaseClaimed &&
            group.HeadDisposition == RpcStreamHeadDisposition.Admitted,
            "token bits alone never select retirement authority across Sessions");
    }

    [Test]
    public async Task PhysicalFenceDoesNotForceAnExplicitBatchWindow()
    {
        var clock = new ManualTimeProvider();
        using var context = new SharpLinkRuntimeContextBuilder().UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "nonforcing-stream-fence", input.Reader, output.Writer,
            RpcSessionTestFixture.ClientOptions(context, new RpcSessionFlushOptions(64 * 1024, TimeSpan.FromSeconds(30))));
        var group = new TestRpcStreamPublication(session, 105);
        try
        {
            group.SendHead();
            SendChunk(group);
            var fence = session.CompleteStreamPublicationAsync(group).AsTask();
            var limit = DateTime.UtcNow + Timeout;
            while (clock.ActiveTimerCount == 0)
            {
                if (DateTime.UtcNow >= limit) throw new TimeoutException("batch window did not arm");
                await Task.Delay(5);
            }
            Ensure(!fence.IsCompleted && session.QueuedSendBytes > 0,
                "zero-wire fence must retain the existing timed batch rather than force an early flush");
            clock.Advance(TimeSpan.FromSeconds(30));
            await fence.WaitAsync(Timeout);
            Ensure(session.QueuedSendBytes == 0, "batch policy completion settles all preceding group owners");
        }
        finally
        {
            await session.DisposeAsync();
            await input.Writer.CompleteAsync();
            await output.Reader.CompleteAsync();
        }
    }

    [Test]
    public async Task NeverAcceptedFenceJoinsActualOutputAwaitRatherThanLogicalClose()
    {
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(static options => options.PerformanceProfile = SharpLinkPerformanceProfile.LowLatency)
            .Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var outputPipe = new Pipe();
        var output = new PhysicallyHeldPipeWriter(outputPipe.Writer);
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "neveraccepted-stream-fence", input.Reader, output, RpcSessionTestFixture.ClientOptions(context));
        var group = new TestRpcStreamPublication(session, 106);
        try
        {
            group.SendHead();
            await output.Entered.Task.WaitAsync(Timeout);
            session.NotifyDisconnected(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "test logical close"));
            var fence = session.CompleteStreamPublicationAsync(group).AsTask();
            Ensure(!fence.IsCompleted, "logical close cannot certify a physically held output await");
            output.Release();
            Ensure(await Failure(fence) is not null, "neveraccepted fence preserves its terminal publication failure");
            Ensure(session.QueuedSendBytes == 0, "fallback must join the exact claimed frame cleanup");
        }
        finally
        {
            output.Release();
            await session.DisposeAsync();
            await input.Writer.CompleteAsync();
            await outputPipe.Reader.CompleteAsync();
        }
    }

    private static IRpcByteBufferWriter Head(RpcStreamPublication group)
    {
        var writer = group.Session.RuntimeContext.Buffers.Rent();
        WriteHead(writer, group);
        return writer;
    }

    private static void WriteHead(IRpcByteBufferWriter writer, RpcStreamPublication group)
    {
        using (writer.BeginPacketScope(ProtocolV2FrameType.Request, ProtocolV2FrameFlags.None, (ulong)group.RequestId))
        {
            var bytes = writer.GetSpan(16);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, group.Binding.Token);
            BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], 1);
            writer.Advance(16);
        }
    }

    private static void SendRelease(RpcBindingPublication binding)
    {
        var writer = binding.Session.RuntimeContext.Buffers.Rent();
        using (writer.BeginPacketScope(ProtocolV2FrameType.ReleaseBinding, ProtocolV2FrameFlags.None, 0))
        {
            BinaryPrimitives.WriteUInt64LittleEndian(writer.GetSpan(8), binding.Token);
            writer.Advance(8);
        }
        binding.Session.SendPacket(writer, publication: binding, publicationRole: RpcFramePublicationRole.ReleaseBinding);
    }

    private static void SendChunk(RpcStreamPublication group)
    {
        var writer = group.Session.RuntimeContext.Buffers.Rent();
        WriteChunk(writer, group);
        group.Session.SendPacket(writer, publication: group, publicationRole: RpcFramePublicationRole.StreamData);
    }

    private static void WriteChunk(IRpcByteBufferWriter writer, RpcStreamPublication group)
    {
        using (writer.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, (ulong)group.RequestId))
        {
            BinaryPrimitives.WriteUInt16LittleEndian(writer.GetSpan(2), 0);
            writer.Advance(2);
        }
    }

    private static async Task<Exception?> Failure(Task task)
    {
        try { await task.WaitAsync(Timeout); return null; }
        catch (Exception exception) when (exception is not TimeoutException) { return exception; }
    }

    private static bool Contains(Exception exception, string message)
        => exception.Message.Contains(message, StringComparison.Ordinal) ||
           exception is AggregateException aggregate && System.Linq.Enumerable.Any(aggregate.InnerExceptions, item => Contains(item, message)) ||
           exception.InnerException is { } inner && Contains(inner, message);

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ThrowingObserver : IRequestEmissionFailureObserver
    {
        internal int Count;
        public void OnRequestEmissionFailure(long requestId, Exception exception)
        {
            Count++;
            throw new InvalidOperationException("injected observer failure");
        }
    }

    private sealed class BlockingObserver(RpcStreamPublication group, ManualResetEventSlim resume)
        : IRequestEmissionFailureObserver
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Count;
        internal long RequestId;
        public void OnRequestEmissionFailure(long requestId, Exception exception)
        {
            Ensure(group.HeadDisposition == RpcStreamHeadDisposition.Rejected, "disposition precedes callback");
            RequestId = requestId;
            Count++;
            Entered.TrySetResult();
            if (!resume.Wait(Timeout)) throw new TimeoutException("failure observer was not resumed");
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly SharpLinkRuntimeContext _context;
        internal BackpressuredSendOwnershipTests.GatePipeWriter Output { get; }
        internal RpcSession Session { get; }
        internal Fixture()
        {
            _context = new SharpLinkRuntimeContextBuilder().Configure(static options =>
            {
                options.FlowControl.MaxSendQueueBytes = 64 * 1024;
                options.PerformanceProfile = SharpLinkPerformanceProfile.LowLatency;
            }).Build(includeGeneratedAssemblyCatalog: false);
            Output = new BackpressuredSendOwnershipTests.GatePipeWriter(_output.Writer);
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport(
                Guid.NewGuid().ToString(), _input.Reader, Output, RpcSessionTestFixture.ClientOptions(_context));
        }
        internal void SendBlockingRequest(long id)
            => new TestRpcStreamPublication(Session, id).SendHead();
        internal async Task<List<long>> ReadRequestIdsAsync()
        {
            var read = await _output.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            var bytes = read.Buffer.ToArray();
            var ids = new List<long>();
            for (var offset = 0; offset < bytes.Length;)
            {
                var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1));
                ids.Add(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset + 7)));
                offset += ProtocolV2Constants.HeaderBytes + length;
            }
            _output.Reader.AdvanceTo(read.Buffer.End);
            return ids;
        }
        public async ValueTask DisposeAsync()
        {
            Output.Release();
            Exception? failure = null;
            try { await Session.DisposeAsync().AsTask().WaitAsync(Timeout); }
            catch (Exception cleanup) { failure = cleanup; }
            try { await _input.Writer.CompleteAsync(); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            try { await _output.Reader.CompleteAsync(); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            try { _context.Dispose(); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            if (failure is not null) throw failure;
        }
    }

    private sealed class PhysicallyHeldPipeWriter(PipeWriter inner) : PipeWriter
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _release.TrySetResult();
        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await _release.Task.ConfigureAwait(false); // Deliberately physical, not cancellation-visible.
            return await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class TrackingWriter(bool throwOnReturn) : IRpcByteBufferWriter
    {
        private readonly PooledByteBufferWriter _inner = new();
        internal int ReturnCount;
        public int WrittenCount => _inner.WrittenCount;
        public ReadOnlyMemory<byte> WrittenMemory => _inner.WrittenMemory;
        public Span<byte> WrittenSpan => _inner.WrittenSpan;
        public int Capacity => _inner.Capacity;
        public void Advance(int count) => _inner.Advance(count);
        public Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
        public void Clear() => _inner.Clear();
        public void Dispose()
        {
            ReturnCount++;
            _inner.Dispose();
            if (throwOnReturn) throw new InvalidOperationException("injected frame return failure");
        }
    }
}
