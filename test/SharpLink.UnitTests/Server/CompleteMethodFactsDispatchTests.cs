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
            "each real dispatch branch must resolve once and forward the exact complete result");
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
        RpcMethodFacts? facts = invalidKind switch
        {
            0 => RpcMethodFacts.UnknownMethod,
            1 => null,
            2 => new RpcMethodFacts(8, 999, RpcMethodKind.Unary, 0, false, true),
            _ => (RpcMethodFacts)RuntimeHelpers.GetUninitializedObject(typeof(RpcMethodFacts))
        };
        var stub = new CompleteFactsStub(facts);
        var output = new Pipe();
        await using var harness = new ServerDispatchHarness(stub, output.Writer, 65536);
        await harness.Dispatch(7003, ProtocolV2FrameFlags.HasReturn).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        EnsureResponseFrame(read.Buffer, harness.Session.RuntimeContext.Protocol, 7003,
            invalidKind == 0 ? SharpLinkErrorCode.Unimplemented : SharpLinkErrorCode.Internal, null);
        output.Reader.AdvanceTo(read.Buffer.End);
        Ensure(stub.Resolutions == 1 && stub.Invocations == 0,
            "unknown/default/identity-invalid facts cannot enter a codec or service");
        Ensure(harness.GlobalActiveCalls == 0 && harness.Connection.ActiveCalls == 0, "rejection retains no call permits");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task QueuedContinuationShouldReuseFactsAndOriginalRegistration(bool oneWay)
    {
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var kind = oneWay ? RpcMethodKind.OneWay : RpcMethodKind.Unary;
        var stub = new CompleteFactsStub(kind) { FirstInvocation = blocker.Task };
        var replacement = new CompleteFactsStub(new RpcMethodFacts(8, 1, kind, 0, true, false,
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
        var flags = oneWay ? ProtocolV2FrameFlags.OneWay : ProtocolV2FrameFlags.None;
        var first = harness.Dispatch(7101, flags).AsTask();
        var second = harness.Dispatch(7102, flags).AsTask();
        try
        {
            Ensure(stub.Resolutions == 2 && stub.Invocations == 1,
                "both incoming calls resolve once, only the holder has entered business code");
            Ensure(harness.Server.AdmissionStateKernelForTests!.QueuedCalls == 1,
                "second call actually waits in admission");
            var registry = (ServerServiceModuleRegistry)typeof(SharpLinkServer).GetField(
                "_serviceModuleRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.Server)!;
            Volatile.Write(ref registry.ServicesStorage, new Dictionary<long, ServiceRegistration>
            {
                [8] = ServiceRegistration.CreateSingleton(typeof(object), replacement, new object(), ownsService: false)
            }.ToFrozenDictionary());
            blocker.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(stub.Resolutions == 2 && stub.Invocations == 2 && replacement.Resolutions == 0 && replacement.Invocations == 0,
                "resume neither resolves again nor targets the replacement");
            Ensure(observer.Methods.Count == 2 && observer.Methods.All(method => method == stub.Facts!.ToDescriptor()),
                "queued observer projection uses the original complete timeout and flags");
            Ensure(harness.Server.AdmissionStateKernelForTests.QueuedCalls == 0 &&
                   harness.GlobalActiveCalls == 0 && harness.Connection.ActiveCalls == 0,
                "queued owners and call permits drain");
        }
        finally
        {
            blocker.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task UntimedStaticServerStreamMustNotRentStateOnlyForFacts()
    {
        var facts = new RpcMethodFacts(8, 1, RpcMethodKind.ServerStreaming, 0, true, true);
        var mapper = new FactsMapper();
        var stub = new CompleteFactsStub(facts) { PumpEmpty = true };
        var output = new Pipe();
        await using var harness = new ServerDispatchHarness(stub, output.Writer, 65536,
            builder => builder.UseExceptionMapper(mapper), useServerBridge: true);
        stub.CountStates = () => harness.Connection.CallCancellations.Count;
        await harness.Dispatch(7201, ProtocolV2FrameFlags.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(stub.StatesAtEntry == 0 && stub.Resolutions == 1 && stub.OriginalFactsForwarded,
            "ordinary untimed stream reaches its original generated bridge without a facts-only call state");
        Ensure(mapper.Context is null && harness.Session.IsConnected, "empty stream completes without mapper fallback");
        Ensure(harness.Connection.CallCancellations.Count == 0, "no call state remains after stream cleanup");
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
        var dispatch = harness.Dispatch(7301, ProtocolV2FrameFlags.None).AsTask();
        try
        {
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var registry = (ServerServiceModuleRegistry)typeof(SharpLinkServer).GetField(
                "_serviceModuleRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.Server)!;
            var services = new Dictionary<long, ServiceRegistration>();
            if (!remove)
                services.Add(8, ServiceRegistration.CreateSingleton(typeof(object), replacement, new object(), ownsService: false));
            Volatile.Write(ref registry.ServicesStorage, services.ToFrozenDictionary());
            stream.Release.TrySetResult();
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(stub.Resolutions == 1 && replacement.Resolutions == 0 && replacement.Invocations == 0,
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
            Ensure(stub.Resolutions == 2 && stub.Invocations == 1,
                "rejected request resolves once but cannot enter business decode/invocation");
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

    private sealed class CompleteFactsStub : IRpcStub
    {
        internal CompleteFactsStub(RpcMethodKind kind) : this(new RpcMethodFacts(8, 1, kind,
            kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming ? 1 : 0,
            true, kind != RpcMethodKind.OneWay, hasMethodTimeout: true,
            methodTimeout: TimeSpan.FromTicks(123456789), isIdempotent: true)) { }
        internal CompleteFactsStub(RpcMethodFacts? facts) => Facts = facts;
        internal RpcMethodFacts? Facts { get; }
        internal int Resolutions;
        internal int Invocations;
        internal bool OriginalFactsForwarded = true;
        internal bool FailInvocation;
        internal Task? FirstInvocation;
        internal bool PumpEmpty;
        internal IAsyncEnumerable<int>? PumpStream;
        internal Func<int>? CountStates;
        internal int StatesAtEntry = -1;
        public long InterfaceHash => 8;
        public RpcMethodFacts ResolveMethodFacts(long methodHash)
        { Interlocked.Increment(ref Resolutions); return methodHash == 1 ? Facts! : RpcMethodFacts.UnknownMethod; }
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args) => Invoke(bridge, method, requestId);
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => Invoke(bridge, method, requestId);
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => Invoke(bridge, method, requestId);
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken)
            => Invoke(bridge, method, requestId);
        private ValueTask Invoke(IRpcGeneratedServerBridge bridge, RpcMethodFacts method, long requestId)
        {
            var invocation = Interlocked.Increment(ref Invocations);
            OriginalFactsForwarded &= ReferenceEquals(Facts, method);
            StatesAtEntry = CountStates?.Invoke() ?? -1;
            if (FailInvocation) return ValueTask.FromException(new InvalidOperationException("business failure"));
            if (PumpEmpty || PumpStream is not null) return bridge.PumpOutboundStreamAsync(requestId, 0, PumpStream ?? new EmptyFactStream(),
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
