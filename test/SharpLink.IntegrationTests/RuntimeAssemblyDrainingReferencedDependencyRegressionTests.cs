using System.Reflection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task ReferencedCodecDependencyShouldRequireRunningProviderOnClientAndServer()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        var directory = GetProjectOutputDirectory("SharpLink.ReferencedCodecConsumer");
        var loadContext = new PluginLoadContext("referenced-codec-draining-admission", directory);
        try
        {
            var provider = loadContext.LoadFromAssemblyPath(
                Path.Combine(directory, "SharpLink.ReferencedCodecProvider.dll"));
            var consumer = loadContext.LoadFromAssemblyPath(
                Path.Combine(directory, "SharpLink.ReferencedCodecConsumer.dll"));

            Ensure(harness.Client.RegisterAssembly(provider).Succeeded,
                "client registers referenced Codec provider before drain");
            Ensure(harness.Server.RegisterAssembly(provider).Succeeded,
                "server registers referenced Codec provider before drain");

            MarkDynamicModuleDraining(harness.Client, provider);
            MarkDynamicModuleDraining(harness.Server, provider);

            var clientResult = harness.Client.RegisterAssembly(consumer);
            Ensure(!clientResult.Succeeded &&
                   clientResult.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.MissingDependency &&
                   clientResult.Error.Message.Contains("registered and running Assembly generation", StringComparison.Ordinal),
                $"client must reject a new typed dependant while its exact provider is draining even though the provider Codec registration is still published: {clientResult.Error}");

            var serverResult = harness.Server.RegisterAssembly(consumer);
            Ensure(!serverResult.Succeeded &&
                   serverResult.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.MissingDependency &&
                   serverResult.Error.Message.Contains("registered and running Assembly generation", StringComparison.Ordinal),
                $"server must reject a new typed dependant while its exact provider is draining even though the provider Codec registration is still published: {serverResult.Error}");

            Ensure((await harness.Client.UnregisterAssemblyAsync(provider, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "client releases manually-draining provider after rejected dependant");
            Ensure((await harness.Server.UnregisterAssemblyAsync(provider, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "server releases manually-draining provider after rejected dependant");
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static void MarkDynamicModuleDraining(object endpoint, Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var modules = ServerRegistryTestAccessor.DynamicModules(endpoint);
        if (modules[assembly] is not { } module)
            throw new InvalidOperationException($"Dynamic module for '{assembly.FullName}' was not registered.");
        var beginDraining = module.GetType().GetMethod("TryBeginDraining", flags)
            ?? throw new MissingMethodException(module.GetType().FullName, "TryBeginDraining");
        Ensure(beginDraining.Invoke(module, null) is true,
            $"dynamic module '{assembly.FullName}' must transition from Running to Draining for admission regression setup");
    }
}
