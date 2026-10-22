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
        var clientBuilder = SharpClientBuilder.Create().DisableRequestTimeout().UseRetry(options => options.MaxAttempts = 2)
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
                return new
                {
                    ClientReady = clients.Length,
                    ServerPhysical = servers.Length,
                    SameConnectionObjects = true,
                    SameSessionObjects = true,
                    AllReady = true,
                    AuthenticatedTls = true,
                    AuthenticationCalls = authenticationCalls,
                    TlsProtocols = clientSessions.Select(x => ((ITransportSecurityInfo)typeof(RpcSession).GetField("_transport", All)!.GetValue(x)!).Protocol.ToString()).ToArray(),
                    TlsCipherSuites = clientSessions.Select(x => ((ITransportSecurityInfo)typeof(RpcSession).GetField("_transport", All)!.GetValue(x)!).CipherSuite.ToString()).ToArray(),
                    Capabilities = clientSessions.Select(x => x.NegotiatedCapabilities.ToString()).ToArray()
                };
            }
            var before = Capture();
            var proxy = client.Get<IHotCostDynamicRpc>();
            async Task ExercisePhase(int expected)
            {
                for (var block = 0; block < 3; block++)
                {
                    for (var i = 0; i < count; i++) Check(await proxy.Add(10, 20) == expected, "RPC result mismatch.");
                    await Task.Delay(100);
                }
            }
            await ExercisePhase(30);
            var preUpdate = Capture();
            string updateStage = "NotRequestedBaseline";
            bool maintenanceEnabled = false;
            bool generationAdvanced = false;
#if MAINTENANCE_FEATURE
            maintenanceEnabled = true;
            var operation = controller.BeginMaintenance(originalGeneration, [new(oldImplementation, replacement)]);
            var result = await operation.WaitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            updateStage = result.Stage.ToString();
            Check(result.Stage == SharpLinkMaintenanceStage.Succeeded, "Maintenance failed: " + result.Failure);
            generationAdvanced = controller.CatalogGeneration > originalGeneration;
            Check(generationAdvanced, "Catalog generation did not advance.");
#endif
            await Task.Delay(100);
            var resumed = Capture();
            var expectedAfter = maintenanceEnabled ? 31 : 30;
            await ExercisePhase(expectedAfter);
            var after = Capture();
            var oldCreated = (int)oldService.GetProperty("Created")!.GetValue(null)!;
            var oldDisposed = (int)oldService.GetProperty("Disposed")!.GetValue(null)!;
            var newCreated = (int)newService.GetProperty("Created")!.GetValue(null)!;
            Check(oldCreated > 0, "Old implementation was not used.");
#if MAINTENANCE_FEATURE
            Check(oldDisposed > 0 && newCreated > 0, "Held proxy did not cross compatible implementation replacement.");
#else
            Check(newCreated == 0, "Baseline unexpectedly used replacement.");
#endif
            return new
            {
                Before = before,
                PreUpdate = preUpdate,
                Resumed = resumed,
                After = after,
                RpcCalls = 6 * count,
                CallsPerPhase = 3 * count,
                BeforeResult = 30,
                Result = expectedAfter,
                MaintenanceEnabled = maintenanceEnabled,
                UpdateStage = updateStage,
                GenerationAdvanced = generationAdvanced,
                HeldProxyAcrossPhases = true,
                OldCreated = oldCreated,
                OldDisposed = oldDisposed,
                ReplacementCreated = newCreated,
                Transport = "TLS with exact SHA256 certificate pin and hostname check",
                AuthenticatedSubject = subject
            };
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
