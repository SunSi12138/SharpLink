using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;

namespace SharpLink.UnitTests.Server;

public partial class SharpLinkServerInvocationTests
{
    [Test]
    public async Task ColdFactsFailureUnary()
    {
        await RepeatedShapeFailuresShouldReuseColdFactsForMapping(RpcMethodKind.Unary);
    }

    [Test]
    public async Task ColdFactsFailureOneWay()
    {
        await RepeatedShapeFailuresShouldReuseColdFactsForMapping(RpcMethodKind.OneWay);
    }

    [Test]
    public async Task ColdFactsFailureClientStreaming()
    {
        await RepeatedShapeFailuresShouldReuseColdFactsForMapping(RpcMethodKind.ClientStreaming);
    }

    [Test]
    public async Task ColdFactsFailureServerStreaming()
    {
        await RepeatedShapeFailuresShouldReuseColdFactsForMapping(RpcMethodKind.ServerStreaming);
    }

    [Test]
    public async Task ColdFactsFailureDuplexStreaming()
    {
        await RepeatedShapeFailuresShouldReuseColdFactsForMapping(RpcMethodKind.DuplexStreaming);
    }

    [Test]
    [NotInParallel]
    public async Task ColdFactsTelemetryUnary()
    {
        await RepeatedShapeTelemetryAndAdmissionShouldReuseColdFacts(RpcMethodKind.Unary);
    }

    [Test]
    [NotInParallel]
    public async Task ColdFactsTelemetryOneWay()
    {
        await RepeatedShapeTelemetryAndAdmissionShouldReuseColdFacts(RpcMethodKind.OneWay);
    }

    [Test]
    [NotInParallel]
    public async Task ColdFactsTelemetryClientStreaming()
    {
        await RepeatedShapeTelemetryAndAdmissionShouldReuseColdFacts(RpcMethodKind.ClientStreaming);
    }

    [Test]
    [NotInParallel]
    public async Task ColdFactsTelemetryServerStreaming()
    {
        await RepeatedShapeTelemetryAndAdmissionShouldReuseColdFacts(RpcMethodKind.ServerStreaming);
    }

    [Test]
    [NotInParallel]
    public async Task ColdFactsTelemetryDuplexStreaming()
    {
        await RepeatedShapeTelemetryAndAdmissionShouldReuseColdFacts(RpcMethodKind.DuplexStreaming);
    }

    private async Task RepeatedShapeFailuresShouldReuseColdFactsForMapping(RpcMethodKind kind)
    {
        var stub = new CompleteFactsStub(kind) { FailInvocation = true };
        var mapper = new IdentityFactsMapper();
        var output = new Pipe();
        await using var harness = new ServerDispatchHarness(stub, output.Writer, 65536,
            builder => builder.UseExceptionMapper(mapper));
        Ensure(stub.Resolutions == 1, "publication resolves the declared method exactly once before requests");
        var flags = kind == RpcMethodKind.OneWay ? ProtocolV2FrameFlags.OneWay : ProtocolV2FrameFlags.None;
        for (var index = 0; index < 3; index++)
        {
            await harness.Dispatch(7901 + index, flags).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(stub.Resolutions == 1 && stub.Invocations == index + 1 && stub.OriginalFactsForwarded,
                "each failing request forwards the original facts without a hot stub resolution");
            var context = mapper.Context ?? throw new InvalidOperationException("The mapper did not observe the failure.");
            Ensure(mapper.Calls == index + 1 && mapper.Exception is InvalidOperationException &&
                   mapper.Exception.Message == "business failure", "each business failure reaches the mapper exactly once");
            Ensure(context.RequestId == 7901 + index && ReferenceEquals(context.MethodFacts, stub.Facts) &&
                   context.Method == stub.Facts!.ToDescriptor(),
                "each failure maps the current request with every original method scalar");
            for (var read = 0; read < 20; read++)
                Ensure(context.Method == stub.Facts!.ToDescriptor(), "repeated descriptor reads retain the complete original value");
            Ensure(stub.Resolutions == 1, "repeated mapper descriptor reads cannot query the stub");
            EnsureFactsDispatchDrained(harness);
        }
    }

    private async Task RepeatedShapeTelemetryAndAdmissionShouldReuseColdFacts(RpcMethodKind kind)
    {
        var observed = new ConcurrentDictionary<long, Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "SharpLink.Server",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (long.TryParse(activity.GetTagItem("rpc.sharplink.request_id")?.ToString(), out var id) &&
                    id is >= 7951 and <= 7953)
                    observed.TryAdd(id, activity);
            }
        };
        ActivitySource.AddActivityListener(listener);
        var stub = new CompleteFactsStub(kind);
        var observer = new IdentityFactsObserver();
        var output = new Pipe();
        await using var harness = new ServerDispatchHarness(stub, output.Writer, 65536, builder => builder
            .AddInterceptor(observer)
            .UseAdmissionControl(options => options.Global.UseConcurrency(1)));
        Ensure(stub.Resolutions == 1, "publication resolves the declared method before telemetry or admission");
        var flags = kind == RpcMethodKind.OneWay ? ProtocolV2FrameFlags.OneWay : ProtocolV2FrameFlags.None;
        for (var index = 0; index < 3; index++)
        {
            var requestId = 7951 + index;
            await harness.Dispatch(requestId, flags).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(stub.Resolutions == 1 && stub.Invocations == index + 1 && stub.OriginalFactsForwarded,
                "admission, interceptor and invocation reuse the original cold facts for every request");
            Ensure(observed.TryGetValue(requestId, out var activity), "each admitted request emits a completed activity");
            var completedActivity = activity ?? throw new InvalidOperationException("Missing completed activity.");
            Ensure(completedActivity.GetTagItem("rpc.sharplink.contract_id")?.ToString() == "8" &&
                   completedActivity.GetTagItem("rpc.sharplink.method_id")?.ToString() == "1",
                "telemetry preserves original method identity");
            Ensure(observer.Contexts.Count == index + 1, "each invocation reaches its interceptor exactly once");
            foreach (var context in observer.Contexts)
                Ensure(ReferenceEquals(context.MethodFacts, stub.Facts) && context.Method == stub.Facts!.ToDescriptor(), "interceptor projection preserves kind, streams, timeout and flags");
            EnsureFactsDispatchDrained(harness);
        }
        Ensure(observed.Count == 3, "all three requests have distinct completed telemetry identities");
    }

    private sealed class IdentityFactsObserver : ISharpLinkServerInterceptor
    {
        internal ConcurrentQueue<SharpLinkServerInvocationContext> Contexts { get; } = new();
        public ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
        {
            Contexts.Enqueue(context);
            return next(context);
        }
    }

    private sealed class IdentityFactsMapper : IRpcExceptionMapper
    {
        internal int Calls;
        internal SharpLinkServerInvocationContext? Context;
        internal Exception? Exception;
        public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
        {
            Calls++;
            Context = context;
            Exception = exception;
            return new SharpLinkException(SharpLinkErrorCode.Internal, "mapped original facts", exception);
        }
    }
}
