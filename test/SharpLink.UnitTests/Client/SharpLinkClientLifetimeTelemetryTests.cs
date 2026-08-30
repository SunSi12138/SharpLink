using System.Diagnostics;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public class SharpLinkClientLifetimeTelemetryTests
{
    private const string LifetimeSourceTag = "rpc.sharplink.lifetime_source";

    [Test]
    [NotInParallel("client-lifetime-telemetry")]
    public async Task RecommendedClientTimeoutShouldTagLogicalCall()
    {
        var source = await CaptureLifetimeSourceAsync(
            builder => builder.UseRequestTimeout(),
            Method(methodId: 901));

        Ensure(source == "client_recommended_timeout", "recommended timeout lifetime source");
    }

    [Test]
    [NotInParallel("client-lifetime-telemetry")]
    public async Task CustomClientTimeoutShouldTagLogicalCall()
    {
        var source = await CaptureLifetimeSourceAsync(
            builder => builder.UseRequestTimeout(TimeSpan.FromSeconds(17)),
            Method(methodId: 902));

        Ensure(source == "client_custom_timeout", "custom timeout lifetime source");
    }

    [Test]
    [NotInParallel("client-lifetime-telemetry")]
    public async Task MethodTimeoutShouldOverrideClientLifetimeSource()
    {
        var source = await CaptureLifetimeSourceAsync(
            builder => builder.UseRequestTimeout(),
            Method(methodId: 903, timeout: TimeSpan.FromSeconds(11)));

        Ensure(source == "method_timeout", "method timeout lifetime source");
    }

    [Test]
    [NotInParallel("client-lifetime-telemetry")]
    public async Task InheritedTimeBudgetShouldTagEffectiveHardCap()
    {
        var source = await CaptureLifetimeSourceAsync(
            builder => builder.UseRequestTimeout(TimeSpan.FromSeconds(30)),
            Method(methodId: 904),
            static () =>
            {
                var provider = TimeProvider.System;
                var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), provider);
                return SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(
                    "parent",
                    null,
                    deadline,
                    provider));
            });

        Ensure(source == "inherited_time_budget", "inherited time budget lifetime source");
    }

    [Test]
    [NotInParallel("client-lifetime-telemetry")]
    public async Task DroppedLogicalActivityShouldNotTagAmbientParent()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "SharpLink.Client",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.None
        };
        ActivitySource.AddActivityListener(listener);

        using var parent = new Activity("ambient-parent").Start();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseRequestTimeout());
        await client.ConnectAsync();

        await InvokeUnaryAsync(client, transport, Method(methodId: 905));

        Ensure(parent.GetTagItem(LifetimeSourceTag) is null,
            "a sampled-out logical call must not write its lifetime source onto the ambient parent activity");
    }

    [Test]
    [NotInParallel("client-lifetime-telemetry")]
    public async Task PropagationOnlyLogicalActivityShouldNotCollectLifetimeSource()
    {
        Activity? logicalActivity = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "SharpLink.Client",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.PropagationData,
            ActivityStopped = activity =>
            {
                if (activity.DisplayName == "sharplink.rpc")
                    logicalActivity = activity;
            }
        };
        ActivitySource.AddActivityListener(listener);

        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseRequestTimeout());
        await client.ConnectAsync();

        await InvokeUnaryAsync(client, transport, Method(methodId: 906));

        var capturedActivity = logicalActivity ??
            throw new Exception("propagation-only logical activity should still be created");
        Ensure(!capturedActivity.IsAllDataRequested,
            "propagation-only logical activity should not request tag data");
        Ensure(capturedActivity.GetTagItem(LifetimeSourceTag) is null,
            "propagation-only logical activity must not collect the lifetime source tag");
    }

    private static async Task<string?> CaptureLifetimeSourceAsync(
        Action<SharpClientBuilder> configure,
        RpcMethodDescriptor method,
        Func<IDisposable>? pushParent = null)
    {
        string? lifetimeSource = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "SharpLink.Client",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.DisplayName == "sharplink.rpc" &&
                    string.Equals(
                        activity.GetTagItem("rpc.sharplink.method_id")?.ToString(),
                        method.MethodId.ToString(),
                        StringComparison.Ordinal))
                {
                    lifetimeSource = activity.GetTagItem(LifetimeSourceTag)?.ToString();
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport, configure);
        await client.ConnectAsync();
        using var parent = pushParent?.Invoke();

        await InvokeUnaryAsync(client, transport, method);
        return lifetimeSource;
    }

    private static async Task InvokeUnaryAsync(
        SharpLinkClient client,
        TestClientTransportFactory transport,
        RpcMethodDescriptor method)
    {
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var invocation = channel.InvokeUnaryAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null,
            cancellationToken: default).AsTask();
        var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)sent.Header.RequestId));
        Ensure(await invocation == 0, "telemetry test response");
    }

    private static RpcMethodDescriptor Method(int methodId, TimeSpan? timeout = null)
        => new(
            ContractId: 1,
            MethodId: methodId,
            Kind: RpcMethodKind.Unary,
            HasResponsePayload: true,
            HasClientStreams: false,
            HasMethodTimeout: timeout.HasValue,
            MethodTimeout: timeout);

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
