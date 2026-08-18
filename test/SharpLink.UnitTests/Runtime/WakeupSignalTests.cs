using System.Threading;
using System.Threading.Tasks;

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

    [Test]
    public void LateLatchCrossingArmPublicationStillCompletesTheArm()
    {
        // The exact lost-wakeup interleaving from the review: the writer observes the idle
        // state and pauses; WaitAsync publishes the arm and consumes the (still empty)
        // latch; the writer then latches. The latch write lands after the arm publication
        // was already latched-checked, so the pending arm must still be completed by the
        // writer's re-check loop — otherwise the queued frame sleeps forever.
        var signal = new WakeupSignal();
        var observedIdle = new ManualResetEventSlim(initialState: false);
        var releaseWriter = new ManualResetEventSlim(initialState: false);
        signal.BeforeLatchWrite = () =>
        {
            observedIdle.Set();
            releaseWriter.Wait();
        };

        var writer = Task.Run(signal.Signal);
        Ensure(observedIdle.Wait(TimeSpan.FromSeconds(5)), "the writer must reach the latch path");

        var wait = signal.WaitAsync();
        Ensure(!wait.IsCompleted, "the arm must stay pending while the writer is parked");

        releaseWriter.Set();
        Ensure(writer.Wait(TimeSpan.FromSeconds(5)), "the writer must finish");

        Ensure(wait.IsCompletedSuccessfully,
            "a latch landing after arm publication must still complete the pending arm");

        // The late-latch claim returned the state to idle: no residue for the next arm.
        var next = signal.WaitAsync();
        Ensure(!next.IsCompleted, "the late-latch claim must not leave a stale latch");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
