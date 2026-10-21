namespace SharpLink.IntegrationTests;

public sealed partial class ColdSessionFenceResearchTests
{
    [Test]
    [NotInParallel]
    public async Task EmptyCallMapAndZeroProgramUsesMustNotHideFinalRequestPermitCleanup()
    {
        ColdFenceResearchService.Reset();
        await using var harness = await Harness.CreateAsync(admission: true);
        var original = harness.Server.CurrentAdmissionProgramForTests!;
        var kernel = original.Kernel;
        using var releaseDisposal = new ManualResetEventSlim();
        var disposalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await harness.Client.Get<IColdFenceResearchService>().NotifyAsync();
        await ColdFenceResearchService.Entered.WaitAsync(PhaseTimeout);
        var replacement = harness.Server.CreateAdmissionProgramForTests(options =>
            options.Global.UseConcurrency(2));
        harness.Server.PublishAdmissionProgramForTests(replacement);
        kernel.BeforeReclaimedStateDisposalForTests = () =>
        {
            disposalEntered.TrySetResult();
            Ensure(releaseDisposal.Wait(PhaseTimeout), "retired admission physical-disposal release");
        };
        try
        {
            await using var pause = await ResearchPause.AcquireAsync(harness.Client);
            await WaitForClientDrainAsync(harness.Connection);
            await FenceAsync(harness.Connection);
            ColdFenceResearchService.ReleaseWork();
            await disposalEntered.Task.WaitAsync(PhaseTimeout);
            await FenceAsync(harness.Connection);
            var serverConnection = ReadField<ServerConnectionRegistry>(harness.Server, "_connectionRegistry")
                .SnapshotActive().Single();
            var entries = new long[1];
            Ensure(serverConnection.CallCancellations.TryCopyEntries(entries, static (id, _) => id, out var count) && count == 0,
                "the request map is already empty before physical ownership cleanup finishes");
            Ensure(original.ActiveUses == 0 && replacement.ActiveUses == 0 && kernel.ActivePermits == 0,
                "all admission uses and admission permits are already zero in this cleanup window");
            Ensure(!original.IsReclaimed && harness.Server.ActiveCallCountForDiagnostics == 1,
                "the existing ServerRequestPermit still owns final teardown after map removal and use release");
            var drained = WaitForServerDrainAsync(harness.Server);
            Ensure(!drained.IsCompleted, "call-permit accounting must close the map/generation-only false-zero window");
            releaseDisposal.Set();
            await drained;
            Ensure(original.IsReclaimed, "physical reclamation finishes before full server drain");
            await pause.DisposeAsync();
            await AssertSameSessionUsableAsync(harness);
        }
        finally
        {
            releaseDisposal.Set();
            kernel.BeforeReclaimedStateDisposalForTests = null;
            ColdFenceResearchService.ReleaseAll();
        }
    }
}
