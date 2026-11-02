namespace SharpLink.UnitTests.Client;

public sealed class ContractScopedRouteRetryControls
{
    [Test]
    [Arguments("binding")]
    [Arguments("policy")]
    [Arguments("replacement")]
    public Task RetrySameIndexMapBindingAndPolicy(string change) => RunAsync(fixture => fixture.ExerciseSameMapRetryAsync(change));

    [Test]
    [Arguments("reorder")]
    [Arguments("attributes")]
    [Arguments("generation")]
    public Task RetryTrueTopologyChange(string change) => RunAsync(fixture => fixture.ExerciseChangedMapRetryAsync(change));

    [Test]
    public Task SelectionLoopDoesNotRestartAfterLiveReorder() => RunAsync(fixture => fixture.ExerciseStableSelectionLoopAsync());

    private static async Task RunAsync(Func<ContractScopedRouteHandoffFixture, Task> body)
    {
        var fixture = new ContractScopedRouteHandoffFixture();
        Exception? primary = null;
        try { await body(fixture); }
        catch (Exception error) { primary = error; }
        await fixture.CleanupRetryAsync(primary);
    }
}
