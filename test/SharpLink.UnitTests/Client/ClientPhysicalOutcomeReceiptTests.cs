using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

// Component SOURCE controls: actual group/pending/Session/strict supervisor, not shipping
// registration closure. The selected legacy-Track integration gate remains explicitly RED.
public sealed class ClientPhysicalOutcomeReceiptTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task DeliveredCancellationHasNoPhysicalResidualAfterActualProducerExit()
    {
        await using var f = new Fixture();
        var group = await f.Create();
        var stream = new CooperativeStream();
        var physical = group.RunWriterAsync(new OneStream(stream, f.Codec), group.ProducerToken);
        await stream.Entered.Task.WaitAsync(Timeout);
        f.Cancel();
        Ensure(await Error(f.Response) is OperationCanceledException, "logical cancellation delivered");
        await physical.WaitAsync(Timeout);
        Ensure(await StrictOutcome(physical) is null, "delivered business cancellation is not physical Stop failure");
        f.AssertReleased();
    }

    [Test]
    public async Task DeliveredZeroSinkOmissionIsLogicalFailureNotSupervisedPhysicalFailure()
    {
        await using var f = new Fixture();
        var group = await f.Create(count: 2);
        var physical = group.RunWriterAsync(new NoStreams(), group.ProducerToken);
        Ensure(await Error(f.Response) is InvalidOperationException, "omission is delivered, never clean business EOF");
        await physical.WaitAsync(Timeout);
        Ensure(await StrictOutcome(physical) is null && f.Session.IsConnected, "actual bounded terminal cleanup succeeds without Stop pollution");
        f.AssertReleased();
    }

    [Test]
    public Task IndependentDisposeOceBeforeSealKeepsOriginalIdentity() => DisposeOce(sealBefore: false);
    [Test]
    public Task IndependentDisposeOceAfterSealKeepsOriginalIdentity() => DisposeOce(sealBefore: true);

    private static async Task DisposeOce(bool sealBefore)
    {
        await using var f = new Fixture();
        var group = await f.Create();
        var expected = new OperationCanceledException(group.ProducerToken);
        var stream = new CooperativeStream(expected);
        var physical = group.RunWriterAsync(new OneStream(stream, f.Codec), group.ProducerToken);
        var supervisor = new FrameworkTaskSupervisor();
        if (sealBefore) supervisor.Seal();
        FrameworkTaskRegistration? receipt = null;
        supervisor.TrackStrictPhysical(physical, "component physical producer", ref receipt);
        await stream.Entered.Task.WaitAsync(Timeout);
        f.Cancel();
        Ensure(await Error(f.Response) is OperationCanceledException, "ordinary logical cancel delivered separately");
        Ensure(await Error(physical) is PhysicalTaskFailureException && physical.IsFaulted && !physical.IsCanceled,
            "independent cleanup OCE crosses async boundary in a non-OCE marker");
        if (!sealBefore) supervisor.Seal();
        Ensure(ReferenceEquals(await Error(supervisor.DrainAsync()), expected), "strict path preserves exact raw OCE before/after Seal");
        f.AssertReleased();
    }

    [Test]
    public async Task SameExceptionObjectFromBusinessAndDisposeRetainsIndependentOccurrence()
    {
        await using var f = new Fixture();
        var group = await f.Create();
        var same = new InvalidOperationException("same object, two independent stages");
        var physical = group.RunWriterAsync(new OneStream(new FailingStream(same, same), f.Codec), group.ProducerToken);
        Ensure(ReferenceEquals(await Error(f.Response), same), "exact business primary delivered");
        Ensure(await Error(physical) is PhysicalTaskFailureException, "independent Dispose remains reportable");
        Ensure(ReferenceEquals(await StrictOutcome(physical), same), "reference equality cannot erase a separately recorded cleanup stage");
        f.AssertReleased();
    }

    [Test]
    public async Task DeliveredOpaqueUserAggregateIsNotFlattenedIntoPhysicalFailures()
    {
        await using var f = new Fixture();
        var group = await f.Create();
        var raw = new AggregateException(new IOException("user one"), new OperationCanceledException("user two"));
        var physical = group.RunWriterAsync(new OneStream(new FailingStream(raw), f.Codec), group.ProducerToken);
        Ensure(ReferenceEquals(await Error(f.Response), raw), "user aggregate retained as one logical object");
        await physical.WaitAsync(Timeout);
        Ensure(await StrictOutcome(physical) is null, "receipt discharges exact opaque primary without flattening its children");
    }

    [Test]
    public async Task SelectedReceiptWaitsForDeliveryAndAcceptsObserverBatchWithoutCallerDoubleThrow()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var expected = new InvalidOperationException("terminal observer");
        await using var f = new Fixture();
        var group = await f.Create(observer: new Observer(() => { entered.Set(); if (!release.Wait(Timeout)) throw new TimeoutException(); throw expected; }));
        var stream = new CooperativeStream();
        var physical = group.RunWriterAsync(new OneStream(stream, f.Codec), group.ProducerToken);
        await stream.Entered.Task.WaitAsync(Timeout);
        var cancellation = Task.Run(f.Cancel);
        try
        {
            Ensure(entered.Wait(Timeout), "actual observer holds selected terminal before sink delivery");
            Ensure(!physical.IsCompleted && f.Connection.PendingCalls.ActiveCount == 1,
                "physical reducer waits selected receipt and original pending owner remains");
            release.Set();
            await cancellation.WaitAsync(Timeout); // Accepted batch no longer belongs to cancellation caller.
            Ensure(await Error(f.Response) is OperationCanceledException, "throwing observer does not skip real result delivery");
            Ensure(await Error(physical) is PhysicalTaskFailureException, "independent observer error transferred to physical reducer");
            Ensure(ReferenceEquals(await StrictOutcome(physical), expected), "one exact raw batch owner, not caller plus supervisor");
            f.AssertReleased();
        }
        finally { release.Set(); await cancellation.WaitAsync(Timeout); await Error(physical); }
    }

    [Test]
    public async Task PendingOwnerFailureAfterSinkStorePreservesConsumedOriginalResultAndCallerFault()
    {
        await using var f = new Fixture();
        var owner = new PostStoreOwner();
        using var pending = new PendingRequestTable(8, f.Context.Codecs, owner, TimeProvider.System);
        var operation = pending.Rent(f.Codec, PendingCallKind.ClientStreaming, default,
            CancellationToken.None, out var id);
        var original = operation.AsValueTask();
        var expected = new IOException("actual owner callback after result store");
        var consumed = 0;
        owner.AfterStore = () =>
        {
            Ensure(original.IsCompleted, "owner callback is strictly after original sink store");
            Exception? logical = null;
            // This ValueTask is proven completed above: direct GetResult consumes it once,
            // not a sync-over-async join or a second AsTask/await of a pooled operation.
            try { _ = original.GetAwaiter().GetResult(); }
            catch (Exception error) { logical = error; }
            Ensure(logical is OperationCanceledException, "original logical cancellation is immutable");
            ++consumed;
            throw expected;
        };
        var callerFailure = Capture(() => pending.TryComplete(id, PendingCallCompletionReason.UserCancellation));
        Ensure(consumed == 1 && owner.Completions == 1 && ReferenceEquals(callerFailure, expected),
            "consumed/pool-reset original result cannot erase the distinct post-store owner failure");
        Ensure(pending.Count == 0 && pending.ActiveCount == 0, "terminal cleanup releases the exact original pending owner");
        // Split scope: this real Pending-table/custom-owner control proves post-store consumption
        // and caller-owned fault. The existing held-observer group test separately proves batch
        // transfer; combined group+post-store-consumed-operation integration remains HOLD.
    }

    [Test]
    public async Task NormalProducerDoesNotWaitForResponseAndLateObserverBatchStaysCallerOwned()
    {
        var expected = new InvalidOperationException("late observer after reduction closed");
        await using var f = new Fixture();
        var group = await f.Create(observer: new Observer(() => throw expected));
        var physical = group.RunWriterAsync(new OneStream(new EmptyStream(), f.Codec), group.ProducerToken);
        await physical.WaitAsync(Timeout);
        Ensure(!f.Response.IsCompleted && f.Connection.CallAdmissionReservationCount == 0,
            "Open/no-failure success releases producer reservation before remote response");
        Ensure(await StrictOutcome(physical) is null, "normal physical outcome already closed successfully");
        Ensure(Contains(Capture(f.Cancel), expected), "closed reducer rejects late batch; terminal caller retains it");
        Ensure(await Error(f.Response) is OperationCanceledException, "late observer failure still cannot skip sink");
        f.AssertReleased();
    }

    [Test]
    public async Task OneWayNormalPhysicalFinishPrecedesLocalStreamCompleteWithoutSelfJoin()
    {
        await using var f = new Fixture();
        var group = await f.Create(oneWay: true);
        var physical = group.RunWriterAsync(new OneStream(new EmptyStream(), f.Codec), group.ProducerToken);
        await physical.WaitAsync(Timeout);
        Ensure(!f.Response.IsCompleted, "group does not wait for future LocalStreamComplete");
        f.Connection.PendingCalls.TryComplete(group.RequestId, PendingCallCompletionReason.LocalStreamComplete);
        await f.Response.WaitAsync(Timeout);
        Ensure(await StrictOutcome(physical) is null, "normal OneWay physical owner is independently complete");
        f.AssertReleased();
    }

    [Test]
    public async Task FenceFailureAfterDeliveredOmissionRemainsPhysical()
    {
        await using var f = new Fixture();
        var group = await f.Create();
        var expected = new IOException("actual cleanup/fence output flush failure");
        f.Output.Failure = expected;
        var physical = group.RunWriterAsync(new NoStreams(), group.ProducerToken);
        Ensure(await Error(f.Response) is InvalidOperationException, "omission already delivered");
        Ensure(await Error(physical) is PhysicalTaskFailureException, "actual failed physical cleanup is enveloped");
        Ensure(Contains(await StrictOutcome(physical), expected), "transport/fence stage cannot be reduced as business omission");
        f.AssertReleased();
    }

    [Test]
    public async Task ProducerOwnerCleanupFailureStillReleasesActualModuleLeaseAndPendingOwners()
    {
        await using var f = new Fixture();
        var group = await f.Create();
        var expected = new IOException("producer module-owner CTS disposal");
        var manifest = new EmptyManifest();
        using var registration = f.Context.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(manifest.OwnerAssembly, manifest, registration);
        Ensure(module.TryAcquire(true, out var lease), "actual module stream lease");
        var owner = new SharpLinkClientStreamModuleLeaseOwner(lease, new ThrowingDisposeSource(expected));
        group.TakeProducerOwner(owner);
        owner.Dispose(); // Logical owner finished, original source/module still held by physical group.
        var physical = group.RunWriterAsync(new NoStreams(), group.ProducerToken);
        Ensure(await Error(f.Response) is InvalidOperationException, "omission primary delivered separately");
        Ensure(await Error(physical) is PhysicalTaskFailureException, "owner cleanup failure remains physical");
        Ensure(ReferenceEquals(await StrictOutcome(physical), expected) && module.RemainingStreams == 0 && module.RemainingCalls == 0,
            "source disposal throw cannot skip actual module release or erase its independent failure");
        module.AssertAccountingInvariant();
        module.TryBeginDraining(); await module.WaitForDrainAsync().WaitAsync(Timeout);
        f.AssertReleased();
    }

    [Test]
    public async Task LateResponseThenDisposePreservesBoundedOpaqueWriterDuplication()
    {
        await using var f = new Fixture();
        var group = await f.Create();
        var same = new IOException("Dispose after a different successful logical response");
        var stream = new HeldDisposeStream(same);
        var physical = group.RunWriterAsync(new OneStream(stream, f.Codec), group.ProducerToken);
        await stream.Disposing.Task.WaitAsync(Timeout);
        try
        {
            f.Respond(group.RequestId);
            await f.Response.WaitAsync(Timeout);
            Ensure(!physical.IsCompleted, "logical response cannot consume held physical Dispose");
            stream.Release.TrySetResult();
            Ensure(await Error(physical) is PhysicalTaskFailureException, "independent Dispose remains physical");
            var raw = await StrictOutcome(physical);
            Ensure(raw is AggregateException aggregate && aggregate.InnerExceptions.Count == 2 &&
                ReferenceEquals(aggregate.InnerExceptions[0], same) && ReferenceEquals(aggregate.InnerExceptions[1], same),
                "one exact slot occurrence plus one opaque writer candidate; bounded duplication is explicit, not exactly-once");
            f.AssertReleased();
        }
        finally { stream.Release.TrySetResult(); await Error(physical); }
    }

    [Test]
    public async Task SameObjectIndependentWriterOccurrenceCannotBeReferenceFiltered()
    {
        await using var f = new Fixture();
        var group = await f.Create();
        var same = new IOException("same object deliberately thrown at two independent boundaries");
        var stream = new HeldDisposeStream(same);
        var physical = group.RunWriterAsync(new IndependentWriter(stream, f.Codec, same), group.ProducerToken);
        await stream.Disposing.Task.WaitAsync(Timeout);
        try
        {
            f.Respond(group.RequestId); await f.Response.WaitAsync(Timeout);
            stream.Release.TrySetResult();
            Ensure(await Error(physical) is PhysicalTaskFailureException, "both independent occurrences survive");
            var raw = await StrictOutcome(physical);
            Ensure(raw is AggregateException aggregate && aggregate.InnerExceptions.Count == 2 &&
                ReferenceEquals(aggregate.InnerExceptions[0], same) && ReferenceEquals(aggregate.InnerExceptions[1], same),
                "opaque writer independence cannot be inferred or erased using reference identity");
            f.AssertReleased();
        }
        finally { stream.Release.TrySetResult(); await Error(physical); }
    }

    [Test]
    [NotInParallel("dispatcher-pool")]
    public async Task FailedExactSinkRetentionMustNotMutateReusedDispatcherGeneration()
    {
        await using var f = new Fixture();
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var original = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var oldLease = original.RetainForRegistration();
        original.Complete(exception: null); await original.DisposeAsync();
        original.ReleaseRegistrationRetention(oldLease);
        var current = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var lease = current.RetainForRegistration();
        var receipt = (IStreamSinkPublicationReceipt)original;
        try
        {
            Ensure(ReferenceEquals(current, original), "actual same pooled object/new generation");
            Ensure(!receipt.TryRetainSinkPublication(oldLease, out _), "actual old-generation retention fails");
            var completion = new PendingCallCompletion(123, PendingCallKind.DuplexStreaming,
                PendingCallCompletionReason.UserCancellation, original, new OperationCanceledException("old call"), false);
            var disposition = StreamSinkPublicationDisposition.NotPublished;
            var failure = Capture(() => ((IPendingCallOwner)f.Connection).OnPendingCallCompleted(in completion, ref disposition, 0));
            Ensure(failure is InvalidOperationException && disposition == StreamSinkPublicationDisposition.NotPublished,
                "failed exact retention is a delivery failure, never a generic direct mutation fallback");
            Ensure(receipt.TryRetainSinkPublication(lease, out var held), "new generation retains independently");
            try { receipt.PublishSinkTerminal(null, false, lease, ref disposition); }
            finally { receipt.ReleaseSinkPublication(held); }
            Ensure(disposition == StreamSinkPublicationDisposition.Published,
                "old full-ID cleanup and stale sink attempt did not complete/local-abort/retire the new generation");
        }
        finally
        {
            current.Complete(exception: null); await current.DisposeAsync();
            current.ReleaseRegistrationRetention(lease);
            PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        }
    }

    private static async Task<Exception?> StrictOutcome(Task task)
    {
        var supervisor = new FrameworkTaskSupervisor();
        FrameworkTaskRegistration? receipt = null;
        supervisor.TrackStrictPhysical(task, "actual Client group reducer component", ref receipt);
        supervisor.Seal();
        return await Error(supervisor.DrainAsync());
    }
    private static async Task<Exception?> Error(Task task)
    { try { await task.WaitAsync(Timeout); return null; } catch (Exception error) when (error is not TimeoutException) { return error; } }
    private static Exception? Capture(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
    private static bool Contains(Exception? error, Exception expected)
        => ReferenceEquals(error, expected) || error is AggregateException aggregate && aggregate.InnerExceptions.Any(e => Contains(e, expected)) ||
            error?.InnerException is { } inner && Contains(inner, expected);
    private static void Ensure(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private readonly struct NoStreams : IRpcClientStreamWriter
    { public ValueTask WriteAsync(IRpcClientStreamSink sink, long id, CancellationToken token = default) => ValueTask.CompletedTask; }
    private readonly struct OneStream(IAsyncEnumerable<int> stream, IRpcCodec<int> codec) : IRpcClientStreamWriter
    { public ValueTask WriteAsync(IRpcClientStreamSink sink, long id, CancellationToken token = default) => new(sink.SendClientStreamAsync(id, 1, stream, codec, token)); }
    private sealed class PostStoreOwner : IPendingCallOwner
    {
        internal Action? AfterStore;
        internal int Completions;
        public void OnPendingCallCompleted(in PendingCallCompletion completion) { ++Completions; AfterStore?.Invoke(); }
        public void OnProducerCancellationCallbackFailed(Exception exception) => throw new InvalidOperationException("Unexpected producer cancellation callback failure.", exception);
    }
    private sealed class Observer(Action action) : IPendingCallCompletionObserver
    { public void OnResponseObserved() { } public void OnPendingCallCompleted(in PendingCallCompletion completion) => action(); }
    private sealed class CooperativeStream(Exception? disposeFailure = null) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private CancellationToken _token;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) { _token = cancellationToken; return this; }
        public async ValueTask<bool> MoveNextAsync() { Entered.TrySetResult(); await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, _token); return false; }
        public ValueTask DisposeAsync() => disposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(disposeFailure);
    }
    private sealed class FailingStream(Exception business, Exception? dispose = null) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromException<bool>(business);
        public ValueTask DisposeAsync() => dispose is null ? ValueTask.CompletedTask : ValueTask.FromException(dispose);
    }
    private sealed class EmptyStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    { public int Current => 0; public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this; public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false); public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class HeldDisposeStream(Exception failure) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal readonly TaskCompletionSource Disposing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);
        public async ValueTask DisposeAsync() { Disposing.TrySetResult(); await Release.Task; throw failure; }
    }
    private readonly struct IndependentWriter(IAsyncEnumerable<int> stream, IRpcCodec<int> codec, Exception same) : IRpcClientStreamWriter
    {
        public async ValueTask WriteAsync(IRpcClientStreamSink sink, long id, CancellationToken token = default)
        {
            try { await sink.SendClientStreamAsync(id, 1, stream, codec, token); }
            catch (Exception) { /* This manual writer deliberately consumes the sink outcome. */ }
            throw same; // A separate manual-writer occurrence, intentionally the exact same object.
        }
    }
    private sealed class ThrowingDisposeSource(Exception failure) : CancellationTokenSource
    { protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) throw failure; } }
    private sealed class EmptyManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => GetType().Assembly;
        public RpcHash128 RpcAssemblyHash => TestGeneratedIdentity.ManifestHash;
        public string CompileTimeDescriptor => "client-physical-outcome-control";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly CancellationTokenSource _cancel = new();
        private readonly SharpLinkClient _client;
        internal readonly SharpLinkRuntimeContext Context;
        internal readonly RpcSession Session;
        internal readonly ClientConnection Connection;
        internal readonly FailingOutput Output;
        internal readonly IRpcCodec<int> Codec;
        internal Task Response = Task.CompletedTask;
        internal Fixture()
        {
            _client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
            Context = (SharpLinkRuntimeContext)_client.RuntimeContext;
            Output = new FailingOutput(_output.Writer);
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport("physical-outcome-receipt", _input.Reader, Output, RpcSessionTestFixture.ClientOptions(Context));
            Session.NotifyConnected();
            Connection = new ClientConnection(_client, Session, new CancellationTokenSource(), 8, Context);
            Codec = Context.Codecs.GetCodec<int>();
        }
        internal async Task<ClientStreamPublicationGroup> Create(int count = 1, bool oneWay = false, IPendingCallCompletionObserver? observer = null)
        {
            Ensure(Connection.TryReserveExactCallAdmission(), "original connection reservation");
            long id;
            if (oneWay)
            {
                var pending = Connection.PendingCalls.RegisterOneWayClientStream(default, _cancel.Token, observer);
                id = pending.Id; Response = pending.Operation.AsValueTask().AsTask();
            }
            else Response = Connection.PendingCalls.Rent(Codec, PendingCallKind.ClientStreaming, default, _cancel.Token, out id, observer).AsValueTask().AsTask();
            var group = new ClientStreamPublicationGroup(Connection, new TestRpcBindingPublication(Session), id, count);
            var writer = Session.RentFrameWriter();
            using (writer.BeginPacketScope(oneWay ? ProtocolV2FrameType.OneWayStreamRequest : ProtocolV2FrameType.Request,
                oneWay ? ProtocolV2FrameFlags.OneWay : ProtocolV2FrameFlags.Cancellable, (ulong)id))
            {
                var length = oneWay ? 18 : 16;
                var span = writer.GetSpan(length);
                BinaryPrimitives.WriteUInt64LittleEndian(span, group.Binding.Token); BinaryPrimitives.WriteInt64LittleEndian(span[8..], 1);
                if (oneWay) BinaryPrimitives.WriteUInt16LittleEndian(span[16..], (ushort)count);
                writer.Advance(length);
            }
            var prepared = Session.PrepareOutboundFrame(writer, group.ProducerToken);
            Ensure(Connection.PendingCalls.TryPublishRequest(id, Session, prepared, false, group.ProducerToken, Connection.PendingCalls, out _, out _, group), "actual head transfer");
            await Session.FlushSendQueueAsync().AsTask().WaitAsync(Timeout);
            if (!oneWay) Connection.PendingCalls.ReceiveRequestAccepted((ulong)id);
            return group;
        }
        internal void Respond(long requestId)
        {
            var buffer = new ArrayBufferWriter<byte>();
            var value = 42;
            Codec.Serialize(in value, buffer);
            var payload = new ReadOnlySequence<byte>(buffer.WrittenMemory);
            Ensure(Connection.PendingCalls.Dispatch(requestId, ref payload), "actual distinct successful response wins before Dispose exit");
        }
        internal void Cancel() => _cancel.Cancel();
        internal void AssertReleased() => Ensure(Connection.PendingCalls.ActiveCount == 0 && Connection.CallAdmissionReservationCount == 0 && Session.QueuedSendBytes == 0, "actual physical owners settled");
        public async ValueTask DisposeAsync()
        {
            _cancel.Cancel(); await Error(Response);
            await Connection.DisposeAsync(); await _client.DisposeAsync();
            await _input.Writer.CompleteAsync(); await _output.Reader.CompleteAsync(); _cancel.Dispose();
        }
    }
    private sealed class FailingOutput(PipeWriter inner) : PipeWriter
    {
        internal Exception? Failure;
        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
            => Failure is { } error ? ValueTask.FromException<FlushResult>(error) : inner.FlushAsync(cancellationToken);
    }
}
