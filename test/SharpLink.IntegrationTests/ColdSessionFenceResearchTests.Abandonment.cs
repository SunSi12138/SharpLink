namespace SharpLink.IntegrationTests;

public sealed partial class ColdSessionFenceResearchTests
{
    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeadlineAbandonmentCanOutliveEveryExistingDrainCounter(bool admission)
    {
        ColdFenceResearchService.Reset();
        await using var harness = await Harness.CreateAsync(admission, freezeClientClock: true);
        await using var reader = harness.Client.Get<IColdFenceResearchService>()
            .IgnoreDeadlineDownloadAsync(CancellationToken.None).GetAsyncEnumerator();
        try
        {
            Ensure(await reader.MoveNextAsync().AsTask().WaitAsync(PhaseTimeout) && reader.Current == 1,
                "deadline test must receive the initial stream element");
            await ColdFenceResearchService.Entered.WaitAsync(PhaseTimeout);
            var next = reader.MoveNextAsync().AsTask();
            await using var pause = await ResearchPause.AcquireAsync(harness.Client);
            // Let the server's own deadline win before the client emits its deadline Cancel.
            // Then expire the client clock and apply the full client-drain/fence/server predicate.
            await WaitForServerDrainAsync(harness.Server);
            harness.ExpireClientDeadline();
            Exception? failure = null;
            try { await next.WaitAsync(PhaseTimeout); }
            catch (Exception exception) { failure = exception; }
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                $"stream must terminate at its RPC deadline, actual {failure}");
            await WaitForClientDrainAsync(harness.Connection);
            await FenceAsync(harness.Connection);
            await WaitForServerDrainAsync(harness.Server);
            Ensure(!ColdFenceResearchService.WorkRelease.IsCompleted &&
                   !ColdFenceResearchService.MoveNextCompleted.IsCompleted &&
                   !ColdFenceResearchService.Completed.IsCompleted,
                "COUNTEREXAMPLE: all existing drain counters are zero while old MoveNext remains executing");
            var serverConnection = ReadField<ServerConnectionRegistry>(harness.Server, "_connectionRegistry")
                .SnapshotActive().Single();
            Ensure(serverConnection.CallCancellations.TryCopyEntries(
                       new long[1], static (id, _) => id, out var count) && count == 0,
                "the old server request has also left the cancellation map");

            await ColdFenceResearchService.CleanupEntered.WaitAsync(PhaseTimeout);
            ColdFenceResearchService.ReleaseWork();
            await ColdFenceResearchService.MoveNextCompleted.WaitAsync(PhaseTimeout);
            Ensure(IsServerDrained(harness.Server) && !ColdFenceResearchService.Completed.IsCompleted,
                "COUNTEREXAMPLE: deferred async Dispose also runs after existing ownership is quiescent");
            ColdFenceResearchService.ReleaseCleanup();
            await ColdFenceResearchService.Completed.WaitAsync(PhaseTimeout);
            await pause.DisposeAsync();
            await AssertSameSessionUsableAsync(harness);
        }
        finally
        {
            ColdFenceResearchService.ReleaseAll();
        }
    }
}
