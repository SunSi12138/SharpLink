using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HotCost.Dynamic;
using Microsoft.Extensions.DependencyInjection;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

internal static partial class Program
{
    private static async Task<object> ExerciseNetwork(int count)
    {
        using var certificate = CreateCertificate();
        await using var services = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var contracts = typeof(IHotCostDynamicRpc).Assembly;
        var pluginPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../HotCostDynamic.Services/release/HotCostDynamic.Services.dll"));
        var oldContext = new ReplacementContext(contracts);
        var newContext = new ReplacementContext(contracts);
        var oldImplementation = oldContext.LoadFromAssemblyPath(pluginPath);
        var replacement = newContext.LoadFromAssemblyPath(pluginPath);
        var oldService = oldImplementation.GetType("HotCost.Dynamic.HotCostDynamicService", true)!;
        var newService = replacement.GetType("HotCost.Dynamic.HotCostDynamicService", true)!;
        // Both arms prepare the same independent replacement state. Only the feature publishes it.
        newService.GetProperty("ResultOffset")!.SetValue(null, 1);
        const string subject = "hot-cost-authenticated-runtime";
        var authenticationCalls = 0;
        var transport = new SocketServerTransportListener(new IPEndPoint(IPAddress.Loopback, 0), backlog: 512,
            tlsOptions: new SslServerAuthenticationOptions { ServerCertificate = certificate });
        var builder = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseServiceProvider(services).UseTransport(transport)
            .UseRuntime(options => options.FlowControl.StreamReceiveWindowBytes = 4096)
            .UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2))
            .UseAuthenticator(SharpLinkAuthenticator.CreateServer((request, _) =>
            {
                Interlocked.Increment(ref authenticationCalls);
                var actual = System.Text.Encoding.UTF8.GetString(request.Payload.Span);
                if (actual != subject) throw new InvalidOperationException("Unexpected authentication subject.");
                return ValueTask.FromResult(SharpLinkAuthenticationResult.Authenticate(new SharpLinkAuthenticationContext(actual)));
            })).RequireAuthentication();
        var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
        await using var server = builder.Build();
        Check(server.RegisterAssembly(contracts).Succeeded, "Server contract registration.");
        Check(server.RegisterAssembly(oldImplementation).Succeeded, "Server implementation registration.");
#if MAINTENANCE_FEATURE
        var domain = Guid.NewGuid();
        var participant = new SharpLinkMaintenanceParticipant(Guid.NewGuid(), Guid.NewGuid(), subject);
        var controller = server.EnableCooperativeMaintenance(domain, [participant]);
        var originalGeneration = controller.CatalogGeneration;
#endif
        await server.StartAsync();
        var clientBuilder = SharpClientBuilder.Create().UseRequestTimeout(TimeSpan.FromHours(1))
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseConnectionPool(options => { options.MinConnections = 2; options.MaxConnections = 2; })
            .UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2))
            .UseAuthenticator(SharpLinkAuthenticator.CreateClient(_ => ValueTask.FromResult<ReadOnlyMemory<byte>>(
                System.Text.Encoding.UTF8.GetBytes(subject))))
            .UseTcp(IPAddress.Loopback.ToString(), port, new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = (_, presented, _, errors) => presented is not null &&
                    (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 &&
                    CryptographicOperations.FixedTimeEquals(presented.GetCertHash(HashAlgorithmName.SHA256),
                        certificate.GetCertHash(HashAlgorithmName.SHA256))
            });
        await using var client = clientBuilder.Build();
        Check(client.RegisterAssembly(contracts).Succeeded, "Client contract registration.");
#if MAINTENANCE_FEATURE
        client.EnableCooperativeMaintenance(new(domain, participant.ParticipantId, participant.RuntimeIncarnation));
