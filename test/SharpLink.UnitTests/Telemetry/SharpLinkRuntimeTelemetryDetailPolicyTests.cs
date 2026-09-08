using System.Diagnostics;
using System.Net;
using System.Reflection;
using SharpLink.Client;
using SharpLink.Server;
using SharpLink.UnitTests.Client;
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleSharedSupport;

namespace SharpLink.UnitTests.Telemetry;

[NotInParallel]
public sealed class SharpLinkRuntimeTelemetryDetailPolicyTests
{
    [Test]
    public async Task ClientTelemetryDetailGenerationShouldPublishAtomicallyAndSealOnStop()
    {
        var client = ClientBuilderTestHelper.Build(new NonConnectingFactory());
        var runtime = (ISharpLinkClient)client;
        try
        {
            var initial = runtime.GetTelemetryDetailPolicySnapshot();
            Ensure(initial.Generation == 0 && initial.Mode == SharpLinkTelemetryDetailMode.Detailed,
                "Client default detail mode must preserve the historical telemetry surface");

            runtime.UpdateTelemetryDetailPolicy(SharpLinkTelemetryDetailMode.Basic);
            var basic = runtime.GetTelemetryDetailPolicySnapshot();
            Ensure(basic.Generation == 1 && basic.Mode == SharpLinkTelemetryDetailMode.Basic,
                "Client Basic detail generation");

            runtime.UpdateTelemetryDetailPolicy(SharpLinkTelemetryDetailMode.Basic);
            Ensure(runtime.GetTelemetryDetailPolicySnapshot() == basic,
                "publishing the same Client detail mode must be a no-op");

            EnsureThrows<ArgumentOutOfRangeException>(() =>
                runtime.UpdateTelemetryDetailPolicy((SharpLinkTelemetryDetailMode)255));
            Ensure(runtime.GetTelemetryDetailPolicySnapshot() == basic,
                "invalid Client detail candidates must not publish");

            await client.StopAsync();
            EnsureThrows<InvalidOperationException>(() =>
                runtime.UpdateTelemetryDetailPolicy(SharpLinkTelemetryDetailMode.Detailed));
            Ensure(runtime.GetTelemetryDetailPolicySnapshot() == basic,
                "Client Stop must seal telemetry-detail publication");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task ServerTelemetryDetailShouldRemoveOnlyRequestIdentityInBasicMode()
    {
        var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .Build();
        var runtime = (ISharpLinkServer)server;
        var method = new RpcMethodDescriptor(
            11,
            22,
            RpcMethodKind.Unary,
            HasResponsePayload: true,
            HasClientStreams: false,
            HasMethodTimeout: false,
            MethodTimeout: null);
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "SharpLink.Server",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        try
        {
            var initial = runtime.GetTelemetryDetailPolicySnapshot();
            Ensure(initial.Generation == 0 && initial.Mode == SharpLinkTelemetryDetailMode.Detailed,
                "Server default detail mode must preserve the historical telemetry surface");

            var detailedTags = CaptureServerCallTags(server, method, requestId: 123);
            Ensure(detailedTags.TryGetValue("rpc.sharplink.request_id", out var requestId) && requestId == "123",
                "Detailed Server telemetry must retain request identity");
            EnsureCoreRpcTags(detailedTags, "Detailed");

            runtime.UpdateTelemetryDetailPolicy(SharpLinkTelemetryDetailMode.Basic);
            var basic = runtime.GetTelemetryDetailPolicySnapshot();
            Ensure(basic.Generation == 1 && basic.Mode == SharpLinkTelemetryDetailMode.Basic,
                "Server Basic detail generation");

            var basicTags = CaptureServerCallTags(server, method, requestId: 456);
            Ensure(!basicTags.ContainsKey("rpc.sharplink.request_id"),
                "Basic Server telemetry must omit request identity");
            EnsureCoreRpcTags(basicTags, "Basic");

            runtime.UpdateTelemetryDetailPolicy(SharpLinkTelemetryDetailMode.Basic);
            Ensure(runtime.GetTelemetryDetailPolicySnapshot() == basic,
                "publishing the same Server detail mode must be a no-op");

            EnsureThrows<ArgumentOutOfRangeException>(() =>
                runtime.UpdateTelemetryDetailPolicy((SharpLinkTelemetryDetailMode)255));
            Ensure(runtime.GetTelemetryDetailPolicySnapshot() == basic,
                "invalid Server detail candidates must not publish");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    private static IReadOnlyDictionary<string, string?> CaptureServerCallTags(
        SharpLinkServer server,
        RpcMethodDescriptor method,
        long requestId)
    {
        var start = typeof(SharpLinkServer).GetMethod(
            "StartServerTelemetryCall",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server telemetry detail boundary");
        var boxedScope = start.Invoke(server, [method, requestId])
            ?? throw new Exception("Server telemetry detail boundary returned no scope");
        var activity = Activity.Current
            ?? throw new Exception("Server telemetry detail boundary did not start an Activity");
        var tags = activity.TagObjects.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value?.ToString());

        var complete = boxedScope.GetType().GetMethod(
            "Complete",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot complete Server telemetry test scope");
        complete.Invoke(boxedScope, [null]);
        return tags;
    }

    private static void EnsureCoreRpcTags(
        IReadOnlyDictionary<string, string?> tags,
        string mode)
    {
        Ensure(tags.TryGetValue("rpc.system", out var rpcSystem) && rpcSystem == "sharplink",
            $"{mode} Server telemetry rpc.system");
        Ensure(tags.TryGetValue("rpc.sharplink.contract_id", out var contractId) && contractId == "11",
            $"{mode} Server telemetry contract id");
        Ensure(tags.TryGetValue("rpc.sharplink.method_id", out var methodId) && methodId == "22",
            $"{mode} Server telemetry method id");
    }

    private static void EnsureThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
