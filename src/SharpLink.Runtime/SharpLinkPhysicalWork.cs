using System.Runtime.CompilerServices;

namespace SharpLink.Runtime;

// Only exceptional handoffs participate. Ordinary calls have no owner, lookup, gate or counter.
internal static class SharpLinkPhysicalWork
{
    private static readonly ConditionalWeakTable<Exception, FailureAssociation> Failures = new();
    private static readonly Lock DependencyGate = new();

    internal static OperationCanceledException CreateCancellationTerminal(OperationCanceledException exception)
        => exception is TaskCanceledException
            ? new TaskCanceledException(exception.Message, exception, exception.CancellationToken)
            : new OperationCanceledException(exception.Message, exception, exception.CancellationToken);

    internal static PhysicalOwner CreateOwner(Exception terminalException, CancellationTokenSource? lifetime = null)
        => CreateOwnerCore(terminalException, lifetime, logicalException: null, retainedPageWriter: null);

    internal static PhysicalOwner CreateCarrierOwner(
        Exception terminalException, Exception originalException, CancellationTokenSource? lifetime = null)
        => CreateOwnerCore(terminalException, lifetime, GetLogicalException(originalException), retainedPageWriter: null);

    // Distinct stream entry points preserve the existing ownership API and keep page
    // resources out of ordinary call handoffs (including baseline-compatible probes).
    internal static PhysicalOwner CreateStreamOwner(
        Exception terminalException, CancellationTokenSource? lifetime,
        EncodedStreamPageWriter? retainedPageWriter)
        => CreateOwnerCore(terminalException, lifetime, logicalException: null, retainedPageWriter);

    internal static PhysicalOwner CreateStreamCarrierOwner(
        Exception terminalException, Exception originalException, CancellationTokenSource? lifetime,
        EncodedStreamPageWriter? retainedPageWriter)
        => CreateOwnerCore(terminalException, lifetime, GetLogicalException(originalException), retainedPageWriter);

    private static PhysicalOwner CreateOwnerCore(
        Exception terminalException, CancellationTokenSource? lifetime, Exception? logicalException,
        EncodedStreamPageWriter? retainedPageWriter)
    {
        var owner = new PhysicalOwner(lifetime, retainedPageWriter);
        // Registration precedes scheduling even when the cleanup's first continuation runs early.
        Failures.Add(terminalException, new FailureAssociation(owner, logicalException));
        return owner;
    }

    // Only explicitly marked framework carriers are transparent to application exception mapping.
    // Keep this metadata after cleanup: mapping can race already-completed physical work.
    internal static Exception GetLogicalException(Exception exception)
        => Failures.TryGetValue(exception, out var association) ? association.LogicalException ?? exception : exception;

    internal static void TrackAbandonedUserWork(Exception terminalException, Task work)
        => CreateOwner(terminalException).Track(work);

    internal static Task? GetAbandonedUserWork(Exception terminalException)
        => Failures.TryGetValue(terminalException, out var association) ? association.Owner.Completion : null;

    internal static void PreserveAbandonedUserWork(Exception source, Exception replacement)
    {
        if (!ReferenceEquals(source, replacement) && Failures.TryGetValue(source, out var association))
            // An arbitration replacement is a new logical terminal, not a transparent carrier.
            Failures.Add(replacement, new FailureAssociation(association.Owner, logicalException: null));
    }

    internal static Exception? TransferLifetime(Exception exception, CancellationTokenSource? lifetime, bool cancelLifetime = true)
        => TransferLifetimeCore(exception, lifetime, cancelLifetime, retainedPageWriter: null);

    internal static Exception? TransferStreamLifetime(
        Exception exception, CancellationTokenSource? lifetime, EncodedStreamPageWriter? retainedPageWriter)
        => TransferLifetimeCore(exception, lifetime, cancelLifetime: true, retainedPageWriter);

    private static Exception? TransferLifetimeCore(
        Exception exception, CancellationTokenSource? lifetime, bool cancelLifetime,
        EncodedStreamPageWriter? retainedPageWriter)
    {
        if (!Failures.TryGetValue(exception, out _))
            return null;
        // All production associations originate at these framework terminal boundaries. Arbitrary
        // application exception subclasses are preserved as InnerException, not cloned by reflection.
        Exception terminal = exception switch
        {
            OperationCanceledException canceled => CreateCancellationTerminal(canceled),
            SharpLinkException failure => new SharpLinkException(failure.Code, failure.DetailCode, failure.Message, failure),
            _ => new Exception(exception.Message, exception)
        };
        var owner = CreateOwnerCore(terminal, lifetime, GetLogicalException(exception), retainedPageWriter);
        owner.Track(CompleteLifetimeAsync(owner, exception, lifetime, cancelLifetime));
        return terminal;
    }

