using System.Reflection;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

// Exact production cold-owner components. These are not full Register/Replace transport tests.
public sealed class RegistrationPreparationOwnershipTests
{
    [Test]
    public void DependencyPreparationFencesRetirementWithoutConsumingRpcStripes()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new EmptyManifest();
        using var codecs = context.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(typeof(RegistrationPreparationOwnershipTests).Assembly, manifest, codecs);
        Ensure(module.TryAcquirePreparation(), "cold dependency owner admitted");
        Ensure(module.PreparationOwnersForDiagnostics == 1 && module.RemainingCalls == 0 && module.RemainingStreams == 0,
            "preparation is not an ordinary call/stream lease");
        Ensure(module.TryBeginDraining(), "same existing Running cut");
        Ensure(!module.TryAcquirePreparation() && !module.TryAcquire(false, out _), "no owner after cut");
        Ensure(!module.WaitForDrainAsync().IsCompleted, "callback ownership prevents physical codec release");
        module.ReleasePreparation();
        Ensure(module.WaitForDrainAsync().IsCompletedSuccessfully && module.PreparationOwnersForDiagnostics == 0,
            "actual callback return releases its final cold owner");
        module.AssertAccountingInvariant();
    }

    [Test]
    public async Task SixteenColdOwnersHaveExactCapacityAndStopJoinsTheirPhysicalExit()
    {
        await using var publicServer = SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(new IdleListener()).Build();
        var server = (SharpLinkServer)publicServer;
        await server.StartAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var owners = Enumerable.Range(0, 16).Select(slot => Task.Run(async () =>
        {
            Ensure(TryEnter(server, out var owner, out _), "one of sixteen cold preparation slots");
            using (owner!)
            {
                // These calls execute on the same production preparation context. They must
                // reject before starting mutation/Stop, rather than deadlocking on this owner.
                ExpectReentry(() => server.RegisterAssembly(typeof(RegistrationPreparationOwnershipTests).Assembly));
                ExpectReentry(() => server.StopAsync(TimeSpan.Zero));
                if (Interlocked.Increment(ref count) == 16) entered.TrySetResult();
                await release.Task;
            }
        })).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!TryEnter(server, out var rejected, out var error) && rejected is null &&
                error?.Code == SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded,
                "seventeenth preparation rejects without a callback");
            var stop = server.StopAsync(TimeSpan.Zero).AsTask();
            var drain = CaptureDrain(server);
            Ensure(!drain.IsCompleted && !stop.IsCompleted,
                "Stop waiter cannot dispose provider/runtime while cold callbacks retain owners");
            Ensure(!TryEnter(server, out _, out error) && error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                "Stop publication seals new preparation before cleanup");
            release.TrySetResult();
            await Task.WhenAll(owners).WaitAsync(TimeSpan.FromSeconds(5));
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(owners).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static bool TryEnter(SharpLinkServer server, out IDisposable? owner,
        out SharpLinkAssemblyRegistrationError? error)
    {
        object?[] values = [null, null];
        var result = (bool)typeof(SharpLinkServer).GetMethod("TryBeginRegistrationPreparation",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(server, values)!;
        owner = (IDisposable?)values[0];
        error = (SharpLinkAssemblyRegistrationError?)values[1];
        return result;
    }

    private static Task CaptureDrain(SharpLinkServer server)
        => (Task)typeof(SharpLinkServer).GetMethod("CaptureRegistrationPreparationsDrain",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(server, null)!;

    private static void ExpectReentry(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Same-server callback mutation was not rejected.");
    }

    private static void Ensure(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class IdleListener : IServerTransportListener
    {
        public System.Net.EndPoint? LocalEndPoint => null;
        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyManifest : ITestGeneratedManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(RegistrationPreparationOwnershipTests).Assembly;
        public string CompileTimeDescriptor => "test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }
}