#endif
        try
        {
            await client.ConnectAsync();
            var c = (SharpLinkClient)client;
            var s = (SharpLinkServer)server;
            var registry = (ServerConnectionRegistry)typeof(SharpLinkServer).GetField("_connectionRegistry", All)!.GetValue(s)!;
            ClientConnection[] ClientConnections() => (ClientConnection[])typeof(SharpLinkClient).GetField("_readyConnections", All)!.GetValue(c)!;
            var capturedClients = ClientConnections().ToArray();
            var capturedServers = registry.SnapshotOwned();
            var clientSessions = capturedClients.Select(x => x.Session).ToArray();
            var serverSessions = capturedServers.Select(x => x.Session).ToArray();
            object Capture()
            {
                var clients = ClientConnections(); var servers = registry.SnapshotOwned();
                Check(clients.Length == 2 && servers.Length == 2, "Actual pool cardinality must remain 2.");
                Check(capturedClients.All(original => clients.Any(current => ReferenceEquals(current, original))) &&
                    capturedServers.All(original => servers.Any(current => ReferenceEquals(current, original))), "Physical connection objects changed.");
                Check(clientSessions.All(session => clients.Any(x => ReferenceEquals(x.Session, session))) &&
                    serverSessions.All(session => servers.Any(x => ReferenceEquals(x.Session, session))), "Session objects changed.");
                Check(clients.All(x => x.Session.IsConnected && x.Session.ProtocolPhase == RpcSessionProtocolPhase.Ready) &&
                    servers.All(x => x.Session.IsConnected && x.Session.ProtocolPhase == RpcSessionProtocolPhase.Ready), "Session not Ready.");
                Check(clients.All(x => HasAuthenticatedTls(x.Session)) && servers.All(x => HasAuthenticatedTls(x.Session)), "TLS transport missing.");
                Check(servers.All(x => x.AuthenticationContext?.Subject == subject) && authenticationCalls == 2, "Authenticated cohort changed.");
#if MAINTENANCE_FEATURE
                Check(clientSessions.All(x => x.MaintenanceEnrollment is not null) && serverSessions.All(x => x.MaintenanceEnrollment is not null), "Cold enrollment missing.");
                Check(clientSessions.All(x => (x.NegotiatedCapabilities & ProtocolV2Capabilities.CooperativeMaintenance) != 0) &&
                    serverSessions.All(x => (x.NegotiatedCapabilities & ProtocolV2Capabilities.CooperativeMaintenance) != 0), "Maintenance capability was not negotiated.");
#endif
                return new { ClientReady = clients.Length, ServerPhysical = servers.Length,
                    SameConnectionObjects = true, SameSessionObjects = true, AllReady = true, AuthenticatedTls = true,
                    AuthenticationCalls = authenticationCalls,
                    TlsProtocols = clientSessions.Select(x => ((ITransportSecurityInfo)typeof(RpcSession).GetField("_transport", All)!.GetValue(x)!).Protocol.ToString()).ToArray(),
                    TlsCipherSuites = clientSessions.Select(x => ((ITransportSecurityInfo)typeof(RpcSession).GetField("_transport", All)!.GetValue(x)!).CipherSuite.ToString()).ToArray(),
                    Capabilities = clientSessions.Select(x => x.NegotiatedCapabilities.ToString()).ToArray() };
            }
            var before = Capture();
            var proxy = client.Get<IHotCostDynamicRpc>();
            var samples = new List<object>();
            for (var sample = 0; sample < 7; sample++)
            {
                oldService.GetMethod("ResetStreamProbe")!.Invoke(null, null);
                var started = (TaskCompletionSource)oldService.GetProperty("StreamStarted")!.GetValue(null)!;
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                async IAsyncEnumerable<int> Items()
                {
                    yield return 0;
                    await release.Task.ConfigureAwait(false);
                    for (var i = 1; i < count; i++) yield return i;
                }
                var work = proxy.UploadNumbers(Items(), CancellationToken.None).AsTask();
                try
                {
                    await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    // Observe ownership once while the producer is deliberately held, before
                    // allocation/time capture. No observation/counter is added per stream item.
                    var active = new List<(string Session, ServerCallCancellationState State)>();
                    foreach (var connection in registry.SnapshotOwned())
                    {
                        var entries = new ServerCallCancellationState[8];
                        var copied = connection.CallCancellations.CopyEntries<ServerCallCancellationState>(entries,
                            static (_, state) => state);
                        for (var i = 0; i < copied; i++) active.Add((connection.Session.Id, entries[i]));
                    }
                    Check(active.Count == 1, "The long stream must have exactly one owning call state.");
                    var state = active[0].State;
                    Check(state.Deadline.HasValue && !state.Deadline.IsExpired(TimeProvider.System),
                        "A real finite, unexpired progress deadline is required.");
                    Check(state.Reason == ServerCallCancellationReason.None && state.InvocationToken.CanBeCanceled &&
                        !state.InvocationToken.IsCancellationRequested, "Expected a live cooperative call generation.");
                    var ownedSession = active[0].Session;
                    var deadlineTimestamp = state.Deadline.Timestamp;
                    var gcBefore = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                    var timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    release.SetResult();
                    var result = await work.ConfigureAwait(false);
                    var elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - timestamp;
                    var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                    Check(result == checked((count - 1) * count / 2), "Stream sum mismatch.");
                    var received = (int)oldService.GetProperty("LastReceivedCount")!.GetValue(null)!;
                    Check(received == count, "Stream item count mismatch.");
                    samples.Add(new { Sample = sample, Warmup = sample < 2, Items = count,
                        ItemsReleasedInsideMeasuredWindow = count - 1, Result = result, Received = received,
                        ElapsedStopwatchTicks = elapsedTicks, StopwatchFrequency = System.Diagnostics.Stopwatch.Frequency,
                        ElapsedMilliseconds = 1000d * elapsedTicks / System.Diagnostics.Stopwatch.Frequency,
                        ProcessManagedAllocatedBytes = allocatedBytes,
                        GCCollections = new[] { GC.CollectionCount(0) - gcBefore[0], GC.CollectionCount(1) - gcBefore[1], GC.CollectionCount(2) - gcBefore[2] },
                        OwnedSession = ownedSession, ActiveOwningCallStatesAtStart = active.Count,
                        FiniteUnexpiredDeadlineAtStart = true, DeadlineTimestamp = deadlineTimestamp,
                        CooperativeCancellationTokenAtStart = true });
                }
                finally { release.TrySetResult(); }
            }
            var after = Capture();
            var callbackOwner = typeof(SharpLinkServer).Assembly.GetType("SharpLink.Server.ServerCancellationCallbackWork");
            Check(callbackOwner?.GetField("s_owners", All)?.GetValue(null) is null,
                "Healthy stream progress unexpectedly created a cancellation callback owner table.");
            bool maintenanceEnabled = false;
#if MAINTENANCE_FEATURE
            maintenanceEnabled = true;
#endif
            return new { Before = before, After = after, MaintenanceEnabled = maintenanceEnabled,
                UpdatePerformed = false, HeldProxyAcrossSamples = true, RequestTimeoutSeconds = 3600,
                ConfiguredServerStreamReceiveWindowBytes = 4096, ItemsPerCall = count, Calls = samples.Count, Samples = samples,
                CancellationCallbackOwnerTableCreated = false,
                Scope = "Real TLS/authenticated client-to-server integer streams. Producer release after first server-consumed item through caller-observed RPC result. Process allocation includes framework, TLS, background work and application work; not caller-thread helper or total-lifetime allocation. All warmup and measured samples retained." };
        }
        finally
        {
            await client.StopAsync();
            await server.StopAsync(TimeSpan.Zero);
            oldContext.Unload(); newContext.Unload();
        }
    }

    private static bool HasAuthenticatedTls(RpcSession session)
        => typeof(RpcSession).GetField("_transport", All)!.GetValue(session) is ITransportSecurityInfo security && security.Protocol != System.Security.Authentication.SslProtocols.None;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class ReplacementContext(Assembly contracts) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
            => AssemblyName.ReferenceMatchesDefinition(name, contracts.GetName()) ? contracts :
                Default.Assemblies.FirstOrDefault(candidate => AssemblyName.ReferenceMatchesDefinition(name, candidate.GetName()));
    }
}
