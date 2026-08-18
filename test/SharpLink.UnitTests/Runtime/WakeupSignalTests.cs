namespace SharpLink.UnitTests.Runtime;

public class WakeupSignalTests
{
    [Test]
    public void ClaimedArmMustNotLatchAStaleSignalForTheNextWait()
    {
        var signal = new WakeupSignal();

        var first = signal.WaitAsync();
        Ensure(!first.IsCompleted, "an armed wait without a signal must stay pending");
        signal.Signal();
        Ensure(first.IsCompletedSuccessfully, "a writer must complete the live arm");

        // The successful claim above must not leave a latched signal behind:
        // the next arm has to wait for a real signal instead of completing
        // spuriously and forcing an empty pump iteration.
        var second = signal.WaitAsync();
        Ensure(!second.IsCompleted, "a claimed arm must not latch a stale signal");
        signal.Signal();
        Ensure(second.IsCompletedSuccessfully, "a fresh signal must complete the next arm");
    }

    [Test]
    public void SignalArrivingBeforeTheArmIsLatchedAndConsumedSynchronously()
    {
        var signal = new WakeupSignal();

        signal.Signal();
        var wait = signal.WaitAsync();
        Ensure(wait.IsCompletedSuccessfully,
            "a signal that arrived before the arm was published must complete the arm synchronously");

        var next = signal.WaitAsync();
        Ensure(!next.IsCompleted, "the latched signal must not survive into the next arm");
    }

    [Test]
    public void TwoWritersCannotDoubleCompleteOneArm()
    {
        var signal = new WakeupSignal();
        var wait = signal.WaitAsync();

        // The per-arm token is the single arbiter: one writer claims the arm, the
        // other loses the race. The loser's signal is latched — its frame is already
        // queued, so it is a real wake for the next arm, not a stale residue.
        Parallel.For(0, 2, _ => signal.Signal());
        Ensure(wait.IsCompletedSuccessfully, "the arm must complete exactly once under concurrent writers");

        var next = signal.WaitAsync();
        Ensure(next.IsCompletedSuccessfully, "the losing writer's latch is a real queued wake for the next arm");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
