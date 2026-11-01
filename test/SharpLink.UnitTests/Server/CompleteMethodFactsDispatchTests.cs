using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public partial class SharpLinkServerInvocationTests
{
    [Test]
    [Arguments(RpcMethodKind.Unary, false)]
    [Arguments(RpcMethodKind.Unary, true)]
    [Arguments(RpcMethodKind.OneWay, false)]
    [Arguments(RpcMethodKind.OneWay, true)]
    [Arguments(RpcMethodKind.ClientStreaming, false)]
    [Arguments(RpcMethodKind.ClientStreaming, true)]
    [Arguments(RpcMethodKind.ServerStreaming, false)]
    [Arguments(RpcMethodKind.ServerStreaming, true)]
    [Arguments(RpcMethodKind.DuplexStreaming, false)]
    [Arguments(RpcMethodKind.DuplexStreaming, true)]
    public async Task EveryCallShapeShouldResolveCompleteFactsOnce(RpcMethodKind kind, bool intercepted)
    {
        var stub = new CompleteFactsStub(kind);
        var observer = new FactsObserver();
        var output = new Pipe();
        await using var harness = new ServerDispatchHarness(stub, output.Writer, 65536,
            builder => { if (intercepted) builder.AddInterceptor(observer); });
        await harness.Dispatch(7001, kind == RpcMethodKind.OneWay
                ? ProtocolV2FrameFlags.OneWay : ProtocolV2FrameFlags.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(stub.Resolutions == 1 && stub.Invocations == 1 && stub.OriginalFactsForwarded,
            "each real dispatch branch must reuse its one cold resolution and forward the exact complete result");
        Ensure(observer.Methods.Count == (intercepted ? 1 : 0), "only configured observers receive contexts");
        foreach (var method in observer.Methods)
            Ensure(method == stub.Facts!.ToDescriptor(), "observer must retain the original complete timeout and flags");
        Ensure(harness.GlobalActiveCalls == 0 && harness.Connection.ActiveCalls == 0,
            "complete dispatch must release both request permits");
    }

    [Test]
    public async Task ExceptionProjectionShouldNotResolveMethodAgain()
    {
        var stub = new CompleteFactsStub(RpcMethodKind.Unary) { FailInvocation = true };
        var mapper = new FactsMapper();
        var output = new Pipe();
        await using var harness = new ServerDispatchHarness(stub, output.Writer, 65536,
            builder => builder.UseExceptionMapper(mapper));
        await harness.Dispatch(7002, ProtocolV2FrameFlags.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(stub.Resolutions == 1 && stub.Invocations == 1, "error mapping cannot resolve again");
        Ensure(mapper.Context?.Method == stub.Facts!.ToDescriptor(), "mapper must receive every original scalar");
        for (var index = 0; index < 20; index++)
            Ensure(mapper.Context!.Method.MethodTimeout == TimeSpan.FromTicks(123456789), "cold projection retains full timeout");
        Ensure(stub.Resolutions == 1, "observer rereads never call the stub");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task UnknownOrInvalidFactsShouldRejectBeforeInvocation(int invalidKind)
    {
        if (invalidKind == 0)
        {
            AssertColdFactsRejection(new CompleteFactsStub(RpcMethodFacts.UnknownMethod));
            // Unknown wire MethodId remains a request-time negative against a valid bound domain.
            var valid = new CompleteFactsStub(RpcMethodKind.Unary);
            var output = new Pipe();
            await using var harness = new ServerDispatchHarness(valid, output.Writer, 65536);
            await harness.Dispatch(7003, ProtocolV2FrameFlags.HasReturn, methodId: 999)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await harness.Session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            EnsureResponseFrame(read.Buffer, harness.Session.RuntimeContext.Protocol, 7003,
                SharpLinkErrorCode.Unimplemented, null);
            output.Reader.AdvanceTo(read.Buffer.End);
            Ensure(valid.Resolutions == 1 && valid.Invocations == 0,
                "unknown wire methods cannot enter a codec, service, or hot stub resolver");
            Ensure(harness.GlobalActiveCalls == 0 && harness.Connection.ActiveCalls == 0,
                "unknown method rejection retains no call permits");
            return;
        }

        RpcMethodFacts? facts = invalidKind switch
        {
            1 => null,
            2 => new RpcMethodFacts(8, 999, RpcMethodKind.Unary, 0, false, true),
            _ => (RpcMethodFacts)RuntimeHelpers.GetUninitializedObject(typeof(RpcMethodFacts))
        };
        AssertColdFactsRejection(new CompleteFactsStub(facts));
    }

    private static void AssertColdFactsRejection(CompleteFactsStub stub)
    {
        var authority = new ColdMetadataAuthority();
        var domain = authority.CreateDomain();
        var rejected = false;
        try { ServerDispatchContractFixture.PrepareFactsForRejection(domain, stub); }
        catch (ServerDeclaredMethodFactsException error)
        {
            rejected = error.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest &&
                       error.ContractId == 8 && error.MethodId == 1;
        }
        Ensure(rejected && stub.Resolutions == 1 && stub.Invocations == 0,
            "unknown/null/default/identity-invalid declared facts must reject cold publication before any request");
        Ensure(authority.Bytes == 0 && authority.Methods == 0,
            "invalid cold facts must roll back every manifest, method-domain and table reservation");
    }

    [Test]
    [Arguments(RpcMethodKind.Unary, false)]
    [Arguments(RpcMethodKind.Unary, true)]
    [Arguments(RpcMethodKind.OneWay, false)]
    [Arguments(RpcMethodKind.OneWay, true)]
    [Arguments(RpcMethodKind.ClientStreaming, false)]
    [Arguments(RpcMethodKind.ClientStreaming, true)]
    [Arguments(RpcMethodKind.ServerStreaming, false)]
    [Arguments(RpcMethodKind.ServerStreaming, true)]
    [Arguments(RpcMethodKind.DuplexStreaming, false)]
    [Arguments(RpcMethodKind.DuplexStreaming, true)]
    public async Task QueuedContinuationShouldReuseFactsAndOriginalRegistration(RpcMethodKind kind, bool remove)
    {
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stub = new CompleteFactsStub(kind) { FirstInvocation = blocker.Task };
        var replacement = new CompleteFactsStub(new RpcMethodFacts(8, 1, kind,
            kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming ? 1 : 0,
            true, kind != RpcMethodKind.OneWay,
            hasMethodTimeout: true, methodTimeout: TimeSpan.FromSeconds(99)));
        var observer = new FactsObserver();
        var output = new Pipe();
        await using var harness = new ServerDispatchHarness(stub, output.Writer, 65536, builder => builder
            .AddInterceptor(observer)
            .UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 4;
                options.MaxQueuedBytes = 65536;
                options.MaxQueueDelay = TimeSpan.FromSeconds(5);
                options.QueueOneWayCalls = true;
            }));
        var flags = kind == RpcMethodKind.OneWay ? ProtocolV2FrameFlags.OneWay : ProtocolV2FrameFlags.None;
        using var originalServices = harness.AcquireServices();
        var first = harness.Dispatch(7101, flags).AsTask();
        var second = harness.Dispatch(7102, flags).AsTask();
        try
        {
            Ensure(stub.Resolutions == 1 && stub.Invocations == 1,
                "both incoming calls reuse one cold resolution, only the holder has entered business code");
            Ensure(harness.Server.AdmissionStateKernelForTests!.QueuedCalls == 1,
                "second call actually waits in admission");
            var services = new Dictionary<long, ServiceRegistration>();
            if (!remove)
                services.Add(8, ServiceRegistration.CreateSingleton(typeof(ThrowingService), replacement, new ThrowingService(), ownsService: false));
            harness.PublishServices(services.ToFrozenDictionary());
            blocker.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(stub.Resolutions == 1 && stub.Invocations == 2 && replacement.Resolutions == (remove ? 0 : 1) && replacement.Invocations == 0,
                "resume neither resolves again nor targets the replacement");
            Ensure(stub.OriginalFactsForwarded, "queued invocation retains the exact originally resolved facts object");
            Ensure(observer.Methods.Count == 2 && observer.Methods.All(method => method == stub.Facts!.ToDescriptor()),
                "queued observer projection uses the original complete timeout and flags");
            Ensure(harness.Server.AdmissionStateKernelForTests.QueuedCalls == 0 &&
                   harness.GlobalActiveCalls == 0 && harness.Connection.ActiveCalls == 0,
                "queued owners and call permits drain");
        }
        finally
        {
            blocker.TrySetResult();
            try { await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { harness.PublishServices(originalServices.Services); }
        }
    }

    [Test]
    public async Task UntimedStaticServerStreamMustRetainItsOriginalPhysicalState()
    {
        var facts = new RpcMethodFacts(8, 1, RpcMethodKind.ServerStreaming, 0, true, true);
        var mapper = new FactsMapper();
        var stub = new CompleteFactsStub(facts) { PumpEmpty = true };
        var output = new Pipe();
        await using var harness = new ServerDispatchHarness(stub, output.Writer, 65536,
            builder => builder.UseExceptionMapper(mapper), useServerBridge: true);
        stub.CountStates = () => harness.Connection.CallCancellations.Count;
        await harness.Dispatch(7201, ProtocolV2FrameFlags.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(stub.StatesAtEntry == 1 && stub.Resolutions == 1 && stub.OriginalFactsForwarded,
            "ordinary untimed stream retains its required original physical call-state owner, not a facts-only allocation");
        Ensure(mapper.Context is null && harness.Session.IsConnected, "empty stream completes without mapper fallback");
        Ensure(harness.Connection.CallCancellations.Count == 0, "no call state remains after stream cleanup");
        EnsureFactsDispatchDrained(harness);
    }

    [Test]
    [Arguments(RpcMethodKind.ServerStreaming, false)]
    [Arguments(RpcMethodKind.ServerStreaming, true)]
    [Arguments(RpcMethodKind.DuplexStreaming, false)]
    [Arguments(RpcMethodKind.DuplexStreaming, true)]
    public async Task InlineStreamShapeShouldOwnPhysicalStateWithoutChangingCooperativePolicy(
        RpcMethodKind kind, bool cancellable)
    {
        var stub = new CompleteFactsStub(new RpcMethodFacts(8, 1, kind,
            kind == RpcMethodKind.DuplexStreaming ? 1 : 0, true, true)) { PumpEmpty = true };
        var mapper = new FactsMapper();
        var output = new Pipe();
        await using var harness = new ServerDispatchHarness(stub, output.Writer, 65536,
            builder => builder.UseExceptionMapper(mapper), useServerBridge: true);
        ServerCallCancellationLease? accepted = null;
        var physicalEntries = 0;
        harness.Server.BeforeRequestAcceptedForTesting = (connection, requestId) =>
            accepted = CaptureFactsCallState(connection, requestId);
        harness.Server.BeforeGeneratedPhysicalInvocationForTesting = lease =>
        {
            physicalEntries++;
            Ensure(lease.RequestId == 7601 && lease.Generation > 0,
                "physical bridge receives the exact live request generation");
            if (accepted is { } original)
                EnsureSameFactsCallState(original, lease);
            var hasCts = typeof(ServerCallCancellationState).GetField(
                "_invocationCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lease.State) is not null;
            Ensure(hasCts == (cancellable || kind == RpcMethodKind.DuplexStreaming),
                "only newly allocated non-cooperative server streams omit CTS; duplex retains its predecode owner");
        };
        try
        {
            await harness.Dispatch(7601, cancellable ? ProtocolV2FrameFlags.Cancellable : ProtocolV2FrameFlags.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(physicalEntries == 1 && stub.Invocations == 1 && stub.Resolutions == 1 && stub.OriginalFactsForwarded,
                "one cold facts resolution and one original physical owner reach the real generated bridge");
            Ensure((accepted is not null) == (kind == RpcMethodKind.DuplexStreaming),
                "only duplex reuses RequestAccepted predecode ownership in this inline matrix");
            Ensure(stub.LastInvocationToken.CanBeCanceled == cancellable,
                "framework ownership does not change the business cooperative-cancellation policy");
            Ensure(mapper.Context is null && harness.Session.IsConnected, "empty stream needs no exception fallback");
            EnsureFactsDispatchDrained(harness);
        }
        finally
        {
            harness.Server.BeforeRequestAcceptedForTesting = null;
            harness.Server.BeforeGeneratedPhysicalInvocationForTesting = null;
        }
    }

    [Test]
    [Arguments(RpcMethodKind.ServerStreaming, false)]
    [Arguments(RpcMethodKind.ServerStreaming, true)]
    [Arguments(RpcMethodKind.DuplexStreaming, false)]
    [Arguments(RpcMethodKind.DuplexStreaming, true)]
    public async Task QueuedStreamShapeShouldReuseAdmittedStateThroughPhysicalJoin(
        RpcMethodKind kind, bool cancellable)
    {
        var held = new HeldPhysicalFactStream();
        var stub = new CompleteFactsStub(new RpcMethodFacts(8, 1, kind,
            kind == RpcMethodKind.DuplexStreaming ? 1 : 0, true, true))
        {
            SelectPumpStream = requestId => requestId == 7701 ? held : new EmptyFactStream()
        };
        var mapper = new FactsMapper();
        var output = new Pipe();
        var harness = new ServerDispatchHarness(stub, output.Writer, 65536, builder => builder
            .UseExceptionMapper(mapper)
            .UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 4;
                options.MaxQueuedBytes = 65536;
                options.MaxQueueDelay = TimeSpan.FromSeconds(5);
            }), useServerBridge: true);
        ServerCallCancellationLease? queuedOwner = null;
        ServerCallCancellationLease? heldOwner = null;
        var physicalEntries = 0;
        harness.Server.BeforeGeneratedPhysicalInvocationForTesting = lease =>
        {
            physicalEntries++;
            if (lease.RequestId == 7701) heldOwner = lease;
            if (lease.RequestId == 7702)
            {
                var original = queuedOwner ?? throw new InvalidOperationException(
                    "Target must have a captured admission owner before resume.");
                EnsureSameFactsCallState(original, lease);
                Ensure(lease.State.InvocationToken.CanBeCanceled,
                    "admission's existing cooperative owner is reused even when the wire flag is absent");
            }
        };
        var flags = cancellable ? ProtocolV2FrameFlags.Cancellable : ProtocolV2FrameFlags.None;
        Task? first = null;
        Task? second = null;
        Exception? primary = null;
        try
        {
            first = harness.Dispatch(7701, flags).AsTask();
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = harness.Dispatch(7702, flags).AsTask();
            Ensure(harness.Server.AdmissionStateKernelForTests!.QueuedCalls == 1 && stub.Invocations == 1,
                "target actually waits behind the physical holder");
            queuedOwner = CaptureFactsCallState(harness.Connection, 7702);
            held.ReleaseMoveNext.TrySetResult();
            await held.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!first.IsCompleted && !second.IsCompleted &&
                   harness.Server.AdmissionStateKernelForTests.QueuedCalls == 1 && stub.Invocations == 1,
                "logical EOF cannot release admission before the held enumerator disposal physically joins");
            Ensure(harness.GlobalActiveCalls == 1 && harness.Connection.ActiveCalls == 1,
                "the original physical request permit remains held during disposal");
            EnsureSameFactsCallState(heldOwner ?? throw new InvalidOperationException("Missing physical holder owner."),
                CaptureFactsCallState(harness.Connection, 7701));
            EnsureSameFactsCallState(queuedOwner ?? throw new InvalidOperationException("Missing queued owner."),
                CaptureFactsCallState(harness.Connection, 7702));
            held.ReleaseDispose.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(physicalEntries == 2 && held.Disposals == 1 && stub.Invocations == 2 &&
                   stub.Resolutions == 1 && stub.OriginalFactsForwarded,
                "queued continuation reuses original facts and state after exactly one holder disposal");
            Ensure(stub.LastInvocationToken.CanBeCanceled == cancellable,
                "reusing an admitted CTS does not opt business code into cooperative cancellation");
            Ensure(mapper.Context is null && harness.Session.IsConnected, "both stream pumps complete without mapper fallback");
            Ensure(harness.Server.AdmissionStateKernelForTests.QueuedCalls == 0, "admission queue drains");
            EnsureFactsDispatchDrained(harness);
        }
        catch (Exception error) { primary = error; }
        finally
        {
            held.ReleaseMoveNext.TrySetResult();
            held.ReleaseDispose.TrySetResult();
            await ServerDispatchContractFixture.CleanupOwnedAsync(primary,
                () => new ValueTask(Task.WhenAll(first ?? Task.CompletedTask, second ?? Task.CompletedTask)
                    .WaitAsync(TimeSpan.FromSeconds(5))),
                () => { harness.Server.BeforeGeneratedPhysicalInvocationForTesting = null; return ValueTask.CompletedTask; },
                () => harness.DisposeAsync());
        }
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StreamShapeTerminalBeforeActivationShouldPreventPhysicalEntry(bool cancellable)
    {
        var stub = new CompleteFactsStub(new RpcMethodFacts(8, 1, RpcMethodKind.ServerStreaming, 0, true, true))
            { PumpEmpty = true };
        var mapper = new FactsMapper();
        var output = new Pipe();
        await using var harness = new ServerDispatchHarness(stub, output.Writer, 65536,
            builder => builder.UseExceptionMapper(mapper), useServerBridge: true);
        var activationEntries = 0;
        var physicalEntries = 0;
        harness.Server.BeforeGeneratedPhysicalInvocationForTesting = _ => physicalEntries++;
        ServerCallCancellationState.BeforeRequestActivationForTests = state =>
        {
            if (state.RequestId != 7801) return;
            activationEntries++;
            Ensure(state.InvocationToken.CanBeCanceled == cancellable, "new shape owner preserves CTS policy before activation");
            Ensure(state.TryCancel(ServerCallCancellationReason.RemoteCancel), "terminal wins before Reserved becomes Active");
        };
        try
        {
            await harness.Dispatch(7801, cancellable ? ProtocolV2FrameFlags.Cancellable : ProtocolV2FrameFlags.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(activationEntries == 1 && physicalEntries == 0 && stub.Invocations == 0 && stub.Resolutions == 1,
                "actual shape-owner terminal prevents generated stub and stream entry without hot facts resolution");
            Ensure(mapper.Context is null && harness.Session.IsConnected, "preactivation rejection is not a business exception");
            EnsureFactsDispatchDrained(harness);
        }
        finally
        {
            ServerCallCancellationState.BeforeRequestActivationForTests = null;
            harness.Server.BeforeGeneratedPhysicalInvocationForTesting = null;
        }
    }

    private static ServerCallCancellationLease CaptureFactsCallState(ServerConnectionState connection, long requestId)
    {
        Ensure(connection.CallCancellations.TryCapture(requestId,
            static (id, state) => state.CaptureLease(id), out var lease), "exact request owns a live call state");
        return lease;
    }

    private static void EnsureSameFactsCallState(ServerCallCancellationLease expected, ServerCallCancellationLease actual)
        => Ensure(ReferenceEquals(expected.State, actual.State) && expected.RequestId == actual.RequestId &&
                  expected.Generation == actual.Generation,
            "continuation must retain the exact existing state and pool generation, not replace its physical owner");

    private static void EnsureFactsDispatchDrained(ServerDispatchHarness harness)
        => Ensure(harness.Connection.CallCancellations.Count == 0 && harness.GlobalActiveCalls == 0 &&
                  harness.Connection.ActiveCalls == 0,
            "physical dispatch join releases the original state and both request permits before harness teardown");

    private sealed class HeldPhysicalFactStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseMoveNext { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disposals;
        public int Current => throw new InvalidOperationException("empty held stream has no item");
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public async ValueTask<bool> MoveNextAsync()
        {
            Entered.TrySetResult();
            await ReleaseMoveNext.Task;
            return false;
        }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals);
            DisposeEntered.TrySetResult();
            await ReleaseDispose.Task;
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SuspendedStreamFailureShouldProjectOriginalFactsAfterRegistryChange(bool remove)
    {
        var stream = new SuspendedFailingFactStream();
        var facts = new RpcMethodFacts(8, 1, RpcMethodKind.ServerStreaming, 0, true, true,
            hasMethodTimeout: true, methodTimeout: TimeSpan.FromTicks(987654321), isIdempotent: true);
        var stub = new CompleteFactsStub(facts) { PumpStream = stream };
        var replacement = new CompleteFactsStub(RpcMethodKind.ServerStreaming);
        var mapper = new FactsMapper();
        var output = new Pipe();
        await using var harness = new ServerDispatchHarness(stub, output.Writer, 65536,
            builder => builder.UseExceptionMapper(mapper), useServerBridge: true);
        // Match the current ServerStreaming client head: it always declares Cancellable.
        // The real generated bridge retains this request's original physical call-state owner.
        var dispatch = harness.Dispatch(7301, ProtocolV2FrameFlags.Cancellable).AsTask();
        try
        {
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var services = new Dictionary<long, ServiceRegistration>();
            if (!remove)
                services.Add(8, ServiceRegistration.CreateSingleton(typeof(ThrowingService), replacement, new ThrowingService(), ownsService: false));
            harness.PublishServices(services.ToFrozenDictionary());
            stream.Release.TrySetResult();
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(stub.Resolutions == 1 && replacement.Resolutions == (remove ? 0 : 1) && replacement.Invocations == 0,
                "suspended stream tail must never consult the original stub or replacement registry");
            Ensure(mapper.Context?.Method == facts.ToDescriptor(),
                "stream failure must project the original complete timeout and flags after registry removal/replacement");
            Ensure(stream.Disposals == 1 && harness.GlobalActiveCalls == 0 && harness.Connection.ActiveCalls == 0,
                "faulted stream disposes once and releases both request permits");
        }
        finally
        {
            stream.Release.TrySetResult();
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class SuspendedFailingFactStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disposals;
        public int Current => throw new InvalidOperationException("failing stream has no current item");
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public async ValueTask<bool> MoveNextAsync()
        {
            Entered.TrySetResult();
            await Release.Task;
            throw new InvalidOperationException("suspended stream failure");
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
    }

    [Test]
    [NotInParallel]
    public async Task EnabledTelemetryAndImmediateAdmissionShouldUseOneResolvedIdentity()
    {
        System.Diagnostics.Activity? observed = null;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = static source => source.Name == "SharpLink.Server",
            Sample = static (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("rpc.sharplink.request_id")?.ToString() == "7401") observed = activity;
            }
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var stub = new CompleteFactsStub(RpcMethodKind.Unary);
        var output = new Pipe();
        await using var harness = new ServerDispatchHarness(stub, output.Writer, 65536,
            builder => builder.UseAdmissionControl(options => options.Global.UseConcurrency(1)));
        await harness.Dispatch(7401, ProtocolV2FrameFlags.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(stub.Resolutions == 1 && stub.Invocations == 1, "enabled telemetry and immediate admission must share one resolver result");
        Ensure(observed?.GetTagItem("rpc.sharplink.contract_id")?.ToString() == "8" &&
               observed.GetTagItem("rpc.sharplink.method_id")?.ToString() == "1",
            "telemetry must report the original contract/method identity");
        Ensure(harness.GlobalActiveCalls == 0 && harness.Connection.ActiveCalls == 0,
            "immediate admission must release both permits");
    }

    [Test]
    public async Task AdmissionRejectionShouldResolveOnceWithoutEnteringBusinessCode()
    {
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stub = new CompleteFactsStub(RpcMethodKind.Unary) { FirstInvocation = blocker.Task };
        var output = new Pipe();
        await using var harness = new ServerDispatchHarness(stub, output.Writer, 65536,
            builder => builder.UseAdmissionControl(options => options.Global.UseConcurrency(1)));
        var holder = harness.Dispatch(7501, ProtocolV2FrameFlags.None).AsTask();
        try
        {
            await harness.Dispatch(7502, ProtocolV2FrameFlags.HasReturn).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await harness.Session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            EnsureResponseFrame(read.Buffer, harness.Session.RuntimeContext.Protocol, 7502,
                SharpLinkErrorCode.ResourceExhausted, null);
            output.Reader.AdvanceTo(read.Buffer.End);
            Ensure(stub.Resolutions == 1 && stub.Invocations == 1,
                "rejected request reuses cold facts without entering a resolver, codec or service");
        }
        finally { blocker.TrySetResult(); await holder.WaitAsync(TimeSpan.FromSeconds(5)); }
        Ensure(harness.GlobalActiveCalls == 0 && harness.Connection.ActiveCalls == 0,
            "holder and rejected request release all admission/call permits");
    }

    private sealed class FactsObserver : ISharpLinkServerInterceptor
    {
        internal ConcurrentQueue<RpcMethodDescriptor> Methods { get; } = new();
        public ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
        { Methods.Enqueue(context.Method); return next(context); }
    }

    private sealed class FactsMapper : IRpcExceptionMapper
    {
        internal SharpLinkServerInvocationContext? Context { get; private set; }
        public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
        { Context = context; return new SharpLinkException(SharpLinkErrorCode.Internal, "mapped original facts", exception); }
    }

    private sealed class CompleteFactsStub : IDeclaredServerFixtureStub
    {
        internal CompleteFactsStub(RpcMethodKind kind) : this(new RpcMethodFacts(8, 1, kind,
            kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming ? 1 : 0,
            true, kind != RpcMethodKind.OneWay, hasMethodTimeout: true,
            methodTimeout: TimeSpan.FromTicks(123456789), isIdempotent: true)) { }
        internal CompleteFactsStub(RpcMethodFacts? facts) => Facts = facts;
        internal RpcMethodFacts? Facts { get; }
        public SharpLinkGeneratedMethodDescriptor DeclaredMethod
            => ServerDispatchContractFixture.DeclareMethod(
                Facts is { IsKnown: true } facts ? facts.Kind : RpcMethodKind.Unary,
                Facts is { IsKnown: true, SupportsCancellation: true });
        internal int Resolutions;
        internal int Invocations;
        internal bool OriginalFactsForwarded = true;
        internal bool FailInvocation;
        internal Task? FirstInvocation;
        internal bool PumpEmpty;
        internal IAsyncEnumerable<int>? PumpStream;
        internal Func<long, IAsyncEnumerable<int>>? SelectPumpStream;
        internal CancellationToken LastInvocationToken;
        internal Func<int>? CountStates;
        internal int StatesAtEntry = -1;
        public long InterfaceHash => 8;
        public RpcMethodFacts ResolveMethodFacts(long methodHash)
        { Interlocked.Increment(ref Resolutions); return methodHash == 1 ? Facts! : RpcMethodFacts.UnknownMethod; }
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args) => Invoke(bridge, method, requestId);
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => Invoke(bridge, method, requestId, cancellationToken);
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => Invoke(bridge, method, requestId);
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken)
            => Invoke(bridge, method, requestId, cancellationToken);
        private ValueTask Invoke(IRpcGeneratedServerBridge bridge, RpcMethodFacts method, long requestId, CancellationToken cancellationToken = default)
        {
            LastInvocationToken = cancellationToken;
            var invocation = Interlocked.Increment(ref Invocations);
            OriginalFactsForwarded &= ReferenceEquals(Facts, method);
            StatesAtEntry = CountStates?.Invoke() ?? -1;
            if (FailInvocation) return ValueTask.FromException(new InvalidOperationException("business failure"));
            if (PumpEmpty || PumpStream is not null || SelectPumpStream is not null) return bridge.PumpOutboundStreamAsync(requestId, 0,
                SelectPumpStream?.Invoke(requestId) ?? PumpStream ?? new EmptyFactStream(),
                RpcSessionTestFixture.RuntimeContext.Codecs.GetCodec<int>(), false, method, CancellationToken.None);
            return invocation == 1 && FirstInvocation is { } first ? new ValueTask(first) : ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyFactStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        public int Current => throw new InvalidOperationException("empty stream has no current item");
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
