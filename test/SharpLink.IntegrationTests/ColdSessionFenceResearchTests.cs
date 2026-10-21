namespace SharpLink.IntegrationTests;

// Research only: these probes do not define a public cold-update API or change a production path.
public sealed partial class ColdSessionFenceResearchTests
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(10);

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SameSessionFenceMustNotMistakeSentOneWayForServerQuiescence(bool admission)
    {
        ColdFenceResearchService.Reset();
        await using var harness = await Harness.CreateAsync(admission);
        var service = harness.Client.Get<IColdFenceResearchService>();
        await service.NotifyAsync();
        await ColdFenceResearchService.Entered.WaitAsync(PhaseTimeout);
        await using var pause = await ResearchPause.AcquireAsync(harness.Client);
        try
        {
            await WaitForClientDrainAsync(harness.Connection);
            await FenceAsync(harness.Connection);
            Ensure(harness.Server.ActiveCallCountForDiagnostics == 1,
                "the FIFO delimiter acknowledges parsing, not delayed async OneWay completion");
            Ensure(!ColdFenceResearchService.Completed.IsCompleted,
                "the OneWay application body is still held after the fence");
            if (admission)
                Ensure(harness.Server.CurrentAdmissionProgramForTests!.ActiveUses == 1,
                    "the original admission use remains owned by the asynchronous OneWay");
            Ensure(!harness.Connection.TryReserveCallAdmission(out _),
                "the research pause must close ordinary selection admission");
            var drained = WaitForServerDrainAsync(harness.Server);
            Ensure(!drained.IsCompleted, "server quiescence must remain blocked by the old OneWay");
            ColdFenceResearchService.ReleaseWork();
            await drained;
            Ensure(ColdFenceResearchService.Completed.IsCompleted,
                "server drain must not precede the delayed OneWay's application completion");
        }
        finally
        {
            ColdFenceResearchService.ReleaseAll();
        }
        await pause.DisposeAsync();
        await AssertSameSessionUsableAsync(harness);
    }

    [Test]
    [NotInParallel]
    public async Task CurrentProgramAndActiveCallsCanBothBeZeroWhileRetiredQueuedWorkRemains()
    {
        ColdFenceResearchService.Reset();
        await using var harness = await Harness.CreateAsync(admission: true);
        var original = harness.Server.CurrentAdmissionProgramForTests!;
        var held = await original.Controller.AcquireAsync(
            new SharpLinkAdmissionContext(1, 2, RpcMethodKind.Unary, "cold-fence-research", null, null),
            1, allowQueue: false, CancellationToken.None);
        Ensure(held.IsAcquired, "the test must occupy the old admission limiter");
        try
        {
            await harness.Client.Get<IColdFenceResearchService>().NotifyAsync();
            await WaitUntilAsync(() => original.Controller.QueuedCalls == 1, "queued old-generation request");
            var replacement = harness.Server.CreateAdmissionProgramForTests(options =>
                options.Global.UseConcurrency(1));
            harness.Server.PublishAdmissionProgramForTests(replacement);
            await using var pause = await ResearchPause.AcquireAsync(harness.Client);
            await WaitForClientDrainAsync(harness.Connection);
            await FenceAsync(harness.Connection);
            Ensure(original.IsRetired && original.ActiveUses == 1,
                "the queued request must retain the retired generation across the fence");
            Ensure(replacement.ActiveUses == 0 && harness.Server.ActiveCallCountForDiagnostics == 0 &&
                   harness.Server.PendingCallAdmissionsForDiagnostics == 0,
                "current-program/global-call-only observations falsely appear quiescent here");
            var drained = WaitForServerDrainAsync(harness.Server);
            Ensure(!drained.IsCompleted, "all-generation drain must observe the queued old owner");
            held.Lease!.Dispose();
            await ColdFenceResearchService.Entered.WaitAsync(PhaseTimeout);
            Ensure(harness.Server.ActiveCallCountForDiagnostics == 1 && !drained.IsCompleted,
                "queued-to-active transfer must not open a false zero-ownership gap");
            ColdFenceResearchService.ReleaseWork();
            await drained;
            Ensure(original.ActiveUses == 0 && original.IsReclaimed,
                "old generation is reclaimable only after its final request owner leaves");
            await pause.DisposeAsync();
            await AssertSameSessionUsableAsync(harness);
        }
        finally
        {
            held.Lease?.Dispose();
            ColdFenceResearchService.ReleaseAll();
        }
    }

    [Test]
    [NotInParallel]
    public async Task SelectionReservationMustDrainBeforeSendingTheDelimiter()
    {
        await using var harness = await Harness.CreateAsync(admission: false);
        using var releaseReservation = new ManualResetEventSlim();
        var reserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Client._callAdmissionReservedTestHook = connection =>
        {
            if (!ReferenceEquals(connection, harness.Connection))
                return;
            reserved.TrySetResult();
            Ensure(releaseReservation.Wait(PhaseTimeout), "selection-reservation release");
        };
        var call = Task.Run(async () => await harness.Client.Get<IColdFenceResearchService>().AddAsync(20, 22));
        try
        {
            await reserved.Task.WaitAsync(PhaseTimeout);
            await using var pause = await ResearchPause.AcquireAsync(harness.Client);
            Ensure(harness.Connection.CallAdmissionReservationCount == 1 &&
                   harness.Connection.ActiveCallCount == 0,
                "a selected call may not yet be present in pending/untracked ownership");
            var clientDrain = WaitForClientDrainAsync(harness.Connection);
            Ensure(!clientDrain.IsCompleted, "pending-only drain would miss the pre-cut selection reservation");
            harness.Client._callAdmissionReservedTestHook = null;
            releaseReservation.Set();
            Ensure(await call.WaitAsync(PhaseTimeout) == 42, "pre-cut reserved unary request completes");
            await clientDrain;
            await FenceAsync(harness.Connection);
            await WaitForServerDrainAsync(harness.Server);
            await pause.DisposeAsync();
            await AssertSameSessionUsableAsync(harness);
        }
        finally
        {
            harness.Client._callAdmissionReservedTestHook = null;
            releaseReservation.Set();
            await call.WaitAsync(PhaseTimeout);
        }
    }

    [Test]
    [NotInParallel]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ReaderMustContinueStreamProgressAndRetainAsyncCleanup(bool admission, bool duplex)
    {
        ColdFenceResearchService.Reset();
        await using var harness = await Harness.CreateAsync(admission);
        var service = harness.Client.Get<IColdFenceResearchService>();
        var stream = duplex ? service.DuplexAsync(ResearchInput()) : service.DownloadAsync();
        await using var reader = stream.GetAsyncEnumerator();
        Ensure(await reader.MoveNextAsync().AsTask().WaitAsync(PhaseTimeout) && reader.Current == 1,
            "first stream item establishes an active server request");
        await using var pause = await ResearchPause.AcquireAsync(harness.Client);
        try
        {
            // This exploratory early delimiter is NOT the complete pause/drain protocol: pending
            // streams still exist. It proves the reader is free to receive later stream progress.
            await FenceAsync(harness.Connection);
            Ensure(harness.Connection.ActiveCallCount != 0, "stream remains in client ownership");
            var serverDrain = WaitForServerDrainAsync(harness.Server);
            Ensure(!serverDrain.IsCompleted, "streaming work retains server ownership after parsing fence");
            ColdFenceResearchService.ReleaseWork();
            Ensure(await reader.MoveNextAsync().AsTask().WaitAsync(PhaseTimeout) && reader.Current == 2,
                "post-fence stream data must progress on the same paused session");
            var terminal = reader.MoveNextAsync().AsTask();
            await ColdFenceResearchService.CleanupEntered.WaitAsync(PhaseTimeout);
            await FenceAsync(harness.Connection);
            Ensure(harness.Server.ActiveCallCountForDiagnostics == 1 && !serverDrain.IsCompleted,
                "async enumerator cleanup must remain included in server request ownership");
            if (admission)
                Ensure(harness.Server.CurrentAdmissionProgramForTests!.ActiveUses == 1,
                    "async cleanup retains its captured admission generation");
            ColdFenceResearchService.ReleaseCleanup();
            Ensure(!await terminal.WaitAsync(PhaseTimeout), "stream completes after cleanup release");
            await WaitForClientDrainAsync(harness.Connection);
            await FenceAsync(harness.Connection);
            await serverDrain;
        }
        finally
        {
            ColdFenceResearchService.ReleaseAll();
        }
        await pause.DisposeAsync();
        await AssertSameSessionUsableAsync(harness);
    }

    private static async IAsyncEnumerable<int> ResearchInput()
    {
        yield return 1;
        await ColdFenceResearchService.WorkRelease;
        yield return 2;
    }
}
