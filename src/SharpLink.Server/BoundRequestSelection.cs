namespace SharpLink.Server;

// Static selections allocate nothing. Dynamic selections own one pre-business module
// protection, held by DispatchBoundRequestAsync through queue/decode/invocation/disposal.
internal readonly struct BoundRequestSelection
{
    private readonly ServiceRegistration? _staticRegistration;
    internal BoundInvocation? Invocation { get; }
    internal StaticBindingMethod Method { get; }
    internal ServiceRegistration Registration => Invocation?.Registration ?? _staticRegistration!;

    internal BoundRequestSelection(ServiceRegistration registration, StaticBindingMethod method,
        SharpLinkDynamicModuleLease protection)
    {
        Method = method;
        if (protection.IsAcquired) Invocation = new BoundInvocation(registration, protection);
        else _staticRegistration = registration;
    }

    internal void Dispose() => Invocation?.Dispose();
}

internal sealed class BoundInvocation(ServiceRegistration registration, SharpLinkDynamicModuleLease protection) : IDisposable
{
    private ServiceRegistration? _registration = registration;
    internal ServiceRegistration Registration => Volatile.Read(ref _registration)
        ?? throw new ObjectDisposedException(nameof(BoundInvocation));
    internal bool Protects(ServiceRegistration candidate) => ReferenceEquals(candidate, Volatile.Read(ref _registration));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _registration, null) is not null) protection.Dispose();
    }
}

internal sealed partial class SharpLinkServer
{
    private async ValueTask DispatchBoundRequestAsync(ServerConnectionState connection, long requestId,
        ProtocolV2FrameFlags flags, ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> cancellations, CancellationToken loopToken,
        AdmissionProgram? admission, int oneWayInputCount)
    {
        BoundRequestSelection selection = default;
        var transferred = false;
        try
        {
            var request = ReadRequestEnvelope(connection.Session, payload, flags);
            var failure = connection.StaticBindings.Acquire(unchecked((ulong)request.InterfaceHash),
                request.MethodHash, flags, out selection);
            if (failure == StaticBindingFailure.UnsupportedShape)
                throw StaticBindingWire.Malformed("Bound request has a conflicting/streaming shape.");
            if (failure == StaticBindingFailure.None && (flags & ProtocolV2FrameFlags.OneWay) != 0 &&
                selection.Method.Descriptor.ClientStreamCount != oneWayInputCount)
                throw StaticBindingWire.Malformed("OneWay wire input count conflicts with bound facts.");
            if (failure != StaticBindingFailure.None || IsDeadlineExceeded(request.RpcDeadline))
            {
                if ((flags & ProtocolV2FrameFlags.OneWay) != 0)
                    DrainRejectedOneWayStreams(connection.Session, requestId, oneWayInputCount);
                else
                    await connection.Session.SendRpcErrorWithBackpressureAsync(requestId,
                        IsDeadlineExceeded(request.RpcDeadline)
                            ? new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "Request deadline expired.")
                            : StaticBindingWire.Error(failure), connection.ConnectionToken).ConfigureAwait(false);
                return;
            }
            request = request with { BoundMethod = selection.Method, BoundCall = selection.Invocation };
            // Each core owns its admission-use release even when it throws synchronously.
            transferred = true;
            var work = (flags & ProtocolV2FrameFlags.OneWay) != 0
                ? DispatchOneWayRpc(connection, requestId, flags, payload, cancellations, loopToken, admission,
                    admittedClientStreamCount: oneWayInputCount, bindingSelection: selection, preparedRequest: request)
                : DispatchRpcAsync(connection, requestId, flags, payload, cancellations, loopToken, admission,
                    bindingSelection: selection, preparedRequest: request);
            payload = default;
            request = default;
            await work.ConfigureAwait(false);
        }
        catch (SharpLinkException failure) when (failure.Code == SharpLinkErrorCode.ProtocolViolation)
        {
            connection.Session.NotifyDisconnected(failure);
        }
        finally
        {
            if (!transferred) admission?.ReleaseUse();
            selection.Dispose();
        }
    }
}

