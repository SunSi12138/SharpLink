namespace SharpLink.UnitTests.Server;

public partial class AbandonedGeneratedMapperOwnershipTests
{
    [Test]
    [Arguments(0, false)]
    [Arguments(0, true)]
    [Arguments(1, false)]
    [Arguments(1, true)]
    [Arguments(2, false)]
    [Arguments(2, true)]
    public async Task NestedCarrierMappingSurvivesPendingAndCompletedPhysicalOwners(int boundary, bool childCompleted)
    {
        var original = new ApplicationDeadline();
        var child = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var carrier = new SharpLinkException(original.Code, original.DetailCode, original.Message, original);
        var owner = SharpLinkPhysicalWork.CreateCarrierOwner(carrier, original);
        owner.Track(child.Task);
        if (childCompleted)
        {
            child.TrySetResult();
            await owner.Completion.WaitAsync(Watchdog);
        }
        await using var fixture = new Fixture(original);
        var stream = new FailureStream(carrier, boundary);
        var pump = fixture.Pump(stream);
        try
        {
            Ensure(ReferenceEquals(await fixture.Mapper.Observed.Task.WaitAsync(Watchdog), original),
                "nested transparent carriers retain mapper identity even after physical completion");
            if (!childCompleted)
                Ensure(!pump.IsCompleted, "pending descendant still retains the server invocation");
            await fixture.AssertMappedWireAsync();
            child.TrySetResult();
            if (boundary == 0)
            {
                await stream.DisposeEntered.Task.WaitAsync(Watchdog);
                Ensure(!pump.IsCompleted, "source cleanup remains retained after nested work finishes");
            }
            stream.DisposeRelease.TrySetResult();
            await pump.WaitAsync(Watchdog);
            Ensure(stream.DisposeCalls == (boundary == 1 ? 0 : 1), "enumerator disposal remains exactly once");
        }
        finally { child.TrySetResult(); stream.DisposeRelease.TrySetResult(); await pump.WaitAsync(Watchdog); }
    }

    [Test]
    public async Task ArbitrationAliasRetainsNewLogicalTerminalAfterPhysicalCompletion()
    {
        var original = new ApplicationDeadline();
        var carrier = new SharpLinkException(original.Code, original.DetailCode, original.Message, original);
        var owner = SharpLinkPhysicalWork.CreateCarrierOwner(carrier, original);
        owner.Track(Task.CompletedTask);
        await owner.Completion.WaitAsync(Watchdog);
        var selected = new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "selected framework deadline", carrier);
        SharpLinkPhysicalWork.PreserveAbandonedUserWork(carrier, selected);
        using var source = new CancellationTokenSource();
        var outer = SharpLinkPhysicalWork.TransferLifetime(selected, source)!;
        Ensure(ReferenceEquals(SharpLinkPhysicalWork.GetLogicalException(outer), selected),
            "physical aliases must not undo a newly selected logical deadline");
        await SharpLinkPhysicalWork.GetAbandonedUserWork(outer)!.WaitAsync(Watchdog);
        Ensure(ReferenceEquals(SharpLinkPhysicalWork.GetLogicalException(outer), selected),
            "logical mapping metadata must survive completed cleanup");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationCarrierPreservesLogicalMapperInstanceAndDeclaredMetadata(bool taskCanceled)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceledTask = Task.FromCanceled(cancellation.Token);
        OperationCanceledException original = taskCanceled
            ? new TaskCanceledException(canceledTask)
            : new ApplicationCancellation("custom application cancellation", cancellation.Token);
        var child = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SharpLinkPhysicalWork.TrackAbandonedUserWork(original, child.Task);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var carrier = SharpLinkPhysicalWork.TransferLifetime(original, lifetime)!;
        var physicalWork = SharpLinkPhysicalWork.GetAbandonedUserWork(carrier)!;

        await using var fixture = new Fixture(original);
        var stream = new FailureStream(carrier, boundary: 0);
        var pump = fixture.Pump(stream);
        try
        {
            Ensure(!ReferenceEquals(carrier, original) && ReferenceEquals(carrier.InnerException, original),
                "physical handoff creates a fresh carrier while retaining its exact original cause");
            Ensure(carrier.GetType() == (taskCanceled ? typeof(TaskCanceledException) : typeof(OperationCanceledException)) &&
                carrier.Message == original.Message &&
                ((OperationCanceledException)carrier).CancellationToken == cancellation.Token,
                "standard cancellation category, message and token survive a custom-subtype or Task-bearing terminal");
            if (taskCanceled)
            {
                Ensure(ReferenceEquals(((TaskCanceledException)original).Task, canceledTask) &&
                    ((TaskCanceledException)carrier).Task is null,
                    "the original Task stays on the original exception; a fresh carrier does not clone Task identity");
            }
            Ensure(ReferenceEquals(await fixture.Mapper.Observed.Task.WaitAsync(Watchdog), original),
                "the application mapper must receive the original cancellation instance, not its public carrier");
            Ensure(!pump.IsCompleted && !physicalWork.IsCompleted,
                "publishing the mapped terminal cannot release its pending physical child");
            await fixture.AssertMappedWireAsync();
            child.TrySetResult();
            await stream.DisposeEntered.Task.WaitAsync(Watchdog);
            Ensure(!pump.IsCompleted && stream.DisposeCalls == 1,
                "the actual invocation continues to own asynchronous producer disposal");
            stream.DisposeRelease.TrySetResult();
            await pump.WaitAsync(Watchdog);
            await physicalWork.WaitAsync(Watchdog);
            Ensure(ReferenceEquals(SharpLinkPhysicalWork.GetLogicalException(carrier), original),
                "completed physical cleanup does not erase the mapper's logical exception identity");
        }
        finally
        {
            child.TrySetResult();
            stream.DisposeRelease.TrySetResult();
            await pump.WaitAsync(Watchdog);
            await physicalWork.WaitAsync(Watchdog);
        }
    }

    private sealed class ApplicationCancellation(string message, CancellationToken cancellationToken)
        : OperationCanceledException(message, cancellationToken);

}
