namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    public async Task CooperativeMaintenanceWaitsForPhysicalClientProducerDisposal()
    {
        var retained = await CompleteProducerMaintenanceAsync();
        await using var harness = retained.Harness;
        for (var attempt = 0; attempt < 20 && retained.OldContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(10);
        }
        Ensure(!retained.OldContext.IsAlive, "completed producer maintenance receipt must release the old implementation");
        await harness.VerifyIdentityAndShapesAsync();
        GC.KeepAlive(retained.Operation);
        GC.KeepAlive(retained.Result);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(MaintenanceHarness Harness, WeakReference OldContext,
        ISharpLinkMaintenanceOperation Operation, SharpLinkMaintenanceResult Result)> CompleteProducerMaintenanceAsync()
    {
        var harness = await MaintenanceHarness.CreateAsync(separateOldImplementation: true);
        var producer = new MaintenanceDisposalGatedProducer();
        try
        {
            var call = InvokeValueTaskAsync<int>(harness.Held[0], harness.Plugin.ContractType,
                "ClientStreamAsync", producer, CancellationToken.None).AsTask();
            await producer.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(await call.WaitAsync(TimeSpan.FromSeconds(10)) == 7,
                "the reply remains observable before physical input disposal");
            await WaitForMaintenanceAsync(() => harness.Captured.SelectMany(static group => group)
                .All(static connection => connection.ActiveCallCount == 0 && connection.Session.StreamManager.ActiveStreamCount == 0));
            Ensure(((SharpLinkClient)harness.Clients[0]).FrameworkTaskSnapshotForDiagnostics.Operations
                .Any(static task => task.Operation == "ClientStreamingProducer"),
                "the whole producer group is still physically supervised after pending completion");
            var oldModule = GetDynamicModule(harness.Server, harness.SourceAssembly);
            var operation = harness.Begin();
            await WaitForMaintenanceAsync(() => harness.Clients.All(static client => ((SharpLinkClient)client).ReadyConnectionCount == 0));
            await Task.WhenAny(operation.WaitAsync(), Task.Delay(150));
            Ensure(!operation.WaitAsync().IsCompleted && oldModule.State == SharpLinkDynamicModuleState.Running,
                "the frozen cohort must not retire old implementation ownership while producer disposal remains held");
            producer.Release.TrySetResult();
            await producer.DisposalFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await operation.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Ensure(result.Stage == SharpLinkMaintenanceStage.Succeeded, $"producer disposal releases maintenance: {result.Failure}");
            await harness.VerifyIdentityAndShapesAsync();
            return (harness, harness.DetachOldImplementation(), operation, result);
        }
        catch { producer.Release.TrySetResult(); await harness.DisposeAsync(); throw; }
        finally { producer.Release.TrySetResult(); }
    }

    private sealed class MaintenanceDisposalGatedProducer : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal readonly TaskCompletionSource DisposalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DisposalFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _sent;
        public int Current => 7;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync()
        {
            var next = !_sent;
            _sent = true;
            return ValueTask.FromResult(next);
        }
        public async ValueTask DisposeAsync()
        {
            DisposalEntered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            DisposalFinished.TrySetResult();
        }
    }
}
