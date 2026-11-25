namespace SharpLink.StreamLoadTest.Tests;

public sealed class QualifiedShortStreamTests
{
    [Test]
    [Arguments("c2s", 32)]
    [Arguments("s2c", 32)]
    [Arguments("duplex", 32)]
    [Arguments("c2s", 256)]
    [Arguments("s2c", 256)]
    [Arguments("duplex", 256)]
    public async Task ValidatedOutputReportsEachApplicationPhase(string operation, int items)
    {
        var report = StreamLoadOptions.Parse([
            "--operation", operation, "--stream-size", items.ToString(), "--short-stream-phases"]);
        var owners = new StageLatencyRecorder(1, 4);
        var observations = new ShortStreamPhaseRecorder(owners);
        var sample = await QualifiedShortStream.ExecuteAsync(new StreamLoadService(), operation,
            Enumerable.Range(1, report.StreamSize).ToArray(), CancellationToken.None);
        observations.Record(0, sample);
        var phases = observations.Complete(operation, owners.Complete());

        await Assert.That(report.ShortStreamPhases).IsTrue();
        await Assert.That(sample.Items).IsEqualTo(items);
        await Assert.That(sample.FirstProgressTicks <= sample.BusinessCompleteTicks &&
                          sample.BusinessCompleteTicks <= sample.ApplicationOwnersExitedTicks).IsTrue();
        await Assert.That(phases.FirstProgressMeaning).IsEqualTo(operation == "c2s" ? "first input pull" : "first response item");
        await Assert.That(phases.FirstProgress.Count).IsEqualTo(1);
        await Assert.That(phases.BusinessComplete.Count).IsEqualTo(1);
        await Assert.That(phases.ApplicationOwnersExited.Count).IsEqualTo(1);
    }
}
