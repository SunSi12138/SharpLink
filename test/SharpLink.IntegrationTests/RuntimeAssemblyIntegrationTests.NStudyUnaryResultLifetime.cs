using System.Reflection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyAwaitedUnaryDoesNotKeepTheCollectibleResultAssembly(string transport)
        => VerifyCollectibleUnaryResultAsync(transport, intercept: false);

    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyInterceptedUnaryDoesNotKeepTheCollectibleResultAssembly(string transport)
        => VerifyCollectibleUnaryResultAsync(transport, intercept: true);

    private static async Task VerifyCollectibleUnaryResultAsync(string transport, bool intercept)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = budget.Token;
        await using var harness = await DynamicHarness.CreateAsync(sharedMemory: transport == "shared-memory");
        var session = NStudyClientSession((SharpLinkClient)harness.Client);
        var directory = GetProjectOutputDirectory("SharpLink.CodecPublicationDrainProvider");
        var path = Path.Combine(directory, "SharpLink.CodecPublicationDrainProvider.dll");
        var serverContext = new PluginLoadContext($"unary-result-server-{transport}", directory);
        try
        {
            var provider = serverContext.LoadFromAssemblyPath(path);
            Ensure(harness.Server.RegisterAssembly(provider).Succeeded, "the server registers its own matching assembly");
            var retired = await RoundTripAndRetireUnaryResultAsync(harness, directory, path, intercept, ct)
                .ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            for (var i = 0; retired.IsAlive && i < 24; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                if (retired.IsAlive)
                    await Task.Delay(25, ct);
            }
            Ensure(!retired.IsAlive && ReferenceEquals(NStudyClientSession((SharpLinkClient)harness.Client), session),
                "the completed logical RPC must not keep the client assembly alive or retire its physical Session");
        }
        finally { serverContext.Unload(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RoundTripAndRetireUnaryResultAsync(
        DynamicHarness harness, string directory, string path, bool intercept, CancellationToken ct)
    {
        var loader = new PluginLoadContext("typed-unary-result-client", directory);
        var hold = new UnaryResultHold();
        var forwarder = intercept ? new UnaryResultForwarder() : null;
        try
        {
            var assembly = loader.LoadFromAssemblyPath(path);
            var contract = FeedType(assembly, "IDrainService");
            var payload = FeedType(assembly, "DrainPayload");
            Ensure(payload.IsCollectible && harness.Client.RegisterAssembly(assembly).Succeeded,
                "a separate collectible client registers the actual result type");
            var proxy = CreatePreparedGeneratedProxy(harness.Client, contract);
            await proxy.PrepareAsync(ct);
            if (forwarder is not null)
                harness.Client.ReplaceInterceptors([forwarder]);
            harness.Server.ReplaceInterceptors([hold]);
            var read = (Task<int>)typeof(RuntimeAssemblyIntegrationTests)
                .GetMethod(nameof(ReadCollectibleUnaryResultAsync), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(payload).Invoke(null, [proxy.Service, contract, Activator.CreateInstance(payload, 37)!, ct])!;
            await hold.Entered.Task.WaitAsync(ct);
            Ensure(!read.IsCompleted, "the result was genuinely asynchronous on the generated public proxy");
            hold.Release.TrySetResult();
            Ensure(await read.WaitAsync(ct) == 37, "the original generated codec returns the typed result");
            Ensure(forwarder is null || forwarder.Visits == 1,
                "the genuinely asynchronous intercepted call took the logical invocation wrapper");
            var retired = await harness.Client.UnregisterAssemblyAsync(assembly, TimeSpan.FromSeconds(5), ct);
            Ensure(retired.ReferencesReleased, "the original local registration retires after the call");
            return new WeakReference(loader);
        }
        finally
        {
            hold.Release.TrySetResult();
            harness.Server.ReplaceInterceptors([]);
            if (forwarder is not null)
                harness.Client.ReplaceInterceptors([]);
            loader.Unload();
        }
    }

    private static async Task<int> ReadCollectibleUnaryResultAsync<T>(
        object proxy, Type contract, object payload, CancellationToken ct)
    {
        var value = await InvokeValueTaskAsync<T>(proxy, contract, "EchoAsync", payload, ct);
        return (int)typeof(T).GetProperty("Value")!.GetValue(value)!;
    }

    private sealed class UnaryResultHold : ISharpLinkServerInterceptor
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

    private sealed class UnaryResultForwarder : ISharpLinkClientInterceptor
    {
        private int _visits;

        internal int Visits => Volatile.Read(ref _visits);

        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
        {
            Interlocked.Increment(ref _visits);
            return next(context);
        }
    }
}
