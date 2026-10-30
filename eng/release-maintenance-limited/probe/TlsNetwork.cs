using System.Diagnostics;
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
    private static async Task<object> ExerciseNetwork(string state, Action<object> recordPartial)
    {
        var maintenanceEnabled = state != "off";
        var postUpdate = state == "post-update";
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
        var domain = Guid.NewGuid();
        var participant = new SharpLinkMaintenanceParticipant(Guid.NewGuid(), Guid.NewGuid(), subject);
        var controller = maintenanceEnabled ? server.EnableCooperativeMaintenance(domain, [participant]) : null;
        var originalGeneration = ((SharpLinkServer)server).MaintenanceCatalogGeneration;
        await server.StartAsync();
        var clientBuilder = SharpClientBuilder.Create().DisableRequestTimeout()
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
        if (maintenanceEnabled)
            client.EnableCooperativeMaintenance(new(domain, participant.ParticipantId, participant.RuntimeIncarnation));
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
                if (maintenanceEnabled)
                {
                Check(clientSessions.All(x => x.MaintenanceEnrollment is not null) && serverSessions.All(x => x.MaintenanceEnrollment is not null), "Cold enrollment missing.");
                Check(clientSessions.All(x => (x.NegotiatedCapabilities & ProtocolV2Capabilities.CooperativeMaintenance) != 0) &&
                    serverSessions.All(x => (x.NegotiatedCapabilities & ProtocolV2Capabilities.CooperativeMaintenance) != 0), "Maintenance capability was not negotiated.");
                }
                else
                {
                    Check(clientSessions.All(x => x.MaintenanceEnrollment is null) && serverSessions.All(x => x.MaintenanceEnrollment is null), "Disabled enrollment present.");
                    Check(clientSessions.All(x => (x.NegotiatedCapabilities & ProtocolV2Capabilities.CooperativeMaintenance) == 0) &&
                        serverSessions.All(x => (x.NegotiatedCapabilities & ProtocolV2Capabilities.CooperativeMaintenance) == 0), "Disabled capability negotiated.");
                }
                return new
                {
                    CatalogGeneration = s.MaintenanceCatalogGeneration,
                    MaintenanceConfigured = s.MaintenanceConfigured,
                    EnrollmentPresent = clientSessions.All(x => x.MaintenanceEnrollment is not null) && serverSessions.All(x => x.MaintenanceEnrollment is not null),
                    EnrollmentAbsent = clientSessions.All(x => x.MaintenanceEnrollment is null) && serverSessions.All(x => x.MaintenanceEnrollment is null),
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
            // The same held dynamic proxy is used before replacement, warmup and all rounds.
            Check(await proxy.Add(10, 20) == 30, "Initial implementation witness failed.");
            var preUpdate = Capture();
            string updateStage = "NotRequested";
            long updateElapsedTicks = 0, updateAllocatedBytes = 0;
            long updateStartedTicks = 0, updateEndedTicks = 0, updateAllocatedBefore = 0, updateAllocatedAfter = 0;
            var updateCount = 0;
            if (postUpdate)
            {
                updateAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                updateStartedTicks = Stopwatch.GetTimestamp();
                var operation = controller!.BeginMaintenance(originalGeneration, [new(oldImplementation, replacement)]);
                var result = await operation.WaitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                updateEndedTicks = Stopwatch.GetTimestamp();
                updateAllocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
                updateElapsedTicks = updateEndedTicks - updateStartedTicks;
                updateAllocatedBytes = updateAllocatedAfter - updateAllocatedBefore;
                updateStage = result.Stage.ToString();
                Check(result.Stage == SharpLinkMaintenanceStage.Succeeded, "Maintenance failed: " + result.Failure);
                updateCount++;
            }
            var steadyGeneration = s.MaintenanceCatalogGeneration;
            Check(postUpdate ? steadyGeneration > originalGeneration : steadyGeneration == originalGeneration,
                "Unexpected catalog generation transition.");
            recordPartial(new { Kind = "update", State = state, Update = new
            {
                OriginalGeneration = originalGeneration, SteadyGeneration = steadyGeneration,
                UpdateCount = updateCount, UpdateStage = updateStage,
                UpdateStartedTicks = updateStartedTicks, UpdateEndedTicks = updateEndedTicks,
                UpdateElapsedTicks = updateElapsedTicks, UpdateAllocatedBefore = updateAllocatedBefore,
                UpdateAllocatedAfter = updateAllocatedAfter, UpdateAllocatedBytes = updateAllocatedBytes,
                Frequency = Stopwatch.Frequency
            }});
            var resumed = Capture();
            var expectedAfter = postUpdate ? 31 : 30;
            Check(await proxy.Add(10, 20) == expectedAfter, "Held proxy implementation witness failed.");
            // Setup, witnesses, update, warmup, round boundaries and captures are outside timing.
            for (var i = 0; i < WarmupCalls; i++) Check(await proxy.Add(10, 20) == expectedAfter, "Warmup result mismatch.");
            var rounds = new List<object>();
            for (var round = 0; round < RoundCount; round++)
            {
                var roundBefore = Capture();
                Check(s.MaintenanceCatalogGeneration == steadyGeneration, "Generation changed before round.");
                var ticks = new long[CallsPerRound];
                var allocated = GC.GetTotalAllocatedBytes(precise: true);
                var started = Stopwatch.GetTimestamp();
                for (var i = 0; i < CallsPerRound; i++)
                {
                    var callStarted = Stopwatch.GetTimestamp();
                    var actual = await proxy.Add(10, 20);
                    ticks[i] = Stopwatch.GetTimestamp() - callStarted;
                    Check(actual == expectedAfter, "Measured RPC result mismatch.");
                }
                var ended = Stopwatch.GetTimestamp();
                var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
                var elapsed = ended - started;
                var allocatedBytes = allocatedAfter - allocated;
                var measurement = new { Round = round, Calls = CallsPerRound, ElapsedTicks = elapsed,
                    Frequency = Stopwatch.Frequency, StartedTicks = started, EndedTicks = ended,
                    AllocatedBefore = allocated, AllocatedAfter = allocatedAfter, AllocatedBytes = allocatedBytes, LatencyTicks = ticks };
                // Flush raw completed-round values before later captures can fail. This is not a success receipt.
                recordPartial(new { Kind = "round", State = state, Measurement = measurement });
                var roundAfter = Capture();
                Check(s.MaintenanceCatalogGeneration == steadyGeneration, "Generation changed during round.");
                rounds.Add(new { Before = roundBefore, After = roundAfter, measurement.Round, measurement.Calls,
                    measurement.ElapsedTicks, measurement.Frequency, measurement.StartedTicks, measurement.EndedTicks,
                    measurement.AllocatedBefore, measurement.AllocatedAfter, measurement.AllocatedBytes, measurement.LatencyTicks });
            }
            var after = Capture();
            var oldCreated = (int)oldService.GetProperty("Created")!.GetValue(null)!;
            var oldDisposed = (int)oldService.GetProperty("Disposed")!.GetValue(null)!;
            var newCreated = (int)newService.GetProperty("Created")!.GetValue(null)!;
            Check(oldCreated > 0, "Old implementation was not used.");
            if (postUpdate)
                Check(oldDisposed > 0 && newCreated > 0, "Held proxy did not cross compatible implementation replacement.");
            else
                Check(oldDisposed == 0 && newCreated == 0, "Non-update arm unexpectedly replaced/disposed implementation.");
            return new
            {
                Before = before,
                PreUpdate = preUpdate,
                Resumed = resumed,
                After = after,
                LoadedAssemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                    .Select(a => new { Name = a.GetName().Name, Path = a.Location,
                        Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(a.Location))),
                        Collectible = AssemblyLoadContext.GetLoadContext(a)!.IsCollectible }).ToArray(),
                State = state,
                Rounds = rounds,
                WarmupCalls,
                CallsPerRound,
                RoundCount,
                MeasuredRpcCalls = RoundCount * CallsPerRound,
                OriginalGeneration = originalGeneration,
                SteadyGeneration = steadyGeneration,
                UpdateCount = updateCount,
                UpdateElapsedTicks = updateElapsedTicks,
                UpdateAllocatedBytes = updateAllocatedBytes,
                UpdateStartedTicks = updateStartedTicks,
                UpdateEndedTicks = updateEndedTicks,
                UpdateAllocatedBefore = updateAllocatedBefore,
                UpdateAllocatedAfter = updateAllocatedAfter,
                Frequency = Stopwatch.Frequency,
                BeforeResult = 30,
                Result = expectedAfter,
                MaintenanceEnabled = maintenanceEnabled,
                UpdateStage = updateStage,
                GenerationAdvanced = steadyGeneration > originalGeneration,
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
