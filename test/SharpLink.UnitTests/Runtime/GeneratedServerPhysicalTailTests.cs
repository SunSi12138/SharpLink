using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SharpLink.Server;

namespace SharpLink.UnitTests.Runtime;

// Real RpcSession generated-pump boundaries with a recording ownership hook. These source
// controls prove Runtime ordering/handoff, not Server module/provider/scope cleanup integration.
public sealed class GeneratedServerPhysicalTailTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly RpcMethodFacts StreamFacts = new(101, 202,
        RpcMethodKind.ServerStreaming, 0, true, true);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeadlineReturnsBeforeUncooperativeMoveNextButDisposalAndOwnerStayOrdered(bool lateFault)
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "generated-physical-deadline", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        var time = new ManualTimeProvider();
        var owner = new RecordingPhysicalOwner();
        var stream = new BarrierStream(cancelMoveWithToken: false);
        try
        {
            Task pump;
            using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(session.Id, authentication: null,
                       RpcDeadline.Create(TimeSpan.FromSeconds(1), time), time)))
            {
                pump = new RpcSessionGeneratedServerBridge(session, owner).PumpOutboundStreamAsync(
                    91, 0, stream, new PoisonCodec(), false, StreamFacts, CancellationToken.None).AsTask();
            }
            await stream.MoveEntered.Task.WaitAsync(TestTimeout);
            time.Advance(TimeSpan.FromSeconds(1));
            var failure = await CaptureFailureAsync(pump);
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                "deadline is observable while the physical MoveNext is still blocked");
            Ensure(!stream.MoveCompletion.Task.IsCompleted && !stream.DisposeEntered.Task.IsCompleted,
                "Dispose must not overlap a still-running MoveNext");
            Ensure(owner.Retains == 1 && owner.Releases == 0, "one transferred owner remains held");

            if (lateFault) stream.MoveCompletion.TrySetException(new InvalidOperationException("late producer fault"));
            else stream.MoveCompletion.TrySetResult(false);
            await stream.DisposeEntered.Task.WaitAsync(TestTimeout);
            Ensure(owner.Releases == 0 && stream.LifetimeUsableDuringDispose,
                "physical owner and linked CTS survive until ordered disposal completes");
            stream.AllowDispose.TrySetResult();
            await owner.Released.Task.WaitAsync(TestTimeout);
            Ensure(owner.Retains == 1 && owner.Releases == 1 && stream.DisposeCalls == 1,
                "late success/fault disposes and releases exactly once");
        }
        finally
        {
            stream.MoveCompletion.TrySetResult(false);
            stream.AllowDispose.TrySetResult();
            if (owner.Retains != 0) await owner.Released.Task.WaitAsync(TestTimeout);
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task CancellationWithoutDeadlineTransfersPendingMoveNextRatherThanDisposingItConcurrently()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "generated-physical-cancel", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        using var cancellation = new CancellationTokenSource();
        var owner = new RecordingPhysicalOwner();
        var stream = new BarrierStream(cancelMoveWithToken: false);
        try
        {
            var pump = new RpcSessionGeneratedServerBridge(session, owner).PumpOutboundStreamAsync(
                92, 0, stream, new PoisonCodec(), false, StreamFacts, cancellation.Token).AsTask();
            await stream.MoveEntered.Task.WaitAsync(TestTimeout);
            cancellation.Cancel();
            Ensure(await CaptureFailureAsync(pump) is OperationCanceledException,
                "logical cancellation does not wait for a noncooperative producer");
            Ensure(owner.Retains == 1 && owner.Releases == 0 && !stream.DisposeEntered.Task.IsCompleted,
                "cancelled wait retains exact physical work instead of starting concurrent Dispose");
            stream.MoveCompletion.TrySetResult(false);
            await stream.DisposeEntered.Task.WaitAsync(TestTimeout);
            Ensure(owner.Releases == 0 && stream.LifetimeUsableDuringDispose, "disposal remains physically owned");
            stream.AllowDispose.TrySetResult();
            await owner.Released.Task.WaitAsync(TestTimeout);
            Ensure(stream.DisposeCalls == 1 && owner.Releases == 1, "single completion");
        }
        finally
        {
            stream.MoveCompletion.TrySetResult(false);
            stream.AllowDispose.TrySetResult();
            if (owner.Retains != 0) await owner.Released.Task.WaitAsync(TestTimeout);
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task AlreadyCancelledMoveNextStillTransfersItsUnfinishedDisposeAndLinkedLifetime()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "generated-physical-dispose", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        var time = new ManualTimeProvider();
        var owner = new RecordingPhysicalOwner();
        var stream = new BarrierStream(cancelMoveWithToken: true);
        try
        {
            Task pump;
            using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(session.Id, authentication: null,
                       RpcDeadline.Create(TimeSpan.FromSeconds(1), time), time)))
            {
                pump = new RpcSessionGeneratedServerBridge(session, owner).PumpOutboundStreamAsync(
                    93, 0, stream, new PoisonCodec(), false, StreamFacts, CancellationToken.None).AsTask();
            }
            await stream.MoveEntered.Task.WaitAsync(TestTimeout);
            time.Advance(TimeSpan.FromSeconds(1));
            Ensure(await CaptureFailureAsync(pump) is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                "unfinished disposal cannot replace or delay the selected deadline");
            await stream.DisposeEntered.Task.WaitAsync(TestTimeout);
            Ensure(stream.MoveCompletion.Task.IsCompleted && stream.LifetimeUsableDuringDispose,
                "completed cancelled MoveNext precedes Dispose with a live linked source");
            Ensure(owner.Retains == 1 && owner.Releases == 0, "Dispose itself owns the transferred tail");
            stream.AllowDispose.TrySetResult();
            await owner.Released.Task.WaitAsync(TestTimeout);
            Ensure(owner.Releases == 1 && stream.DisposeCalls == 1, "Dispose tail completes once");
        }
        finally
        {
            stream.MoveCompletion.TrySetResult(false);
            stream.AllowDispose.TrySetResult();
            if (owner.Retains != 0) await owner.Released.Task.WaitAsync(TestTimeout);
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task DeadlineDuringActualSizedSendKeepsCodecTaskAheadOfEnumeratorDisposal()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "generated-physical-send", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions(),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(session, ProtocolV2Capabilities.FlowControl,
            streamReceiveWindowBytes: 4, connectionReceiveWindowBytes: 4);
        await session.AcquireStreamSendCreditAsync(90, 0, 4, CancellationToken.None);
        var time = new ManualTimeProvider();
        var owner = new RecordingPhysicalOwner();
        var stream = new BarrierStream(cancelMoveWithToken: false, producesValue: true);
        stream.MoveCompletion.SetResult(true);
        using var codec = new BarrierSizedCodec();
        try
        {
            Task pump;
            using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(session.Id, authentication: null,
                       RpcDeadline.Create(TimeSpan.FromSeconds(1), time), time)))
            {
                pump = new RpcSessionGeneratedServerBridge(session, owner).PumpOutboundStreamAsync(
                    96, 0, stream, codec, false, StreamFacts, CancellationToken.None).AsTask();
            }
            // The real async send first waits for credit; releasing credit lets codec work
            // resume independently while the generated pump waits for its physical send task.
            session.ApplyWindowUpdate(90, new ProtocolV2WindowUpdate(0, 4));
            await codec.SerializeEntered.Task.WaitAsync(TestTimeout);
            time.Advance(TimeSpan.FromSeconds(1));
            Ensure(await CaptureFailureAsync(pump) is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                "logical deadline can complete while the actual send codec is blocked");
            Ensure(owner.Retains == 1 && owner.Releases == 0 && !stream.DisposeEntered.Task.IsCompleted,
                "send work, like MoveNext, must finish before enumerator disposal");
            codec.AllowSerialize.Set();
            await stream.DisposeEntered.Task.WaitAsync(TestTimeout);
            Ensure(owner.Releases == 0 && stream.LifetimeUsableDuringDispose,
                "send completion transfers directly to ordered physical disposal");
            stream.AllowDispose.TrySetResult();
            await owner.Released.Task.WaitAsync(TestTimeout);
            Ensure(owner.Releases == 1 && stream.DisposeCalls == 1, "single send-tail owner release");
            await session.FlushSendQueueAsync().AsTask().WaitAsync(TestTimeout);
            if (output.Reader.TryRead(out var read))
            {
                Ensure(read.Buffer.IsEmpty, "send that finishes after the deadline cannot publish late StreamData");
                output.Reader.AdvanceTo(read.Buffer.End);
            }
        }
        finally
        {
            codec.AllowSerialize.Set();
            stream.AllowDispose.TrySetResult();
            if (owner.Retains != 0) await owner.Released.Task.WaitAsync(TestTimeout);
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task OrdinaryEofStillAwaitsDisposeWithoutInventingADeadlineAfterSuccessPublication()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "generated-normal-dispose-order", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        var time = new ManualTimeProvider();
        var owner = new RecordingPhysicalOwner();
        var stream = new BarrierStream(cancelMoveWithToken: false);
        stream.MoveCompletion.SetResult(false);
        try
        {
            Task pump;
            using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(session.Id, authentication: null,
                       RpcDeadline.Create(TimeSpan.FromSeconds(1), time), time)))
            {
                pump = new RpcSessionGeneratedServerBridge(session, owner).PumpOutboundStreamAsync(
                    97, 0, stream, new PoisonCodec(), false, StreamFacts, CancellationToken.None).AsTask();
            }
            await stream.DisposeEntered.Task.WaitAsync(TestTimeout);
            time.Advance(TimeSpan.FromSeconds(2));
            Ensure(!pump.IsCompleted && owner.Retains == 0 && owner.Releases == 0,
                "normal EOF preserves awaited Dispose, with no second terminal or detach");
            stream.AllowDispose.TrySetResult();
            await pump.WaitAsync(TestTimeout);
            await session.FlushSendQueueAsync().AsTask().WaitAsync(TestTimeout);
        }
        finally
        {
            stream.AllowDispose.TrySetResult();
            if (owner.Retains != 0) await owner.Released.Task.WaitAsync(TestTimeout);
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task OrdinaryCompletedPumpDoesNotTouchPhysicalTailHook()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "generated-no-physical-split", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        var owner = new RecordingPhysicalOwner();
        var stream = new BarrierStream(cancelMoveWithToken: false);
        stream.MoveCompletion.SetResult(false);
        stream.AllowDispose.SetResult();
        await new RpcSessionGeneratedServerBridge(session, owner).PumpOutboundStreamAsync(
            94, 0, stream, new PoisonCodec(), false, StreamFacts, CancellationToken.None);
        Ensure(owner.Retains == 0 && owner.Releases == 0 && stream.DisposeCalls == 1,
            "ordinary completion adds no tail counter traffic");
        await session.FlushSendQueueAsync().AsTask().WaitAsync(TestTimeout);
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task ActualRequestAndModuleOwnerSurviveLogicalDeadlineThroughEnumeratorServiceAndScopeCleanup()
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(new WaitingListener())
            .Build();
        await server.StartAsync().AsTask().WaitAsync(TestTimeout);
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "generated-actual-physical-owner", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        var connection = new ServerConnectionState(session, new RpcSessionGeneratedServerBridge(session),
            new StripedLongMap<ServerCallCancellationState>(), CancellationToken.None, TimeProvider.System);
        Ensure(connection.MarkReady(null), "actual connection is ready for call admission");
        using var runtime = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new EmptyManifest(typeof(GeneratedServerPhysicalTailTests).Assembly);
        using var codecs = runtime.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(manifest.OwnerAssembly, manifest, codecs);
        var service = new BarrierService();
        var scopes = new RecordingScopeFactory();
        var registration = ServiceRegistration.CreatePerCall(typeof(BarrierService), new PoisonStub(),
            scopes, _ => service, disposeService: true, module);
        var stream = new BarrierStream(cancelMoveWithToken: false);
        ServerRequestPermit? permit = null;
        Task? cleanup = null;
        try
        {
            Ensure(server.TryReserveCall(connection, mayDecode: false, out permit) == ServerCallAdmissionResult.Acquired,
                "actual request permit acquired at its existing boundary");
            var owner = permit!;
            Ensure(owner.TryProtectRegistration(registration, StreamFacts), "one existing module stream lease acquired");
            owner.Activate();
            var bridge = new RpcSessionGeneratedServerBridge(session, owner);
            var lease = await registration.AcquirePreparedAsync(connection, owner, bridge, 95);
            Ensure(module.RemainingCalls == 1 && module.RemainingStreams == 1,
                "activation consumes proof instead of acquiring a second module owner");
            var time = new ManualTimeProvider();
            Task pump;
            using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(session.Id, authentication: null,
                       RpcDeadline.Create(TimeSpan.FromSeconds(1), time), time)))
            {
                pump = bridge.PumpOutboundStreamAsync(95, 0, stream, new PoisonCodec(), false,
                    StreamFacts, CancellationToken.None).AsTask();
            }
            await stream.MoveEntered.Task.WaitAsync(TestTimeout);
            time.Advance(TimeSpan.FromSeconds(1));
            Ensure(await CaptureFailureAsync(pump) is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                "logical failure is visible before physical producer completion");
            cleanup = DisposeServiceAfterTailAsync(owner, lease);
            owner.AttachPhysicalServiceCleanup(cleanup);
            owner.Dispose(); // Actual logical release must defer the same request/module owner.
            module.TryBeginDraining();
            Ensure(module.RemainingCalls == 1 && module.RemainingStreams == 1 && connection.ActiveCalls == 1 &&
                   server.ActiveCallCountForDiagnostics == 1 && !service.DisposeEntered.Task.IsCompleted,
                "retirement cannot drain the retained physical request/module/service owner");
            Ensure(!stream.DisposeEntered.Task.IsCompleted, "no concurrent producer disposal");

            stream.MoveCompletion.TrySetResult(false);
            await stream.DisposeEntered.Task.WaitAsync(TestTimeout);
            Ensure(!service.DisposeEntered.Task.IsCompleted && scopes.Disposals == 0, "service/scope follow enumerator");
            stream.AllowDispose.TrySetResult();
            await service.DisposeEntered.Task.WaitAsync(TestTimeout);
            Ensure(module.RemainingCalls == 1 && connection.ActiveCalls == 1 && scopes.Disposals == 0,
                "asynchronous service disposal remains inside the same physical owner");
            service.AllowDispose.TrySetResult();
            await cleanup.WaitAsync(TestTimeout);
            owner.Dispose();
            await module.WaitForDrainAsync().WaitAsync(TestTimeout);
            Ensure(module.RemainingCalls == 0 && module.RemainingStreams == 0 && connection.ActiveCalls == 0 &&
                   server.ActiveCallCountForDiagnostics == 0 && scopes.Disposals == 1,
                "ordered service/scope completion releases exact physical ownership once");
        }
        finally
        {
            stream.MoveCompletion.TrySetResult(false);
            stream.AllowDispose.TrySetResult();
            service.AllowDispose.TrySetResult();
            if (cleanup is not null) await cleanup.WaitAsync(TestTimeout);
            else if (permit is not null) await permit.JoinPhysicalTailsAsync().AsTask().WaitAsync(TestTimeout);
            permit?.Dispose();
            await registration.DisposeAsync();
            await connection.CloseAsync();
            await connection.ServiceCleanupTask;
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    // This component control uses actual production owner/lease types, while explicitly
    // composing their cleanup handoff. It is not a generated TCP-dispatch integration proof.
    private static async Task DisposeServiceAfterTailAsync(ServerRequestPermit owner, ServiceLease lease)
    {
        await owner.JoinPhysicalTailsAsync();
        await lease.DisposeAsync();
    }

    private static async Task<Exception> CaptureFailureAsync(Task task)
    {
        try { await task.WaitAsync(TestTimeout); }
        catch (Exception exception) when (exception is not TimeoutException) { return exception; }
        throw new InvalidOperationException("Expected logical terminal before physical cleanup.");
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RecordingPhysicalOwner : IGeneratedStreamPhysicalOwner
    {
        private int _retains;
        private int _releases;
        internal int Retains => Volatile.Read(ref _retains);
        internal int Releases => Volatile.Read(ref _releases);
        internal TaskCompletionSource Released { get; } = NewSignal();
        public void RetainPhysicalTail() => Interlocked.Increment(ref _retains);
        public void ReleasePhysicalTail()
        {
            Interlocked.Increment(ref _releases);
            Released.TrySetResult();
        }
    }

    private sealed class BarrierStream(bool cancelMoveWithToken, bool producesValue = false) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal TaskCompletionSource MoveEntered { get; } = NewSignal();
        internal TaskCompletionSource<bool> MoveCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeEntered { get; } = NewSignal();
        internal TaskCompletionSource AllowDispose { get; } = NewSignal();
        internal int DisposeCalls;
        internal bool LifetimeUsableDuringDispose;
        private CancellationToken _token;
        private CancellationTokenRegistration _cancellation;
        public int Current => producesValue ? 17 : throw new InvalidOperationException("No item may be read after the selected terminal.");
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            _token = cancellationToken;
            if (cancelMoveWithToken)
                _cancellation = cancellationToken.Register(() => MoveCompletion.TrySetCanceled(cancellationToken));
            return this;
        }
        public ValueTask<bool> MoveNextAsync()
        {
            MoveEntered.TrySetResult();
            return new ValueTask<bool>(MoveCompletion.Task);
        }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCalls);
            // WaitHandle acquisition throws if the linked source was disposed prematurely.
            _ = _token.WaitHandle;
            LifetimeUsableDuringDispose = true;
            DisposeEntered.TrySetResult();
            await AllowDispose.Task;
            _ = _token.WaitHandle;
            _cancellation.Dispose();
        }
    }

    private sealed class PoisonCodec : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> buffer)
            => throw new InvalidOperationException("No late item may be serialized.");
        public int Deserialize(in ReadOnlySequence<byte> buffer)
            => throw new InvalidOperationException("No inbound decode expected.");
    }
    private sealed class BarrierSizedCodec : IRpcCodec<int>, IRpcSizedCodec<int>, IDisposable
    {
        internal TaskCompletionSource SerializeEntered { get; } = NewSignal();
        internal ManualResetEventSlim AllowSerialize { get; } = new();
        public bool CanExactSize => true;
        public bool TryGetEncodedSize(in int value, out int size) { size = 4; return true; }
        public bool TryGetEncodedSize(in int value, out int size, out IRpcSizedCodecSnapshot? snapshot)
        { size = 4; snapshot = null; return true; }
        public void Serialize(in int value, IBufferWriter<byte> buffer) => throw new InvalidOperationException("Expected sized path.");
        public void SerializeSized(in int value, IBufferWriter<byte> buffer, int size, IRpcSizedCodecSnapshot? snapshot)
        {
            SerializeEntered.TrySetResult();
            if (!AllowSerialize.Wait(TestTimeout)) throw new TimeoutException("Physical codec barrier was not released.");
            BitConverter.TryWriteBytes(buffer.GetSpan(4), value);
            buffer.Advance(4);
        }
        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot) { }
        public int Deserialize(in ReadOnlySequence<byte> buffer) => throw new NotSupportedException();
        public void Dispose() => AllowSerialize.Dispose();
    }

    private sealed class BarrierService : IAsyncDisposable
    {
        internal TaskCompletionSource DisposeEntered { get; } = NewSignal();
        internal TaskCompletionSource AllowDispose { get; } = NewSignal();
        public async ValueTask DisposeAsync()
        {
            DisposeEntered.TrySetResult();
            await AllowDispose.Task;
        }
    }

    private sealed class RecordingScopeFactory : IServiceScopeFactory
    {
        internal int Disposals;
        public IServiceScope CreateScope() => new Scope(this);
        private sealed class Scope(RecordingScopeFactory owner) : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type serviceType) => throw new InvalidOperationException("Unexpected provider lookup.");
            public void Dispose() => Interlocked.Increment(ref owner.Disposals);
        }
    }

    private sealed class WaitingListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;
        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancelled listener returned.");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyManifest(Assembly assembly) : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => assembly;
        public RpcHash128 RpcAssemblyHash => new(0x706879736963616cUL, 0x7461696c2d763431UL);
        public string CompileTimeDescriptor => "physical-tail-fixture";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class PoisonStub : IRpcStub
    {
        public long InterfaceHash => 101;
        public RpcMethodFacts ResolveMethodFacts(long methodHash) => throw new InvalidOperationException("No runtime facts callback.");
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args) => throw new NotSupportedException();
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => throw new NotSupportedException();
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

}
