using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;

internal static partial class CoordinationSmoke
{
    internal static async Task RunIndependentEndpointsAsync(string transport, string topology, string pluginDirectory)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var timeout = TimeSpan.FromSeconds(5);
        ReadOnlyMemory<byte> proof = "local-version-endpoints"u8.ToArray();
        await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();

        (SharpLinkServerBuilder Builder, SharpLinkEndpoint Endpoint) Listen(string id, EndpointCallProbe probe)
        {
            var builder = SharpLinkServerBuilder.Create().UseServiceProvider(provider)
                .UseAuthenticator(SharpLinkAuthenticator.CreateServer((request, _) => ValueTask.FromResult(
                    request.Payload.Span.SequenceEqual(proof.Span)
                        ? SharpLinkAuthenticationResult.Authenticate(new SharpLinkAuthenticationContext("version-endpoints"))
                        : SharpLinkAuthenticationResult.Reject())))
                .RequireAuthentication().AddInterceptor(probe);
            SharpLinkTransportAddress address;
            if (transport == "tcp")
            {
                builder.UseTcp(0, IPAddress.Loopback);
                address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port);
            }
            else
            {
                var memory = $"sl-endpoint-{Guid.NewGuid():N}";
                builder.UseSharedMemory(memory);
                address = new SharpLinkSharedMemoryAddress(memory);
            }
            return (builder, new() { Id = id, Address = address });
        }

        var targetCalls = new EndpointCallProbe();
        var untouchedCalls = new EndpointCallProbe();
        var targetSetup = Listen("target", targetCalls);
        var untouchedSetup = Listen("untouched", untouchedCalls);
        var endpoints = new[] { targetSetup.Endpoint, untouchedSetup.Endpoint };
        var clientBuilder = SharpClientBuilder.Create().UseRequestTimeout()
            .UseAuthenticator(SharpLinkAuthenticator.CreateClient(_ => ValueTask.FromResult(proof)))
            .UseEndpointSelector(new PreferredEndpoint("untouched"))
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            });
        static IClientTransportFactory Dial(SharpLinkEndpoint endpoint) => endpoint.Address switch
        {
            SharpLinkTcpAddress tcp => new SocketClientTransportFactory(new IPEndPoint(IPAddress.Parse(tcp.Host), tcp.Port)),
            SharpLinkSharedMemoryAddress shared => new SharedMemoryClientTransportFactory(shared.Name),
            _ => throw new InvalidOperationException("Unexpected endpoint transport.")
        };
        if (topology == "static")
            clientBuilder.UseEndpoints(endpoints, Dial);
        else
            clientBuilder.UseEndpointResolver(new LocalEndpointResolver(new(1, endpoints)), Dial);

        using var contracts = new PluginContext("independent-contract", pluginDirectory);
        var contractFile = Path.Combine(pluginDirectory, "SharpLink.DynamicPlugin.Contracts.dll");
        var contract = contracts.LoadFromAssemblyPath(contractFile);
        using var targetOldLoader = new PluginContext("independent-target-r1", pluginDirectory, contract);
        using var targetNewLoader = new PluginContext("independent-target-r2", pluginDirectory, contract);
        using var untouchedLoader = new PluginContext("independent-untouched", pluginDirectory, contract);
        var serviceFile = Path.Combine(pluginDirectory, "SharpLink.DynamicPlugin.Services.dll");
        var targetOld = targetOldLoader.LoadFromAssemblyPath(serviceFile);
        var targetNew = targetNewLoader.LoadFromAssemblyPath(serviceFile);
        var untouchedAssembly = untouchedLoader.LoadFromAssemblyPath(serviceFile);
        var contractType = contract.GetType("SharpLink.DynamicPlugin.IDynamicPluginService", throwOnError: true)!;
        var oldService = targetOld.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
        var newService = targetNew.GetType(oldService.FullName!, throwOnError: true)!;

        await using var target = targetSetup.Builder.Build();
        await using var untouched = untouchedSetup.Builder.Build();
        await using var client = clientBuilder.Build();
        var automatic = SharpLinkVersionCoordinationMode.Automatic;
        target.SetVersionCoordinationMode(automatic);
        untouched.SetVersionCoordinationMode(automatic);
        client.SetVersionCoordinationMode(automatic);
        RequireRegistration(target.RegisterAssembly(contract), "target contracts");
        RequireRegistration(target.RegisterAssembly(targetOld), "target R1");
        RequireRegistration(untouched.RegisterAssembly(contract), "untouched contracts");
        RequireRegistration(untouched.RegisterAssembly(untouchedAssembly), "untouched implementation");
        RequireRegistration(client.RegisterAssembly(contract), "client contracts");
        await target.StartAsync(ct);
        await untouched.StartAsync(ct);
        await client.WaitForReadinessAsync(2, ct);

        var bootstrap = client.GetVersionConnections();
        Check(bootstrap.Count == 2 && bootstrap.All(s => s.ObservedSession is null), "two cold physical selectors");
        var originalTargetSelector = bootstrap.Single(s => s.EndpointId == "target");
        var untouchedSelector = bootstrap.Single(s => s.EndpointId == "untouched");
        var targetBefore = await client.QueryPeerVersionInventoryAsync(originalTargetSelector, ct);
        var untouchedBefore = await client.QueryPeerVersionInventoryAsync(untouchedSelector, ct);
        var targetSession = targetBefore.Session!.Value;
        var untouchedSession = untouchedBefore.Session!.Value;
        Check(targetSession.ServerInstanceId != untouchedSession.ServerInstanceId &&
            targetSession.ServerRegistryEpoch != untouchedSession.ServerRegistryEpoch &&
            targetSession.PhysicalSessionId != untouchedSession.PhysicalSessionId &&
            target.GetVersionPeers().Single() == targetSession && untouched.GetVersionPeers().Single() == untouchedSession &&
            targetBefore.PublisherEpoch == targetSession.ServerRegistryEpoch &&
            untouchedBefore.PublisherEpoch == untouchedSession.ServerRegistryEpoch,
            "client observed the two actual, independent registry authorities");

        var factory = typeof(CoordinationSmoke).GetMethod(nameof(PreparePlugin), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(contractType);
        var pinned = (PluginProbe)factory.Invoke(null, [client, null])!;
        var balanced = (PluginProbe)factory.Invoke(null, [client, new RpcProxyOptions { BalancePreparedEndpoints = true }])!;
        var declaration = await client.DeclareVersionTargetAsync(pinned.Local, originalTargetSelector, ct);
        var otherDeclaration = await client.DeclareVersionTargetAsync(pinned.Local, untouchedSelector, ct);
        var contractId = declaration.LocalRegistration.ContractId;
        var r1 = targetBefore.Registrations.Single(r => r.ContractId == contractId);
        var otherR1 = untouchedBefore.Registrations.Single(r => r.ContractId == contractId);
        Check(declaration.Status == SharpLinkVersionDeclarationStatus.Observed && declaration.Session == targetSession &&
            declaration.ObservedServerRegistration == r1 && otherDeclaration.Status == SharpLinkVersionDeclarationStatus.Observed &&
            otherDeclaration.Session == untouchedSession && otherDeclaration.ObservedServerRegistration == otherR1 &&
            r1.RegistrationId != otherR1.RegistrationId && r1.AssemblyHash == otherR1.AssemblyHash,
            "same contract/hash cannot alias the endpoints' separately registered versions");

        await pinned.Prepare(timeout, ct);
        await balanced.Prepare(timeout, ct);
        await CallOnAsync(pinned, untouchedCalls, targetCalls, 1);
        var untouchedPin = untouchedCalls.Last;
        client.UpdateEndpointSelector(new PreferredEndpoint("target"));
        await CallOnAsync(balanced, targetCalls, untouchedCalls, 2);
        var oldCall = balanced.Block(ct).AsTask();
        SharpLinkVersionChange change;
        Task<SharpLinkServerVersionChangeResult> replacement;
        try
        {
            await ((Task)oldService.GetProperty("BlockStarted")!.GetValue(null)!).WaitAsync(ct);
            var targetPin = targetCalls.Last;
            var participant = new SharpLinkVersionParticipant(targetSession, SharpLinkVersionInitiator.Server);
            var join = client.JoinVersionChangeAsync(pinned.Local, participant, cancellationToken: ct).AsTask();
            replacement = target.ReplaceAssemblyCoordinatedAsync(targetOld, targetNew, contractId,
                [participant], timeout, cancellationToken: ct).AsTask();
            var result = await join.WaitAsync(ct);
            change = result.Change;
            Check(result.Resumed && change.Session == targetSession && change.Current == r1 &&
                change.Candidate.RegistrationId != r1.RegistrationId && !oldCall.IsCompleted && !replacement.IsCompleted,
                "only the chosen target resumes while its admitted R1 call is still held");
            await CallOnAsync(balanced, targetCalls, untouchedCalls, 3);
            Check(targetCalls.Last == targetPin && Count(newService, "Created") == 1 && Count(oldService, "Disposed") == 0,
                "target R2 uses its own original physical connection without restarting or rebinding the cohort");
            await CallOnAsync(pinned, untouchedCalls, targetCalls, 4);
            Check(untouchedCalls.Last == untouchedPin, "the default pin does not migrate to the target");
            await CheckUntouchedInventoryAsync();
            client.UpdateEndpointSelector(new PreferredEndpoint("untouched"));
            await CallOnAsync(balanced, untouchedCalls, targetCalls, 5);
            Check(untouchedCalls.Last == untouchedPin, "balanced cohort preserves the unchanged endpoint's original receipt");
        }
        finally
        {
            oldService.GetMethod("ReleaseBlock")!.Invoke(null, null);
        }
        Check(await oldCall.WaitAsync(ct) == 42, "target R1 stays bound through physical completion");
        var outcome = await replacement.WaitAsync(ct);
        Check(outcome.Replacement.Succeeded && outcome.Replacement.ReferencesReleased &&
              outcome.Peers.Single().ActivationSent && outcome.Peers.Single().Session == targetSession &&
              Count(oldService, "Disposed") == 1, "target publication and drain affect only the selected server");

        await target.PublishDesiredSessionAsync(new() { MaxFramePayloadBytes = 128 * 1024 },
            SharpLinkSessionRolloutMode.RollingRefresh, ct);
        SharpLinkVersionConnection? freshSelector;
        do
        {
            freshSelector = client.GetVersionConnections().SingleOrDefault(s => s.EndpointId == "target");
            if (freshSelector is { ObservedSession: null })
                break;
            await Task.Delay(20, ct);
        }
        while (true);
        await ExpectUnavailable(() => client.QueryPeerVersionInventoryAsync(originalTargetSelector, ct));
        await ExpectUnavailable(() => client.JoinVersionChangeAsync(pinned.Local,
            new(targetSession, SharpLinkVersionInitiator.Server), cancellationToken: ct));
        var newTarget = await client.QueryPeerVersionInventoryAsync(freshSelector, ct);
        var fresh = newTarget.Session!.Value;
        Check(fresh.ServerInstanceId == targetSession.ServerInstanceId &&
            fresh.ServerRegistryEpoch == targetSession.ServerRegistryEpoch &&
            fresh.PhysicalSessionId != targetSession.PhysicalSessionId && newTarget.Registrations.Contains(change.Candidate),
            "only target refreshes its physical identity; R2 registry publication persists");
        await CheckUntouchedInventoryAsync();
        client.UpdateEndpointSelector(new PreferredEndpoint("target"));
        await CallOnAsync(balanced, untouchedCalls, targetCalls, 6);
        await balanced.Prepare(timeout, ct);
        await CallOnAsync(balanced, targetCalls, untouchedCalls, 7);
        await CallOnAsync(pinned, untouchedCalls, targetCalls, 8);
        Check(untouchedCalls.Last == untouchedPin, "target refresh and explicit prepare leave the default pin unchanged");
        Console.WriteLine($"VERSION_INDEPENDENT_ENDPOINTS_PASS transport={transport} topology={topology} isolated=true refreshed=true");

        async Task CheckUntouchedInventoryAsync()
        {
            var snapshot = await client.QueryPeerVersionInventoryAsync(untouchedSelector, ct);
            Check(snapshot.Session == untouchedSession && snapshot.PublisherEpoch == untouchedBefore.PublisherEpoch &&
                snapshot.Revision == untouchedBefore.Revision && snapshot.Registrations.SequenceEqual(untouchedBefore.Registrations),
                "untouched endpoint retains its session, epoch, revision and full registration set");
        }

        async Task CallOnAsync(PluginProbe proxy, EndpointCallProbe expected, EndpointCallProbe other, int value)
        {
            var hits = expected.Count;
            var otherHits = other.Count;
            Check(await proxy.Unary(value, ct) == value + 1 && expected.Count == hits + 1 && other.Count == otherHits,
                "ordinary generated unary arrives at exactly the expected server");
        }
    }

    private sealed class PreferredEndpoint(string endpointId) : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context)
        {
            var fallback = -1;
            for (var index = 0; index < context.Count; index++)
            {
                if (((context.ExcludedMask >> index) & 1UL) != 0)
                    continue;
                if (context[index].Endpoint.Id == endpointId)
                    return index;
                if (fallback < 0)
                    fallback = index;
            }
            return fallback;
        }
    }

    private sealed class LocalEndpointResolver(SharpLinkEndpointSnapshot snapshot) : ISharpLinkEndpointResolver
    {
        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(snapshot);

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EndpointCallProbe : ISharpLinkServerInterceptor
    {
        private string? _last;
        private int _count;
        internal string? Last => Volatile.Read(ref _last);
        internal int Count => Volatile.Read(ref _count);

        public ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
        {
            Volatile.Write(ref _last, context.ConnectionId);
            Interlocked.Increment(ref _count);
            return next(context);
        }
    }
}
