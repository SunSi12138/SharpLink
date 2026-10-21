using System.Linq;
using System.Reflection;
using SharpLink.Client;
using SharpLink.Server;
using SharpLink.Sdk;
using static SharpLink.UnitTests.Runtime.ImmutableRouteTransportSupport;

namespace SharpLink.UnitTests.Runtime;

// These tests run the production RPC client/server and socket transport, but deliberately use
// handwritten prepared descriptors and emitted fixture manifests, not a new public prepared API.
public sealed class ImmutableRouteTransportTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PreparedUnaryAndOneWayShouldPreserveClientLogicalIdentityOverTcp(bool withCallbacks)
    {
        using var plugin = CreatePlugin();
        await using var fixture = await StartServerAsync(plugin);
        var observer = withCallbacks ? new IdentityObserver() : null;
        await using var client = CreateClient(fixture.Endpoint, observer);
        await client.ConnectAsync().AsTask().WaitAsync(ImmutableRouteTransportSupport.Timeout);
        Exception? wrongHash = null;
        try { _ = client.PrepareMethodForTesting(Unary, new RpcHash128(1, 2)); }
        catch (Exception exception) { wrongHash = exception; }
        Ensure(wrongHash is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition },
            "Cold preparation must reject a mismatched full RPC assembly hash.");
        var unary = client.PrepareMethodForTesting(Unary, AssemblyHash);
        var oneWay = client.PrepareMethodForTesting(OneWay, AssemblyHash);
        Ensure(unary.ContractId == ContractId && unary.WireRouteId != ContractId,
            "Prepared call must carry distinct logical and wire identities.");
        Ensure(await InvokeUnary(client, unary).AsTask().WaitAsync(ImmutableRouteTransportSupport.Timeout) == plugin.State.Marker,
            "Prepared unary must reach the actual registered service.");
        await InvokeOneWay(client, oneWay).AsTask().WaitAsync(ImmutableRouteTransportSupport.Timeout);
        await WaitUntilAsync(() => Volatile.Read(ref plugin.State.OneWayCalls) == 1);
        if (observer is not null)
        {
            Ensure(observer.Intercepted.Count == 2 && observer.Intercepted.All(id => id == ContractId),
                "Client interceptors must observe the original logical contract on both shapes.");
            Ensure(observer.Admitted.Count >= 1 && observer.Admitted.All(id => id == ContractId),
                "Endpoint policy must retain the original logical contract, not wire RouteId.");
        }
    }

    [Test]
    public async Task SameHashReplacementShouldInvalidateHeldRouteWithoutRetargeting()
    {
        using var oldPlugin = CreatePlugin();
        using var newPlugin = CreatePlugin();
        await using var fixture = await StartServerAsync(oldPlugin);
        await using var client = CreateClient(fixture.Endpoint);
        await client.ConnectAsync().AsTask().WaitAsync(ImmutableRouteTransportSupport.Timeout);
        var oldMethod = client.PrepareMethodForTesting(Unary, AssemblyHash);
        Ensure(await InvokeUnary(client, oldMethod) == oldPlugin.State.Marker, "Initial route must invoke R1.");

        var replacement = await fixture.Server.ReplaceAssemblyAsync(
            oldPlugin.Assembly, newPlugin.Assembly, ImmutableRouteTransportSupport.Timeout).AsTask().WaitAsync(ImmutableRouteTransportSupport.Timeout);
        Ensure(replacement.Succeeded, $"Same-hash replacement must publish: {replacement.Error}");
        var nextMethod = await WaitForNewRouteAsync(client, oldMethod);
        var failure = await CaptureFailureAsync(InvokeUnary(client, oldMethod).AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.Unimplemented or SharpLinkErrorCode.Unavailable },
            $"Old route must fail instead of resolving R2, got {failure}.");
        Ensure(Volatile.Read(ref newPlugin.State.Calls) == 0, "Rejected old route must never execute R2.");
        Ensure(await InvokeUnary(client, nextMethod) == newPlugin.State.Marker, "Fresh preparation must reach R2.");
        await oldPlugin.State.Disposed.Task.WaitAsync(ImmutableRouteTransportSupport.Timeout);
    }

    [Test]
    public async Task RequestSerializedBeforeReplaceButSentAfterShouldNotInvokeReplacement()
    {
        using var oldPlugin = CreatePlugin();
        using var newPlugin = CreatePlugin();
        await using var fixture = await StartServerAsync(oldPlugin);
        await using var client = CreateClient(fixture.Endpoint);
        await client.ConnectAsync().AsTask().WaitAsync(ImmutableRouteTransportSupport.Timeout);
        var oldMethod = client.PrepareMethodForTesting(Unary, AssemblyHash);
        using var codec = new BlockingEmptyCodec();
        var delayed = Task.Run(async () => await InvokeUnary(client, oldMethod, codec));
        try
        {
            await codec.Started.Task.WaitAsync(ImmutableRouteTransportSupport.Timeout);
            var replacement = await fixture.Server.ReplaceAssemblyAsync(
                oldPlugin.Assembly, newPlugin.Assembly, ImmutableRouteTransportSupport.Timeout).AsTask().WaitAsync(ImmutableRouteTransportSupport.Timeout);
            Ensure(replacement.Succeeded, $"Replacement while old writer paused: {replacement.Error}");
        }
        finally { codec.Release.Set(); }
        var failure = await CaptureFailureAsync(delayed);
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.Unimplemented or SharpLinkErrorCode.Unavailable },
            $"Delayed old request must fail, got {failure}.");
        Ensure(Volatile.Read(ref newPlugin.State.Calls) == 0, "Delayed old request executed R2.");
    }

    [Test]
    public async Task RemovedOneWayRouteShouldCloseOriginalSessionWithoutInvokingReplacement()
    {
        using var oldPlugin = CreatePlugin();
        using var newPlugin = CreatePlugin();
        await using var fixture = await StartServerAsync(oldPlugin);
        await using var client = CreateClient(fixture.Endpoint);
        await client.ConnectAsync().AsTask().WaitAsync(ImmutableRouteTransportSupport.Timeout);
        var oldMethod = client.PrepareMethodForTesting(OneWay, AssemblyHash);
        var connections = (ClientConnection[])typeof(SharpLinkClient).GetField(
            "_readyConnections", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        Ensure(connections.Length == 1, "The OneWay closure assertion requires one original session.");
        var originalSession = connections[0].Session;
        var replacement = await fixture.Server.ReplaceAssemblyAsync(
            oldPlugin.Assembly, newPlugin.Assembly, ImmutableRouteTransportSupport.Timeout).AsTask().WaitAsync(ImmutableRouteTransportSupport.Timeout);
        Ensure(replacement.Succeeded, $"Replacement must publish: {replacement.Error}");
        await InvokeOneWay(client, oldMethod).AsTask().WaitAsync(ImmutableRouteTransportSupport.Timeout);
        await WaitUntilAsync(() => !originalSession.IsConnected);
        Ensure(Volatile.Read(ref newPlugin.State.OneWayCalls) == 0,
            "An unresolvable old OneWay route must not execute the replacement.");
        // This intentionally records the existing fail-closed collateral effect: other calls on
        // this session can fail too. The experiment does not claim independent OneWay rejection.
    }

    [Test]
    public async Task QueuedOldRequestAndHeldInvocationShouldRespectReplacementAndDrain()
    {
        using var oldPlugin = CreatePlugin();
        using var newPlugin = CreatePlugin();
        oldPlugin.State.Hold = true;
        await using var fixture = await StartServerAsync(oldPlugin, admission: true);
        await using var client = CreateClient(fixture.Endpoint);
        await client.ConnectAsync().AsTask().WaitAsync(ImmutableRouteTransportSupport.Timeout);
        var oldMethod = client.PrepareMethodForTesting(Unary, AssemblyHash);
        var held = InvokeUnary(client, oldMethod).AsTask();
        Task<int>? queued = null;
        Task<SharpLinkAssemblyReplacementResult>? replacing = null;
        try
        {
            await oldPlugin.State.Entered.Task.WaitAsync(ImmutableRouteTransportSupport.Timeout);
            queued = InvokeUnary(client, oldMethod).AsTask();
            await WaitUntilAsync(() => fixture.Server.AdmissionStateKernelForTests!.QueuedCalls == 1);
            replacing = fixture.Server.ReplaceAssemblyAsync(
                oldPlugin.Assembly, newPlugin.Assembly, ImmutableRouteTransportSupport.Timeout).AsTask();
            _ = await WaitForNewRouteAsync(client, oldMethod);
            Ensure(!oldPlugin.State.Disposed.Task.IsCompleted && !replacing.IsCompleted,
                "Held production invocation lease must defer disposal and completion of drain.");
        }
        finally { oldPlugin.State.Release.TrySetResult(); }
        Ensure(await held.WaitAsync(ImmutableRouteTransportSupport.Timeout) == oldPlugin.State.Marker, "Admitted old invocation must finish on R1.");
        Ensure(replacing is not null && (await replacing.WaitAsync(ImmutableRouteTransportSupport.Timeout)).Succeeded,
            "Replacement must finish after the final old invocation releases its lease.");
        Ensure(queued is not null, "Queued request must have started.");
        var queuedFailure = await CaptureFailureAsync(queued!);
        Ensure(queuedFailure is SharpLinkException,
            $"Queued old request must fail rather than invoke R2, got {queuedFailure}.");
        Ensure(Volatile.Read(ref oldPlugin.State.Calls) == 1 && Volatile.Read(ref newPlugin.State.Calls) == 0,
            "The queued payload must not re-resolve to R2 or reopen R1 after retirement.");
        await oldPlugin.State.Disposed.Task.WaitAsync(ImmutableRouteTransportSupport.Timeout);
    }

    [Test]
    public async Task ReconnectToDifferentServerScopeShouldFailBeforeReadyPublication()
    {
        using var firstPlugin = CreatePlugin();
        using var secondPlugin = CreatePlugin();
        await using var first = await StartServerAsync(firstPlugin);
        await using var second = await StartServerAsync(secondPlugin);
        var transport = new SwitchingTransportFactory(first.Endpoint, second.Endpoint);
        await using var client = (SharpLinkClient)SharpClientBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(transport).UseRequestTimeout(ImmutableRouteTransportSupport.Timeout).Build();
        client.EnableImmutableRoutesForTesting = true;
        var rejectedScope = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ImmutableRouteScopeRejectedForTesting = scope => rejectedScope.TrySetResult(scope);
        await client.ConnectAsync().AsTask().WaitAsync(ImmutableRouteTransportSupport.Timeout);
        var heldDescriptor = client.PrepareMethodForTesting(Unary, AssemblyHash);
        Ensure(await InvokeUnary(client, heldDescriptor) == firstPlugin.State.Marker, "Initial server invocation.");

        transport.Switch();
        await first.Server.StopAsync(TimeSpan.Zero).AsTask().WaitAsync(ImmutableRouteTransportSupport.Timeout);
        _ = await rejectedScope.Task.WaitAsync(ImmutableRouteTransportSupport.Timeout);
        Ensure(client.ReadyConnectionCount == 0,
            "A different server scope must be rejected before its connection reaches the usable pool.");
        var failure = await CaptureFailureAsync(InvokeUnary(client, heldDescriptor).AsTask());
        Ensure(failure is SharpLinkException, "Old prepared route must not become callable in the new scope.");
        Ensure(Volatile.Read(ref secondPlugin.State.Calls) == 0,
            "A numerically colliding route in the other server must never execute.");
    }
}
