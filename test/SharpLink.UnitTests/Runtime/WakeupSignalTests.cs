using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.UnitTests.Runtime;

public class WakeupSignalTests
{
    [Test]
    public async Task ClaimedArmMustNotLatchAStaleSignalForTheNextWait()
    {
        var signal = new WakeupSignal();

        var first = signal.WaitAsync();
        Ensure(!first.IsCompleted, "an armed wait without a signal must stay pending");
        signal.Signal();
        Ensure(first.IsCompletedSuccessfully, "a writer must complete the live arm");
        await first;

        // The successful claim above must not leave a latched signal behind:
        // the next arm has to wait for a real signal instead of completing
        // spuriously and forcing an empty pump iteration.
        var second = signal.WaitAsync();
        Ensure(!second.IsCompleted, "a claimed arm must not latch a stale signal");
        signal.Signal();
        Ensure(second.IsCompletedSuccessfully, "a fresh signal must complete the next arm");
        await second;
    }

    [Test]
    public async Task SignalArrivingBeforeTheArmIsLatchedAndConsumedSynchronously()
    {
        var signal = new WakeupSignal();

        signal.Signal();
        var wait = signal.WaitAsync();
        Ensure(wait.IsCompletedSuccessfully,
            "a signal that arrived before the arm was published must complete the arm synchronously");
        await wait;

        var next = signal.WaitAsync();
        Ensure(!next.IsCompleted, "the latched signal must not survive into the next arm");
    }

    [Test]
    public async Task TwoWritersCannotDoubleCompleteOneArm()
    {
        var signal = new WakeupSignal();
        var wait = signal.WaitAsync();

        // The arm token is the single arbiter: one writer claims the arm, the
        // other loses the race. The loser's signal is latched — its frame is already
        // queued, so it is a real wake for the next arm, not a stale residue.
        Parallel.For(0, 2, _ => signal.Signal());
        Ensure(wait.IsCompletedSuccessfully, "the arm must complete exactly once under concurrent writers");
        await wait;

        var next = signal.WaitAsync();
        Ensure(next.IsCompletedSuccessfully, "the losing writer's latch is a real queued wake for the next arm");
        await next;
    }

    [Test]
    public async Task LateLatchCrossingArmPublicationStillCompletesTheArm()
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
        await wait;

        // The late-latch claim returned the state to idle: no residue for the next arm.
        var next = signal.WaitAsync();
        Ensure(!next.IsCompleted, "the late-latch claim must not leave a stale latch");
    }

    [Test]
    public async Task TimedWaitDeadlineClaimsTheSameArmAndReturnsFalse()
    {
        var clock = new ManualTimeProvider();
        var signal = new WakeupSignal();

        var wait = signal.WaitAsync(clock, TimeSpan.FromMilliseconds(10));
        Ensure(!wait.IsCompleted, "timed wait must remain pending before its deadline");
        Ensure(clock.ActiveTimerCount == 1, "timed wait must own exactly one timer");

        clock.Advance(TimeSpan.FromMilliseconds(10));

        Ensure(wait.IsCompletedSuccessfully, "deadline must complete the armed wait");
        Ensure(!await wait, "deadline winner must be surfaced as false");
        Ensure(clock.ActiveTimerCount == 0, "deadline completion must dispose its timer");

        var next = signal.WaitAsync();
        Ensure(!next.IsCompleted, "deadline completion must not leave a stale wake for the next arm");
    }

    [Test]
    public async Task ProducerSignalCancelsTimedArmAndAStaleTimerCannotCompleteTheNextArm()
    {
        var clock = new ManualTimeProvider();
        var signal = new WakeupSignal();

        var first = signal.WaitAsync(clock, TimeSpan.FromSeconds(1));
        Ensure(clock.ActiveTimerCount == 1, "timed arm must publish one timer");

        signal.Signal();
        Ensure(first.IsCompletedSuccessfully, "producer signal must claim the timed arm");
        Ensure(await first, "producer winner must be surfaced as true");
        Ensure(clock.ActiveTimerCount == 0, "producer claim must dispose the arm's timer");

        var second = signal.WaitAsync();
        Ensure(!second.IsCompleted, "the next arm must start clean");
        clock.Advance(TimeSpan.FromSeconds(2));
        Ensure(!second.IsCompleted, "a disposed timer from the prior generation must not complete a later arm");

        signal.Signal();
        Ensure(await second, "a real producer signal must still complete the later arm");
    }

    [Test]
    public async Task LatchedProducerSignalWinsTimedArmWithoutPublishingATimer()
    {
        var clock = new ManualTimeProvider();
        var signal = new WakeupSignal();

        signal.Signal();
        var wait = signal.WaitAsync(clock, TimeSpan.FromSeconds(1));

        Ensure(wait.IsCompletedSuccessfully, "a latched producer signal must complete timed arm synchronously");
        Ensure(await wait, "latched producer signal must win over the not-yet-armed deadline");
        Ensure(clock.ActiveTimerCount == 0, "synchronous producer win must not leave a timer behind");
    }

    [Test]
    public async Task ConsumedObservedLatchMustNotShortenASubsequentTimedWait()
    {
        var clock = new ManualTimeProvider();
        var signal = new WakeupSignal();

        // Model the pump having already drained the frame associated with this coalesced signal.
        signal.Signal();
        signal.ConsumeLatched();

        var wait = signal.WaitAsync(clock, TimeSpan.FromMilliseconds(10));
        Ensure(!wait.IsCompleted, "an already-observed latch must not complete the timed wait");
        clock.Advance(TimeSpan.FromMilliseconds(10));
        Ensure(!await wait, "after consuming the old latch, the deadline must be the winner");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
