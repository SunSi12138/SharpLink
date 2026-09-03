using System.Reflection;
using System.Runtime.Loader;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task SameFullNameReferencedCodecDependencyShouldRequireExactGenerationOnClientAndServer()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        var directory = GetProjectOutputDirectory("SharpLink.ReferencedCodecConsumer");
        var firstContext = new PluginLoadContext("referenced-codec-generation-1", directory);
        var secondContext = new PluginLoadContext("referenced-codec-generation-2", directory);
        try
        {
            var providerPath = Path.Combine(directory, "SharpLink.ReferencedCodecProvider.dll");
            var consumerPath = Path.Combine(directory, "SharpLink.ReferencedCodecConsumer.dll");
            var provider1 = firstContext.LoadFromAssemblyPath(providerPath);
            var provider2 = secondContext.LoadFromAssemblyPath(providerPath);
            var consumer2 = secondContext.LoadFromAssemblyPath(consumerPath);

            Ensure(provider1.FullName == provider2.FullName && !ReferenceEquals(provider1, provider2),
                "test setup must load two distinct provider generations with the same Assembly.FullName");
            var consumerManifestType = consumer2.GetType(
                "SharpLink.ReferencedCodecConsumer.ConsumerManifest",
                throwOnError: true)!;
            var consumerManifest = (ISharpLinkReferencedCodecDependencyManifest)Activator.CreateInstance(
                consumerManifestType)!;
            var typedDependency = consumerManifest.ReferencedCodecDependencies.Single();
            Ensure(ReferenceEquals(typedDependency.TargetType.Assembly, provider2),
                "consumer generation 2 must retain the exact provider generation selected by its runtime Type binding");

            Ensure(harness.Client.RegisterAssembly(provider1).Succeeded,
                "client registers generation-1 provider");
            Ensure(harness.Server.RegisterAssembly(provider1).Succeeded,
                "server registers generation-1 provider");

            var wrongClient = harness.Client.RegisterAssembly(consumer2);
            Ensure(!wrongClient.Succeeded &&
                   wrongClient.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest &&
                   wrongClient.Error.Message.Contains("exact bound runtime Type/assembly generation", StringComparison.Ordinal),
                $"client must reject generation-2 consumer when only same-FullName generation-1 provider is registered: {wrongClient.Error}");
            var wrongServer = harness.Server.RegisterAssembly(consumer2);
            Ensure(!wrongServer.Succeeded &&
                   wrongServer.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest &&
                   wrongServer.Error.Message.Contains("exact bound runtime Type/assembly generation", StringComparison.Ordinal),
                $"server must reject generation-2 consumer when only same-FullName generation-1 provider is registered: {wrongServer.Error}");

            Ensure((await harness.Client.UnregisterAssemblyAsync(provider1, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "client releases generation-1 provider after rejected consumer");
            Ensure((await harness.Server.UnregisterAssemblyAsync(provider1, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "server releases generation-1 provider after rejected consumer");

            Ensure(harness.Client.RegisterAssembly(provider2).Succeeded,
                "client registers exact generation-2 provider");
            Ensure(harness.Server.RegisterAssembly(provider2).Succeeded,
                "server registers exact generation-2 provider");

            var clientReplacement = await harness.Client.ReplaceAssemblyAsync(
                provider2, consumer2, TimeSpan.FromSeconds(2));
            Ensure(!clientReplacement.Succeeded &&
                   clientReplacement.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest &&
                   clientReplacement.Error.Message.Contains("exact Type", StringComparison.Ordinal),
                $"client replacement must validate the pending consumer against the final candidate snapshot: {clientReplacement.Error}");
            var serverReplacement = await harness.Server.ReplaceAssemblyAsync(
                provider2, consumer2, TimeSpan.FromSeconds(2));
            Ensure(!serverReplacement.Succeeded &&
                   serverReplacement.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest &&
                   serverReplacement.Error.Message.Contains("exact Type", StringComparison.Ordinal),
                $"server replacement must validate the pending consumer against the final candidate snapshot: {serverReplacement.Error}");

            Ensure(harness.Client.RegisterAssembly(consumer2).Succeeded,
                "client accepts consumer with exact bound provider generation and expected CodecHash");
            Ensure(harness.Server.RegisterAssembly(consumer2).Succeeded,
                "server accepts consumer with exact bound provider generation and expected CodecHash");

            var clientCodec = ResolveManifestCodec(harness.Client, consumer2, typedDependency.TargetType);
            var serverCodec = ResolveManifestCodec(harness.Server, consumer2, typedDependency.TargetType);
            Ensure(ReferenceEquals(clientCodec.GetType().Assembly, provider2),
                "client contract provider resolves the exact referenced generated Codec rather than falling back");
            Ensure(ReferenceEquals(serverCodec.GetType().Assembly, provider2),
                "server contract provider resolves the exact referenced generated Codec rather than falling back");

            try
            {
                _ = await harness.Client.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2));
                throw new Exception("assert failed: client must reject provider unregister while exact typed consumer depends on it");
            }
            catch (InvalidOperationException exception)
            {
                Ensure(exception.Message.Contains("depends on it", StringComparison.Ordinal),
                    "client reverse dependency check uses exact provider Assembly generation");
            }
            try
            {
                _ = await harness.Server.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2));
                throw new Exception("assert failed: server must reject provider unregister while exact typed consumer depends on it");
            }
            catch (InvalidOperationException exception)
            {
                Ensure(exception.Message.Contains("depends on it", StringComparison.Ordinal),
                    "server reverse dependency check uses exact provider Assembly generation");
            }

            Ensure((await harness.Client.UnregisterAssemblyAsync(consumer2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "client releases typed consumer before provider");
            Ensure((await harness.Server.UnregisterAssemblyAsync(consumer2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "server releases typed consumer before provider");
            Ensure((await harness.Client.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "client releases exact provider after dependant removal");
            Ensure((await harness.Server.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "server releases exact provider after dependant removal");
        }
        finally
        {
            firstContext.Unload();
            secondContext.Unload();
        }
    }

    [Test]
    [NotInParallel]
    public async Task SameFullNameDeclaredModuleDependencyShouldRequireExactBoundGenerationOnClientAndServer()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        var directory = GetProjectOutputDirectory("SharpLink.ModuleDependencyConsumer");
        var firstContext = new PluginLoadContext("module-dependency-generation-1", directory);
        var secondContext = new PluginLoadContext("module-dependency-generation-2", directory);
        try
        {
            var providerPath = Path.Combine(directory, "SharpLink.ReferencedCodecProvider.dll");
            var consumerPath = Path.Combine(directory, "SharpLink.ModuleDependencyConsumer.dll");
            var provider1 = firstContext.LoadFromAssemblyPath(providerPath);
            var provider2 = secondContext.LoadFromAssemblyPath(providerPath);
            var consumer2 = secondContext.LoadFromAssemblyPath(consumerPath);

            Ensure(provider1.FullName == provider2.FullName && !ReferenceEquals(provider1, provider2),
                "module dependency setup must load distinct same-FullName provider generations");
            Ensure(harness.Client.RegisterAssembly(provider1).Succeeded,
                "client registers only the wrong provider generation");
            Ensure(harness.Server.RegisterAssembly(provider1).Succeeded,
                "server registers only the wrong provider generation");

            var wrongClient = harness.Client.RegisterAssembly(consumer2);
            Ensure(!wrongClient.Succeeded &&
                   wrongClient.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                $"client must not satisfy a CLR-bound module dependency with another same-FullName generation: {wrongClient.Error}");
            var wrongServer = harness.Server.RegisterAssembly(consumer2);
            Ensure(!wrongServer.Succeeded &&
                   wrongServer.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                $"server must not satisfy a CLR-bound module dependency with another same-FullName generation: {wrongServer.Error}");

            Ensure((await harness.Client.UnregisterAssemblyAsync(provider1, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "client removes wrong provider generation");
            Ensure((await harness.Server.UnregisterAssemblyAsync(provider1, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "server removes wrong provider generation");
            Ensure(harness.Client.RegisterAssembly(provider2).Succeeded,
                "client registers exact bound provider generation");
            Ensure(harness.Server.RegisterAssembly(provider2).Succeeded,
                "server registers exact bound provider generation");
            Ensure(harness.Client.RegisterAssembly(consumer2).Succeeded,
                "client accepts ordinary module dependency with exact bound provider generation");
            Ensure(harness.Server.RegisterAssembly(consumer2).Succeeded,
                "server accepts ordinary module dependency with exact bound provider generation");

            await EnsureDependencyPreventsUnregisterAsync(
                () => harness.Client.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2)),
                "client ordinary module dependency reverse check");
            await EnsureDependencyPreventsUnregisterAsync(
                () => harness.Server.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2)),
                "server ordinary module dependency reverse check");

            Ensure((await harness.Client.UnregisterAssemblyAsync(consumer2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "client releases ordinary dependant before provider");
            Ensure((await harness.Server.UnregisterAssemblyAsync(consumer2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "server releases ordinary dependant before provider");
            Ensure((await harness.Client.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "client releases exact ordinary dependency provider");
            Ensure((await harness.Server.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "server releases exact ordinary dependency provider");
        }
        finally
        {
            firstContext.Unload();
            secondContext.Unload();
        }
    }

    private static async Task EnsureDependencyPreventsUnregisterAsync(
        Func<ValueTask<SharpLinkAssemblyUnregisterResult>> unregister,
        string message)
    {
        try
        {
            _ = await unregister();
            throw new Exception($"assert failed: {message}");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("depends on it", StringComparison.Ordinal), message);
        }
    }

    private static object ResolveManifestCodec(object endpoint, Assembly ownerAssembly, Type targetType)
    {
        var runtimeContext = GetEndpointRuntimeContext(endpoint);
        var provider = RpcGeneratedCodecResolver.GetProvider(runtimeContext, ownerAssembly);
        var method = typeof(IRpcCodecProvider).GetMethod(nameof(IRpcCodecProvider.GetCodec))
            ?? throw new MissingMethodException(nameof(IRpcCodecProvider), nameof(IRpcCodecProvider.GetCodec));
        return method.MakeGenericMethod(targetType).Invoke(provider, null)
            ?? throw new InvalidOperationException($"Codec resolution for '{targetType}' returned null.");
    }

    private static IRpcRuntimeContext GetEndpointRuntimeContext(object endpoint)
    {
        if (endpoint is IRpcChannel channel)
            return channel.RuntimeContext;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        return endpoint.GetType().GetField("_runtimeContext", flags)?.GetValue(endpoint) as IRpcRuntimeContext
            ?? throw new InvalidOperationException($"Runtime context was not available from '{endpoint.GetType()}'.");
    }

    private static string GetProjectOutputDirectory(string projectName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sharplink.slnx")))
            directory = directory.Parent;
        if (directory is null)
            throw new DirectoryNotFoundException("SharpLink workspace root was not found.");
        return Path.Combine(
            directory.FullName,
            "test",
            projectName,
            "bin",
            "Release",
            "net10.0");
    }
}
