namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private readonly bool _writerOwnedStreamMode;
    private WriterOwnedStreamScheduler? _writerOwnedStreams;

    // This decision is immutable for a connection. The receive controller remains in place;
    // its sender APIs must never create a second independent connection-credit authority.
    internal bool UsesWriterOwnedStreams => _writerOwnedStreamMode && HasStreamFlowControl;
    internal WriterOwnedStreamScheduler? WriterOwnedStreams => Volatile.Read(ref _writerOwnedStreams);

    private StreamFlowController? GetLegacyStreamSendController()
    {
        if (UsesWriterOwnedStreams)
            throw new InvalidOperationException("Producer-side send credit is disabled on a writer-owned connection.");
        return Volatile.Read(ref _protocolState).FlowController;
    }

    private WriterOwnedStreamScheduler GetOrCreateWriterOwnedStreams()
    {
        if (!UsesWriterOwnedStreams)
            throw new InvalidOperationException("Writer-owned stream sending was not selected for this connection.");
        var existing = Volatile.Read(ref _writerOwnedStreams);
        if (existing is not null) return existing;
        lock (_pumpGate)
        {
            if (_writerOwnedStreams is not null) return _writerOwnedStreams;
            if (Volatile.Read(ref _terminal) is { } terminal) throw terminal.Exception;
            var options = NegotiatedOptions!;
            var pump = GetOrCreatePump();
            var owner = new WriterOwnedStreamScheduler(
                options.StreamReceiveWindowBytes,
                options.ConnectionReceiveWindowBytes,
                RuntimeContext.Protocol.MaxConcurrentStreamsPerConnection,
                slots: 16,
                maxPacketBytes: checked(ProtocolV2Constants.HeaderBytes + options.MaxFramePayloadBytes),
                maxCreditBytes: options.MaxFramePayloadBytes - sizeof(ushort),
                preparedByteLimit: RuntimeContext.FlowControl.MaxPreCreditSerializedBytes,
                signal: pump.SignalWriterReadySource,
                returnBuffer: ReturnBuffer,
                cancellationToken: LifetimeToken);
            try { pump.AttachWriterReadySource(owner); }
            catch (Exception error) { owner.Stopped(error); throw; }
            Volatile.Write(ref _writerOwnedStreams, owner);
            return owner;
        }
    }

    internal ValueTask AcquireStreamSendCreditAsync(
        long requestId,
        ushort streamId,
        int encodedBytes,
        CancellationToken cancellationToken)
    {
        var controller = GetLegacyStreamSendController();
        return controller is null
            ? ValueTask.CompletedTask
            : controller.AcquireSendCreditAsync(requestId, streamId, encodedBytes, cancellationToken);
    }

    internal void ReturnUnsentStreamCredit(long requestId, ushort streamId, int encodedBytes)
        => GetLegacyStreamSendController()?.ReturnUnsentCredit(requestId, streamId, encodedBytes);

    internal ValueTask<StreamFlowController.ResolvedSendCreditLease> AcquireStreamSendCreditAsync(
        StreamFlowController.ResolvedSendCreditLease lease,
        long requestId,
        ushort streamId,
        int encodedBytes,
        CancellationToken cancellationToken)
    {
        var controller = GetLegacyStreamSendController();
        if (controller is null)
            return new ValueTask<StreamFlowController.ResolvedSendCreditLease>(lease);

        if (lease.IsResolved)
        {
            var pending = controller.AcquireSendCreditAsync(in lease, encodedBytes, cancellationToken);
            if (pending.IsCompletedSuccessfully)
            {
                pending.GetAwaiter().GetResult();
                return new ValueTask<StreamFlowController.ResolvedSendCreditLease>(lease);
            }

            return AwaitResolvedSendCreditAsync(pending, lease);
        }

        return controller.AcquireSendCreditLeaseAsync(
            requestId,
            streamId,
            encodedBytes,
            cancellationToken);
    }

    private static async ValueTask<StreamFlowController.ResolvedSendCreditLease>
        AwaitResolvedSendCreditAsync(
            ValueTask pending,
            StreamFlowController.ResolvedSendCreditLease lease)
    {
        await pending.ConfigureAwait(false);
        return lease;
    }

    internal bool TryAcquireStreamSendCredit(
        StreamFlowController.ResolvedSendCreditLease lease,
        long requestId,
        ushort streamId,
        int encodedBytes,
        out StreamFlowController.ResolvedSendCreditLease resolvedLease)
    {
        var controller = GetLegacyStreamSendController();
        if (controller is null)
        {
            resolvedLease = lease;
            return true;
        }

        if (lease.IsResolved)
        {
            resolvedLease = lease;
            return controller.TryAcquireSendCredit(in lease, encodedBytes);
        }

        return controller.TryAcquireSendCreditLease(requestId, streamId, encodedBytes, out resolvedLease);
    }

    internal void ReturnUnsentStreamCredit(
        StreamFlowController.ResolvedSendCreditLease lease,
        long requestId,
        ushort streamId,
        int encodedBytes)
    {
        var controller = GetLegacyStreamSendController();
        if (controller is null)
            return;
        if (lease.IsResolved)
            controller.ReturnUnsentCredit(in lease, encodedBytes);
        else
            controller.ReturnUnsentCredit(requestId, streamId, encodedBytes);
    }

    internal void ApplyWindowUpdate(long requestId, in ProtocolV2WindowUpdate update)
    {
        var controller = Volatile.Read(ref _protocolState).FlowController ??
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.ProtocolState,
                "WindowUpdate was received without negotiated flow control.");
        var bytes = checked((int)update.Credit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        if (UsesWriterOwnedStreams)
        {
            // Before the first outgoing stream there is no sender state to credit. Do not
            // create an owner, or debit the legacy controller, for an unsolicited update.
            Volatile.Read(ref _writerOwnedStreams)?.PostWindowUpdate(requestId, update.StreamId, bytes);
            return;
        }
        controller.ApplyWindowUpdate(requestId, update.StreamId, bytes);
    }

    internal void CompleteSendStream(long requestId, ushort streamId, Exception? exception = null)
    {
        if (UsesWriterOwnedStreams)
        {
            Volatile.Read(ref _writerOwnedStreams)?.AbortStream(
                requestId, streamId, exception ?? new SharpLinkException(
                    SharpLinkErrorCode.ConnectionClosed, "The stream is closed."));
            return;
        }
        Volatile.Read(ref _protocolState).FlowController?.CompleteSendStream(requestId, streamId, exception);
    }

    internal void AbortSendStreams(long requestId, Exception exception)
    {
        if (UsesWriterOwnedStreams)
        {
            Volatile.Read(ref _writerOwnedStreams)?.AbortRequest(requestId, exception);
            return;
        }
        Volatile.Read(ref _protocolState).FlowController?.AbortSendStreams(requestId, exception);
    }
}
