namespace SharpLink.UnitTests.Runtime;

public sealed class StrictPhysicalTaskSupervisorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OriginalPhysicalCancellationIsRetainedBeforeAndAfterSeal(bool afterSeal)
    {
        var raw = new OperationCanceledException("exact independent physical cancellation");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new List<Exception>();
        var observedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var supervisor = new FrameworkTaskSupervisor((_, error) => { observed.Add(error); observedSignal.TrySetResult(); });
        var task = EmitPhysicalFailureAsync(release.Task, raw);
        FrameworkTaskRegistration? receipt = null;
        supervisor.TrackStrictPhysical(task, "physical-oce", ref receipt);
        if (afterSeal) supervisor.Seal();
        release.SetResult();
        var emitted = await Failure(task);
        Ensure(emitted is PhysicalTaskFailureException marker && ReferenceEquals(marker.RawException, raw) && task.IsFaulted,
            "raw OCE is transported before Task cancellation conversion");
        supervisor.CompleteAcceptedRegistration(receipt!);
        if (!afterSeal) supervisor.Seal();
        var drained = await Failure(supervisor.DrainAsync());
        await observedSignal.Task.WaitAsync(Timeout);
        Ensure(ReferenceEquals(drained, raw) && observed.Count == 1 && ReferenceEquals(observed[0], raw) &&
            supervisor.CaptureSnapshot().SuppressedShutdownFailures == 0,
            "strict completion never classifies away or reconstructs the exact raw cancellation");
    }

    [Test]
    public async Task RawUserAggregateRemainsOneOpaqueOriginalObject()
    {
        var raw = new AggregateException(new OperationCanceledException("child"),
            new AggregateException(new InvalidOperationException("nested")));
        var observed = new List<Exception>();
        var supervisor = new FrameworkTaskSupervisor((_, error) => observed.Add(error));
        FrameworkTaskRegistration? receipt = null;
        supervisor.TrackStrictPhysical(Task.FromException(new PhysicalTaskFailureException(raw)), "aggregate", ref receipt);
        supervisor.Seal();
        Ensure(ReferenceEquals(await Failure(supervisor.DrainAsync()), raw) && observed.Count == 1 && ReferenceEquals(observed[0], raw),
            "strict completion unwraps only Task's direct marker, never user aggregate children");
    }

    [Test]
    public async Task MultipleDirectTaskFaultsPreserveEachRawPayloadWithoutFlattening()
    {
        var first = new AggregateException(new InvalidOperationException("opaque"));
        var second = new OperationCanceledException("independent");
        var task = Task.WhenAll(Task.FromException(new PhysicalTaskFailureException(first)),
            Task.FromException(new PhysicalTaskFailureException(second)));
        var supervisor = new FrameworkTaskSupervisor();
        FrameworkTaskRegistration? receipt = null;
        supervisor.TrackStrictPhysical(task, "direct-entries", ref receipt);
        supervisor.Seal();
        var failure = await Failure(supervisor.DrainAsync());
        Ensure(failure is AggregateException aggregate && aggregate.InnerExceptions.Count == 2 &&
            ReferenceEquals(aggregate.InnerExceptions[0], first) && ReferenceEquals(aggregate.InnerExceptions[1], second),
            "only the outer Task container is traversed, preserving both original raw objects");
    }

    [Test]
    public async Task CanceledStrictTaskIsAnInvariantFailureNotInventedOriginalCancellation()
    {
        var supervisor = new FrameworkTaskSupervisor();
        FrameworkTaskRegistration? receipt = null;
        supervisor.Seal();
        supervisor.TrackStrictPhysical(Task.FromCanceled(new CancellationToken(true)), "broken-transport", ref receipt);
        var failure = await Failure(supervisor.DrainAsync());
        Ensure(failure is InvalidOperationException && failure.Message.Contains("original raw failure", StringComparison.Ordinal) &&
            supervisor.CaptureSnapshot().SuppressedShutdownFailures == 0,
            "missing original identity is explicitly diagnosed, never fabricated or suppressed");
    }

    // The two disposed-Task attachment/ABA fixtures were invalid on pinned .NET 10.0.2.
    // Their full obligations move to the separately identified validation-only
    // ContinuationAttachmentBoundaryValidationTests, not weaker ordinary-suite assertions.
    // Historical failed source/results remain immutable; this is a proposed future successor.

    [Test]
    public async Task PreInsertionRejectionLeavesOriginWithNoAcceptedReceipt()
    {
        var supervisor = new FrameworkTaskSupervisor();
        supervisor.Seal();
        await supervisor.DrainAsync();
        var raw = new OperationCanceledException("unaccepted raw");
        var task = Task.FromException(new PhysicalTaskFailureException(raw));
        FrameworkTaskRegistration? receipt = null;
        Exception? rejection = null;
        try { supervisor.TrackStrictPhysical(task, "rejected", ref receipt); }
        catch (Exception failure) { rejection = failure; }
        var actual = await Failure(task);
        Ensure(rejection is InvalidOperationException && receipt is null && ReferenceEquals(PhysicalTaskFailureException.GetRaw(actual), raw),
            "origin retains and joins the exact rejected task; no acceptance is inferred from task completion");
    }

    private static async Task EmitPhysicalFailureAsync(Task release, Exception raw)
    {
        await release.ConfigureAwait(false);
        throw PhysicalTaskFailureException.From(raw);
    }

    private static async Task<Exception> Failure(Task task)
    {
        try { await task.WaitAsync(Timeout); }
        catch (Exception failure) when (failure is not TimeoutException) { return failure; }
        throw new InvalidOperationException("Expected a retained failure.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
