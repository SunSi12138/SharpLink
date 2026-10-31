namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private int _bindingInvalidationDirty;
    private int _bindingInvalidationScheduled;

    // Call after releasing the registry gate. One coalesced server worker owns all
    // notification work; it never allocates a task/tombstone for each retired token.
    private void ScheduleBindingInvalidations()
    {
        if (CurrentState != ServerState.Running)
            return; // Shutdown's admission/Session cut is authoritative; notifications are best effort.
        Volatile.Write(ref _bindingInvalidationDirty, 1);
        if (Interlocked.CompareExchange(ref _bindingInvalidationScheduled, 1, 0) != 0)
            return;
        var task = PublishBindingInvalidationsAsync();
        try
        {
            TrackFrameworkTask(task, "BindingInvalidation");
        }
        catch (InvalidOperationException) when (CurrentState != ServerState.Running)
        {
            // The supervisor observes a rejected task. A concurrent stop may already
            // have completed its drain; the worker below then drops remaining notifications.
        }
    }

    private async Task PublishBindingInvalidationsAsync()
    {
        await Task.Yield();
        try
        {
            var tokens = new ulong[256];
            do
            {
                Interlocked.Exchange(ref _bindingInvalidationDirty, 0);
                if (CurrentState != ServerState.Running)
                    return;
                foreach (var connection in _connectionRegistry.SnapshotOwned())
                {
                    if (connection.BindingTableOrNull is not { } table || table.IsClosed)
                        continue;
                    var cursor = 0;
                    while (cursor < table.SlotCount && !table.IsClosed && CurrentState == ServerState.Running)
                    {
                        // At most256 slot inspections and notifications per pump turn.
                        var count = table.TakeRetiredTokens(tokens, ref cursor);
                        for (var index = 0; index < count; index++)
                            await TrySendBindingInvalidationAsync(connection, tokens[index]).ConfigureAwait(false);
                        await Task.Yield();
                    }
                }
            }
            while (Volatile.Read(ref _bindingInvalidationDirty) != 0);
        }
        finally
        {
            Volatile.Write(ref _bindingInvalidationScheduled, 0);
            if (Volatile.Read(ref _bindingInvalidationDirty) != 0)
                ScheduleBindingInvalidations();
        }
    }

    private async ValueTask TrySendBindingInvalidationAsync(ServerConnectionState connection, ulong token)
    {
        var session = connection.Session;
        if (!session.IsConnected || session.NegotiatedOptions is not { } options)
            return;
        var limits = options.Authority.Limits;
        if (!connection.TryReserveBindingControlFrame(limits.QueuedControlFrames))
            return; // Best-effort overflow drops notification, never the authoritative cut.

        ServerBindingDeadline? deadlineOwner = null;
        try
        {
            var deadline = RpcDeadline.Create(TimeSpan.FromMilliseconds(limits.OperationDeadlineMs), _runtimeContext.TimeProvider);
            deadlineOwner = new ServerBindingDeadline(connection, deadline, _runtimeContext.TimeProvider);
            deadlineOwner.Arm();
            deadlineOwner.ThrowIfExpired();
            var writer = session.RentFrameWriter();
            var ownsWriter = true;
            try
            {
                using (writer.BeginPacketScope(ProtocolV2FrameType.BindingInvalidated, ProtocolV2FrameFlags.None, 0))
                    Protocol3BindingControlCodec.WriteBindingInvalidated(writer,
                        new Protocol3BindingInvalidated(token, Protocol3BindingInvalidationReason.RegistrationRetired), limits.Slots);
                ownsWriter = false;
                await session.SendPacketWithBackpressureAsync(writer, connection.ConnectionToken).ConfigureAwait(false);
                // Retain the frame quota until physical flush, just like the bounded Bind result path.
                await session.FlushSendQueueAsync(connection.ConnectionToken).ConfigureAwait(false);
                deadlineOwner.Complete();
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }
        catch (Exception exception)
        {
            connection.FailBindingOperation(exception);
        }
        finally
        {
            try
            {
                if (deadlineOwner is not null)
                    await deadlineOwner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                connection.FailBindingOperation(exception);
            }
            finally
            {
                connection.ReleaseBindingControlFrame();
            }
        }
    }
}
