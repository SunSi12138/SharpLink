using System.Reflection;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

/// <summary>
/// Locks the allocation boundary of <see cref="SharpLinkClient.ClientLogicalCallState"/>: only call
/// shapes with more than one participant may observe the shared deadline claim, because only those
/// participants need the object at all. The decision must also come from the interceptor generation
/// captured by the same entry point that later executes the pipeline.
/// </summary>
public sealed class SharpLinkClientLogicalCallAllocationTests
{
    [Test]
    public async Task OnlyMultiParticipantCallShapesShouldAllocateASharedLogicalCall()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport, builder => builder.UseTimeProvider(timeProvider));
        var interceptors = CaptureInterceptorGeneration(client);

        var unary = Resolve(client, RpcMethodKind.Unary, hasClientStreams: false, interceptors);
        Ensure(unary.LogicalCall is null,
            "a plain timed Unary is single-participant and must not allocate a shared logical call");
        Ensure(unary.RetryGeneration is not null,
            "an idempotent Unary must still carry its captured retry generation on the control");

        var oneWay = Resolve(client, RpcMethodKind.OneWay, hasClientStreams: false, interceptors);
        Ensure(oneWay.LogicalCall is null,
            "a plain timed OneWay is single-participant and must not allocate a shared logical call");

        var streamedOneWay = Resolve(client, RpcMethodKind.OneWay, hasClientStreams: true, interceptors);
        Ensure(streamedOneWay.LogicalCall is not null,
            "a OneWay client-stream producer shares its deadline claim with the invoker");

        foreach (var kind in new[]
                 {
                     RpcMethodKind.ClientStreaming,
                     RpcMethodKind.ServerStreaming,
                     RpcMethodKind.DuplexStreaming
                 })
        {
            var control = Resolve(
                client, kind, hasClientStreams: kind is not RpcMethodKind.ServerStreaming, interceptors);
            Ensure(control.LogicalCall is not null,
                $"{kind} has more than one participant and must share one logical call");
        }
    }

    [Test]
    public async Task SharedLogicalCallAllocationShouldFollowTheCapturedInterceptorGeneration()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport, builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        // Capture the generation the way an entry point does, then publish an interceptor. The
        // allocation decision belongs to the captured generation: a live re-read here could select
        // an intercepted pipeline while the control carries no shared deadline state.
        var capturedBeforeReplacement = CaptureInterceptorGeneration(client);
        Ensure(capturedBeforeReplacement.Count == 0, "the client starts without interceptors");
        client.ReplaceInterceptors([new NoOpInterceptor()]);

        var staleControl = Resolve(
            client, RpcMethodKind.Unary, hasClientStreams: false, capturedBeforeReplacement);
        Ensure(staleControl.LogicalCall is null,
            "an interceptor published after the capture belongs to the next logical call");

        var live = CaptureInterceptorGeneration(client);
        Ensure(live.Count == 1, "the replaced generation publishes one interceptor");
        var interceptedControl = Resolve(client, RpcMethodKind.Unary, hasClientStreams: false, live);
        Ensure(interceptedControl.LogicalCall is not null,
            "a captured interceptor pipeline must share the logical deadline state");
        Ensure(interceptedControl.RetryGeneration is not null,
            "the captured retry generation must survive the interceptor path");
    }

    private static SharpLinkClient.ResolvedCallControl Resolve(
        SharpLinkClient client,
        RpcMethodKind kind,
        bool hasClientStreams,
        SharpLinkClient.ClientInterceptorGeneration interceptors)
    {
        var method = new RpcMethodDescriptor(
            ContractId: 1,
            MethodId: 2801,
            Kind: kind,
            HasResponsePayload: kind is not RpcMethodKind.OneWay,
            HasClientStreams: hasClientStreams,
            HasMethodTimeout: true,
            MethodTimeout: TimeSpan.FromSeconds(5),
            IsIdempotent: kind is RpcMethodKind.Unary);
        return client.ResolveCallControlForInvocation(
            method, metadata: null, includeClientDefault: false, interceptors);
    }

    private static SharpLinkClient.ClientInterceptorGeneration CaptureInterceptorGeneration(
        SharpLinkClient client)
        => (SharpLinkClient.ClientInterceptorGeneration)(typeof(SharpLinkClient).GetField(
                "_clientInterceptorGeneration",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(client) ?? throw new Exception("cannot find the interceptor generation field"));

    private sealed class NoOpInterceptor : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
            => next(context);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
