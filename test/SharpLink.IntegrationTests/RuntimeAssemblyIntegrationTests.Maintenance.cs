namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CooperativeMaintenanceKeepsTwoRuntimesAndFourTlsSessions(bool cancelOldInvocation)
    {
        await using var harness = await MaintenanceHarness.CreateAsync();
        await harness.VerifyIdentityAndShapesAsync();
        using var cancellation = new CancellationTokenSource();
        var oldCall = InvokeValueTaskAsync<int>(harness.Held[0], harness.Plugin.ContractType,
            "BlockIgnoringCancellationAsync", cancellation.Token).AsTask();
        await harness.Plugin.GetStaticTask("BlockStarted").WaitAsync(TimeSpan.FromSeconds(10));
        var operation = harness.Begin();
        await WaitForMaintenanceAsync(() => harness.Clients.All(static client => ((SharpLinkClient)client).ReadyConnectionCount == 0));
        using var cancelledObserver = new CancellationTokenSource();
        cancelledObserver.Cancel();
        try
        {
            await operation.WaitAsync(cancelledObserver.Token);
            throw new InvalidOperationException("Cancelled observation unexpectedly succeeded.");
        }
        catch (OperationCanceledException) { }
        Ensure(!operation.WaitAsync().IsCompleted, "wait cancellation must not terminate maintenance");
        Exception? frozenFailure = null;
        try { await InvokeValueTaskAsync<int>(harness.Held[1], harness.Plugin.ContractType, "UnaryAsync", 1, CancellationToken.None); }
        catch (Exception exception) { frozenFailure = exception; }
        Ensure(frozenFailure is SharpLinkException { Code: SharpLinkErrorCode.Unavailable },
            $"ordinary calls while frozen use the existing readiness failure: {frozenFailure}");
        if (cancelOldInvocation)
        {
            cancellation.Cancel();
            try { await oldCall.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
            await WaitForMaintenanceAsync(() => operation.Stage == SharpLinkMaintenanceStage.Draining);
            Ensure(harness.Plugin.GetStaticInt("Disposed") == 0, "old disposal waits abandoned physical invocation");
            await using var independent = await MaintenanceHarness.CreateAsync();
            await independent.VerifyIdentityAndShapesAsync();
        }
        Ensure(!operation.WaitAsync().IsCompleted, "blocked old invocation prevents success");
        harness.Plugin.ReleaseBlock();
        if (!cancelOldInvocation)
            Ensure(await oldCall.WaitAsync(TimeSpan.FromSeconds(10)) == 43, "old invocation completes before replacement");
        var result = await operation.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Ensure(result.Stage == SharpLinkMaintenanceStage.Succeeded, $"maintenance success: {result.Failure}");
        await harness.VerifyIdentityAndShapesAsync();
        Ensure(harness.Plugin.GetStaticInt("Disposed") > 0, "old implementation physically disposed");
        Ensure((int)harness.TargetService.GetProperty("Created")!.GetValue(null)! > 0, "held old proxy invokes new implementation");
    }

    [Test]
    public async Task CooperativeMaintenancePartialCommitDisconnectFailsClosed()
    {
        await using var harness = await MaintenanceHarness.CreateAsync();
        var blockedAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((SharpLinkClient)harness.Clients[1]).MaintenanceControlSendBarrierForTesting = async (message, _) =>
        {
            if (message.Stage != CooperativeMaintenanceStage.Committed) return;
            blockedAck.TrySetResult();
            await releaseAck.Task;
        };
        try
        {
            var originalGeneration = harness.Controller.CatalogGeneration;
            var operation = harness.Begin();
            await blockedAck.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Ensure(operation.Stage == SharpLinkMaintenanceStage.CommittedPaused &&
                harness.Controller.CatalogGeneration > originalGeneration, "replacement is committed but no resume is allowed");
            Ensure(harness.Captured.SelectMany(static items => items).All(static connection =>
                connection.State == ClientConnectionState.MaintenancePaused), "all participants remain unavailable");
            harness.Captured[0][0].Session.NotifyDisconnected(new IOException("deterministic partial-commit peer loss"));
            var result = await operation.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(result.Stage == SharpLinkMaintenanceStage.FailedClosed, "partial commit cannot roll back or reopen");
            await WaitForMaintenanceAsync(() => harness.Clients.All(static client => ((SharpLinkClient)client).ReadyConnectionCount == 0));
            Ensure(harness.Captured.SelectMany(static items => items).All(static connection => !connection.CanAcceptCalls), "every domain session stays closed");
        }
        finally { releaseAck.TrySetResult(); }
    }

    [Test]
    [Arguments("operation")]
    [Arguments("digest")]
    [Arguments("runtime")]
    [Arguments("session")]
    [Arguments("replay")]
    public async Task CooperativeMaintenanceRejectsForgedOrReplayedAcknowledgement(string mismatch)
    {
        await using var harness = await MaintenanceHarness.CreateAsync();
        var injected = 0;
        ((SharpLinkClient)harness.Clients[0]).MaintenanceControlSendBarrierForTesting = async (message, session) =>
        {
            if (message.Stage != CooperativeMaintenanceStage.IngressFence || Interlocked.Exchange(ref injected, 1) != 0) return;
            var invalid = mismatch switch
            {
                "operation" => message with { Operation = Guid.NewGuid() },
                "digest" => message with { Digest = new(4, 3, 2, 1) },
                "runtime" => message with { Runtime = Guid.NewGuid() },
                "session" => message with { Session = Guid.NewGuid() },
                _ => message
            };
            // Replay injects an exact first ACK before the ordinary sender emits it again.
            await session.SendMaintenanceAsync(invalid);
        };
        var result = await harness.Begin().WaitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Ensure(result.Stage == SharpLinkMaintenanceStage.FailedClosed, $"{mismatch} must fail closed: {result.Failure}");
        Ensure(harness.Plugin.GetStaticInt("Disposed") == 0, "invalid evidence must not retire the old module");
    }

    [Test]
    public async Task CooperativeMaintenanceRejectsStaleCatalogBeforeFreeze()
    {
        await using var harness = await MaintenanceHarness.CreateAsync();
        var operation = harness.Controller.BeginMaintenance(harness.Controller.CatalogGeneration - 1,
            [new(harness.Plugin.ServiceAssembly, harness.Replacement)]);
        var result = await operation.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Ensure(result.Stage == SharpLinkMaintenanceStage.RejectedBeforeSeal, "stale host plan is rejected before freeze");
        await harness.VerifyIdentityAndShapesAsync();
        Ensure((await harness.Begin().WaitAsync().WaitAsync(TimeSpan.FromSeconds(15))).Stage == SharpLinkMaintenanceStage.Succeeded,
            "preflight rejection releases cold authority for the next valid operation");
    }
    [Test]
    public async Task CooperativeMaintenanceWaitsForPhysicalStreamDisposal()
    {
        await using var harness = await MaintenanceHarness.CreateAsync();
        harness.Plugin.InvokeServiceStatic(PhysicalProbeService, "Reset");
        var contract = harness.Plugin.GetContractType("SharpLink.DynamicPlugin.IPhysicalOwnershipProbe");
        var proxy = GetProxy(harness.Clients[0], contract);
        using var cancellation = new CancellationTokenSource();
        await using var reader = InvokeStream(proxy, contract, "DeadlineStreamAsync", cancellation.Token).GetAsyncEnumerator();
        Ensure(await reader.MoveNextAsync(), "initial stream element");
        await harness.Plugin.GetServiceStaticTask(PhysicalProbeService, "WorkStarted").WaitAsync(TimeSpan.FromSeconds(10));
        var next = reader.MoveNextAsync().AsTask();
        var operation = harness.Begin();
        await WaitForMaintenanceAsync(() => harness.Clients.All(static client => ((SharpLinkClient)client).ReadyConnectionCount == 0));
        cancellation.Cancel();
        try { await next.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
        await WaitForMaintenanceAsync(() => operation.Stage == SharpLinkMaintenanceStage.Draining);
        Ensure(!operation.WaitAsync().IsCompleted, "non-cooperative MoveNext remains owned");
        harness.Plugin.InvokeServiceStatic(PhysicalProbeService, "ReleaseWork");
        await harness.Plugin.GetServiceStaticTask(PhysicalProbeService, "DisposeStarted").WaitAsync(TimeSpan.FromSeconds(10));
        Ensure(harness.Plugin.GetServiceStaticInt(PhysicalProbeService, "ConcurrentDispose") == 0, "no MoveNext/disposal overlap");
        Ensure(!operation.WaitAsync().IsCompleted && operation.Stage == SharpLinkMaintenanceStage.Draining,
            "asynchronous iterator disposal retains the complete maintenance cut");
        harness.Plugin.InvokeServiceStatic(PhysicalProbeService, "ReleaseDispose");
        var result = await operation.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Ensure(result.Stage == SharpLinkMaintenanceStage.Succeeded, $"stream disposal update: {result.Failure}");
        await harness.VerifyIdentityAndShapesAsync();
    }

    [Test]
    public async Task CooperativeMaintenanceAuthenticationExpiryDuringDrainFailsClosed()
    {
        await using var harness = await MaintenanceHarness.CreateAsync(expiringAuthentication: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = (SharpLinkServer)harness.Server;
        server.ContractManifestPublishBarrierForTesting = async () => { entered.TrySetResult(); await release.Task; };
        typeof(SharpLinkServer).GetMethod("ScheduleContractManifestPublish",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(server, null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var cancellation = new CancellationTokenSource();
            var oldCall = InvokeValueTaskAsync<int>(harness.Held[0], harness.Plugin.ContractType,
                "BlockIgnoringCancellationAsync", cancellation.Token).AsTask();
            await harness.Plugin.GetStaticTask("BlockStarted").WaitAsync(TimeSpan.FromSeconds(10));
            var operation = harness.Begin();
            cancellation.Cancel();
            try { await oldCall.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
            await WaitForMaintenanceAsync(() => operation.Stage == SharpLinkMaintenanceStage.Draining);
            harness.Clock.Advance(TimeSpan.FromHours(2));
            var result = await operation.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(result.Stage == SharpLinkMaintenanceStage.FailedClosed &&
                result.Failure is SharpLinkException { Code: SharpLinkErrorCode.AuthenticationExpired },
                $"expired participants cannot authorize retirement/resume: {result.Failure}");
            Ensure(harness.Plugin.GetStaticInt("Disposed") == 0, "expiry must not retire the old implementation");
        }
        finally
        {
            server.ContractManifestPublishBarrierForTesting = null;
            release.TrySetResult();
        }
    }

    [Test]
    public async Task CooperativeMaintenanceOptInSealsCatalogAndConfigurationWriters()
    {
        await using var harness = await MaintenanceHarness.CreateAsync();
        var client = harness.Clients[0];
        Ensure(!client.TryUpdateFixedConnectionPoolSizing(1, 2).Succeeded, "Try pool writer closed");
        Ensure(!client.TryUpdateHeartbeat(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)).Succeeded, "Try heartbeat writer closed");
        var rejected = false;
        try { client.UpdateFixedConnectionPoolSizing(1, 2); }
        catch (InvalidOperationException) { rejected = true; }
        Ensure(rejected, "throwing pool writer closed");
        rejected = false;
        try { await client.UnregisterAssemblyAsync(harness.Plugin.ContractAssembly, TimeSpan.Zero); }
        catch (InvalidOperationException) { rejected = true; }
        Ensure(rejected, "independent client unregister writer closed");
        Ensure(!client.RegisterAssembly(harness.Plugin.ContractAssembly).Succeeded, "client register writer closed");
        Ensure(!harness.Server.RegisterAssembly(harness.Plugin.ServiceAssembly).Succeeded, "server registry writer closed");
        await harness.VerifyIdentityAndShapesAsync();
    }

    [Test]
    public async Task CooperativeMaintenanceOptInRejectsTimedOutPhysicalCatalogRetirement()
    {
        using var plugin = PluginBundle.Load($"maintenance-client-retirement-{Guid.NewGuid()}", loadService: false);
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTcp(IPAddress.Loopback.ToString(), 1).Build();
        Ensure(client.RegisterAssembly(plugin.ContractAssembly).Succeeded, "register client-only immutable contract candidate");
        var module = GetDynamicModule(client, plugin.ContractAssembly);
        Ensure(module.TryAcquire(stream: false, out var physicalLease), "existing physical contract owner");
        try
        {
            var retirement = await client.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.Zero);
            Ensure(!retirement.ReferencesReleased, "public timeout is not physical release");
            var rejected = false;
            try { client.EnableCooperativeMaintenance(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())); }
            catch (InvalidOperationException) { rejected = true; }
            Ensure(rejected, "opt-in must reject background catalog retirement after public timeout");
        }
        finally { physicalLease.Dispose(); }
        await WaitForMaintenanceAsync(() => module.State == SharpLinkDynamicModuleState.Released);
        client.EnableCooperativeMaintenance(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
    }


    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CooperativeMaintenanceCompletedReceiptReleasesOldImplementationWhileClientsStayLive(bool callbackTail)
    {
        var retained = await CompleteCollectibleMaintenanceAsync(callbackTail);
        await using var harness = retained.Harness;
        for (var attempt = 0; attempt < 20 && retained.OldContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(10);
        }
        Ensure(!retained.OldContext.IsAlive, "retained public operation/result and live immutable contracts must not root old implementation");
        await harness.VerifyIdentityAndShapesAsync();
        GC.KeepAlive(retained.Operation);
        GC.KeepAlive(retained.Result);
        GC.KeepAlive(harness.Plugin.ContractAssembly);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<(MaintenanceHarness Harness, WeakReference OldContext,
        ISharpLinkMaintenanceOperation Operation, SharpLinkMaintenanceResult Result)> CompleteCollectibleMaintenanceAsync(bool callbackTail)
    {
        var harness = await MaintenanceHarness.CreateAsync(separateOldImplementation: true,
            requestTimeout: callbackTail ? TimeSpan.FromHours(1) : null);
        try
        {
            await harness.VerifyIdentityAndShapesAsync();
            var operation = callbackTail ? await CompleteCallbackMaintenanceAsync(harness) : harness.Begin();
            var result = await operation.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Ensure(result.Stage == SharpLinkMaintenanceStage.Succeeded, $"collectible public operation: {result.Failure}");
            await harness.VerifyIdentityAndShapesAsync();
            return (harness, harness.DetachOldImplementation(), operation, result);
        }
        catch { await harness.DisposeAsync(); throw; }
    }
}