    private static async Task CompleteLifetimeAsync(PhysicalOwner owner, Exception exception, CancellationTokenSource? lifetime, bool cancelLifetime)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (cancelLifetime)
        {
            try { lifetime?.Cancel(); }
            catch { /* User callbacks cannot replace the selected terminal. */ }
        }
        await owner.WaitForDescendantAsync(exception).ConfigureAwait(false);
    }

    private sealed class FailureAssociation(PhysicalOwner owner, Exception? logicalException)
    {
        internal PhysicalOwner Owner { get; } = owner;
        internal Exception? LogicalException { get; } = logicalException;
    }

    internal sealed class PhysicalOwner
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _lifetime;
        private EncodedStreamPageWriter? _retainedPageWriter;
        private CompletionGroup _group;
        private DependencyWait? _waiting;
        private bool _tracked;
        private bool _cleanupFinished;

        internal PhysicalOwner(CancellationTokenSource? lifetime, EncodedStreamPageWriter? retainedPageWriter = null)
        {
            _lifetime = lifetime;
            _retainedPageWriter = retainedPageWriter;
            _group = new CompletionGroup(this);
        }

        internal Task Completion => _completion.Task;

        internal void Track(Task work)
        {
            lock (DependencyGate)
            {
                if (_tracked)
                    throw new InvalidOperationException("A physical owner can track only one local cleanup.");
                _tracked = true;
            }
            _ = ObserveAsync(work);
        }

        internal async Task WaitForDescendantAsync(Exception exception)
        {
            if (!Failures.TryGetValue(exception, out var association))
                return;
            var descendant = association.Owner;
            DependencyWait wait;
            lock (DependencyGate)
            {
                if (_cleanupFinished || _waiting is not null)
                    throw new InvalidOperationException("Physical descendant waits must be sequential and precede local completion.");
                if (ReferenceEquals(_group, descendant._group) || descendant.Completion.IsCompleted)
                    return;
                wait = new DependencyWait(descendant);
                _waiting = wait;
                MergeCycle(this, descendant);
            }
            try
            {
                // An SCC releases only its internal metadata waits. Its external completions and
                // lifetime sources remain held until every member's real sequential cleanup ends.
                await Task.WhenAny(descendant.Completion, wait.InternalEdgeReleased.Task).ConfigureAwait(false);
            }
            finally
            {
                lock (DependencyGate)
                    _waiting = null;
            }
        }

        private async Task ObserveAsync(Task work)
        {
            try { await work.ConfigureAwait(false); }
            catch (Exception exception) { await WaitForDescendantAsync(exception).ConfigureAwait(false); }
            finally { CompleteLocalCleanup(); }
        }

        private void CompleteLocalCleanup()
        {
            PhysicalOwner[]? finished = null;
            lock (DependencyGate)
            {
                _cleanupFinished = true;
                if (!_group.Completing && _group.Members.TrueForAll(static member => member._cleanupFinished))
                {
                    _group.Completing = true;
                    finished = [.. _group.Members];
                }
            }
            if (finished is null)
                return;
            _ = FinalizeGroupAsync(finished);
        }

        private static async Task FinalizeGroupAsync(PhysicalOwner[] finished)
        {
            // A synchronously completed cleanup must still not run a potentially blocking linked
            // registration disposal on the public terminal continuation.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            // No member can add another dependency after its local cleanup finished. This closed
            // group cannot enter another cycle while lifetime sources are disposed outside the gate.
            // Finish ALL linked-source disposals before returning ANY producer page.
            // Disposal can join an in-progress cancellation callback in another SCC member.
            // Completion is a physical join, not a second public terminal: isolate cleanup
            // faults so one terminated cleanup cannot strand the rest of the closed group.
            foreach (var member in finished)
            {
                var lifetime = member._lifetime;
                member._lifetime = null;
                try { lifetime?.Dispose(); }
                catch { /* Preserve the already-selected terminal and finish the whole group. */ }
            }
            foreach (var member in finished)
            {
                var pageWriter = member._retainedPageWriter;
                member._retainedPageWriter = null;
                try { pageWriter?.Dispose(); }
                catch { /* Framework page release is single-shot even if pool cleanup faults. */ }
            }
            lock (DependencyGate)
                finished[0]._group.Members.Clear();
            foreach (var member in finished)
                member._completion.TrySetResult();
        }

        private static void MergeCycle(PhysicalOwner source, PhysicalOwner descendant)
        {
            var reachable = Reachable(descendant._group);
            if (!reachable.Contains(source._group))
                return;
            var group = source._group;
            var component = reachable.Where(candidate => Reachable(candidate).Contains(group)).ToArray();
            foreach (var memberGroup in component)
            {
                if (ReferenceEquals(memberGroup, group))
                    continue;
                foreach (var member in memberGroup.Members)
                {
                    member._group = group;
                    group.Members.Add(member);
                }
                memberGroup.Members.Clear();
            }
            foreach (var member in group.Members)
            {
                if (member._waiting is { } edge && ReferenceEquals(edge.Descendant._group, group))
                    edge.InternalEdgeReleased.TrySetResult();
            }
        }

        private static HashSet<CompletionGroup> Reachable(CompletionGroup start)
        {
            var reached = new HashSet<CompletionGroup>();
            var pending = new Stack<CompletionGroup>();
            pending.Push(start);
            while (pending.TryPop(out var group))
            {
                if (!reached.Add(group))
                    continue;
                foreach (var member in group.Members)
                {
                    if (member._waiting is { } edge && !ReferenceEquals(edge.Descendant._group, group))
                        pending.Push(edge.Descendant._group);
                }
            }
            return reached;
        }

        private sealed class CompletionGroup(PhysicalOwner first)
        {
            internal List<PhysicalOwner> Members { get; } = [first];
            internal bool Completing;
        }

        private sealed class DependencyWait(PhysicalOwner descendant)
        {
            internal PhysicalOwner Descendant { get; } = descendant;
            internal TaskCompletionSource InternalEdgeReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
