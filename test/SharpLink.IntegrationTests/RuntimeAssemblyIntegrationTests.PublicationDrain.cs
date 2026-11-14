using SharpLink.CodecPublicationProbe;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    [NotInParallel]
    public async Task PublicationMustRejectConsumerWhenProviderBeginsActiveDrain(bool server, bool replace)
    {
        await using var harness = await DynamicHarness.CreateAsync();
        var directory = GetProjectOutputDirectory("SharpLink.CodecPublicationDrainConsumer");
        var context = new PluginLoadContext($"publication-active-drain-{server}-{replace}", directory);
        var provider = context.LoadFromAssemblyPath(Path.Combine(directory, "SharpLink.CodecPublicationDrainProvider.dll"));
        var consumer = context.LoadFromAssemblyPath(Path.Combine(directory, "SharpLink.CodecPublicationDrainConsumer.dll"));
        var contract = provider.GetType("SharpLink.CodecPublicationDrainProvider.IDrainService", throwOnError: true)!;
        var service = provider.GetType("SharpLink.CodecPublicationDrainProvider.DrainService", throwOnError: true)!;
        Task<int>? activeCall = null;
        Task<SharpLinkAssemblyUnregisterResult>? drain = null;
        try
        {
            Ensure(harness.Client.RegisterAssembly(provider).Succeeded, "client registers generated active provider");
            Ensure(harness.Server.RegisterAssembly(provider).Succeeded, "server registers generated active provider");
            await WaitForRemoteContractManifestAsync(harness.Client, contract);
            var endpoint = server ? (ISharpLinkAssemblyRegistry)harness.Server : harness.Client;
            var probe = typeof(PublicationProbeManifest).Assembly;
            Ensure(endpoint.RegisterAssembly(probe).Succeeded, "public metadata trigger registers");
            var proxy = GetProxy(harness.Client, contract);
            activeCall = InvokeValueTaskAsync<int>(proxy, contract, "BlockAsync", CancellationToken.None).AsTask();
            await ((Task)service.GetProperty("Started")!.GetValue(null)!).WaitAsync(TimeSpan.FromSeconds(2));
            PublicationProbeManifest.OnRead = () =>
            {
                drain = endpoint.UnregisterAssemblyAsync(provider, TimeSpan.FromSeconds(2)).AsTask();
                Ensure(!drain.IsCompleted, "public retirement started while a real RPC still owns the provider");
            };
            var admitted = replace
                ? (await endpoint.ReplaceAssemblyAsync(probe, consumer, TimeSpan.Zero)).Succeeded
                : endpoint.RegisterAssembly(consumer).Succeeded;
            Ensure(drain is not null && !drain.IsCompleted, "metadata validation crossed a live public provider drain");
            // Keep the baseline counterexample cleanly recoverable before asserting rejection.
            if (admitted)
                Ensure((await endpoint.UnregisterAssemblyAsync(consumer, TimeSpan.Zero)).ReferencesReleased,
                    "baseline-only stale consumer cleanup");
            service.GetMethod("Release")!.Invoke(null, null);
            Ensure(await activeCall.WaitAsync(TimeSpan.FromSeconds(3)) == 7, "the original active call still completes normally");
            Ensure((await drain!.WaitAsync(TimeSpan.FromSeconds(3))).ReferencesReleased,
                "the provider retires and releases references after its call ends");
            Ensure(!admitted, $"no new consumer may commit after its exact provider starts draining (server={server}, replace={replace})");
        }
        finally
        {
            PublicationProbeManifest.OnRead = null;
            service.GetMethod("Release")!.Invoke(null, null);
            if (activeCall is not null)
                await activeCall.WaitAsync(TimeSpan.FromSeconds(3));
            context.Unload();
        }
    }
}
