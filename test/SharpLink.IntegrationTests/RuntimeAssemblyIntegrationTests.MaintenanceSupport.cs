using System.Reflection;
using System.Runtime.Loader;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    private sealed class MaintenanceHarness : IAsyncDisposable
    {
        internal readonly PluginBundle Plugin;
        private readonly bool _separateOldImplementation;
        private MaintenanceReplacementContext? _oldImplementationContext;
        internal Assembly SourceAssembly;
        private readonly X509Certificate2 _certificate = CreateMaintenanceCertificate();
        private readonly ServiceProvider _services = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        private readonly MaintenanceReplacementContext _replacementContext;
        internal readonly Guid Domain = Guid.NewGuid();
        internal readonly SharpLinkMaintenanceParticipant[] Roster =
        [
            new(Guid.NewGuid(), Guid.NewGuid(), "maintenance-a"),
            new(Guid.NewGuid(), Guid.NewGuid(), "maintenance-b")
        ];
        internal readonly List<ISharpLinkClient> Clients = [];
        internal readonly Assembly Replacement;
        internal readonly Type TargetService;
        internal ISharpLinkServer Server = null!;
        internal ISharpLinkMaintenanceController Controller = null!;
        internal int Port;
        internal MaintenanceSendGateListener? SendGate;
        internal readonly MaintenanceClock Clock = new();
        private DateTimeOffset? _authenticationExpiresAt;
        private TimeSpan? _requestTimeout;
        private Action? _duringStubPreparation;
        internal object[] Held = [];
        internal object[] HeldNotifications = [];
        internal ClientConnection[][] Captured = [];
        internal Func<ValueTask>? BeforeCleanupForTesting;

        private MaintenanceHarness(bool separateOldImplementation)
        {
            _separateOldImplementation = separateOldImplementation;
            Plugin = PluginBundle.Load($"maintenance-old-{Guid.NewGuid()}", loadService: !separateOldImplementation);
            if (separateOldImplementation)
            {
                _oldImplementationContext = new MaintenanceReplacementContext(Plugin.ContractAssembly);
                SourceAssembly = _oldImplementationContext.LoadFromAssemblyPath(
                    Path.Combine(Path.GetDirectoryName(Plugin.ContractAssembly.Location)!, "SharpLink.DynamicPlugin.Services.dll"));
                SourceAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", true)!.GetMethod("Reset")!.Invoke(null, null);
            }
            else
            {
                SourceAssembly = Plugin.ServiceAssembly;
                Plugin.ResetServiceState();
            }
            _replacementContext = new MaintenanceReplacementContext(Plugin.ContractAssembly);
            Replacement = _replacementContext.LoadFromAssemblyPath(SourceAssembly.Location);
            TargetService = Replacement.GetType("SharpLink.DynamicPlugin.DynamicPluginService", true)!;
            TargetService.GetMethod("Reset")!.Invoke(null, null);
        }

        internal static async Task<MaintenanceHarness> CreateAsync(bool expiringAuthentication = false, bool separateOldImplementation = false, Action<SharpLinkServer>? beforeOptIn = null, bool gateSends = false, TimeSpan? requestTimeout = null, Action? duringStubPreparation = null)
        {
            var harness = new MaintenanceHarness(separateOldImplementation);
            harness._requestTimeout = requestTimeout;
            harness._duringStubPreparation = duringStubPreparation;
            if (expiringAuthentication)
                harness._authenticationExpiresAt = harness.Clock.GetUtcNow().AddHours(1);
            try { await harness.StartAsync(beforeOptIn, gateSends); return harness; }
            catch (Exception startupFailure)
            {
                try { await harness.DisposeAsync(); }
                catch (Exception cleanupFailure) { throw new AggregateException(startupFailure, cleanupFailure); }
                throw;
            }
        }

        private async Task StartAsync(Action<SharpLinkServer>? beforeOptIn, bool gateSends)
        {
            IServerTransportListener transport = new SocketServerTransportListener(new IPEndPoint(IPAddress.Loopback, 0),
                backlog: 512, tlsOptions: new SslServerAuthenticationOptions { ServerCertificate = _certificate });
            if (gateSends) transport = SendGate = new MaintenanceSendGateListener(transport);
            IGeneratedManifestSource manifestSource = FixedGeneratedManifestSource.Empty;
            if (_duringStubPreparation is not null)
            {
                Ensure(SharpLinkAssemblyManifestLoader.TryLoad(Plugin.ContractAssembly, out var manifest).Succeeded,
                    "load immutable contract metadata for callback control");
                manifestSource = new FixedGeneratedManifestSource([new MaintenancePreparationManifest(manifest!, _duringStubPreparation)]);
            }
            var builder = SharpLinkServerBuilder.Create()
                .UseGeneratedManifestSource(manifestSource)
                .UseServiceProvider(_services)
                .UseTimeProvider(Clock)
                .UseTransport(transport)
                .UseAuthenticator(SharpLinkAuthenticator.CreateServer((request, _) =>
                    ValueTask.FromResult(SharpLinkAuthenticationResult.Authenticate(
                        new SharpLinkAuthenticationContext(System.Text.Encoding.UTF8.GetString(request.Payload.Span), expiresAt: _authenticationExpiresAt)))))
                .RequireAuthentication();
            Port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
            Server = builder.Build();
            if (_duringStubPreparation is null)
                Ensure(Server.RegisterAssembly(Plugin.ContractAssembly).Succeeded, "register immutable server contracts");
            Ensure(Server.RegisterAssembly(SourceAssembly).Succeeded, "register old implementation");
            beforeOptIn?.Invoke((SharpLinkServer)Server);
            Controller = Server.EnableCooperativeMaintenance(Domain, Roster);
            await Server.StartAsync();
            foreach (var participant in Roster)
            {
                var client = CreateClient(participant);
                Clients.Add(client);
                await client.ConnectAsync();
            }
            Captured = Clients.Select(CaptureMaintenanceConnections).ToArray();
            Ensure(Captured.All(static items => items.Length == 2), "two physical sessions per runtime");
            Held = Clients.Select(client => GetProxy(client, Plugin.ContractType)).ToArray();
            HeldNotifications = Clients.Select(client => GetProxy(client,
                Plugin.ContractAssembly.GetType("SharpLink.DynamicPlugin.IMaintenanceNotificationProbe", true)!)).ToArray();
        }

        internal ISharpLinkClient CreateClient(SharpLinkMaintenanceParticipant participant, bool enroll = true, bool tls = true)
        {
            var builder = SharpClientBuilder.Create().DisableRequestTimeout()
                .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .UseConnectionPool(options => { options.MinConnections = 2; options.MaxConnections = 2; })
                .UseAuthenticator(SharpLinkAuthenticator.CreateClient(_ =>
                    ValueTask.FromResult<ReadOnlyMemory<byte>>(System.Text.Encoding.UTF8.GetBytes(participant.AuthenticatedSubject))));
            if (_requestTimeout is { } timeout) builder.UseRequestTimeout(timeout);
            if (tls)
                builder.UseTcp(IPAddress.Loopback.ToString(), Port, new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    RemoteCertificateValidationCallback = (_, presented, _, errors) =>
                        presented is not null && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 &&
                        CryptographicOperations.FixedTimeEquals(presented.GetCertHash(HashAlgorithmName.SHA256),
                            _certificate.GetCertHash(HashAlgorithmName.SHA256))
                });
            else
                builder.UseTcp(IPAddress.Loopback.ToString(), Port);
            var client = builder.Build();
            Ensure(client.RegisterAssembly(Plugin.ContractAssembly).Succeeded, "register immutable client contracts");
            if (enroll)
                client.EnableCooperativeMaintenance(new(Domain, participant.ParticipantId, participant.RuntimeIncarnation));
            return client;
        }

        internal ISharpLinkMaintenanceOperation Begin()
            => Controller.BeginMaintenance(Controller.CatalogGeneration, [new(SourceAssembly, Replacement)]);

        internal async Task VerifyIdentityAndShapesAsync()
        {
            for (var index = 0; index < Clients.Count; index++)
            {
                var after = CaptureMaintenanceConnections(Clients[index]);
                Ensure(after.Length == 2 && Captured[index].All(after.Contains), "same physical connection objects and cardinality");
                Ensure(after.All(static connection => connection.Session.IsConnected), "same sessions remain connected");
                await VerifyMaintenanceShapesAsync(Held[index], Plugin.ContractType);
                var notificationContract = Plugin.ContractAssembly.GetType("SharpLink.DynamicPlugin.IMaintenanceNotificationProbe", true)!;
                await InvokeValueTaskAsync(HeldNotifications[index], notificationContract, "CompleteAsync", CancellationToken.None);
                await InvokeValueTaskAsync(HeldNotifications[index], notificationContract, "ConsumeAsync", ToMaintenanceStream([1, 2]), CancellationToken.None);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal WeakReference DetachOldImplementation()
        {
            var context = _oldImplementationContext ?? throw new InvalidOperationException("Separate implementation context required.");
            var weak = new WeakReference(context);
            SourceAssembly = null!;
            _oldImplementationContext = null;
            context.Unload();
            return weak;
        }

        public async ValueTask DisposeAsync()
        {
            List<Exception> failures = [];
            void Attempt(Action cleanup)
            {
                try { cleanup(); }
                catch (Exception failure) { failures.Add(failure); }
            }
            async ValueTask AttemptAsync(Func<ValueTask> cleanup)
            {
                try { await cleanup(); }
                catch (Exception failure) { failures.Add(failure); }
            }
            // A real cleanup failure must still fail the test, but cannot strand the other
            // clients, server or collectible contexts and contaminate subsequent fixtures.
            if (BeforeCleanupForTesting is not null)
                await AttemptAsync(BeforeCleanupForTesting);
            Attempt(() => SendGate?.Release());
            if (!_separateOldImplementation)
            {
                Attempt(Plugin.ReleaseBlock);
                Attempt(() => Plugin.InvokeServiceStatic("SharpLink.DynamicPlugin.PhysicalOwnershipProbeService", "ReleaseAll"));
            }
            else if (SourceAssembly is not null)
            {
                Attempt(() => SourceAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", true)!.GetMethod("ReleaseBlock")!.Invoke(null, null));
                Attempt(() => SourceAssembly.GetType("SharpLink.DynamicPlugin.PhysicalOwnershipProbeService", true)!.GetMethod("ReleaseAll")!.Invoke(null, null));
            }
            foreach (var client in Clients)
            {
                Attempt(() => ((SharpLinkClient)client).MaintenanceControlSendBarrierForTesting = null);
                await AttemptAsync(client.DisposeAsync);
            }
            if (Server is not null)
                await AttemptAsync(Server.DisposeAsync);
            await AttemptAsync(_services.DisposeAsync);
            Attempt(_replacementContext.Unload);
            Attempt(() => _oldImplementationContext?.Unload());
            Attempt(Plugin.Dispose);
            Attempt(_certificate.Dispose);
            if (failures.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1)
                throw new AggregateException(failures);
        }
    }

    private static async Task VerifyMaintenanceShapesAsync(object proxy, Type contract)
    {
        Ensure(await InvokeValueTaskAsync<int>(proxy, contract, "UnaryAsync", 7, CancellationToken.None) == 8, "unary");
        await InvokeValueTaskAsync(proxy, contract, "NotifyAsync", 3, CancellationToken.None);
        Ensure(await InvokeValueTaskAsync<int>(proxy, contract, "ClientStreamAsync", ToMaintenanceStream([1, 2, 3]), CancellationToken.None) == 6, "client stream");
        var serverValues = new List<int>();
        await foreach (var item in InvokeStream(proxy, contract, "ServerStreamAsync", 3, CancellationToken.None))
            serverValues.Add(item);
        Ensure(serverValues.SequenceEqual([0, 1, 2]), "server stream");
        var duplexValues = new List<int>();
        await foreach (var item in InvokeStream(proxy, contract, "DuplexAsync", ToMaintenanceStream([2, 4]), CancellationToken.None))
            duplexValues.Add(item);
        Ensure(duplexValues.SequenceEqual([4, 8]), "duplex");
    }

    private static async IAsyncEnumerable<int> ToMaintenanceStream(int[] values)
    {
        foreach (var value in values) { yield return value; await Task.Yield(); }
    }

    private static ClientConnection[] CaptureMaintenanceConnections(ISharpLinkClient client)
        => (ClientConnection[])typeof(SharpLinkClient).GetField("_readyConnections", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;

    private static async Task WaitForMaintenanceAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    private static X509Certificate2 CreateMaintenanceCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class MaintenanceClock : TimeProvider
    {
        private long _offsetTicks;
        private long _monotonicOffset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));
        public override long GetTimestamp() => base.GetTimestamp() + Interlocked.Read(ref _monotonicOffset);
        internal void Advance(TimeSpan amount) => Interlocked.Add(ref _offsetTicks, amount.Ticks);
        internal void AdvanceMonotonicWithoutRunningTimers(TimeSpan amount)
            => Interlocked.Add(ref _monotonicOffset, checked((long)(amount.TotalSeconds * TimestampFrequency)));
    }

    private sealed class MaintenanceReplacementContext(Assembly contracts) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (AssemblyName.ReferenceMatchesDefinition(name, contracts.GetName())) return contracts;
            return Default.Assemblies.FirstOrDefault(candidate => AssemblyName.ReferenceMatchesDefinition(name, candidate.GetName()));
        }
    }
}
