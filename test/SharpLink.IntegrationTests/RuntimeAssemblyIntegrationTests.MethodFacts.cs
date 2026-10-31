using System.Reflection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel]
    public async Task RetainedCompleteFactsShouldNotRootRetiredCollectibleGeneration(bool retainStubControl)
    {
        var (tracked, facts, expected, root) = await RetireGenerationWithRetainedFacts(retainStubControl);
        if (retainStubControl)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Ensure(tracked.AnyAlive, "the deliberately retained collectible stub must keep the negative control alive");
            root.Value = null;
        }
        for (var attempt = 0; attempt < 20 && tracked.AnyAlive; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Task.Delay(20);
        }
        Ensure(!tracked.AnyAlive, $"retained host scalar facts must not root a retired generation: {tracked.AliveNames}");
        Ensure(facts.Length > 5 && facts.Length == expected.Length, "retain a real generated multi-shape method table");
        for (var index = 0; index < facts.Length; index++)
        {
            Ensure(facts[index].GetType() == typeof(RpcMethodFacts), "facts must use the shared host Abstractions type");
            Ensure(facts[index].ToDescriptor() == expected[index], "complete facts remain usable after producer ALC collection");
        }
        GC.KeepAlive(facts);
        GC.KeepAlive(root);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(TrackedWeakReferences Tracked, RpcMethodFacts[] Facts,
        RpcMethodDescriptor[] Expected, StrongBox<object?> Root)> RetireGenerationWithRetainedFacts(bool retainStubControl)
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("retained-complete-method-facts");
        await RegisterAllAsync(harness, plugin);
        var server = (SharpLinkServer)harness.Server;
        var registry = (ServerServiceModuleRegistry)typeof(SharpLinkServer).GetField(
            "_serviceModuleRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var registration = registry.ServicesStorage.Values.Single(item => item.ContractType == plugin.ContractType);
        var result = SharpLinkAssemblyManifestLoader.TryLoad(plugin.ContractAssembly, out var manifest);
        Ensure(result.Succeeded && manifest is not null, "current generated plugin manifest loads");
        var contract = manifest!.Contracts.Single(item => item.ContractType == plugin.ContractType);
        var facts = new RpcMethodFacts[contract.Methods.Count];
        var expected = new RpcMethodDescriptor[facts.Length];
        for (var index = 0; index < facts.Length; index++)
        {
            facts[index] = registration.Stub.ResolveMethodFacts(contract.Methods[index].MethodId);
            Ensure(ReferenceEquals(facts[index], registration.Stub.ResolveMethodFacts(contract.Methods[index].MethodId)),
                "generated method facts are a shared cold instance, not a per-lookup allocation");
            expected[index] = facts[index].ToDescriptor();
        }
        var root = new StrongBox<object?>(retainStubControl ? registration.Stub : null);
        var tracked = new TrackedWeakReferences();
        tracked.Add("original stub", registration.Stub);
        tracked.Add("contract assembly", plugin.ContractAssembly);
        tracked.Add("service assembly", plugin.ServiceAssembly);
        registration = null!;
        contract = null!;
        manifest = null;
        Ensure((await harness.Server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "implementation generation released");
        Ensure((await harness.Server.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "server contract generation released");
        Ensure((await harness.Client.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "client contract generation released");
        tracked.Add("producer ALC", plugin.Unload());
        return (tracked, facts, expected, root);
    }
}
