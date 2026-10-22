using System.Runtime.CompilerServices;
using System.Linq;

namespace SharpLink.UnitTests.Runtime;

public class AbandonedPhysicalWorkCycleTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    [Test]
    public async Task CycleRetainsEveryLifetimeUntilEveryLocalCleanupCompletes()
    {
        using var a = new Node();
        using var b = new Node();
        var firstWait = a.Owner.WaitForDescendantAsync(b.Terminal);
        var secondWait = b.Owner.WaitForDescendantAsync(a.Terminal);
        await Task.WhenAll(firstWait, secondWait).WaitAsync(Watchdog);
        // Completed Task observation marks A locally finished synchronously, before this assertion.
        a.Owner.Track(Task.CompletedTask);
        Ensure(!a.Completion.IsCompleted && !b.Completion.IsCompleted, "all component owners remain pending");
        Ensure(a.IsLifetimeAlive && b.IsLifetimeAlive, "all component token sources remain alive");
        b.Owner.Track(Task.CompletedTask);
        await Task.WhenAll(a.Completion, b.Completion).WaitAsync(Watchdog);
        Ensure(!a.IsLifetimeAlive && !b.IsLifetimeAlive, "all sources release before external completion");
    }

    [Test]
    public async Task MergedGroupTracksOutgoingEdgesFromLocallyCompletedMembersPeers()
    {
        using var a = new Node();
        using var b = new Node();
        using var c = new Node();
        using var d = new Node();
        using var unrelated = new Node();
        var hold = Signal();
        var releaseC = Signal();
        var aLocalDone = Signal();
        var bWaitingC = Signal();
        var cInsideCleanup = Signal();
        unrelated.Owner.Track(hold.Task);
        a.Owner.Track(First());
        b.Owner.Track(Second());
        try
        {
            await aLocalDone.Task.WaitAsync(Watchdog);
            await bWaitingC.Task.WaitAsync(Watchdog);
            d.Owner.Track(d.Owner.WaitForDescendantAsync(a.Terminal));
            c.Owner.Track(Third());
            await cInsideCleanup.Task.WaitAsync(Watchdog);
            Ensure(new[] { a, b, c, d }.All(node => !node.Completion.IsCompleted && node.IsLifetimeAlive),
                "the second SCC includes the old group's locally completed member");
            releaseC.TrySetResult();
            await Task.WhenAll(a.Completion, b.Completion, c.Completion, d.Completion).WaitAsync(Watchdog);
            Ensure(!unrelated.Completion.IsCompleted, "an unrelated abandoned call is not a global drain dependency");
        }
        finally { releaseC.TrySetResult(); hold.TrySetResult(); }

        async Task First() { await a.Owner.WaitForDescendantAsync(b.Terminal); aLocalDone.TrySetResult(); }
        async Task Second()
        {
            await b.Owner.WaitForDescendantAsync(a.Terminal);
            var wait = b.Owner.WaitForDescendantAsync(c.Terminal);
            bWaitingC.TrySetResult();
            await wait;
        }
        async Task Third()
        {
            await c.Owner.WaitForDescendantAsync(d.Terminal);
            cInsideCleanup.TrySetResult();
            await releaseC.Task;
        }
    }

    [Test]
    public async Task CycleMergesEveryReturnPathButNotUnrelatedDownstreamWork()
    {
        using var x = new Node();
        using var y = new Node();
        using var c = new Node();
        using var d = new Node();
        using var back = new Node();
        var xWaiting = Signal();
        var yWaiting = Signal();
        var enteredBack = Signal();
        var releaseBack = Signal();
        x.Owner.Track(Branch(x, y, c, xWaiting));
        y.Owner.Track(Branch(y, x, d, yWaiting));
        try
        {
            await Task.WhenAll(xWaiting.Task, yWaiting.Task).WaitAsync(Watchdog);
            c.Owner.Track(c.Owner.WaitForDescendantAsync(back.Terminal));
            d.Owner.Track(d.Owner.WaitForDescendantAsync(back.Terminal));
            back.Owner.Track(Return());
            await enteredBack.Task.WaitAsync(Watchdog);
            Ensure(new[] { x, y, c, d, back }.All(node => !node.Completion.IsCompleted && node.IsLifetimeAlive),
                "both branches belong to the merged strongly connected component");
            releaseBack.TrySetResult();
            await Task.WhenAll(x.Completion, y.Completion, c.Completion, d.Completion, back.Completion).WaitAsync(Watchdog);
        }
        finally { releaseBack.TrySetResult(); }

        static async Task Branch(Node node, Node peer, Node next, TaskCompletionSource waiting)
        {
            await node.Owner.WaitForDescendantAsync(peer.Terminal);
            var wait = node.Owner.WaitForDescendantAsync(next.Terminal);
            waiting.TrySetResult();
            await wait;
        }
        async Task Return()
        {
            await back.Owner.WaitForDescendantAsync(x.Terminal);
            enteredBack.TrySetResult();
            await releaseBack.Task;
        }
    }

    [Test]
    public async Task BlockingLinkedSourceDisposalDoesNotHoldDependencyGate()
    {
        using var parent = new CancellationTokenSource();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
        using var release = new ManualResetEventSlim(false);
        var entered = Signal();
        using var callback = lifetime.Token.Register(() => { entered.TrySetResult(); release.Wait(); });
        await LongRunningTestWorker.Run(() =>
        {
            var cancellation = LongRunningTestWorker.Run(() => parent.Cancel());
            try
            {
                LongRunningTestWorker.WaitForPhase(entered.Task, Watchdog);
                var terminal = new InvalidOperationException("blocked finalizer");
                var owner = SharpLinkPhysicalWork.CreateOwner(terminal, lifetime);
                owner.Track(Task.CompletedTask);
                using var lateDependent = new Node();
                lateDependent.Owner.Track(lateDependent.Owner.WaitForDescendantAsync(terminal));
                var frozen = false;
                try { LongRunningTestWorker.WaitForPhase(owner.WaitForDescendantAsync(lateDependent.Terminal), Watchdog); }
                catch (InvalidOperationException) { frozen = true; }
                Ensure(frozen, "a locally completed owner cannot mutate finalizing group membership");
                using var unrelated = new Node();
                unrelated.Owner.Track(Task.CompletedTask);
                LongRunningTestWorker.WaitForPhase(unrelated.Completion, Watchdog);
                Ensure(!owner.Completion.IsCompleted && !lateDependent.Completion.IsCompleted,
                    "linked source propagation must finish before this owner or a late dependent can release");
                release.Set();
                LongRunningTestWorker.WaitForPhase(Task.WhenAll(cancellation, owner.Completion, lateDependent.Completion), Watchdog);
            }
            finally { release.Set(); LongRunningTestWorker.Join(cancellation, Watchdog); }
        });
    }

    [Test]
    public async Task RetainedTerminalDoesNotRetainCompletedTaskPayloadOrLifetime()
    {
        var probe = await CompletePayload();
        for (var index = 0; index < 4; index++)
        {
            await Task.Yield();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Ensure(!probe.Payload.IsAlive && !probe.Lifetime.IsAlive, "completed cold owner must release user roots");
        GC.KeepAlive(probe.Terminal);
    }

    [Test]
    public async Task RetainedCycleTerminalDoesNotRetainEitherMembersPayloads()
    {
        var probe = await CompleteCyclePayloads();
        for (var index = 0; index < 4; index++)
        {
            await Task.Yield();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Ensure(probe.Roots.All(static root => !root.IsAlive), "completed SCC must not retain either member's user graph");
        GC.KeepAlive(probe.Terminal);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(Exception Terminal, WeakReference[] Roots)> CompleteCyclePayloads()
    {
        var aTerminal = new InvalidOperationException("retained group member");
        var bTerminal = new InvalidOperationException("other group member");
        var aLifetime = new CancellationTokenSource();
        var bLifetime = new CancellationTokenSource();
        object[] aPayload = [new object(), new object(), new object()];
        object[] bPayload = [new object(), new object(), new object()];
        var roots = aPayload.Concat(bPayload).Append(aLifetime).Append(bLifetime).Select(static value => new WeakReference(value)).ToArray();
        var a = SharpLinkPhysicalWork.CreateOwner(aTerminal, aLifetime);
        var b = SharpLinkPhysicalWork.CreateOwner(bTerminal, bLifetime);
        a.Track(Local(a, bTerminal, aPayload));
        b.Track(Local(b, aTerminal, bPayload));
        await Task.WhenAll(a.Completion, b.Completion);
        return (aTerminal, roots);

        static async Task Local(SharpLinkPhysicalWork.PhysicalOwner owner, Exception other, object[] payload)
        {
            await owner.WaitForDescendantAsync(other);
            GC.KeepAlive(payload);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(Exception Terminal, WeakReference Payload, WeakReference Lifetime)> CompletePayload()
    {
        var terminal = new InvalidOperationException("retained terminal");
        var payload = new object();
        var lifetime = new CancellationTokenSource();
        var work = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = SharpLinkPhysicalWork.CreateOwner(terminal, lifetime);
        owner.Track(work.Task);
        work.SetResult(payload);
        await owner.Completion;
        return (terminal, new WeakReference(payload), new WeakReference(lifetime));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Ensure(bool value, string message) { if (!value) throw new Exception(message); }

    private sealed class Node : IDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        internal Exception Terminal { get; } = new InvalidOperationException("terminal");
        internal SharpLinkPhysicalWork.PhysicalOwner Owner { get; }
        internal Task Completion => Owner.Completion;
        internal bool IsLifetimeAlive
        {
            get { try { _ = _lifetime.Token.WaitHandle; return true; } catch (ObjectDisposedException) { return false; } }
        }
        internal Node() => Owner = SharpLinkPhysicalWork.CreateOwner(Terminal, _lifetime);
        public void Dispose() => _lifetime.Dispose();
    }
}
