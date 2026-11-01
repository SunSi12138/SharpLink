namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private int _bindingInvalidationDirty;
    private int _bindingInvalidationScheduled;

    // Retirement work never detaches a facts owner. It requests one lossless intake
    // safe point per Session and wakes an idle reader without completing its buffer.
    private void ScheduleBindingInvalidations()
    {
        foreach (var connection in _connectionRegistry.SnapshotOwned())
            connection.RequestBindingMaintenance();
    }

    private void ScheduleBindingInvalidationNotifications()
    {
        Volatile.Write(ref _bindingInvalidationDirty, 1);
        if (Interlocked.CompareExchange(ref _bindingInvalidationScheduled, 1, 0) != 0) return;
        var task = PublishBindingInvalidationsAsync();
        try { TrackFrameworkTask(task, "BindingInvalidation"); }
        catch (InvalidOperationException) when (CurrentState != ServerState.Running) { }
    }

    private async Task PublishBindingInvalidationsAsync()
    {
        await Task.Yield();
        try
        {
            do
            {
                Interlocked.Exchange(ref _bindingInvalidationDirty, 0);
                foreach (var connection in _connectionRegistry.SnapshotOwned())
                {
                    while (connection.TryTakeBindingInvalidation(out var token))
                        await TrySendBindingInvalidationAsync(connection, token).ConfigureAwait(false);
                }
            }
            while (Volatile.Read(ref _bindingInvalidationDirty) != 0);
        }
        finally
        {
            Volatile.Write(ref _bindingInvalidationScheduled, 0);
            if (Volatile.Read(ref _bindingInvalidationDirty) != 0)
                ScheduleBindingInvalidationNotifications();
        }
    }

    private async ValueTask AcknowledgeBindingRetirementsAtIntakeAsync(ServerConnectionState connection)
    {
        if (!connection.TryDetachBindingRetirementSlice(out var tokens, out var count)) return;
        var queued = false;
        for (var index = 0; index < count; index++)
        {
            queued |= connection.QueueBindingInvalidation(tokens[index]);
            tokens[index] = 0;
        }
        if (queued) ScheduleBindingInvalidationNotifications();
        connection.CompleteBindingRetirementSlice();
        // Exactly one <=256-entry slice precedes this loop's actual Read/parse turn.
        // Yield bounds cold CPU scheduling too, but never substitutes for reading.
        await Task.Yield();
    }

    internal Action? BeforeBindingInvalidationPhysicalDrainForTesting { get; set; }

    internal async ValueTask TrySendBindingInvalidationAsync(ServerConnectionState connection, ulong token)
    {
        var session = connection.Session;
        // Queue admission already owns one control-frame reservation. Every early
        // terminal/drop path below returns it, independently of the facts owner.
        ServerBindingDeadline? deadlineOwner = null;
        try
        {
            if (!session.IsConnected || session.NegotiatedOptions is not { } options) return;
            var limits = options.Authority.Limits;
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
            try { connection.FailBindingOperation(exception); }
            finally
            {
                // A cancellable flush wait can end while its accepted frame/marker is
                // still physically owned. Keep the control-frame permit through the
                // exact outbound pump AND pending-enqueue cleanup, not whole disposal.
                try { BeforeBindingInvalidationPhysicalDrainForTesting?.Invoke(); }
                finally { await session.CloseAndDrainOutboundAsync(exception).ConfigureAwait(false); }
            }
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
