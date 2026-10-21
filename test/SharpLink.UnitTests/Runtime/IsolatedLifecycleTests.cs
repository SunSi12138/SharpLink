using System.Runtime.CompilerServices;
using SharpLink.Client;
using SharpLink.Server;
using SharpLink.Sdk;
using static SharpLink.UnitTests.Runtime.IsolatedLifecycleSupport;

namespace SharpLink.UnitTests.Runtime;

// Baseline production source, real TCP, handwritten direct-channel fixtures.
// These tests distinguish supported isolation from counterexamples to stronger guarantees.
public sealed class IsolatedLifecycleTests
{
    [Test]
    public async Task SeparateDomainsKeepHeldClientOnOriginalRegistration()
    {
        using var oldPlugin = CreatePlugin();
        using var newPlugin = CreatePlugin();
        await using var oldServer = await StartServerAsync(oldPlugin);
        await using var newServer = await StartServerAsync(newPlugin);
        await using var oldClient = CreateClient(oldServer.Endpoint);
        await using var newClient = CreateClient(newServer.Endpoint);
        await oldClient.ConnectAsync();
        Ensure(await InvokeUnary(oldClient, Unary) == oldPlugin.State.Marker, "old domain ready");
        await newClient.ConnectAsync();
        // Acquisition changes once at a business-session boundary, not on each RPC.
        var nextBusinessSessionClient = newClient;
        Ensure(await InvokeUnary(nextBusinessSessionClient, Unary) == newPlugin.State.Marker, "new acquisition reaches new domain");
        Ensure(await InvokeUnary(oldClient, Unary) == oldPlugin.State.Marker, "held old client remains old");
        await InvokeOneWay(oldClient, OneWay);
        await InvokeOneWay(newClient, OneWay);
        await WaitUntilAsync(() => oldPlugin.State.OneWayCalls == 1 && newPlugin.State.OneWayCalls == 1);
        Ensure(Unsafe.SizeOf<RpcMethodDescriptor>() == 32, "unchanged descriptor ABI");
    }

    [Test]
    public async Task FailedNewAuthenticationLeavesExistingDomainUsable()
    {
        using var oldPlugin = CreatePlugin();
        using var newPlugin = CreatePlugin();
        var accepted = 0;
        var expired = 0;
        var authenticator = SharpLinkAuthenticator.CreateServer((_, _) =>
        {
            if (Volatile.Read(ref expired) != 0)
                return ValueTask.FromResult(SharpLinkAuthenticationResult.Reject(SharpLinkErrorCode.AuthenticationExpired));
            Interlocked.Increment(ref accepted);
            return ValueTask.FromResult(SharpLinkAuthenticationResult.Success);
        });
        await using var oldServer = await StartServerAsync(oldPlugin, authenticator: authenticator);
        await using var newServer = await StartServerAsync(newPlugin, authenticator: authenticator);
        await using var oldClient = CreateClient(oldServer.Endpoint);
        await oldClient.ConnectAsync();
        Ensure(await InvokeUnary(oldClient, Unary) == oldPlugin.State.Marker, "old authenticated session established");
        Volatile.Write(ref expired, 1);
        await using var newClient = CreateClient(newServer.Endpoint);
        var failure = await CaptureFailureAsync(newClient.ConnectAsync().AsTask());
        Ensure(failure is SharpLinkException, $"new preparation must fail authentication: {failure}");
        Ensure(await InvokeUnary(oldClient, Unary) == oldPlugin.State.Marker, "failed new preparation did not affect established session");
        Ensure(accepted == 1 && newPlugin.State.Calls == 0, "no new-domain execution with unavailable bootstrap authentication");
    }

