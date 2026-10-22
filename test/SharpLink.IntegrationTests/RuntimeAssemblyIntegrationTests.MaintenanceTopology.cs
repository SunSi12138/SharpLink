namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    public async Task CooperativeMaintenanceReconnectsOutsideTransactionAndRejectsNewPeerWhileFrozen()
    {
        await using var harness = await MaintenanceHarness.CreateAsync();
        var disconnected = harness.Captured[0][0];
        disconnected.Session.NotifyDisconnected(new IOException("planned outside-maintenance disconnect"));
        await WaitForMaintenanceAsync(() =>
        {
            var ready = CaptureMaintenanceConnections(harness.Clients[0]);
            return ready.Length == 2 && !ready.Contains(disconnected);
        });
        await WaitForMaintenanceAsync(() =>
        {
            try { return ((SharpLinkServer)harness.Server).CaptureMaintenanceCohort().Length == 4; }
            catch (SharpLinkException) { return false; }
        });
        harness.Captured = harness.Clients.Select(CaptureMaintenanceConnections).ToArray();
        var oldCall = InvokeValueTaskAsync<int>(harness.Held[0], harness.Plugin.ContractType,
            "BlockIgnoringCancellationAsync", CancellationToken.None).AsTask();
        await harness.Plugin.GetStaticTask("BlockStarted").WaitAsync(TimeSpan.FromSeconds(10));
        var operation = harness.Begin();
        await WaitForMaintenanceAsync(() => harness.Clients.All(static client => ((SharpLinkClient)client).ReadyConnectionCount == 0));
        await using (var additional = harness.CreateClient(harness.Roster[0]))
            await AssertMaintenanceConnectRejectedAsync(additional);
        Ensure(!operation.WaitAsync().IsCompleted, "new peer quarantine must not release or abort admitted old work");
        harness.Plugin.ReleaseBlock();
        Ensure(await oldCall.WaitAsync(TimeSpan.FromSeconds(10)) == 43, "old call finishes on the fixed cohort");
        var result = await operation.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Ensure(result.Stage == SharpLinkMaintenanceStage.Succeeded, $"new connection rejected without corrupting cohort: {result.Failure}");
        await harness.VerifyIdentityAndShapesAsync();
    }

    [Test]
    [Arguments("capability")]
    [Arguments("subject")]
    [Arguments("incarnation")]
    public async Task CooperativeMaintenanceRejectsUnenrolledOrMismatchedPeers(string mismatch)
    {
        await using var harness = await MaintenanceHarness.CreateAsync();
        var participant = harness.Roster[0];
        if (mismatch == "subject") participant = participant with { AuthenticatedSubject = "not-in-roster" };
        if (mismatch == "incarnation") participant = participant with { RuntimeIncarnation = Guid.NewGuid() };
        await using (var additional = harness.CreateClient(participant, enroll: mismatch != "capability"))
            await AssertMaintenanceConnectRejectedAsync(additional);
        await WaitForMaintenanceAsync(() =>
        {
            try { return ((SharpLinkServer)harness.Server).CaptureMaintenanceCohort().Length == 4; }
            catch (SharpLinkException) { return false; }
        });
        await harness.VerifyIdentityAndShapesAsync();
        var result = await harness.Begin().WaitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Ensure(result.Stage == SharpLinkMaintenanceStage.Succeeded, $"rejected peer leaves whole configured domain usable: {result.Failure}");
        await harness.VerifyIdentityAndShapesAsync();
    }

    [Test]
    public async Task CooperativeMaintenanceStopOwnsConcurrentOperationAndRejectsLaterStart()
    {
        await using var harness = await MaintenanceHarness.CreateAsync();
        var operation = harness.Begin();
        await harness.Server.StopAsync(TimeSpan.Zero).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Ensure(operation.WaitAsync().IsCompleted, "Stop must join the registered maintenance terminal owner");
        var result = await operation.WaitAsync();
        Ensure(result.Stage is SharpLinkMaintenanceStage.RejectedBeforeSeal or SharpLinkMaintenanceStage.FailedClosed or SharpLinkMaintenanceStage.Succeeded,
            "concurrent operation has a terminal receipt");
        var rejected = false;
        try { harness.Begin(); }
        catch (InvalidOperationException) { rejected = true; }
        Ensure(rejected, "no post-Stop operation starts candidate preparation without an owned handle");
    }

    [Test]
    public async Task CooperativeMaintenanceRejectsPreviousOperationAckOnNextGeneration()
    {
        await using var harness = await MaintenanceHarness.CreateAsync();
        CooperativeMaintenanceMessage? previous = null;
        ((SharpLinkClient)harness.Clients[0]).MaintenanceControlSendBarrierForTesting = (message, _) =>
        {
            if (message.Stage == CooperativeMaintenanceStage.IngressFence) previous ??= message;
            return Task.CompletedTask;
        };
        Ensure((await harness.Begin().WaitAsync().WaitAsync(TimeSpan.FromSeconds(15))).Stage == SharpLinkMaintenanceStage.Succeeded,
            "first generation commits");
        await harness.VerifyIdentityAndShapesAsync();
        var nextContext = new MaintenanceReplacementContext(harness.Plugin.ContractAssembly);
        try
        {
            var nextAssembly = nextContext.LoadFromAssemblyPath(harness.Replacement.Location);
            var injected = 0;
            ((SharpLinkClient)harness.Clients[0]).MaintenanceControlSendBarrierForTesting = async (message, session) =>
            {
                if (message.Stage == CooperativeMaintenanceStage.IngressFence && Interlocked.Exchange(ref injected, 1) == 0)
                    await session.SendMaintenanceAsync(previous!.Value);
            };
            var generation = harness.Controller.CatalogGeneration;
            var operation = harness.Controller.BeginMaintenance(generation, [new(harness.Replacement, nextAssembly)]);
            var result = await operation.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Ensure(result.Stage == SharpLinkMaintenanceStage.FailedClosed && harness.Controller.CatalogGeneration == generation,
                "previous operation ACK cannot authorize a second-generation commit");
        }
        finally { nextContext.Unload(); }
    }

    [Test]
    public async Task CooperativeMaintenanceReentrantMetadataCannotReplaceActiveAuthority()
    {
        await using var harness = await MaintenanceHarness.CreateAsync();
        var oldCall = InvokeValueTaskAsync<int>(harness.Held[0], harness.Plugin.ContractType,
            "BlockIgnoringCancellationAsync", CancellationToken.None).AsTask();
        await harness.Plugin.GetStaticTask("BlockStarted").WaitAsync(TimeSpan.FromSeconds(10));
        ISharpLinkMaintenanceOperation? nested = null;
        var source = new MaintenanceReentrantAssembly(harness.SourceAssembly, () => nested = harness.Begin());
        var rejected = false;
        try { harness.Controller.BeginMaintenance(harness.Controller.CatalogGeneration, [new(source, harness.Replacement)]); }
        catch (InvalidOperationException) { rejected = true; }
        Ensure(rejected && nested is not null, "a nested metadata callback winner cannot be overwritten by the outer operation");
        harness.Plugin.ReleaseBlock();
        await oldCall.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await nested!.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Ensure(result.Stage == SharpLinkMaintenanceStage.Succeeded, $"the sole authority completes normally: {result.Failure}");
        await harness.VerifyIdentityAndShapesAsync();
    }

    private sealed class MaintenanceReentrantAssembly(System.Reflection.Assembly inner, Action callback) : System.Reflection.Assembly
    {
        public override System.Reflection.Module ManifestModule { get { callback(); return inner.ManifestModule; } }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CooperativeMaintenanceRequiresActuallyNegotiatedTls(bool maintenance)
    {
        var participant = new SharpLinkMaintenanceParticipant(Guid.NewGuid(), Guid.NewGuid(), "raw-tcp-control");
        var domain = Guid.NewGuid();
        var builder = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTcp(0, IPAddress.Loopback)
            .UseAuthenticator(SharpLinkAuthenticator.CreateServer((_, _) =>
                ValueTask.FromResult(SharpLinkAuthenticationResult.Authenticate(new SharpLinkAuthenticationContext(participant.AuthenticatedSubject)))))
            .RequireAuthentication();
        var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
        await using var server = builder.Build();
        if (maintenance) server.EnableCooperativeMaintenance(domain, [participant]);
        await server.StartAsync();
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).UseTcp(IPAddress.Loopback.ToString(), port)
            .UseAuthenticator(SharpLinkAuthenticator.CreateClient(_ => ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 })))
            .Build();
        if (maintenance) client.EnableCooperativeMaintenance(new(domain, participant.ParticipantId, participant.RuntimeIncarnation));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Exception? failure = null;
        try { await client.ConnectAsync(timeout.Token); }
        catch (Exception exception) { failure = exception; }
        if (maintenance)
            Ensure(failure is not null and not OperationCanceledException && ((SharpLinkClient)client).ReadyConnectionCount == 0,
                $"authenticated raw TCP cannot acquire maintenance capability: {failure}");
        else
            Ensure(failure is null && ((SharpLinkClient)client).ReadyConnectionCount == 1,
                $"ordinary authenticated raw TCP remains supported: {failure}");
    }

    private static async Task AssertMaintenanceConnectRejectedAsync(ISharpLinkClient client)
    {
        Exception? failure = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await client.ConnectAsync(timeout.Token); }
        catch (Exception exception) { failure = exception; }
        Ensure(failure is not null && failure is not OperationCanceledException, $"peer must be rejected by protocol, not timeout: {failure}");
        Ensure(((SharpLinkClient)client).ReadyConnectionCount == 0, "rejected peer never publishes Ready");
    }
}
