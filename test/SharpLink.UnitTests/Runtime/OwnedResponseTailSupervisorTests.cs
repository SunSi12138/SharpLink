namespace SharpLink.UnitTests.Runtime;

public sealed class OwnedResponseTailSupervisorTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Test]
    public async Task AlreadyOwnedRootedTailMayJoinAfterSealButNewAnchorCannotStart()
    {
        var supervisor = new FrameworkTaskSupervisor(); var completion = Signal(); var root = new object();
        supervisor.Seal(); FrameworkTaskRegistration? accepted = null;
        supervisor.TrackOwnedStrictPhysicalTail(completion.Task, "already-owned", root, ref accepted);
        var drain = supervisor.DrainAsync();
        try
        {
            Ensure(ReferenceEquals(accepted!.RetainedOwnerRoot, root) && !drain.IsCompleted,
                "accepted already-owned task roots its actual owner until physical completion");
            FrameworkTaskRegistration? rejected = null;
            try { supervisor.AdmitStrictPhysicalAnchor(Signal().Task, "new-work", new object(), ref rejected); throw new InvalidOperationException("missing Seal rejection"); }
            catch (InvalidOperationException error) { Ensure(error.Message.Contains("cannot start after", StringComparison.Ordinal) && rejected is null, "fresh admission still rejects Seal"); }
            completion.TrySetResult(); await drain.WaitAsync(Bound);
            Ensure(accepted!.RetainedOwnerRoot is null && supervisor.CaptureSnapshot().LateRegistrations == 1,
                "only actual accepted tail is late and its root clears on exact completion");
        }
        finally { completion.TrySetResult(); await drain.WaitAsync(Bound); }
    }

    [Test]
    public async Task AcceptedReceiptPrecedesActualSynchronousCompletionObserver()
    {
        var raw = new OperationCanceledException("raw already-owned failure");
        FrameworkTaskRegistration? receipt = null; var observed = 0; var receiptVisible = false; var observerExited = Signal();
        var task = Task.FromException(PhysicalTaskFailureException.From(raw));
        var supervisor = new FrameworkTaskSupervisor((_, error) =>
        {
            observed++; receiptVisible = receipt is not null && ReferenceEquals(receipt.Task, task) && ReferenceEquals(error, raw);
            observerExited.TrySetResult();
        });
        supervisor.TrackOwnedStrictPhysicalTail(task, "synchronous", new object(), ref receipt);
        supervisor.Seal(); var failure = await Failure(supervisor.DrainAsync());
        await observerExited.Task.WaitAsync(Bound);
        Ensure(ReferenceEquals(failure, raw) && observed == 1 && receiptVisible && receipt!.RetainedOwnerRoot is null,
            "receipt commits before genuine ContinueWith observer; strict raw cancellation is not suppressed");
    }

    [Test]
    public async Task RejectedAfterDrainTailRemainsWithCallerAndHasNoReceipt()
    {
        var supervisor = new FrameworkTaskSupervisor(); supervisor.Seal(); await supervisor.DrainAsync();
        var task = Signal(); FrameworkTaskRegistration? receipt = null;
        try
        {
            try { supervisor.TrackOwnedStrictPhysicalTail(task.Task, "too-late", new object(), ref receipt); throw new InvalidOperationException("missing completed-drain rejection"); }
            catch (InvalidOperationException error) { Ensure(error.Message.Contains("drain has completed", StringComparison.Ordinal), "exact completed-drain gate"); }
            Ensure(receipt is null && !task.Task.IsCompleted, "no receipt means physical owner remains caller, not inferred acceptance");
        }
        finally { task.TrySetResult(); await task.Task; }
    }

    [Test]
    public async Task LegacyRefReceiptPreservesExternalObservationMode()
    {
        var supervisor = new FrameworkTaskSupervisor(); var raw = new InvalidOperationException("externally observed");
        var task = Task.FromException(raw); FrameworkTaskRegistration? receipt = null;
        supervisor.Track(task, "legacy-external", TaskObservationMode.ExternallyObserved, static _ => false, ref receipt);
        Ensure(receipt is not null && !receipt.IsStrictPhysical && receipt.ObservationMode == TaskObservationMode.ExternallyObserved,
            "receipt overload does not silently change legacy classifier or outcome owner");
        Ensure(ReferenceEquals(await Failure(task), raw), "caller retains exact external outcome");
        supervisor.Seal(); await supervisor.DrainAsync().WaitAsync(Bound);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<Exception> Failure(Task task)
    {
        try { await task.WaitAsync(Bound); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected an actual failure.");
    }
}