    [Test]
    public async Task BaselineReconnectCanRetargetHeldClientToDifferentRegistration()
    {
        using var firstPlugin = CreatePlugin();
        using var secondPlugin = CreatePlugin();
        await using var first = await StartServerAsync(firstPlugin);
        var factory = new SocketClientTransportFactory(first.Endpoint);
        await using var client = (SharpLinkClient)SharpClientBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(factory).UseRequestTimeout(IsolatedLifecycleSupport.Timeout).Build();
        await client.ConnectAsync();
        Ensure(await InvokeUnary(client, Unary) == firstPlugin.State.Marker, "initial domain");
        await first.Server.StopAsync(TimeSpan.Zero);
        await using var second = await StartServerAsync(secondPlugin, bindEndpoint: first.Endpoint);
        await WaitUntilAsync(() => client.ReadyConnectionCount > 0);
        // Counterexample: same descriptor/client can reach a same-hash other server after reconnect.
        Ensure(await InvokeUnary(client, Unary) == secondPlugin.State.Marker,
            "expected counterexample: baseline does not pin registry lifetime in handshake");
    }

    [Test]
    public async Task GracefulStopKeepsEnteredOldCallAliveWhileNewDomainServes()
    {
        using var oldPlugin = CreatePlugin();
        using var newPlugin = CreatePlugin();
        oldPlugin.State.Hold = true;
        await using var oldServer = await StartServerAsync(oldPlugin);
        await using var newServer = await StartServerAsync(newPlugin);
        await using var oldClient = CreateClient(oldServer.Endpoint);
        await using var newClient = CreateClient(newServer.Endpoint);
        await oldClient.ConnectAsync();
        await newClient.ConnectAsync();
        var held = InvokeUnary(oldClient, Unary).AsTask();
        await oldPlugin.State.Entered.Task.WaitAsync(IsolatedLifecycleSupport.Timeout);
        var stopping = oldServer.Server.StopAsync(IsolatedLifecycleSupport.Timeout).AsTask();
        try
        {
            Ensure(!oldPlugin.State.Disposed.Task.IsCompleted, "entered old service cannot be disposed yet");
            Ensure(await InvokeUnary(newClient, Unary) == newPlugin.State.Marker, "independent new domain remains live");
        }
        finally { oldPlugin.State.Release.TrySetResult(); }
        Ensure(await held.WaitAsync(IsolatedLifecycleSupport.Timeout) == oldPlugin.State.Marker, "entered old call completes normally");
        await stopping.WaitAsync(IsolatedLifecycleSupport.Timeout);
        await oldPlugin.State.Disposed.Task.WaitAsync(IsolatedLifecycleSupport.Timeout);
    }

    [Test]
    public async Task ModuleTimeoutDoesNotProveResourceRelease()
    {
        using var plugin = CreatePlugin();
        plugin.State.Hold = true;
        await using var fixture = await StartServerAsync(plugin);
        await using var client = CreateClient(fixture.Endpoint);
        await client.ConnectAsync();
        var held = InvokeUnary(client, Unary).AsTask();
        await plugin.State.Entered.Task.WaitAsync(IsolatedLifecycleSupport.Timeout);
        try
        {
            var result = await fixture.Server.UnregisterAssemblyAsync(plugin.Assembly, TimeSpan.Zero);
            Ensure(!result.ReferencesReleased && result.RemainingCalls == 1, "timeout must retain active resource ownership");
            Ensure(!plugin.State.Disposed.Task.IsCompleted, "service not prematurely disposed");
        }
        finally { plugin.State.Release.TrySetResult(); }
        Ensure(await held.WaitAsync(IsolatedLifecycleSupport.Timeout) == plugin.State.Marker, "uncancellable entered call finishes");
        await plugin.State.Disposed.Task.WaitAsync(IsolatedLifecycleSupport.Timeout);
    }
    [Test]
    public async Task StopCancelsQueuedRequestRatherThanCompletingAllReceivedWork()
    {
        using var plugin = CreatePlugin();
        plugin.State.Hold = true;
        await using var fixture = await StartServerAsync(plugin, admission: true);
        await using var client = CreateClient(fixture.Endpoint);
        await client.ConnectAsync();
        var held = InvokeUnary(client, Unary).AsTask();
        await plugin.State.Entered.Task.WaitAsync(IsolatedLifecycleSupport.Timeout);
        var queued = InvokeUnary(client, Unary).AsTask();
        await WaitUntilAsync(() => fixture.Server.AdmissionStateKernelForTests!.QueuedCalls == 1);
        var stopping = fixture.Server.StopAsync(IsolatedLifecycleSupport.Timeout).AsTask();
        try
        {
            var failure = await CaptureFailureAsync(queued);
            Ensure(failure is SharpLinkException, "received but not admitted request is rejected on shutdown");
            Ensure(plugin.State.Calls == 1, "queued request must not enter service");
        }
        finally { plugin.State.Release.TrySetResult(); }
        Ensure(await held.WaitAsync(IsolatedLifecycleSupport.Timeout) == plugin.State.Marker, "admitted call completes");
        await stopping.WaitAsync(IsolatedLifecycleSupport.Timeout);
    }

