using System.Runtime.CompilerServices;

namespace SharpLink.Server;

/// <summary>
/// Owns only cancellation work explicitly detached from the connection reader. The weak key is
/// the existing physical-connection call map, not a runtime-wide or ordinary invocation owner.
/// </summary>
internal static class ServerCancellationCallbackWork
{
    private static ConditionalWeakTable<StripedLongMap<ServerCallCancellationState>, Owner>? s_owners;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void QueueNotification(
        StripedLongMap<ServerCallCancellationState> calls,
        ServerCallCancellationLease callLease)
    {
        // This method owns the acquired lease on entry, including initialization failures.
        // Only a successfully scheduled worker may take over its release.
        Owner? startedOwner = null;
        Task work;
        try
        {
            var owners = LazyInitializer.EnsureInitialized(ref s_owners);
            var owner = owners.GetValue(calls, static _ => new Owner());
            owner.Begin();
            startedOwner = owner;
            work = ScheduleNotification(callLease);
        }
        catch (Exception schedulingFailure)
        {
            try
            {
                callLease.ReleaseUse();
            }
            catch (Exception cleanupFailure)
            {
                if (startedOwner is null)
                {
                    try
                    {
                        RecordCleanupFailure(calls, cleanupFailure);
                    }
                    catch (Exception publicationFailure)
                    {
                        throw new AggregateException(schedulingFailure, cleanupFailure, publicationFailure);
                    }
                }
                throw new AggregateException(schedulingFailure, cleanupFailure);
            }
            finally
            {
                // Keep the published batch pending through synchronous ReturnCore as well.
                // Complete records its sticky failure before releasing batch ownership.
                startedOwner?.Complete(schedulingFailure);
            }
            throw;
        }

        // The worker already owns release, even if observer setup subsequently fails.
        _ = startedOwner.ObserveAsync(work);
    }

    private static Task ScheduleNotification(ServerCallCancellationLease callLease)
        // Keep even compiler-generated closure construction inside QueueNotification's try.
        => Task.Run(() =>
        {
            try
            {
                callLease.State.NotifyInvocationCancellation();
            }
            finally
            {
                // This can include linked-source, payload and admission cleanup. The work
                // remains published until the actual Task, including this finally, finishes.
                callLease.ReleaseUse();
            }
        });

    internal static void RecordCleanupFailure(
        StripedLongMap<ServerCallCancellationState> calls,
        Exception failure)
    {
        var owner = LazyInitializer.EnsureInitialized(ref s_owners)
            .GetValue(calls, static _ => new Owner());
        owner.Begin();
        owner.Complete(failure);
    }

    internal static Task CaptureDrain(StripedLongMap<ServerCallCancellationState> calls)
    {
        var owners = Volatile.Read(ref s_owners);
        return owners is not null && owners.TryGetValue(calls, out var owner)
            ? owner.CaptureDrain()
            : Task.CompletedTask;
    }

    private sealed class Owner
    {
        private readonly Lock _gate = new();
        private int _active;
        private TaskCompletionSource? _drain;
        private string? _cleanupFailureType;

        internal void Begin()
        {
            lock (_gate)
            {
                if (_active == 0)
                    _drain = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _active++;
            }
        }

        internal Task CaptureDrain()
        {
            lock (_gate)
                return _drain?.Task ?? Task.CompletedTask;
        }

        internal async Task ObserveAsync(Task work)
        {
            Exception? failure = null;
            try
            {
                await work.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            Complete(failure);
        }

        internal void Complete(Exception? failure)
        {
            lock (_gate)
            {
                // NotifyInvocationCancellation preserves its existing best-effort callback
                // semantics. Only failed scheduling/physical cleanup reaches this sticky fault;
                // a later certificate must not mistake incomplete resource cleanup for success.
                // Keep a diagnostic, not the failed Task/exception and its potentially collectible
                // application graph. Ordinary user callback errors were already swallowed above.
                if (failure is not null)
                    _cleanupFailureType ??= failure.GetType().FullName ?? failure.GetType().Name;
                if (--_active != 0)
                    return;
                if (_cleanupFailureType is { } failureType)
                {
                    _drain!.TrySetException(new InvalidOperationException(
                        $"Physical cancellation work failed to schedule or release resources ({failureType})."));
                    _ = _drain.Task.Exception;
                }
                else
                    _drain!.TrySetResult();
            }
        }
    }
}
