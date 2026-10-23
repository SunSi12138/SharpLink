namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    public async Task CooperativeMaintenanceAbandonedImmutableSerializerCannotPublishAfterResume()
    {
        await using var harness = await MaintenanceHarness.CreateAsync();
        Ensure(SharpLinkAssemblyManifestLoader.TryLoad(harness.Plugin.ContractAssembly, out var manifest).Succeeded,
            "immutable contract descriptor");
        var contract = manifest!.Contracts.Single(item => item.ContractType == harness.Plugin.ContractType);
        var method = contract.Methods.Single(item => item.Name == "BlockIgnoringCancellationAsync");
        var descriptor = new RpcMethodDescriptor(contract.ContractId, method.MethodId, RpcMethodKind.Unary,
            HasResponsePayload: true, HasClientStreams: false, HasMethodTimeout: false, MethodTimeout: null);
        var module = GetDynamicModule(harness.Clients[0], harness.Plugin.ContractAssembly);
        IRpcChannel channel = new SharpLinkModuleRpcChannel((SharpLinkClient)harness.Clients[0], module);
        using var codec = new MaintenanceBlockingEmptyCodec();
        using var cancelled = new CancellationTokenSource();
        var invocation = Task.Factory.StartNew(() => channel.InvokeUnaryAsync(descriptor, default(RpcEmptyRequest),
            codec, channel.RuntimeContext.Codecs.GetCodec<int>(), null, cancelled.Token).AsTask(),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        try
        {
            await codec.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(!codec.UsedThreadPool, "deliberately blocked codec must not starve shared continuation workers");
            cancelled.Cancel();
            await WaitForMaintenanceAsync(() => harness.Captured.SelectMany(static items => items)
                .All(static connection => connection.ActiveCallCount == 0 && connection.CallAdmissionReservationCount == 0));
            var result = await harness.Begin().WaitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Ensure(result.Stage == SharpLinkMaintenanceStage.Succeeded, $"cancelled pending identity permits scoped maintenance: {result.Failure}");
            Ensure(module.RemainingCalls > 0 && !codec.Completed,
                "immutable client physical codec ownership may remain without owning the retired server implementation");
            codec.Release.Set();
            Exception? failure = null;
            try { await invocation.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception exception) { failure = exception; }
            Ensure(failure is OperationCanceledException, $"late serializer observes its already selected cancellation: {failure}");
            Ensure(!harness.Plugin.GetStaticTask("BlockStarted").IsCompleted &&
                !((Task)harness.TargetService.GetProperty("BlockStarted")!.GetValue(null)!).IsCompleted,
                "late old request cannot publish on either implementation after resume");
            await harness.VerifyIdentityAndShapesAsync();
        }
        finally
        {
            codec.Release.Set();
            try { await invocation.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CooperativeMaintenanceOptInRejectsInFlightClientCatalogPreparation(bool replacement)
    {
        using var plugin = PluginBundle.Load($"maintenance-client-prepare-{Guid.NewGuid()}", loadService: false);
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTcp(IPAddress.Loopback.ToString(), 1).Build();
        if (replacement) Ensure(client.RegisterAssembly(plugin.ContractAssembly).Succeeded, "existing client contract");
        using var gated = new MaintenanceMetadataGateAssembly(plugin.ContractAssembly);
        var writer = Task.Factory.StartNew(() => replacement
            ? client.ReplaceAssemblyAsync(plugin.ContractAssembly, gated, TimeSpan.Zero).AsTask().GetAwaiter().GetResult().Succeeded
            : client.RegisterAssembly(gated).Succeeded,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var rejected = false;
            try { client.EnableCooperativeMaintenance(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())); }
            catch (InvalidOperationException) { rejected = true; }
            Ensure(rejected, "opt-in cannot cross an already admitted metadata/candidate writer");
        }
        finally { gated.Release.Set(); await writer.WaitAsync(TimeSpan.FromSeconds(10)); }
        Ensure(!await writer, "the wrapper has no separately owned manifest and must fail normal identity validation");
        client.EnableCooperativeMaintenance(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
    }

    private sealed class MaintenanceMetadataGateAssembly(System.Reflection.Assembly inner) : System.Reflection.Assembly, IDisposable
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        public override string? FullName => inner.FullName;
        public override System.Reflection.AssemblyName GetName() => inner.GetName();
        public override IList<System.Reflection.CustomAttributeData> GetCustomAttributesData()
        {
            Entered.TrySetResult();
            Release.Wait();
            return inner.GetCustomAttributesData();
        }
        public void Dispose() => Release.Dispose();
    }

    private sealed class MaintenanceBlockingEmptyCodec : IRpcCodec<RpcEmptyRequest>, IDisposable
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        internal bool UsedThreadPool;
        internal bool Completed;
        public void Serialize(in RpcEmptyRequest value, IBufferWriter<byte> buffer)
        {
            UsedThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            Entered.TrySetResult();
            Release.Wait();
            RpcEmptyRequestCodec.Instance.Serialize(value, buffer);
            Completed = true;
        }
        public RpcEmptyRequest Deserialize(in ReadOnlySequence<byte> buffer) => RpcEmptyRequestCodec.Instance.Deserialize(buffer);
        public void Dispose() => Release.Dispose();
    }
}
