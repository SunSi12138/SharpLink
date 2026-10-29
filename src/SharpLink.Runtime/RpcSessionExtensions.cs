namespace SharpLink.Runtime;

/// <summary>Writes Protocol v2 control, response, and streaming frames through an RPC session.</summary>
internal static class RpcSessionExtensions
{
    extension(RpcSession session)
    {
        /// <summary>Sends and flushes a client handshake request.</summary>
        internal async ValueTask SendHandshakeRequestAndFlushAsync(
            ProtocolV2HandshakeRequest request,
            SharpLinkProtocolOptions limits,
            CancellationToken cancellationToken = default)
        {
            var writer = session.RentFrameWriter();
            var ownsWriter = true;
            try
            {
                var token = writer.BeginPacket(
                    ProtocolV2FrameType.HandshakeRequest, ProtocolV2FrameFlags.None, 0);
                ProtocolV2PayloadCodec.WriteHandshakeRequest(writer, request, limits);
                writer.EndPacket(token);
                ownsWriter = false;
                await session
                    .SendPacketAndFlushAsync(writer, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        /// <summary>Sends and flushes a successful server handshake response.</summary>
        internal async ValueTask SendHandshakeResponseAndFlushAsync(
            ProtocolV2HandshakeResponse response,
            CancellationToken cancellationToken = default)
        {
            var writer = session.RentFrameWriter();
            var ownsWriter = true;
            try
            {
                var token = writer.BeginPacket(
                    ProtocolV2FrameType.HandshakeResponse, ProtocolV2FrameFlags.None, 0);
                ProtocolV2PayloadCodec.WriteHandshakeResponse(writer, response);
                writer.EndPacket(token);
                ownsWriter = false;
                await session
                    .SendPacketAndFlushAsync(writer, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        /// <summary>Sends and flushes a bounded handshake rejection.</summary>
        internal ValueTask SendHandshakeErrorAndFlushAsync(
            SharpLinkErrorCode code,
            string? message,
            int maxMessageBytes,
            CancellationToken cancellationToken = default)
            => SendErrorFrameAndFlushAsync(
                session,
                ProtocolV2FrameType.HandshakeResponse,
                0,
                code,
                message,
                maxMessageBytes,
                cancellationToken);

        /// <summary>Queues a payload-free protocol frame for sending.</summary>
        internal void SendPacketAsync(ProtocolV2FrameType frameType, ProtocolV2FrameFlags flags, long requestId)
        {
            var writer = session.RentFrameWriter();
            var ownsWriter = true;
            try
            {
                writer.WritePacket(frameType, flags, unchecked((ulong)requestId));
                ownsWriter = false;
                session.SendPacket(writer);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        internal async ValueTask SendPacketWithBackpressureAsync(
            ProtocolV2FrameType frameType,
            ProtocolV2FrameFlags flags,
            long requestId,
            CancellationToken cancellationToken = default)
        {
            var writer = session.RentFrameWriter();
            var ownsWriter = true;
            try
            {
                writer.WritePacket(frameType, flags, unchecked((ulong)requestId));
                ownsWriter = false;
                await session
                    .SendPacketWithBackpressureAsync(writer, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        internal ValueTask SendRpcErrorWithBackpressureAsync(
            long requestId,
            SharpLinkException exception,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(exception);
            return SendErrorFrameWithBackpressureAsync(
                session,
                ProtocolV2FrameType.Response,
                requestId,
                exception.Code,
                exception.DetailCode,
                exception.Message,
                GetMaxErrorMessageBytes(session),
                cancellationToken);
        }

        /// <summary>Sends a negotiated protocol cancellation for one active request.</summary>
        /// <param name="requestId">The non-zero request identifier to cancel.</param>
        /// <param name="reason">The stable client-side cancellation reason.</param>
        internal void SendCancelAsync(long requestId, ProtocolV2CancelReason reason)
        {
            var runtimeSession = session;
            if ((runtimeSession.NegotiatedCapabilities & ProtocolV2Capabilities.CancellationReason) == 0)
            {
                session.SendPacketAsync(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, requestId);
                return;
            }

            var writer = runtimeSession.RentFrameWriter();
            var ownsWriter = true;
            try
            {
                using (writer.BeginPacketScope(
                           ProtocolV2FrameType.Cancel,
                           ProtocolV2FrameFlags.None,
                           unchecked((ulong)requestId)))
                {
                    ProtocolV2PayloadCodec.WriteCancelReason(writer, reason);
                }
                ownsWriter = false;
                runtimeSession.SendPacket(writer);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        internal ProtocolV2CancelReason ReadNegotiatedCancelReason(ReadOnlySequence<byte> payload)
        {
            var hasReasonCapability =
                (session.NegotiatedCapabilities &
                 ProtocolV2Capabilities.CancellationReason) != 0;
            if (!hasReasonCapability)
            {
                if (!payload.IsEmpty)
                {
                    throw ProtocolV2FrameParser.Violation(
                        "Cancel reason payload was sent without negotiating CancellationReason.");
                }
                return ProtocolV2CancelReason.Unspecified;
            }

            if (payload.IsEmpty)
            {
                throw ProtocolV2FrameParser.Violation(
                    "Cancel reason payload is required after negotiating CancellationReason.");
            }
            return ProtocolV2PayloadCodec.ReadCancelReason(payload);
        }

        internal ValueTask SendPingWithBackpressureAsync(CancellationToken cancellationToken = default)
            => SendTimestampFrameWithBackpressureAsync(
                session,
                ProtocolV2FrameType.Ping,
                session.RuntimeContext.TimeProvider.GetTimestamp(),
                cancellationToken);

        internal ValueTask SendPongWithBackpressureAsync(
            long timestamp,
            CancellationToken cancellationToken = default)
            => SendTimestampFrameWithBackpressureAsync(
                session,
                ProtocolV2FrameType.Pong,
                timestamp,
                cancellationToken);

        /// <summary>Sends a protocol-level health request on a negotiated session.</summary>
        /// <param name="requestId">The non-zero health request identifier.</param>
        internal void SendHealthCheck(long requestId)
            => session.SendPacketAsync(
                ProtocolV2FrameType.HealthCheck,
                ProtocolV2FrameFlags.None,
                requestId);

        internal async ValueTask SendHealthResponseWithBackpressureAsync(
            long requestId,
            SharpLinkHealthStatus status,
            CancellationToken cancellationToken = default)
        {
            IRpcByteBufferWriter? writer = null;
            var ownsWriter = true;
            try
            {
                writer = session.RentFrameWriter();
                using (writer.BeginPacketScope(
                           ProtocolV2FrameType.HealthResponse,
                           ProtocolV2FrameFlags.None,
                           unchecked((ulong)requestId)))
                {
                    ProtocolV2PayloadCodec.WriteHealthResponse(writer, status);
                }
                ownsWriter = false;
                await session
                    .SendPacketWithBackpressureAsync(writer, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (ownsWriter && writer is not null)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        /// <summary>Sends and flushes a connection-drain frame with the last accepted request.</summary>
        internal async ValueTask SendGoAwayAsync(
            long lastAcceptedRequestId,
            SharpLinkErrorCode code,
            string? message,
            CancellationToken cancellationToken = default)
        {
            var writer = session.RentFrameWriter();
            var ownsWriter = true;
            try
            {
                var token = writer.BeginPacket(
                    ProtocolV2FrameType.GoAway,
                    ProtocolV2FrameFlags.Error,
                    0);
                var idSpan = writer.GetSpan(sizeof(ulong));
                BinaryPrimitives.WriteUInt64LittleEndian(idSpan, unchecked((ulong)lastAcceptedRequestId));
                writer.Advance(sizeof(ulong));
                ProtocolV2PayloadCodec.WriteError(
                    writer, code, message, GetMaxErrorMessageBytes(session), out var truncated);
                writer.EndPacket(token);
                if (truncated)
                    SetTruncatedFlag(writer, token);
                ownsWriter = false;
                await session
                    .SendPacketAndFlushAsync(writer, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        internal void SendWindowUpdate(long requestId, ushort streamId, int credit)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(credit);
            var writer = session.RentFrameWriter();
            var ownsWriter = true;
            try
            {
                var token = writer.BeginPacket(
                    ProtocolV2FrameType.WindowUpdate,
                    ProtocolV2FrameFlags.None,
                    unchecked((ulong)requestId));
                var update = new ProtocolV2WindowUpdate(streamId, checked((uint)credit));
                ProtocolV2PayloadCodec.WriteWindowUpdate(writer, update);
                writer.EndPacket(token);
                ownsWriter = false;
                session.SendWindowUpdatePacket(writer);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }
    }

    private static async ValueTask SendTimestampFrameWithBackpressureAsync(
        RpcSession session,
        ProtocolV2FrameType type,
        long timestamp,
        CancellationToken cancellationToken)
    {
        var writer = session.RentFrameWriter();
        var ownsWriter = true;
        try
        {
            using (writer.BeginPacketScope(type, ProtocolV2FrameFlags.None, 0))
            {
                var span = writer.GetSpan(sizeof(long));
                BinaryPrimitives.WriteInt64LittleEndian(span, timestamp);
                writer.Advance(sizeof(long));
            }
            ownsWriter = false;
            await session
                .SendPacketWithBackpressureAsync(writer, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (ownsWriter)
                session.RuntimeContext.Buffers.Return(writer);
        }
    }

    private static async ValueTask SendErrorFrameAndFlushAsync(
        RpcSession session,
        ProtocolV2FrameType frameType,
        long requestId,
        SharpLinkErrorCode code,
        string? message,
        int maxMessageBytes,
        CancellationToken cancellationToken)
    {
        var writer = session.RentFrameWriter();
        var ownsWriter = true;
        try
        {
            var token = writer.BeginPacket(
                frameType, ProtocolV2FrameFlags.Error, unchecked((ulong)requestId));
            ProtocolV2PayloadCodec.WriteError(writer, code, message, maxMessageBytes, out var truncated);
            writer.EndPacket(token);
            if (truncated)
                SetTruncatedFlag(writer, token);
            ownsWriter = false;
            await session
                .SendPacketAndFlushAsync(writer, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (ownsWriter)
                session.RuntimeContext.Buffers.Return(writer);
        }
    }

    private static async ValueTask SendErrorFrameWithBackpressureAsync(
        RpcSession session,
        ProtocolV2FrameType frameType,
        long requestId,
        SharpLinkErrorCode code,
        ushort detailCode,
        string? message,
        int maxMessageBytes,
        CancellationToken cancellationToken)
    {
        var writer = session.RentFrameWriter();
        var ownsWriter = true;
        try
        {
            var token = writer.BeginPacket(
                frameType, ProtocolV2FrameFlags.Error, unchecked((ulong)requestId));
            ProtocolV2PayloadCodec.WriteError(
                writer,
                code,
                detailCode,
                message,
                maxMessageBytes,
                out var truncated);
            writer.EndPacket(token);
            if (truncated)
                SetTruncatedFlag(writer, token);
            ownsWriter = false;
            await session
                .SendPacketWithBackpressureAsync(writer, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (ownsWriter)
                session.RuntimeContext.Buffers.Return(writer);
        }
    }

    private static void SetTruncatedFlag(IRpcByteBufferWriter writer, PacketToken token)
    {
        var span = writer.WrittenSpan;
        span[token.StartOffset + 6] |= (byte)ProtocolV2FrameFlags.Truncated;
    }

    private static int GetMaxErrorMessageBytes(RpcSession session)
        => session.RuntimeContext.Protocol.MaxErrorMessageBytes;

}
