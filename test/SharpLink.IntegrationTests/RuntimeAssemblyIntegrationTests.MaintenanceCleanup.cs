using System.Runtime.Loader;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    public async Task CooperativeMaintenanceFixtureCleanupAttemptsEveryOwnerAndPreservesFailure()
    {
        var harness = await MaintenanceHarness.CreateAsync(separateOldImplementation: true);
        var unloading = 0;
        var assemblies = new[] { harness.SourceAssembly, harness.Replacement, harness.Plugin.ContractAssembly };
        Ensure(assemblies.All(assembly => SharpLinkGeneratedAssemblyCatalog.CreateSnapshot().Any(manifest =>
            ReferenceEquals(manifest.OwnerAssembly, assembly))), "the fixture manifests start discoverable");
        foreach (var assembly in assemblies)
            AssemblyLoadContext.GetLoadContext(assembly)!.Unloading += _ => Interlocked.Increment(ref unloading);
        var expected = new InvalidOperationException("deliberate fixture cleanup failure");
        harness.BeforeCleanupForTesting = async () => { await Task.Yield(); throw expected; };
        Exception? observed = null;
        try { await harness.DisposeAsync(); }
        catch (Exception failure) { observed = failure; }
        Ensure(ReferenceEquals(observed, expected), "the original failure must remain visible without hiding extra cleanup faults");
        Ensure(harness.Clients.All(static client => client.State == SharpLinkConnectionState.Stopped) &&
            harness.Server.LifecycleState == SharpLinkServerLifecycleState.Stopped,
            "every client and the server must stop despite an earlier fixture cleanup failure");
        Ensure(unloading == 3, "all three collectible context unload paths must run despite failure");
        Ensure(SharpLinkGeneratedAssemblyCatalog.CreateSnapshot().All(manifest =>
            !assemblies.Contains(manifest.OwnerAssembly)), "failed cleanup cannot pollute later manifest discovery");
    }
}
