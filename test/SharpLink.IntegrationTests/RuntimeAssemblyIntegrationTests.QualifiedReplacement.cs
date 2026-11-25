using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    private static readonly TimeSpan s_replaceEvidenceBudget = TimeSpan.FromSeconds(5);
    private static readonly byte[] s_replaceEvidenceCredential = "replace-workload"u8.ToArray();

    [Test]
    [NotInParallel]
    public Task QualifiedReplacementPreservesAnUnrelatedServiceOnOnePhysicalSessionTcp() => RunReplacementEvidenceAsync("tcp");

    [Test]
    [NotInParallel]
    public Task QualifiedReplacementPreservesAnUnrelatedServiceOnOnePhysicalSessionSharedMemory() =>
        RunReplacementEvidenceAsync("shared-memory");

    [Test]
    [NotInParallel]
    public Task QualifiedDynamicShortStreamsUseThePreparedRouteTcp() =>
        RunReplacementEvidenceAsync("tcp", verifyDynamicStreams: true);

    [Test]
    [NotInParallel]
    public Task QualifiedDynamicShortStreamsUseThePreparedRouteSharedMemory() =>
        RunReplacementEvidenceAsync("shared-memory", verifyDynamicStreams: true);

    private static async Task RunReplacementEvidenceAsync(string transport, bool verifyDynamicStreams = false)
    {
        var interceptor = ReadReplacementInterceptor();
        var timing = ReplacementTimingOptions.FromEnvironment(interceptor);
        var dynamicTiming = verifyDynamicStreams ? DynamicStreamTimingOptions.FromEnvironment() : null;
        Ensure(!verifyDynamicStreams || timing is null, "replacement timing cannot run in the dynamic stream fixture");
        if (timing is not null || dynamicTiming is not null ||
            Environment.GetEnvironmentVariable("SHARPLINK_999_REPLACE_REQUIRE_NO_TRACING") == "true")
        {
            RequireNoReplacementTracing();
            Console.WriteLine("REPLACE_NO_RPC_TRACING");
        }
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(dynamicTiming is not null ? 130 : timing is null ? 30 : 110));
        var ct = guard.Token;
        using var contracts = PluginBundle.Load($"replace-evidence-{transport}", loadService: false);
        var path = Path.Combine(Path.GetDirectoryName(contracts.ContractAssembly.Location)!, "SharpLink.DynamicPlugin.Services.dll");
        var manifestStatus = SharpLinkAssemblyManifestLoader.TryLoad(typeof(IShutdownCleanupProbe).Assembly, out var manifest);
        var dynamicStatus = SharpLinkAssemblyManifestLoader.TryLoad(contracts.ContractAssembly, out var dynamicManifest);
        Ensure(manifestStatus.Succeeded && dynamicStatus.Succeeded, "generated local and dynamic manifests must load");
        var source = new FixedGeneratedManifestSource([manifest!]);
        var contractId = dynamicManifest!.Contracts.Single(item => item.ContractType == contracts.ContractType).ContractId;
        var siblingContractId = manifest!.Contracts.Single(item => item.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
        var siblingProbe = new QualifiedReplacementSibling();
        var interceptionProbe = interceptor == "forward" && timing is null && dynamicTiming is null
            ? new QualifiedReplacementInterceptorProbe(contractId, siblingContractId)
            : null;
        await using var services = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var serverBuilder = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(source).UseServiceProvider(services)
            .ReplaceService<IShutdownCleanupProbe>(siblingProbe)
            .UseAuthenticator(SharpLinkAuthenticator.CreateServer(static (request, _) => ValueTask.FromResult(
                request.Payload.Span.SequenceEqual(s_replaceEvidenceCredential)
                    ? SharpLinkAuthenticationResult.Authenticate(new SharpLinkAuthenticationContext("replace-evidence"))
                    : SharpLinkAuthenticationResult.Reject())))
            .RequireAuthentication()
            .UseHeartbeat(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10));
        if (interceptor == "forward")
            serverBuilder.AddInterceptor((ISharpLinkServerInterceptor?)interceptionProbe ?? QualifiedReplacementForwarder.Instance);
        var clientBuilder = SharpClientBuilder.Create().UseGeneratedManifestSource(source).UseRequestTimeout()
            .UseAuthenticator(SharpLinkAuthenticator.CreateClient(static _ =>
                ValueTask.FromResult<ReadOnlyMemory<byte>>(s_replaceEvidenceCredential)))
            .UseHeartbeat(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10));
        if (transport == "tcp")
        {
            serverBuilder.UseTcp(0, IPAddress.Loopback);
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            clientBuilder.UseTcp(IPAddress.Loopback.ToString(), port);
        }
        else
        {
            Ensure(transport == "shared-memory", "only the two registered transports are supported");
            var name = $"sl-replace-evidence-{Guid.NewGuid():N}";
            serverBuilder.UseSharedMemory(name);
            clientBuilder.UseSharedMemory(name);
        }

        var contexts = new List<SharedContractServiceLoadContext>(3);
        try
        {
            await using var server = serverBuilder.Build();
            await using var client = clientBuilder.Build();
            PrepareReplacementControl(server, client);
            var current = LoadGeneration();
            Ensure(server.RegisterAssembly(contracts.ContractAssembly).Succeeded &&
                   server.RegisterAssembly(current.Assembly).Succeeded &&
                   client.RegisterAssembly(contracts.ContractAssembly).Succeeded,
                "real separate contract and implementation assemblies must register");
            await server.StartAsync(ct);
            await client.ConnectAsync(ct);
            var (a, b) = await OpenReplacementServicesAsync(client, contracts, ct);
            var unary = contracts.ContractType.GetMethod("UnaryAsync")!
                .CreateDelegate<Func<int, CancellationToken, ValueTask<int>>>(a);
            Ensure(await unary(1, ct) == 2 && await b.TouchAsync(1, ct) == 101 &&
                   GenerationCounter(current.ServiceType, "Created") == 1 && siblingProbe.SessionId is not null,
                "baseline A and B serve before replacing the implementation");
            var originalSession = siblingProbe.SessionId;
            if (verifyDynamicStreams)
            {
                var streams = new QualifiedDynamicStreams(a, contracts.ContractType);
                if (dynamicTiming is null)
                    await VerifyQualifiedDynamicStreamsAsync(streams, current.ServiceType, transport, interceptor, ct);
                else
                    await MeasureDynamicStreamsAsync(dynamicTiming, streams, current.ServiceType, transport, interceptor, ct);
                Ensure(await b.TouchAsync(2, ct) == 102 && siblingProbe.SessionId == originalSession &&
                       (interceptionProbe is null || interceptionProbe.Visits == (7, 2)),
                    "the real dynamic streams and the sibling probe use only the original physical Session");
                return;
            }
            if (timing is not null)
            {
                var measurement = MeasureReplacementAsync(timing, transport, unary, b, current, LoadGeneration,
                    ReleaseGeneration, (old, next, token) => ReplaceEvidenceAsync(server, client, contracts, contractId, old, next, token), ct);
                current = null!; // Ownership moved into the A worker; do not root the first retired generation here.
                await measurement;
                Ensure(siblingProbe.SessionId == originalSession, "B kept the original physical Session through both phases");
                return;
            }

            var block = contracts.ContractType.GetMethod("BlockAsync")!
                .CreateDelegate<Func<CancellationToken, ValueTask<int>>>(a);
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var old = current;
                var next = LoadGeneration();
                var running = block(ct).AsTask();
                try
                {
                    await ((Task)old.ServiceType.GetProperty("BlockStarted")!.GetValue(null)!).WaitAsync(ct);
                    var update = ReplaceEvidenceAsync(server, client, contracts, contractId, old.Assembly, next.Assembly, ct);
                    Ensure(!update.IsCompleted && !running.IsCompleted && await b.TouchAsync(attempt + 1, ct) == attempt + 101,
                        "unrelated B succeeds on the old physical Session during a replacement that must still join R1");
                    old.ServiceType.GetMethod("ReleaseBlock")!.Invoke(null, null);
                    Ensure(await running.WaitAsync(ct) == 42, "already-admitted A finishes on its real old implementation");
                    await update.WaitAsync(ct);
                    Ensure(await unary(attempt + 2, ct) == attempt + 3 &&
                           GenerationCounter(next.ServiceType, "Created") == 1 &&
                           GenerationCounter(old.ServiceType, "Disposed") == 1 &&
                           await b.TouchAsync(attempt + 3, ct) == attempt + 103 &&
                           siblingProbe.SessionId == originalSession,
                        "the generated A proxy now reaches the distinct new registration; B was never reconnected");
                    current = next;
                    Ensure(interceptionProbe is null || interceptionProbe.Visits == (1 + attempt * 2, 1 + attempt * 2),
                        "each actual A and B call visits the configured interceptor exactly once");
                    Console.WriteLine($"REPLACE_WORKLOAD_VERIFIED transport={transport} replacements={attempt} b-same-session=true interceptor={interceptor}");
                }
                finally { old.ServiceType.GetMethod("ReleaseBlock")!.Invoke(null, null); }
            }
        }
        finally
        {
            foreach (var context in contexts)
                context.Unload();
        }

        ReplacementGeneration LoadGeneration()
        {
            var context = new SharedContractServiceLoadContext(contracts.ContractAssembly);
            contexts.Add(context);
            var assembly = context.LoadFromAssemblyPath(path);
            return new(context, assembly, assembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!);
        }

        void ReleaseGeneration(ReplacementGeneration generation)
        {
            Ensure(contexts.Remove(generation.Context), "retired generation must have exactly one fixture owner");
            generation.Context.Unload();
        }
    }

    private sealed record ReplacementGeneration(SharedContractServiceLoadContext Context, Assembly Assembly, Type ServiceType);

    private static int GenerationCounter(Type service, string property) => (int)service.GetProperty(property)!.GetValue(null)!;

    // The timed variant is exactly a forwarding callback: no logging, counters or application work.
    private sealed class QualifiedReplacementForwarder : ISharpLinkServerInterceptor
    {
        internal static readonly QualifiedReplacementForwarder Instance = new();

        public ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
            => next(context);
    }

    private sealed class QualifiedReplacementInterceptorProbe(long dynamicContract, long siblingContract)
        : ISharpLinkServerInterceptor
    {
        private int _dynamicVisits;
        private int _siblingVisits;

        internal (int, int) Visits => (Volatile.Read(ref _dynamicVisits), Volatile.Read(ref _siblingVisits));

        public ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
        {
            if (context.Method.ContractId == dynamicContract)
                Interlocked.Increment(ref _dynamicVisits);
            else if (context.Method.ContractId == siblingContract)
                Interlocked.Increment(ref _siblingVisits);
            return next(context);
        }
    }

    private sealed class QualifiedReplacementSibling : IShutdownCleanupProbe
    {
        private string? _sessionId;
        internal string? SessionId => Volatile.Read(ref _sessionId);

        public ValueTask<int> TouchAsync(int value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = SharpLinkCallContext.Current?.SessionId ??
                          throw new InvalidOperationException("Expected the actual authenticated RPC session.");
            var previous = Interlocked.CompareExchange(ref _sessionId, session, null);
            if (previous is not null && !string.Equals(previous, session, StringComparison.Ordinal))
                throw new InvalidOperationException("Unrelated workload moved to another physical Session.");
            return ValueTask.FromResult(value + 100);
        }
    }
}
