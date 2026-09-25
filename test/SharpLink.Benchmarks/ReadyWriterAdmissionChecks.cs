#if SHARPLINK_READY_WRITER_EXPERIMENT
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal sealed partial class ReadyWriterCoordinator
{
    internal static async Task<int> RunAdmissionChecksAsync()
    {
        string[] scenarios = ["late-credit", "live-limit", "pending-limit", "cancel-before-inbox", "cancel-waiting",
            "cancel-after-admission", "writer-pin", "held-capacity", "no-barging", "terminal", "connection-cancel",
            "same-key", "generation-overflow", "reuse1000", "frozen-capacity-oracle", "cancel-claim-race"];
        foreach (var scenario in scenarios)
        {
            await CheckAdmissionAsync(scenario).WaitAsync(TimeSpan.FromSeconds(10));
            Console.WriteLine($"PASS admission/{scenario}");
        }
        return scenarios.Length;
    }

    private static async Task ExpectAdmissionCanceledAsync(Task operation, CancellationToken token)
    {
        try { await operation.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException error) when (error.CancellationToken == token) { return; }
        throw new InvalidOperationException("Expected cancellation with the original token.");
    }

    private static async Task ExpectCapacityLimitAsync(Task operation)
    {
        try { await operation.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.ResourceExhausted) { return; }
        throw new InvalidOperationException("Expected the original ResourceExhausted capacity bound.");
    }

    private static async Task CheckAdmissionAsync(string scenario)
    {
        await using var fixture = await LifecycleFixture.CreateAsync(1);
        var owner = fixture.Owner;
        var old = new StreamHandle(owner, 0, 1);
        var state = owner._streams[0];
        using var cancel = new CancellationTokenSource();
        if (scenario == "live-limit")
        {
            var denied = owner.AcquireStreamAsync(2, 1); owner.DrainNotifications();
            await ExpectCapacityLimitAsync(denied);
            RequireWire(owner._pendingAdmission is null && owner._identities.Count == 1 && state.Generation == 1,
                "All live slots must fail without creating a pending admission or touching generation.");
            return;
        }
        if (scenario == "cancel-before-inbox")
        {
            await fixture.CloseAsync(old);
            var canceled = owner.AcquireStreamAsync(2, 1, cancel.Token);
            cancel.Cancel();
            await ExpectAdmissionCanceledAsync(canceled, cancel.Token);
            owner.DrainNotifications();
            RequireWire(state.Generation == 1 && state.Retired && owner._identities.Count == 0,
                "Cancellation before writer admission must never secretly register the stream.");
            return;
        }
        if (scenario == "held-capacity")
        {
            await fixture.EnqueueAsync(old, 1, 1);
            var held = state.WaitForSpace(CancellationToken.None);
            await fixture.CloseAsync(old);
            var waiting = owner.AcquireStreamAsync(2, 1); owner.DrainNotifications();
            RequireWire(!waiting.IsCompleted && !state.Retired && held.IsCompleted,
                "SetResult is not ownership release; a held capacity result still pins registration space.");
            try { held.GetAwaiter().GetResult(); throw new Exception("Closed capacity unexpectedly succeeded."); }
            catch (InvalidOperationException) { }
            owner.DrainNotifications();
            RequireWire((await waiting).Generation == 2, "Consuming the held result must wake capacity admission.");
            return;
        }
        if (scenario == "frozen-capacity-oracle")
        {
            var frozen = new StreamFlowController(16, 16, 4096, 1);
            await frozen.AcquireSendCreditAsync(1, 1, 16, CancellationToken.None);
            await ExpectCapacityLimitAsync(Capture(() => frozen.AcquireSendCreditAsync(2, 1, 16, CancellationToken.None)));
            frozen.CompleteSendStream(1, 1);
            var waiting = frozen.AcquireSendCreditAsync(2, 1, 16, cancel.Token).AsTask();
            await ExpectCapacityLimitAsync(Capture(() => frozen.AcquireSendCreditAsync(3, 1, 16, CancellationToken.None)));
            RequireWire(!waiting.IsCompleted, "The reference must actually wait on a retained completed identity.");
            cancel.Cancel(); await ExpectAdmissionCanceledAsync(waiting, cancel.Token);
            waiting = frozen.AcquireSendCreditAsync(3, 1, 16, CancellationToken.None).AsTask();
            frozen.ApplyWindowUpdate(1, 1, 16); await waiting.WaitAsync(TimeSpan.FromSeconds(2));
            return;
        }
        if (scenario == "cancel-claim-race")
        {
            var current = old;
            for (var index = 0; index < 100; index++)
            {
                using var raceCancel = new CancellationTokenSource();
                var frame = await fixture.TakeAsync(current, index + 1, 1); fixture.Release(frame);
                await fixture.CloseAsync(current);
                var waiting = owner.AcquireStreamAsync(index + 2, 1, raceCancel.Token); owner.DrainNotifications();
                var cancellation = Task.Run(raceCancel.Cancel);
                await fixture.UpdateAsync(index + 1, 1, 16);
                await cancellation;
                try { current = await waiting; }
                catch (OperationCanceledException error) when (error.CancellationToken == raceCancel.Token)
                {
                    owner.DrainNotifications();
                    RequireWire(owner._identities.Count == 0 && state.Retired,
                        "Cancellation won: no unobserved registration may remain.");
                    current = await fixture.OpenAsync(index + 2, 1);
                }
                RequireWire(current.Generation == index + 2 && owner._identities.Count == 1 &&
                    state.RequestId == index + 2 && owner._pendingAdmission is null,
                    "Either winner must transfer exactly one slot, never cancel an already admitted handle.");
            }
            return;
        }
        if (scenario == "reuse1000")
        {
            var current = old;
            for (var index = 0; index < 1000; index++)
            {
                var frame = await fixture.TakeAsync(current, index + 1, 1); fixture.Release(frame);
                await fixture.CloseAsync(current);
                var waiting = owner.AcquireStreamAsync(index + 2, 1); owner.DrainNotifications();
                RequireWire(owner._pendingAdmission is not null && !waiting.IsCompleted, "Every cycle must use the wait slot.");
                await fixture.UpdateAsync(index + 1, 1, 16);
                current = await waiting;
            }
            RequireWire(current.Generation == 1001 && owner._pendingAdmission is null &&
                ReferenceEquals(state, owner._streams[0]) && state.Credit == 16 && owner._connectionCredit == 16,
                "Repeated capacity admission must reuse one bounded state without orphaned waiters or credit.");
            return;
        }
        var retained = await fixture.TakeAsync(old, 1, 1);
        if (scenario != "writer-pin") fixture.Release(retained);
        else await fixture.UpdateAsync(1, 1, 16);
        await fixture.CloseAsync(old);
        var pending = owner.AcquireStreamAsync(2, 1, cancel.Token); owner.DrainNotifications();
        RequireWire(owner._pendingAdmission is not null && !pending.IsCompleted && !state.Retired,
            "Retained tombstone capacity must wait, not reject or allocate an extra state.");
        if (scenario == "pending-limit")
        {
            var denied = owner.AcquireStreamAsync(3, 1); owner.DrainNotifications();
            await ExpectCapacityLimitAsync(denied);
            RequireWire(!pending.IsCompleted && owner._identities.Count == 1, "A second waiter must not displace the first.");
        }
        if (scenario == "same-key")
        {
            var duplicate = owner.AcquireStreamAsync(1, 1); owner.DrainNotifications();
            await RejectLifecycleAsync(duplicate);
            RequireWire(!pending.IsCompleted, "Same-key rejection must not disturb another identity's capacity wait.");
        }
        if (scenario is "cancel-waiting" or "connection-cancel")
        {
            if (scenario == "cancel-waiting") cancel.Cancel(); else owner._cancel.Cancel();
            await ExpectAdmissionCanceledAsync(pending, scenario == "cancel-waiting" ? cancel.Token : owner._stopToken);
            RequireWire(state.Generation == 1 && state.Outstanding == 16 && !state.Retired,
                "A cancellation callback must not mutate retained DATA, generation or owner maps.");
            if (scenario == "connection-cancel") return;
            RequireWire(owner.HasWork, "Canceled pending admission must signal owner cleanup without another message.");
            owner.DrainNotifications();
            pending = owner.AcquireStreamAsync(3, 1); owner.DrainNotifications();
        }
        if (scenario == "terminal")
        {
            var failure = new InvalidOperationException("original terminal admission failure");
            owner.Stopped(failure);
            try { await pending; throw new Exception("Terminal admission succeeded."); }
            catch (InvalidOperationException error) when (ReferenceEquals(error, failure)) { }
            RequireWire(owner._pendingAdmission is null && state.Generation == 1, "Stop must release the pending slot without admission.");
            return;
        }
        if (scenario == "generation-overflow") state.Generation = long.MaxValue;
        if (scenario == "writer-pin") fixture.Release(retained);
        else if (scenario == "no-barging")
        {
            await owner.EnqueueUpdateAsync(1, 1, 16);
            var younger = owner.OpenStreamAsync(3, 1);
            owner.DrainNotifications();
            await RejectLifecycleAsync(younger);
        }
        else await fixture.UpdateAsync(1, 1, 16);
        if (scenario == "generation-overflow")
        {
            try { await pending; throw new Exception("Capacity admission wrapped the generation."); }
            catch (OverflowException) { }
            RequireWire(state.Retired && state.Generation == long.MaxValue && owner._pendingAdmission is null &&
                owner._identities.Count == 0, "Failed admission must leave the free slot unmodified and release waiter ownership.");
            return;
        }
        if (scenario == "cancel-after-admission") cancel.Cancel();
        var next = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        RequireWire(next.Generation == 2 && next.Slot == old.Slot && ReferenceEquals(state, owner._streams[0]) &&
            owner._pendingAdmission is null && owner._identities.Count == 1 && state.Credit == 16 && state.Outstanding == 0,
            "Retirement must hand the same bounded slot to its waiting owner exactly once.");
        RequireWire(state.RequestId == (scenario == "cancel-waiting" ? 3 : 2), "A canceled waiter cannot secretly register ahead of its successor.");
        await RejectLifecycleAsync(owner.EnqueueAsync(old, fixture.Packet(1, 1)).AsTask());
    }

    private static async Task Capture(Func<ValueTask> action) => await action();

    internal static async Task<int> RunAdmissionOrderingChecksAsync()
    {
        string[] scenarios = ["canceled-before-delivery", "canceled-before-retirement", "canceled-live-limit", "canceled-before-observer"];
        var failures = new List<Exception>();
        foreach (var scenario in scenarios)
        {
            try
            {
                await CheckAdmissionOrderingAsync(scenario).WaitAsync(TimeSpan.FromSeconds(10));
                Console.WriteLine($"PASS admission-order/{scenario}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"FAIL admission-order/{scenario}: {error}");
                failures.Add(error);
            }
        }
        if (failures.Count != 0) throw new AggregateException("Registration cancellation ordering failed.", failures);
        return scenarios.Length;
    }

    private static async Task CheckAdmissionOrderingAsync(string scenario)
    {
        await using var fixture = await LifecycleFixture.CreateAsync(1);
        var owner = fixture.Owner;
        var state = owner._streams[0];
        var first = new StreamHandle(owner, 0, 1);
        using var cancel = new CancellationTokenSource();
        if (scenario == "canceled-before-delivery")
            await fixture.CloseAsync(first);
        else if (scenario != "canceled-live-limit")
        {
            var frame = await fixture.TakeAsync(first, 1, 1);
            fixture.Release(frame);
            await fixture.CloseAsync(first);
        }

        var pending = owner.AcquireStreamAsync(2, 1, cancel.Token);
        if (scenario is "canceled-before-retirement" or "canceled-before-observer")
        {
            owner.DrainNotifications();
            RequireWire(owner._pendingAdmission is not null && !pending.IsCompleted,
                "The fixture must first park behind a real retained lifetime.");
        }
        if (scenario == "canceled-before-observer")
        {
            cancel.Cancel();
            await ExpectAdmissionCanceledAsync(pending, cancel.Token);
            // A cold command is an owner observation boundary. It must not see a
            // canceled request occupying the sole registration wait slot.
            var observed = owner.OnWriterAsync(() => owner._pendingAdmission is null);
            owner.DrainNotifications();
            RequireWire(await observed, "Canceled admission must be reaped before the next owner operation observes state.");
            RequireWire(state.Generation == 1 && state.Outstanding == 16 && owner._identities.Count == 1,
                "Reaping registration must not release the retained DATA lifetime.");
            return;
        }

        // Cancel marks the original token before running callbacks, in LIFO order.
        // This later callback delays forwarding to the linked request token. The
        // writer must observe the original token rather than wait for that callback.
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var blocker = cancel.Token.UnsafeRegister(_ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(8)))
                throw new TimeoutException("Cancellation-order fixture was not released.");
        }, null);
        var cancellation = Task.Run(cancel.Cancel);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            RequireWire(cancel.IsCancellationRequested && !pending.IsCompleted,
                "The original token must be canceled while its forwarding callback is held.");
            if (scenario == "canceled-before-retirement")
                await fixture.UpdateAsync(1, 1, 16);
            else
                owner.DrainNotifications();
            await ExpectAdmissionCanceledAsync(pending, cancel.Token);
            RequireWire(state.Generation == 1 && owner._pendingAdmission is null,
                "Cancellation observed before claim cannot grant or retain an unobserved generation.");
            if (scenario == "canceled-live-limit")
            {
                RequireWire(owner._identities.Count == 1 && !state.Retired && state.RequestId == 1,
                    "Canceled registration cannot mutate the existing live identity.");
            }
            else
            {
                RequireWire(state.Retired && owner._identities.Count == 0,
                    "A canceled request cannot consume the released slot.");
                var replacement = await fixture.OpenAsync(3, 1);
                RequireWire(replacement.Generation == 2 && replacement.Slot == first.Slot,
                    "The successor must receive the same slot without a hidden generation increment.");
            }
        }
        finally
        {
            release.Set();
            await cancellation.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
#endif
