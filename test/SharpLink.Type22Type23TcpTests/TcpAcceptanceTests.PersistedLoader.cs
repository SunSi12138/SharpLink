using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.Type22Type23TcpTests;

public sealed partial class TcpAcceptanceTests
{
    [Test]
    public async Task PersistedGeneratedPluginUsesProductionRegistrationAndSameReceiverPreparationOverTcp()
    {
        await using var services = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        // Build before loading: the plugin must enter through real RegisterAssembly, not a static catalog snapshot.
        var harness = new Harness(services: services);
        PersistedPlugin? plugin = null;
        try
        {
            await using (harness)
            {
                plugin = new PersistedPlugin();
                Ensure(harness.Client.RegisterAssembly(plugin.Contracts).Succeeded, "production client contract registration");
                Ensure(harness.Server.RegisterAssembly(plugin.Contracts).Succeeded, "production server contract registration");
                Ensure(harness.Server.RegisterAssembly(plugin.Services).Succeeded, "production server generated service registration");
                await harness.ConnectAsync();
                var task = (Task)typeof(TcpAcceptanceTests).GetMethod(nameof(ExercisePluginAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(plugin.ContractType).Invoke(null, new object[] { harness, plugin.ContractType, plugin.ServiceType })!;
                await task.WaitAsync(Budget);
                await harness.AssertQuiescentAsync();
                Ensure(harness.Accepted == 2, "generated plugin clientstream/duplex ACK; unary/oneway/serverstream/NotifyStream do not ACK");
                AssertReleased(await harness.Server.UnregisterAssemblyAsync(plugin.Services, Budget), "server service");
                AssertReleased(await harness.Server.UnregisterAssemblyAsync(plugin.Contracts, Budget), "server contract");
                AssertReleased(await harness.Client.UnregisterAssemblyAsync(plugin.Contracts, Budget), "client contract");
            }
        }
        finally { plugin?.Dispose(); }
        // The collectible ALC owner outlives all physical harness cleanup, even on an assertion failure.
    }

    private static async Task ExercisePluginAsync<TContract>(Harness harness, Type contract, Type serviceType)
        where TContract : IService
    {
        var proxy = harness.Client.CreateProxy<TContract>();
        await proxy.PrepareAsync(Budget);
        object Invoke(string name, params object?[] args) => contract.GetMethod(name)!.Invoke(proxy.Service, args)
            ?? throw new InvalidOperationException($"{name} returned null.");
        Ensure(await (ValueTask<int>)Invoke("UnaryAsync", 10, CancellationToken.None) == 11, "persisted generated unary");
        await (ValueTask)Invoke("NotifyAsync", 7, CancellationToken.None);
        Ensure(await (ValueTask<int>)Invoke("ClientStreamAsync", new Producer(2, 3), CancellationToken.None) == 5, "persisted generated clientstream");
        Ensure((await CollectAsync((IAsyncEnumerable<int>)Invoke("ServerStreamAsync", 3, CancellationToken.None)))
            .SequenceEqual(new[] { 0, 1, 2 }), "persisted generated serverstream");
        Ensure((await CollectAsync((IAsyncEnumerable<int>)Invoke("DuplexAsync", new Producer(2, 3), CancellationToken.None)))
            .SequenceEqual(new[] { 4, 6 }), "persisted generated duplex");
        await (ValueTask)Invoke("NotifyStreamAsync", new Producer(1, 2), CancellationToken.None);
        await WaitUntilAsync(() => (int)serviceType.GetProperty("Notifications")!.GetValue(null)! == 10,
            "persisted generated plain/streamed OneWay business values");
    }

    private static void AssertReleased(SharpLinkAssemblyUnregisterResult result, string owner)
        => Ensure(result.ReferencesReleased && result.RemainingCalls == 0 && result.RemainingStreams == 0,
            $"{owner} returned actual production module references after quiescence");

    private sealed class PersistedPlugin : AssemblyLoadContext, IDisposable
    {
        private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "type22-type23-payloads");
        internal Assembly Contracts { get; }
        internal Assembly Services { get; }
        internal Type ContractType { get; }
        internal Type ServiceType { get; }
        internal PersistedPlugin() : base("type22-type23-persisted-generated-" + Guid.NewGuid().ToString("N"), isCollectible: true)
        {
            try
            {
                Contracts = LoadOwned("SharpLink.DynamicPlugin.Contracts");
                Services = LoadOwned("SharpLink.DynamicPlugin.Services");
                ContractType = Contracts.GetType("SharpLink.DynamicPlugin.IDynamicPluginService", throwOnError: true)!;
                ServiceType = Services.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            }
            catch { Unload(); throw; }
        }
        private Assembly LoadOwned(string name)
        {
            // Real persisted bytes generated by the separately referenced fixture projects.
            using var bytes = File.OpenRead(Path.Combine(_directory, name + ".dll"));
            return LoadFromStream(bytes);
        }
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is "SharpLink.DynamicPlugin.Contracts" or "SharpLink.DynamicPlugin.Services")
                return Assemblies.FirstOrDefault(candidate => AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), name))
                    ?? LoadOwned(name.Name);
            // Share host runtime, SDK, serializer and framework identities; never a second SharpLink ABI.
            return Default.Assemblies.FirstOrDefault(candidate => AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), name));
        }
        public void Dispose() => Unload();
    }
}
