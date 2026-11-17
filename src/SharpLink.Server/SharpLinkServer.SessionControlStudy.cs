namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    // Variant S test-only observation points. Both are null in an ordinary server process.
    internal Action? PinnedSessionCutCommittedForTesting { get; set; }
    internal Func<Task>? PinnedSessionRetirementNotificationBarrierForTesting { get; set; }
    internal Action<RpcSession>? PinnedSessionRetirementNotificationSentForTesting { get; set; }
    internal int PinnedSessionRetirementNotificationCountForTesting
        => Volatile.Read(ref _pinnedSessionRetirementNotificationCount);

    private long _pinnedSessionRetirementGeneration;
    private int _pinnedSessionRetirementNotificationCount;

    /// <summary>
    /// Called after the Replace commit and after the old module admission gate is closed, but
    /// outside <c>_registryGate</c>. The study variant S has already published the new registry
    /// and pinned old sessions to their bootstrap snapshot; this method only schedules the
    /// control notification that makes the client establish a new physical Session.
    /// </summary>
    private void OnPinnedGroupCutCommitted(SharpLinkDynamicModule oldModule)
    {
        PinnedSessionCutCommittedForTesting?.Invoke();
        TrackFrameworkTask(
            RetirePinnedSessionsAsync(oldModule),
            "PinnedSessionRetirement");
    }

    private async Task RetirePinnedSessionsAsync(SharpLinkDynamicModule oldModule)
    {
        if (PinnedSessionRetirementNotificationBarrierForTesting is { } barrier)
            await barrier().ConfigureAwait(false);

        var connections = _connectionRegistry.SnapshotActive();
        for (var index = 0; index < connections.Length; index++)
        {
            var connection = connections[index];
            if (connection.LifecycleState != ServerConnectionLifecycleState.Ready ||
                !connection.PinsModule(oldModule))
            {
                continue;
            }

            if ((connection.Session.NegotiatedCapabilities & ProtocolV2Capabilities.SessionRefresh) == 0)
                continue;

            var generation = checked((ulong)Interlocked.Increment(
                ref _pinnedSessionRetirementGeneration) + 1_000_000_000UL);
            var request = new ProtocolV2SessionRefreshRequested(
                _desiredSessionServerInstanceId,
                generation);
            try
            {
                await connection.Session.SendSessionRefreshRequestedWithBackpressureAsync(
                    request,
                    connection.ConnectionToken).ConfigureAwait(false);
                Interlocked.Increment(ref _pinnedSessionRetirementNotificationCount);
                PinnedSessionRetirementNotificationSentForTesting?.Invoke(connection.Session);
            }
            catch (Exception exception) when (IsExpectedSessionShutdownException(exception))
            {
            }
        }
    }
}
