namespace SharpLink.UnitTests.Runtime;

public class AbandonedPhysicalWorkTests
{
    [Test]
    public async Task ExceptionOwnerWaitsForTransitivePhysicalChild()
    {
        var parent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentTerminal = new InvalidOperationException("parent terminal");
        var childTerminal = new InvalidOperationException("child terminal");
        SharpLinkPhysicalWork.TrackAbandonedUserWork(parentTerminal, parent.Task);
        SharpLinkPhysicalWork.TrackAbandonedUserWork(childTerminal, child.Task);
        parent.TrySetException(childTerminal);
        var ownership = SharpLinkPhysicalWork.GetAbandonedUserWork(parentTerminal)!;
        Ensure(!ownership.IsCompleted, "physical child must outlive completed parent wrapper");
        child.TrySetException(new InvalidOperationException("late user failure"));
        await ownership.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ReplacedTerminalKeepsSamePhysicalOwner()
    {
        var physical = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new InvalidOperationException("original");
        var replacement = new InvalidOperationException("replacement");
        SharpLinkPhysicalWork.TrackAbandonedUserWork(original, physical.Task);
        SharpLinkPhysicalWork.PreserveAbandonedUserWork(original, replacement);
        var originalOwner = SharpLinkPhysicalWork.GetAbandonedUserWork(original)!;
        Ensure(ReferenceEquals(originalOwner, SharpLinkPhysicalWork.GetAbandonedUserWork(replacement)),
            "deadline arbitration must preserve physical ownership");
        physical.TrySetResult();
        await originalOwner.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
