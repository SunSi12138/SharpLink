using System.Net;
using System.Reflection;
using SharpLink.Client;
using SharpLink.CodecPublicationProbe;
using SharpLink.ReferencedCodecConsumer;
using SharpLink.ReferencedCodecProvider;
using SharpLink.Server;

namespace SharpLink.UnitTests.Runtime;

public sealed class CodecPublicationReentrancyTests
{
    private static Assembly ProviderAssembly => typeof(ProviderManifest).Assembly;
    private static Assembly ConsumerAssembly => typeof(ConsumerManifest).Assembly;
    private static Assembly ProbeAssembly => typeof(PublicationProbeManifest).Assembly;

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel]
    public async Task PublicConsumerPublicationMustRejectProviderRetiredDuringValidation(bool server)
    {
        await using var endpoint = CreateEndpoint(server);
        Register(endpoint.Registry, ProviderAssembly);
        Register(endpoint.Registry, ProbeAssembly);
        var retired = false;
        PublicationProbeManifest.OnRead = () => retired = endpoint.Registry
            .UnregisterAssemblyAsync(ProviderAssembly, TimeSpan.Zero).AsTask().GetAwaiter().GetResult().ReferencesReleased;
        try
        {
            var result = endpoint.Registry.RegisterAssembly(ConsumerAssembly);
            Ensure(retired, "provider retirement completed inside publication validation");
            Ensure(!result.Succeeded, "a consumer cannot publish after its exact provider physically retired");
            Register(endpoint.Registry, ProviderAssembly);
            _ = endpoint.Context.Codecs.GetCodec<Payload>();
            Register(endpoint.Registry, ConsumerAssembly);
            await Remove(endpoint.Registry, ConsumerAssembly);
            await Remove(endpoint.Registry, ProviderAssembly);
        }
        finally
        {
            PublicationProbeManifest.OnRead = null;
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel]
    public async Task PublicCompetingPublicationMustPreserveNestedWinnerAfterOuterRollback(bool server)
    {
        await using var endpoint = CreateEndpoint(server);
        Register(endpoint.Registry, ProbeAssembly);
        SharpLinkAssemblyRegistrationResult? winner = null;
        PublicationProbeManifest.OnRead = () => winner = endpoint.Registry.RegisterAssembly(ProviderAssembly);
        try
        {
            var outer = endpoint.Registry.RegisterAssembly(ProviderAssembly);
            Ensure(winner?.Succeeded == true, "nested provider registration committed");
            Ensure(!outer.Succeeded, "the conflicting outer candidate must not overwrite the winner");
            _ = endpoint.Context.Codecs.GetCodec<Payload>();
            _ = RpcGeneratedCodecResolver.GetProvider(endpoint.Context, ProviderAssembly).GetCodec<Payload>();
            await Remove(endpoint.Registry, ProviderAssembly);
        }
        finally
        {
            PublicationProbeManifest.OnRead = null;
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel]
    public async Task PublicPublicationShouldRebuildAfterUnrelatedRetirement(bool server)
    {
        await using var endpoint = CreateEndpoint(server);
        Register(endpoint.Registry, ProviderAssembly);
        Register(endpoint.Registry, ProbeAssembly);
        var before = endpoint.Context.Codecs.GetCodec<Payload>();
        var retired = false;
        PublicationProbeManifest.OnRead = () => retired = endpoint.Registry
            .UnregisterAssemblyAsync(ProbeAssembly, TimeSpan.Zero).AsTask().GetAwaiter().GetResult().ReferencesReleased;
        try
        {
            Register(endpoint.Registry, ConsumerAssembly);
            Ensure(retired, "unrelated owner physically retired");
            Ensure(ReferenceEquals(before, endpoint.Context.Codecs.GetCodec<Payload>()),
                "surviving exact provider and its cached codec retain identity");
            await Remove(endpoint.Registry, ConsumerAssembly);
            await Remove(endpoint.Registry, ProviderAssembly);
        }
        finally { PublicationProbeManifest.OnRead = null; }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel]
    public async Task PublicReplacementMustRetainOldModuleWhenProviderRetiresDuringValidation(bool server)
    {
        await using var endpoint = CreateEndpoint(server);
        Register(endpoint.Registry, ProviderAssembly);
        Register(endpoint.Registry, ProbeAssembly);
        var retired = false;
        PublicationProbeManifest.OnRead = () => retired = endpoint.Registry
            .UnregisterAssemblyAsync(ProviderAssembly, TimeSpan.Zero).AsTask().GetAwaiter().GetResult().ReferencesReleased;
        try
        {
            var result = await endpoint.Registry.ReplaceAssemblyAsync(ProbeAssembly, ConsumerAssembly, TimeSpan.Zero);
            Ensure(retired && !result.Succeeded, "replacement cannot install a consumer after provider retirement");
            Ensure(!endpoint.Registry.RegisterAssembly(ProbeAssembly).Succeeded, "failed replacement preserves the old module");
            Register(endpoint.Registry, ProviderAssembly);
            Ensure((await endpoint.Registry.ReplaceAssemblyAsync(ProbeAssembly, ConsumerAssembly, TimeSpan.Zero)).Succeeded,
                "replacement succeeds with the restored exact provider");
            _ = endpoint.Context.Codecs.GetCodec<Payload>();
            await Remove(endpoint.Registry, ConsumerAssembly);
            await Remove(endpoint.Registry, ProviderAssembly);
        }
        finally { PublicationProbeManifest.OnRead = null; }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel]
    public async Task PublicRetirementMustPreserveProviderPublishedDuringValidation(bool server)
    {
        await using var endpoint = CreateEndpoint(server);
        Register(endpoint.Registry, ProbeAssembly);
        SharpLinkAssemblyRegistrationResult? winner = null;
        PublicationProbeManifest.OnRead = () => winner = endpoint.Registry.RegisterAssembly(ProviderAssembly);
        try
        {
            await Remove(endpoint.Registry, ProbeAssembly);
            Ensure(winner?.Succeeded == true, "nested provider committed before retirement publication resumed");
            _ = endpoint.Context.Codecs.GetCodec<Payload>();
            Register(endpoint.Registry, ConsumerAssembly);
            await Remove(endpoint.Registry, ConsumerAssembly);
            await Remove(endpoint.Registry, ProviderAssembly);
        }
        finally { PublicationProbeManifest.OnRead = null; }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel]
    public async Task PublicConflictedCandidateCleanupFailureMustNotDisposeNestedWinner(bool server)
    {
        var previous = Environment.GetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC");
        await SharpLink.RollbackPlugin.RollbackState.TestIsolation.WaitAsync();
        try
        {
            Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", null);
            await using var endpoint = CreateEndpoint(server);
            Register(endpoint.Registry, ProbeAssembly);
            var disposedBefore = SharpLink.RollbackPlugin.RollbackState.ScopeDisposeCount;
            SharpLinkAssemblyRegistrationResult? winner = null;
            PublicationProbeManifest.OnRead = () => winner = endpoint.Registry.RegisterAssembly(ProviderAssembly);
            var failure = endpoint.Registry.RegisterAssembly(typeof(SharpLink.RollbackPlugin.RollbackManifest).Assembly);
            Ensure(!failure.Succeeded && failure.Error?.Message.Contains("rollback Adapter scope cleanup failed", StringComparison.Ordinal) == true,
                "candidate cleanup failure is reported instead of claiming publication");
            Ensure(winner?.Succeeded == true, "nested winner committed");
            Ensure(SharpLink.RollbackPlugin.RollbackState.ScopeDisposeCount == disposedBefore + 1,
                "the abandoned candidate scope is physically cleaned once");
            _ = endpoint.Context.Codecs.GetCodec<Payload>();
            await Remove(endpoint.Registry, ProviderAssembly);
        }
        finally
        {
            PublicationProbeManifest.OnRead = null;
            Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", previous);
            SharpLink.RollbackPlugin.RollbackState.TestIsolation.Release();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel]
    public async Task PublicCrossThreadRetirementMustRemainBehindConsumerCommit(bool server)
    {
        await using var endpoint = CreateEndpoint(server);
        Register(endpoint.Registry, ProviderAssembly);
        Register(endpoint.Registry, ProbeAssembly);
        var timeout = TimeSpan.FromSeconds(10);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var removalStarted = new ManualResetEventSlim();
        PublicationProbeManifest.OnRead = () =>
        {
            entered.Set();
            Ensure(release.Wait(timeout), "bounded metadata gate release");
        };
        var publication = Task.Factory.StartNew(() => endpoint.Registry.RegisterAssembly(ConsumerAssembly),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<bool>? removal = null;
        try
        {
            Ensure(entered.Wait(timeout), "consumer publication reached metadata validation");
            removal = Task.Factory.StartNew(() =>
            {
                removalStarted.Set();
                try
                {
                    return endpoint.Registry.UnregisterAssemblyAsync(ProviderAssembly, TimeSpan.Zero)
                        .AsTask().GetAwaiter().GetResult().ReferencesReleased;
                }
                catch (InvalidOperationException error) when (error.Message.Contains("depends on it", StringComparison.Ordinal))
                {
                    return false;
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Ensure(removalStarted.Wait(timeout), "retirement worker started");
            release.Set();
            Ensure((await publication.WaitAsync(timeout)).Succeeded, "consumer commits before competing retirement");
            Ensure(!await removal.WaitAsync(timeout), "the live consumer still protects its exact provider");
            _ = endpoint.Context.Codecs.GetCodec<Payload>();
            await Remove(endpoint.Registry, ConsumerAssembly);
            await Remove(endpoint.Registry, ProviderAssembly);
        }
        finally
        {
            release.Set();
            PublicationProbeManifest.OnRead = null;
            await publication.WaitAsync(timeout);
            if (removal is not null)
                await removal.WaitAsync(timeout);
        }
    }

    private static Endpoint CreateEndpoint(bool server)
    {
        if (!server)
        {
            var client = SharpClientBuilder.Create().DisableRequestTimeout().UseTcp("127.0.0.1", 1).Build();
            return new Endpoint(client, ((IRpcChannel)client).RuntimeContext);
        }
        var instance = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback).Build();
        // Observe the server's existing context; all mutations still use the public registry.
        var context = (IRpcRuntimeContext)(instance.GetType().GetField("_runtimeContext", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance)!);
        return new Endpoint(instance, context);
    }

    private static void Register(ISharpLinkAssemblyRegistry registry, Assembly assembly)
    {
        var result = registry.RegisterAssembly(assembly);
        Ensure(result.Succeeded, $"registration of {assembly.FullName}: {result.Error}");
    }

    private static async Task Remove(ISharpLinkAssemblyRegistry registry, Assembly assembly)
        => Ensure((await registry.UnregisterAssemblyAsync(assembly, TimeSpan.Zero)).ReferencesReleased,
            $"release of {assembly.FullName}");

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Endpoint(ISharpLinkAssemblyRegistry registry, IRpcRuntimeContext context) : IAsyncDisposable
    {
        internal ISharpLinkAssemblyRegistry Registry { get; } = registry;
        internal IRpcRuntimeContext Context { get; } = context;
        public ValueTask DisposeAsync() => ((IAsyncDisposable)Registry).DisposeAsync();
    }
}