    [Test]
    public async Task CompletedStopDoesNotImplyHeldUserCodeOrDisposalCompleted()
    {
        using var plugin = CreatePlugin();
        plugin.State.Hold = true;
        await using var fixture = await StartServerAsync(plugin);
        await using var client = CreateClient(fixture.Endpoint);
        await client.ConnectAsync();
        var held = InvokeUnary(client, Unary).AsTask();
        await plugin.State.Entered.Task.WaitAsync(IsolatedLifecycleSupport.Timeout);
        try
        {
            await fixture.Server.StopAsync(TimeSpan.Zero).AsTask().WaitAsync(IsolatedLifecycleSupport.Timeout);
            Ensure(!plugin.State.Release.Task.IsCompleted && !plugin.State.Disposed.Task.IsCompleted,
                "Stop returns while noncooperative user code and deferred disposal remain");
            var failure = await CaptureFailureAsync(held);
            Ensure(failure is SharpLinkException, "client call fails after forced session closure");
        }
        finally { plugin.State.Release.TrySetResult(); }
        await plugin.State.Disposed.Task.WaitAsync(IsolatedLifecycleSupport.Timeout);
    }

    [Test]
    public async Task ExistingHandshakeAuthenticatorCanRejectWrongLifetimeDomain()
    {
        using var firstPlugin = CreatePlugin();
        using var secondPlugin = CreatePlugin();
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Synthetic nonsecret generation tags only. Production must additionally authenticate
        // credentials and server identity; a caller-supplied tag is NOT an access credential.
        var firstAuth = SharpLinkAuthenticator.CreateServer((request, _) => ValueTask.FromResult(
            request.Payload.Span.SequenceEqual(new byte[] { 1 })
                ? SharpLinkAuthenticationResult.Success : SharpLinkAuthenticationResult.Reject()));
        var secondAuth = SharpLinkAuthenticator.CreateServer((request, _) =>
        {
            if (request.Payload.Span.SequenceEqual(new byte[] { 2 }))
                return ValueTask.FromResult(SharpLinkAuthenticationResult.Success);
            rejected.TrySetResult();
            return ValueTask.FromResult(SharpLinkAuthenticationResult.Reject());
        });
        await using var first = await StartServerAsync(firstPlugin, authenticator: firstAuth);
        var factory = new SocketClientTransportFactory(first.Endpoint);
        await using var client = (SharpLinkClient)SharpClientBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(factory)
            .UseAuthenticator(SharpLinkAuthenticator.CreateClient(
                _ => ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 })))
            .UseRequestTimeout(IsolatedLifecycleSupport.Timeout).Build();
        await client.ConnectAsync();
        Ensure(await InvokeUnary(client, Unary) == firstPlugin.State.Marker, "first scoped domain is ready");
        await first.Server.StopAsync(TimeSpan.Zero);
        await using var second = await StartServerAsync(secondPlugin,
            authenticator: secondAuth, bindEndpoint: first.Endpoint);
        await rejected.Task.WaitAsync(IsolatedLifecycleSupport.Timeout);
        Ensure(client.ReadyConnectionCount == 0, "new domain rejected before Ready");
        var failure = await CaptureFailureAsync(InvokeUnary(client, Unary).AsTask());
        Ensure(failure is SharpLinkException, "held old client cannot invoke new domain");
        Ensure(secondPlugin.State.Calls == 0, "wrong-domain call never executes");
    }

}
