using System.IO.Pipelines;

namespace SharpLink.UnitTests.Runtime;

public partial class SessionTransportQuiescenceTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task BuiltInAbortMustJoinPhysicalWriteBeforePipeCompletion(bool fault, bool abortUnblocks)
    {
        using var pool = new TrackingMemoryPool();
        var stream = new PendingWriteStream(abortUnblocks);
        var transport = new StreamTransportConnection(stream);
        var writer = ObserveWriterPool(transport, stream, pool);
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var session = CreateSession(transport, context);
        Task? reentrantSession = null, reentrantTransport = null;
        stream.OnDispose = () =>
        {
            reentrantTransport = transport.DisposeAsync().AsTask();
            reentrantSession = session.DisposeAsync().AsTask();
        };
        var packet = SendPacket(session);
        Task? disposal = null;
        try
        {
            await stream.WriteEntered.Task.WaitAsync(Bound);
            if (fault)
                session.NotifyDisconnected(new IOException("read-side fault during TLS-like write"));
            disposal = session.DisposeAsync().AsTask();
            await stream.AbortEntered.Task.WaitAsync(Bound);
            Ensure(!disposal.IsCompleted, "abort is not physical writer completion");
            Ensure(stream.OverlappingWrites == 0, "pipe completion must not start a second write");
            Ensure(pool.RentCount > 0 && pool.ReturnCount == 0 && writer.CompleteCount == 0,
                "the built-in transport must retain its actual pipeline memory through physical IO");
            Ensure(packet.WrittenCount > 0, "the actual pump still owns its source frame");
            Ensure(ReferenceEquals(reentrantTransport, transport.DisposeAsync().AsTask()),
                "reentrant transport disposal must observe the published single-flight task");
            if (abortUnblocks)
                await stream.WriteUnblocked.Task.WaitAsync(Bound);
            else
                Ensure(!stream.WriteUnblocked.Task.IsCompleted, "noncooperative I/O must remain physically owned");
        }
        finally
        {
            stream.ReleaseWrite();
            await Task.WhenAll(disposal ?? session.DisposeAsync().AsTask(),
                reentrantSession ?? Task.CompletedTask, reentrantTransport ?? Task.CompletedTask).WaitAsync(Bound);
        }
        Ensure(stream.DisposeCount == 1 && stream.OverlappingWrites == 0 && stream.WritesAfterAbort == 0,
            "abort once, join the writer, then discard buffered bytes without retrying the closed stream");
        Ensure(pool.ReturnCount == pool.RentCount && writer.CompleteCount == 1,
            "built-in aborted completion must return every pipeline segment exactly once");
        EnsureReturned(packet);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnknownTransportMustRetainPipelineMemoryUntilNoncooperativeWriteEnds(bool fault)
    {
        using var pool = new TrackingMemoryPool();
        var stream = new PendingWriteStream(abortUnblocks: false);
        var transport = new CustomTransport(stream, pool);
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var session = CreateSession(transport, context);
        var packet = SendPacket(session);
        Task? disposal = null;
        try
        {
            await stream.WriteEntered.Task.WaitAsync(Bound);
            if (fault) session.NotifyDisconnected();
            disposal = session.DisposeAsync().AsTask();
            await Task.WhenAny(transport.Writer.CancellationRequested.Task, transport.DisposalStarted.Task).WaitAsync(Bound);
            Ensure(!disposal.IsCompleted && transport.DisposeCount == 0,
                "unknown transport disposal cannot replace the physical writer join");
            Ensure(pool.ReturnCount == 0 && transport.Writer.CompleteCount == 0,
                "pending stream write memory must not be returned or completed after logical cancellation");
            Ensure(packet.WrittenCount > 0 && stream.CapturedMemory.Span[0] != 0xDD,
                "both original frame and pipeline storage remain valid");
        }
        finally
        {
            stream.ReleaseWrite();
            await (disposal ?? session.DisposeAsync().AsTask()).WaitAsync(Bound);
        }
        Ensure(transport.DisposeCount == 1 && pool.ReturnCount > 0, "physical completion releases the custom pipeline once");
        EnsureReturned(packet);
    }

    [Test]
    public async Task PumpOriginatedFaultMustNotAwaitItsOwnCleanup()
    {
        var stream = new PendingWriteStream(abortUnblocks: false) { FailImmediately = true };
        var transport = new StreamTransportConnection(stream);
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var session = CreateSession(transport, context);
        var packet = SendPacket(session);
        try
        {
            await stream.AbortEntered.Task.WaitAsync(Bound);
            await session.DisposeAsync().AsTask().WaitAsync(Bound);
            Ensure(stream.DisposeCount == 1, "pump-originated fault must finish its single-flight cleanup");
            EnsureReturned(packet);
        }
        finally { stream.ReleaseWrite(); await session.DisposeAsync(); }
    }

    [Test]
    public async Task AbortFailureMustStillJoinWriterAndCompleteIndependentReader()
    {
        var expected = new ApplicationException("underlying abort failed after signaling I/O");
        var stream = new PendingWriteStream(abortUnblocks: true) { AbortFailure = expected };
        var transport = new StreamTransportConnection(stream);
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var session = CreateSession(transport, context);
        var packet = SendPacket(session);
        Task? disposal = null;
        Exception? assertionFailure = null;
        Exception? failure = null;
        try
        {
            await stream.WriteEntered.Task.WaitAsync(Bound);
            session.NotifyDisconnected();
            disposal = session.DisposeAsync().AsTask();
            await stream.AbortEntered.Task.WaitAsync(Bound);
            Ensure(!disposal.IsCompleted && packet.WrittenCount > 0,
                "an abort failure must not skip the still-running writer owner");
        }
        catch (Exception exception) { assertionFailure = exception; }
        finally
        {
            stream.ReleaseWrite();
            try { await (disposal ?? session.DisposeAsync().AsTask()).WaitAsync(Bound); }
            catch (Exception exception) { failure = exception; }
        }
        if (assertionFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure is null
                ? assertionFailure : new AggregateException(assertionFailure, failure)).Throw();
        Ensure(ReferenceEquals(expected, failure), "preserve the original abort failure, not a replacement or suppression");
        Ensure(((ReadOwnershipPipeReader)transport.Input).CompletionRequested,
            "independent reader completion must still be attempted");
        EnsureReturned(packet);
    }

    [Test]
    public async Task DirectTransportDisposalMustStillFlushBufferedOutput()
    {
        var stream = new MemoryStream();
        var transport = new StreamTransportConnection(stream);
        transport.Output.Write(new byte[] { 1, 2, 3, 4 });
        await transport.DisposeAsync();
        Ensure(stream.ToArray().AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4 }),
            "standalone transport disposal retains its existing buffered-flush contract");
    }

    [Test]
    public async Task AlreadyCompletedCustomPipesShouldStillDisposeExactlyOnce()
    {
        var input = new Pipe();
        var output = new Pipe();
        await input.Reader.CompleteAsync();
        await output.Writer.CompleteAsync();
        var transport = RpcSessionTestFixture.Transport("completed-pipes", input.Reader, output.Writer);
        var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions());
        await session.DisposeAsync();
        await session.DisposeAsync();
        Ensure(transport.DisposeCount == 1, "completed custom pipes remain compatible with repeated session shutdown");
    }

    [Test]
    public async Task CancellationRequestFailureMustNotSkipPhysicalJoinOrProviderDisposal()
    {
        using var pool = new TrackingMemoryPool();
        var expected = new ApplicationException("custom cancellation request failed");
        var stream = new PendingWriteStream(abortUnblocks: false);
        var transport = new CustomTransport(stream, pool);
        transport.Writer.CancellationFailure = expected;
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var session = CreateSession(transport, context);
        var packet = SendPacket(session);
        Task? disposal = null;
        Exception? assertionFailure = null, failure = null;
        try
        {
            await stream.WriteEntered.Task.WaitAsync(Bound);
            disposal = session.DisposeAsync().AsTask();
            await transport.Writer.CancellationRequested.Task.WaitAsync(Bound);
            Ensure(!disposal.IsCompleted && transport.DisposeCount == 0 && pool.ReturnCount == 0,
                "a failed cancel request cannot release physical writer ownership");
        }
        catch (Exception exception) { assertionFailure = exception; }
        finally
        {
            stream.ReleaseWrite();
            try { await (disposal ?? session.DisposeAsync().AsTask()).WaitAsync(Bound); }
            catch (Exception exception) { failure = exception; }
        }
        if (assertionFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure is null
                ? assertionFailure : new AggregateException(assertionFailure, failure)).Throw();
        Ensure(ReferenceEquals(expected, failure), "the actual cancellation-request failure remains observable");
        Ensure(transport.DisposeCount == 1 && pool.ReturnCount == pool.RentCount,
            "independent custom disposal must run after the physical join despite cancellation-request failure");
        EnsureReturned(packet);
    }

    [Test]
    public async Task AnonymousSessionAbortFailureMustStillDisposeTheInput()
    {
        var expected = new ApplicationException("anonymous output abort failed");
        var input = new DisposeOnlyPipeStream();
        var output = new DisposeOnlyPipeStream(expected);
        var transport = new AnonymousPipeTransportConnection(input, output);
        var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions());
        Exception? failure = null;
        try { await session.DisposeAsync().AsTask().WaitAsync(Bound); }
        catch (Exception exception) { failure = exception; }
        Ensure(ReferenceEquals(expected, failure), "anonymous output abort failure must be preserved");
        Ensure(input.DisposeCount == 1 && output.DisposeCount == 1,
            "input abort must be attempted even when output abort fails");
        Ensure(((ReadOwnershipPipeReader)transport.Input).CompletionRequested,
            "anonymous reader completion remains independent of output failure");
    }

    private static TrackingWriter ObserveWriterPool(StreamTransportConnection transport, Stream stream, MemoryPool<byte> pool)
    {
        var writer = new TrackingWriter(PipeWriter.Create(stream,
            new StreamPipeWriterOptions(pool, minimumBufferSize: 1024, leaveOpen: true)));
        // Install a pool-observable equivalent before creating the session or starting IO.
        // This test-only reflection avoids a production hook or a normal-path pool parameter.
        typeof(StreamTransportConnection).GetField("<Output>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(transport, writer);
        return writer;
    }

    private static RpcSession CreateSession(ITransportConnection transport, SharpLinkRuntimeContext context)
    {
        var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions(context));
        RpcSessionTestFixture.CompleteHandshake(session);
        return session;
    }

    private static IRpcByteBufferWriter SendPacket(RpcSession session)
    {
        var packet = session.RuntimeContext.Buffers.Rent();
        packet.WritePacket(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, 1);
        session.SendPacket(packet);
        return packet;
    }

    private static void EnsureReturned(IRpcByteBufferWriter packet)
    {
        try { _ = packet.WrittenCount; }
        catch (ObjectDisposedException) { return; }
        throw new Exception("the completed pump must return its packet");
    }

    private static void Ensure(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
