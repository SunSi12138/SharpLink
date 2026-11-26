using System.Reflection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AwaitedDynamicUnaryReleasesItsCollectibleResultAssembly(bool sharedMemory)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var harness = await DynamicHarness.CreateAsync(sharedMemory: sharedMemory);
        var directory = GetProjectOutputDirectory("SharpLink.CodecPublicationDrainProvider");
        var path = Path.Combine(directory, "SharpLink.CodecPublicationDrainProvider.dll");
        var serverContext = new PluginLoadContext("dynamic-unary-result-server", directory);
        try
        {
            var provider = serverContext.LoadFromAssemblyPath(path);
            Ensure(harness.Server.RegisterAssembly(provider).Succeeded, "the listener registers the matching contract");
            var retired = await RetireCompletedDynamicUnaryAsync(harness, directory, path, budget.Token)
                .ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            for (var i = 0; retired.IsAlive && i < 24; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                if (retired.IsAlive)
                    await Task.Delay(25, budget.Token);
            }
            Ensure(!retired.IsAlive, "the completed Unary must not root the old client load context");
        }
        finally { serverContext.Unload(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RetireCompletedDynamicUnaryAsync(
        DynamicHarness harness, string directory, string path, CancellationToken ct)
    {
        var clientContext = new PluginLoadContext("dynamic-unary-result-client", directory);
        var hold = new DynamicUnaryResultHold();
        try
        {
            var assembly = clientContext.LoadFromAssemblyPath(path);
            var contract = assembly.GetType("SharpLink.CodecPublicationDrainProvider.IDrainService", throwOnError: true)!;
            var payload = assembly.GetType("SharpLink.CodecPublicationDrainProvider.DrainPayload", throwOnError: true)!;
            Ensure(payload.IsCollectible && harness.Client.RegisterAssembly(assembly).Succeeded,
                "the client registers the actual collectible result type");
            await WaitForRemoteContractManifestAsync(harness.Client, contract);
            var proxy = GetProxy(harness.Client, contract);
            harness.Server.ReplaceInterceptors([hold]);
            var read = (Task<int>)typeof(RuntimeAssemblyIntegrationTests)
                .GetMethod(nameof(ReadDynamicUnaryEchoAsync), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(payload).Invoke(null, [proxy, contract, Activator.CreateInstance(payload, 37)!, ct])!;
            await hold.Entered.Task.WaitAsync(ct);
            Ensure(!read.IsCompleted, "the generated Unary genuinely suspended");
            hold.Release.TrySetResult();
            Ensure(await read.WaitAsync(ct) == 37, "the original codec returns the collectible value");
            var retired = await harness.Client.UnregisterAssemblyAsync(assembly, TimeSpan.FromSeconds(5), ct);
            Ensure(retired.ReferencesReleased, "the completed Unary releases its original client module lease");
            return new WeakReference(clientContext);
        }
        finally
        {
            hold.Release.TrySetResult();
            harness.Server.ReplaceInterceptors([]);
            clientContext.Unload();
        }
    }

    private static async Task<int> ReadDynamicUnaryEchoAsync<T>(
        object proxy, Type contract, object payload, CancellationToken ct)
    {
        var value = await InvokeValueTaskAsync<T>(proxy, contract, "EchoAsync", payload, ct);
        return (int)typeof(T).GetProperty("Value")!.GetValue(value)!;
    }

    private sealed class DynamicUnaryResultHold : ISharpLinkServerInterceptor
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(context.CancellationToken);
            await next(context);
        }
    }
}
