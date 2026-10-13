namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private ValueTask SendRpcCall<TRequest>(
        RpcSession session,
        RpcMethodDescriptor method,
        ClientContractBinding? binding,
        long requestId,
        ProtocolV2FrameFlags flags,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        RpcDeadline deadline,
        SharpLinkMetadata? metadata,
        bool observeEmission = false,
        CancellationToken cancellationToken = default,
        IRequestEmissionFailureObserver? failureObserver = null,
        PendingRequestTable? publicationTable = null)
    {
        binding ??= ResolveLocalBinding(method);
        if (method.ContractId != binding.ContractId)
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "The method does not belong to this proxy contract.");
        var identity = new ProtocolV2RequestIdentity(method.ContractId, method.MethodId,
            binding.RpcAssemblyHash, method.Kind, checked((ushort)method.ClientStreamCount),
            binding.Receipt, binding.Requirement);
        var hasMetadata = metadata is { Count: > 0 };
        var metadataLength = 0;
        if (deadline.HasValue)
            flags |= ProtocolV2FrameFlags.HasTimeBudget;
        if (hasMetadata)
        {
            if ((session.NegotiatedCapabilities & ProtocolV2Capabilities.Metadata) == 0)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.Unimplemented,
                    "The connected server did not negotiate request metadata support.");
            }
            metadataLength = ProtocolV2PayloadCodec.GetMetadataPayloadLength(metadata!);
            if (metadataLength > _protocolOptions.MaxMetadataBytes)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ResourceExhausted,
                    $"Request metadata exceeds {_protocolOptions.MaxMetadataBytes} bytes.");
            }
            flags |= ProtocolV2FrameFlags.HasMetadata;
        }

        var writer = session.RentFrameWriter();
        var ownsWriter = true;
        try
        {
            using (writer.BeginPacketScope(
                       ProtocolV2FrameType.Request,
                       flags,
                       unchecked((ulong)requestId)))
            {
                ProtocolV2RequestIdentityCodec.Write(writer, identity);
                if (deadline.HasValue)
                {
                    var span = writer.GetSpan(sizeof(long));
                    BinaryPrimitives.WriteInt64LittleEndian(span,
                        deadline.GetRemaining(_runtimeContext.TimeProvider).Ticks);
                    writer.Advance(sizeof(long));
                }
                if (hasMetadata)
                {
                    ProtocolV2PayloadCodec.WriteVarUInt32(writer, checked((uint)metadataLength));
                    ProtocolV2PayloadCodec.WriteMetadata(writer, metadata!);
                }
                requestCodec.Serialize(request, writer);
            }

            if (publicationTable is { } table)
            {
                // Preparation runs outside the completion gate twice over: it invokes the
                // compression provider, which is user code, and it has to surface its faults before
                // the caller decides whether this Request is published. Hand ownership over first -
                // preparation returns the writer on every failure path.
                ownsWriter = false;
                var prepared = session.PrepareOutboundFrame(writer, cancellationToken);

                if (!table.TryPublishRequest(
                        requestId,
                        session,
                        prepared,
                        observeEmission,
                        cancellationToken,
                        failureObserver,
                        out var emission,
                        out var rejection))
                {
                    if (rejection is not null)
                    {
                        // The session refused admission and already returned the frame; the caller
                        // observes the same failure it would have seen from a throwing send.
                        throw rejection;
                    }

                    // The call already reached its terminal decision while this Request was still
                    // being serialized. Publishing it now would deliver a Request after the cancel
                    // that terminal decision just emitted - a cancel the peer discards, followed by
                    // a Request it happily dispatches. Drop the frame instead; the caller observes
                    // the terminal reason through its pending operation.
                    _runtimeContext.Buffers.Return(prepared);
                    return ValueTask.CompletedTask;
                }
                return emission;
            }

            // Plain OneWay is the only dispatch shape without a pending entry, so it is also the
            // only one that has to refuse its own Request here: nothing else can retract a frame
            // whose budget already elapsed while its payload was still being serialized, and the
            // peer would dispatch work whose caller has already failed. This first check only
            // spares an already expired frame the compression work; the authoritative refusal is
            // the one taken after preparation, immediately before admission.
            if (deadline.HasValue &&
                deadline.GetRemaining(_runtimeContext.TimeProvider) <= TimeSpan.Zero)
            {
                ownsWriter = false;
                _runtimeContext.Buffers.Return(writer);
                throw CreateDeadlineExceededException();
            }

            ownsWriter = false;
            var plain = session.PrepareOutboundFrame(writer, cancellationToken);

            // Preparation runs the user-provided compression provider, so the budget can elapse
            // between the check above and the linearization point below. A Request that is already
            // expired when it would be admitted must never be published: the peer would execute a
            // one-way call whose caller has already failed, and the cancel that follows it cannot
            // retract work that already ran.
            if (deadline.HasValue && deadline.IsExpired(_runtimeContext.TimeProvider))
            {
                _runtimeContext.Buffers.Return(plain);
                throw CreateDeadlineExceededException();
            }

            if (!session.TryEnqueuePreparedFrame(
                    plain,
                    observeEmission,
                    cancellationToken,
                    failureObserver,
                    out var plainEmission,
                    out var plainRejection))
            {
                throw plainRejection!;
            }

            return plainEmission;
        }
        finally
        {
            if (ownsWriter)
                _runtimeContext.Buffers.Return(writer);
        }
    }
}
