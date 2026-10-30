using System.IO;
using System.Linq;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public partial class TransportOutputFaultTests
{
    [Test]
    [Arguments("write-sync", 0, 0)]
    [Arguments("write-partial", 7, 0)]
    [Arguments("flush", 79, 1)]
    public async Task OutputFaultMustNotReplayBufferedFrame(string failure, int expectedBytes, int expectedFlushes)
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var stream = new ObservingStream(failure: failure);
        var connection = new StreamTransportConnection(stream);
        var session = CreateSession(context, connection);
        var frame = CreateFrame(session, 64, 1);
        Exception? disconnected = null;
        session.OnDisconnected += exception => disconnected = exception;
        var failureResult = await CaptureAsync(() => session.SendPacketAndFlushAsync(frame));
        await session.DisposeAsync();
        Ensure(failureResult is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
            "output fault must still fail the waiter as ConnectionClosed");
        Ensure(ReferenceEquals(disconnected, failureResult), "terminal exception identity must be retained");
        EnsureReturned(frame);
        Ensure(!session.IsConnected && session.QueuedSendBytes == 0 && stream.DisposeCount == 1,
            "terminal cleanup must release frame/credits and owned stream exactly once");
        Ensure(stream.WriteCount == 1 && stream.BytesWritten == expectedBytes && stream.FlushCount == expectedFlushes,
            $"terminal output cleanup must not replay: writes={stream.WriteCount}, bytes={stream.BytesWritten}, flushes={stream.FlushCount}");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NormalDisposalMustStillFlushBufferedTransportBytes(bool throughSession)
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var stream = new ObservingStream();
        var connection = new StreamTransportConnection(stream);
        connection.Output.Write(new byte[64]);
        if (throughSession)
            await CreateSession(context, connection).DisposeAsync();
        else
            await connection.DisposeAsync();
        Ensure(stream.WriteCount == 1 && stream.BytesWritten == 64 && stream.FlushCount == 1 && stream.DisposeCount == 1,
            "normal disposal must preserve flushing previously buffered bytes");
    }

    [Test]
    public async Task ReadSideFaultMustNotClaimOutputQuiescence()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var stream = new ObservingStream();
        var connection = new StreamTransportConnection(stream);
        connection.Output.Write(new byte[64]);
        var session = CreateSession(context, connection);
        session.NotifyDisconnected(new IOException("read side fault"));
        await session.DisposeAsync();
        Ensure(stream.WriteCount == 1 && stream.BytesWritten == 64 && stream.FlushCount == 1,
            "general session fault must retain legacy transport completion, not infer idle output");
    }

    [Test]
    public async Task OutputFailureMustNotReplacePriorStoppingWinner()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var stream = new ObservingStream(blockWrite: true, failure: "write-prefix-after-gate");
        var session = CreateSession(context, new StreamTransportConnection(stream));
        var frame = CreateFrame(session, 64, 1);
        var sent = session.SendPacketAndFlushAsync(frame).AsTask();
        Exception? firstTerminal = null;
        var notifications = 0;
        session.OnDisconnected += exception => { firstTerminal ??= exception; notifications++; };
        try
        {
            await stream.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            session.BeginShutdown();
            var stopping = firstTerminal;
            stream.ReleaseWrite.TrySetResult();
            _ = await CaptureAsync(() => new ValueTask(sent));
            await session.DisposeAsync();
            Ensure(stopping is not null && ReferenceEquals(stopping, firstTerminal) && notifications == 1,
                "the first stopping terminal remains the only public notification");
            Ensure(stream.WriteCount == 1 && stream.BytesWritten == 7,
                "a later output failure must not replay its unknown prefix after Stopping won");
            EnsureReturned(frame);
        }
        finally
        {
            stream.ReleaseWrite.TrySetResult();
            await session.DisposeAsync();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CustomTransportMustRetainItsExistingCompletionContract(bool failCompletion)
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new CompletionRecordingWriter(failCompletion);
        var transport = RpcSessionTestFixture.Transport("custom-output", input.Reader, output);
        var session = CreateSession(context, transport);
        var frame = CreateFrame(session, 64, 1);
        _ = await CaptureAsync(() => session.SendPacketAndFlushAsync(frame));
        if (failCompletion)
        {
            var first = await CaptureAsync(session.DisposeAsync);
            var second = await CaptureAsync(session.DisposeAsync);
            Ensure(ReferenceEquals(first, output.CompletionFailure) && ReferenceEquals(first, second),
                "custom cleanup failure and single-flight exception identity must remain unchanged");
        }
        else
            await session.DisposeAsync();
        Ensure(output.CompletionException is null && output.CompleteCount == 1 && transport.DisposeCount == 1,
            "session must not take over custom writer completion or add a new fault argument");
        EnsureReturned(frame);
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task QuiescentPumpFaultMustTolerateReentrantShutdownAndDisposal()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var stream = new ObservingStream(failure: "write-partial");
        var session = CreateSession(context, new StreamTransportConnection(stream));
        var frame = CreateFrame(session, 64, 1);
        var callback = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? disconnected = null;
        session.OnDisconnected += exception =>
        {
            disconnected = exception;
            session.BeginShutdown();
            // Do not synchronously join the pump from its own terminal callback.
            callback.TrySetResult(session.DisposeAsync().AsTask());
        };
        var terminal = await CaptureAsync(() => session.SendPacketAndFlushAsync(frame));
        var reentrantDisposal = await callback.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await reentrantDisposal.WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync();
        Ensure(ReferenceEquals(terminal, disconnected), "reentrant shutdown must retain the winning terminal exception");
        Ensure(stream.WriteCount == 1 && stream.BytesWritten == 7 && stream.DisposeCount == 1,
            "reentrant disposal must neither replay bytes nor dispose the stream twice");
        EnsureReturned(frame);
        Ensure(session.QueuedSendBytes == 0, "reentrant disposal must release all queue credits");
    }

    [Test]
    public async Task QuiescentPumpFaultMustPreserveNativeDisposalFailureIdentity()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var cleanupFailure = new ApplicationException("native stream disposal failure");
        var stream = new ObservingStream(failure: "write-partial", disposeFailure: cleanupFailure);
        var session = CreateSession(context, new StreamTransportConnection(stream));
        var frame = CreateFrame(session, 64, 1);
        var terminal = await CaptureAsync(() => session.SendPacketAndFlushAsync(frame));
        var first = await CaptureAsync(session.DisposeAsync);
        var second = await CaptureAsync(session.DisposeAsync);
        Ensure(terminal is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
            "native cleanup failure must not replace the send's terminal failure");
        Ensure(ReferenceEquals(first, cleanupFailure) && ReferenceEquals(second, first),
            "all disposal waiters must observe the same native cleanup exception instance");
        Ensure(stream.WriteCount == 1 && stream.BytesWritten == 7 && stream.DisposeCount == 1,
            "failed native cleanup must not replay output or restart disposal");
        EnsureReturned(frame);
        Ensure(session.QueuedSendBytes == 0, "native cleanup failure must not leak queue credits");
    }

    [Test]
    [Arguments("write-after-gate", 0)]
    [Arguments("write-prefix-after-gate", 7)]
    public async Task QuiescentPumpFaultMustReturnInFlightQueuedAndCapacityWaitingOwnersOnce(string failure, int expectedBytes)
    {
        using var context = new SharpLinkRuntimeContextBuilder().Configure(options =>
        {
            options.FlowControl.MaxSendQueueBytes = 3 * 79;
            options.PerformanceProfile = SharpLinkPerformanceProfile.LowLatency;
        }).Build(includeGeneratedAssemblyCatalog: false);
        // Cover both failure before delivery and failure after an unknown partial prefix.
        var stream = new ObservingStream(blockWrite: true, failure: failure);
        var session = CreateSession(context, new StreamTransportConnection(stream));
        var frames = new CountingWriter[4];
        for (var index = 0; index < frames.Length; index++)
        {
            frames[index] = new CountingWriter();
            using (frames[index].BeginPacketScope(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, (ulong)index + 1))
            {
                frames[index].GetSpan(64)[..64].Fill(0x5a);
                frames[index].Advance(64);
            }
        }
        var sends = new Task[4];
        sends[0] = session.SendPacketAndFlushAsync(frames[0]).AsTask();
        try
        {
            await stream.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 1; index < sends.Length; index++)
                sends[index] = session.SendPacketAndFlushAsync(frames[index]).AsTask();
            Ensure(session.QueuedSendBytes == 3 * 79 && !sends[3].IsCompleted,
                "three reserved frames must hold capacity while the fourth waits for admission");
            Ensure(frames.All(frame => frame.ReturnCount == 0), "no owner may return before the held write terminates");
            Ensure(stream.WriteCount == 1 && stream.BytesWritten == expectedBytes && stream.DisposeCount == 0,
                "the held write must deliver exactly its injected prefix without beginning transport disposal");
            stream.ReleaseWrite.TrySetResult();
            foreach (var send in sends)
            {
                var failure = await CaptureAsync(() => new ValueTask(send));
                Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
                    "all accepted and capacity-waiting sends must observe connection closure");
            }
            await session.DisposeAsync();
            Ensure(frames.All(frame => frame.ReturnCount == 1) && session.QueuedSendBytes == 0,
                "in-flight, queued and admission-waiting owners and credits must be returned exactly once");
            Ensure(stream.WriteCount == 1 && stream.BytesWritten == expectedBytes && stream.DisposeCount == 1,
                $"terminal cleanup must not write the failed or queued frames again: writes={stream.WriteCount}, bytes={stream.BytesWritten}, disposals={stream.DisposeCount}");
        }
        finally
        {
            stream.ReleaseWrite.TrySetResult();
            await session.DisposeAsync();
        }
    }

    private static RpcSession CreateSession(SharpLinkRuntimeContext context, ITransportConnection connection)
    {
        var session = new RpcSession(connection, RpcSessionTestFixture.ClientOptions(context));
        RpcSessionTestFixture.CompleteHandshake(session);
        return session;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AnonymousPipeMustKeepNormalFlushButNotReplayOutputFault(bool failOutput)
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var input = new RecordingPipeStream(failFirstWrite: false);
        var output = new RecordingPipeStream(failOutput);
        var connection = new AnonymousPipeTransportConnection(input, output);
        if (failOutput)
        {
            var session = CreateSession(context, connection);
            var frame = CreateFrame(session, 64, 1);
            _ = await CaptureAsync(() => session.SendPacketAndFlushAsync(frame));
            await session.DisposeAsync();
            EnsureReturned(frame);
            Ensure(output.WriteCount == 1 && output.BytesWritten == 0,
                "anonymous-pipe output failure must not replay during disposal");
        }
        else
        {
            connection.Output.Write(new byte[64]);
            await connection.DisposeAsync();
            Ensure(output.WriteCount == 1 && output.BytesWritten == 64,
                "normal anonymous-pipe disposal must still flush buffered bytes");
        }
        Ensure(input.DisposeCount == 1 && output.DisposeCount == 1,
            "both anonymous-pipe streams must be released exactly once");
    }

    [Test]
    [Arguments("reader")]
    [Arguments("heartbeat")]
    public async Task NonOutputFaultWhileWriteIsHeldMustNeverClaimQuiescence(string origin)
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var transport = new QuiescenceRecordingTransport();
        var session = CreateSession(context, transport);
        var frame = CreateFrame(session, 64, 1);
        var sent = session.SendPacketAndFlushAsync(frame).AsTask();
        try
        {
            await transport.Writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            session.NotifyDisconnected(new IOException(origin + " fault"));
            var disposing = session.DisposeAsync().AsTask();
            Ensure(transport.FaultDisposals == 0 && transport.NormalDisposals == 0,
                "non-output fault must not invoke any provider disposal before physical write completion");
            Ensure(frame.WrittenCount == 79 && session.QueuedSendBytes == 79 && !disposing.IsCompleted,
                "pump must retain owner/credits while the actual output await is still held");
            transport.Writer.Release.TrySetResult();
            _ = await CaptureAsync(() => new ValueTask(sent));
            await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(transport.FaultDisposals == 0 && transport.NormalDisposals == 1 &&
                transport.Writer.Returned.Task.IsCompleted,
                "join the write before exactly one provider disposal without replacing the earlier terminal");
            EnsureReturned(frame);
            Ensure(session.QueuedSendBytes == 0, "actual output completion must release queue credits");
        }
        finally
        {
            transport.Writer.Release.TrySetResult();
            await session.DisposeAsync();
        }
    }

    [Test]
    public async Task OutputFaultCapabilityMustReceiveOriginalTerminalOnlyAfterWriteReturns()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var transport = new QuiescenceRecordingTransport();
        var session = CreateSession(context, transport);
        var frame = CreateFrame(session, 64, 1);
        var sent = session.SendPacketAndFlushAsync(frame).AsTask();
        await transport.Writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(transport.FaultDisposals == 0, "an in-flight writer cannot authorize discard completion");
        transport.Writer.Release.TrySetResult();
        var terminal = await CaptureAsync(() => new ValueTask(sent));
        await session.DisposeAsync();
        Ensure(transport.FaultDisposals == 1 && transport.QuiescentAtFaultDisposal &&
               ReferenceEquals(terminal, transport.FaultException),
            "cold capability must receive the exact winning terminal after real output completion");
        EnsureReturned(frame);
    }

    private static async Task<Exception> CaptureAsync(Func<ValueTask> operation)
    {
        try { await operation().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception exception) { return exception; }
        throw new Exception("expected operation failure");
    }

    private sealed class CountingWriter : IRpcByteBufferWriter
    {
        private readonly PooledByteBufferWriter _inner = new();
        internal int ReturnCount;
        public int WrittenCount => _inner.WrittenCount;
        public ReadOnlyMemory<byte> WrittenMemory => _inner.WrittenMemory;
        public Span<byte> WrittenSpan => _inner.WrittenSpan;
        public int Capacity => _inner.Capacity;
        public void Clear() => _inner.Clear();
        public void Advance(int count) => _inner.Advance(count);
        public Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
        public void Dispose()
        {
            Interlocked.Increment(ref ReturnCount);
            _inner.Dispose();
        }
    }

    private sealed class CompletionRecordingWriter(bool failCompletion) : PipeWriter
    {
        private readonly byte[] _buffer = new byte[4096];
        internal readonly Exception CompletionFailure = new ApplicationException("custom cleanup failure");
        internal Exception? CompletionException;
        internal int CompleteCount;
        public override void Advance(int bytes) { }
        public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer;
        public override Span<byte> GetSpan(int sizeHint = 0) => _buffer;
        public override void CancelPendingFlush() { }
        public override void Complete(Exception? exception = null)
        {
            CompleteCount++;
            CompletionException = exception;
            if (failCompletion) throw CompletionFailure;
        }
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<FlushResult>(new IOException("custom output fault"));
    }

    private sealed class RecordingPipeStream(bool failFirstWrite) : PipeStream(PipeDirection.InOut, 1)
    {
        internal int WriteCount;
        internal int BytesWritten;
        internal int DisposeCount;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            if (WriteCount == 1 && failFirstWrite)
                return ValueTask.FromException(new IOException("injected anonymous-pipe output failure"));
            BytesWritten += buffer.Length;
            return ValueTask.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class QuiescenceRecordingTransport : ITransportConnection, IQuiescentPumpFaultDisposal
    {
        private readonly Pipe _input = new();
        private Task? _dispose;
        internal readonly HeldFaultWriter Writer = new();
        internal int NormalDisposals;
        internal int FaultDisposals;
        internal bool QuiescentAtFaultDisposal;
        internal Exception? FaultException;
        public string Id => "quiescence-probe";
        public PipeReader Input => _input.Reader;
        public PipeWriter Output => Writer;
        public System.Net.EndPoint? LocalEndPoint => null;
        public System.Net.EndPoint? RemoteEndPoint => null;
        public ValueTask DisposeAsync()
        {
            NormalDisposals++;
            return new ValueTask(_dispose ??= DisposeCoreAsync());
        }
        public ValueTask DisposeAfterPumpFaultAsync(Exception exception)
        {
            FaultDisposals++;
            FaultException = exception;
            QuiescentAtFaultDisposal = Writer.Returned.Task.IsCompleted;
            return new ValueTask(_dispose ??= DisposeCoreAsync());
        }
        private async Task DisposeCoreAsync()
        {
            await Writer.Returned.Task;
            await Writer.CompleteAsync();
            await _input.Reader.CompleteAsync();
            await _input.Writer.CompleteAsync();
        }
    }

    private sealed class HeldFaultWriter : PipeWriter
    {
        private readonly byte[] _buffer = new byte[4096];
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Advance(int bytes) { }
        public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer;
        public override Span<byte> GetSpan(int sizeHint = 0) => _buffer;
        public override void CancelPendingFlush() { }
        public override void Complete(Exception? exception = null) { }
        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                await Release.Task;
                throw new IOException("held output failure");
            }
            finally { Returned.TrySetResult(); }
        }
    }

    private static IRpcByteBufferWriter CreateFrame(RpcSession session, int size, ulong requestId)
    {
        var frame = session.RentFrameWriter();
        using (frame.BeginPacketScope(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, requestId))
        {
            var payload = frame.GetSpan(size)[..size];
            payload.Fill(0x5a);
            frame.Advance(size);
        }
        return frame;
    }

    private static void EnsureReturned(IRpcByteBufferWriter frame)
    {
        try { _ = frame.WrittenCount; }
        catch (ObjectDisposedException) { return; }
        throw new Exception("frame owner was not returned");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }


    private sealed class ObservingStream(bool blockWrite = false, bool blockFlush = false,
        string? failure = null, Exception? disposeFailure = null) : Stream
    {
        internal readonly TaskCompletionSource WriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FlushEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseFlush = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ReadOnlyMemory<byte> Source { get; private set; }
        internal int WriteCount;
        internal int FlushCount;
        internal int BytesWritten;
        internal int DisposeCount;
        internal readonly TaskCompletionSource DisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action? OnWrite;
        internal Action? OnDispose;
        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            DisposeEntered.TrySetResult();
            OnDispose?.Invoke();
            return disposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(disposeFailure);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Source = buffer;
            var count = Interlocked.Increment(ref WriteCount);
            OnWrite?.Invoke();
            if (count == 1 && failure == "write-sync") throw new IOException("injected synchronous write failure");
            if (count == 1 && failure == "write-partial")
            {
                BytesWritten += Math.Min(7, buffer.Length);
                return ValueTask.FromException(new IOException("injected failure after prefix write"));
            }
            if (blockWrite)
            {
                if (count == 1 && failure is "write-prefix-after-gate" or "cancel-prefix-after-gate")
                    BytesWritten += Math.Min(7, buffer.Length);
                // Publish only after the injected prefix is visible to held-write assertions.
                WriteEntered.TrySetResult();
                return WaitForWriteAsync(buffer);
            }
            BytesWritten += buffer.Length;
            return ValueTask.CompletedTask;
        }

        private async ValueTask WaitForWriteAsync(ReadOnlyMemory<byte> buffer)
        {
            // Deliberately ignores cancellation: physical completion remains independently held.
            await ReleaseWrite.Task;
            if (failure is "write-after-gate" or "write-prefix-after-gate" && WriteCount == 1)
                throw new IOException("injected write failure after gate");
            if (failure == "cancel-prefix-after-gate" && WriteCount == 1)
                throw new OperationCanceledException("injected canceled write after prefix");
            BytesWritten += buffer.Length;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref FlushCount);
            FlushEntered.TrySetResult();
            if (count == 1 && failure == "flush") return Task.FromException(new IOException("injected flush failure"));
            return blockFlush ? WaitForFlushAsync() : Task.CompletedTask;
        }

        private async Task WaitForFlushAsync()
        {
            await ReleaseFlush.Task;
            if (failure == "flush-after-gate" && FlushCount == 1)
                throw new IOException("injected flush failure after gate");
        }
    }
}
