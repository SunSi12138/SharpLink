namespace SharpLink.Runtime;

public static class RpcSessionExtensions
{
    extension(IRpcSession session)
    {
        public async ValueTask SendHandshakeRequestAndFlushAsync(
            ProtocolV2HandshakeRequest request,
            SharpLinkProtocolOptions limits,
            CancellationToken cancellationToken = default)
        {
            var writer = GetRuntimeSession(session).RentFrameWriter();
            var ownsWriter = true;
            try
            {
                var token = writer.BeginPacket(
                    ProtocolV2FrameType.HandshakeRequest, ProtocolV2FrameFlags.None, 0);
                ProtocolV2PayloadCodec.WriteHandshakeRequest(writer, request, limits);
                writer.EndPacket(token);
                ownsWriter = false;
                await GetRuntimeSession(session)
                    .SendPacketAndFlushAsync(writer, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        public async ValueTask SendHandshakeResponseAndFlushAsync(
            ProtocolV2HandshakeResponse response,
            CancellationToken cancellationToken = default)
        {
            var writer = GetRuntimeSession(session).RentFrameWriter();
            var ownsWriter = true;
            try
            {
                var token = writer.BeginPacket(
                    ProtocolV2FrameType.HandshakeResponse, ProtocolV2FrameFlags.None, 0);
                ProtocolV2PayloadCodec.WriteHandshakeResponse(writer, response);
                writer.EndPacket(token);
                ownsWriter = false;
                await GetRuntimeSession(session)
                    .SendPacketAndFlushAsync(writer, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        public ValueTask SendHandshakeErrorAndFlushAsync(
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

        public void SendPacketAsync(ProtocolV2FrameType frameType, ProtocolV2FrameFlags flags, long requestId)
        {
            var writer = GetRuntimeSession(session).RentFrameWriter();
            var ownsWriter = true;
            try
            {
                writer.WritePacket(frameType, flags, unchecked((ulong)requestId));
                ownsWriter = false;
                GetRuntimeSession(session).SendPacket(writer);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        public void SendRpcErrorAsync(long requestId, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            var code = exception is SharpLinkException sharpLinkException
                ? sharpLinkException.Code
                : SharpLinkErrorCode.Internal;
            var message = exception is SharpLinkException ? exception.Message : "Internal service error.";
            SendErrorFrame(
                session,
                ProtocolV2FrameType.Response,
                requestId,
                code,
                message,
                GetMaxErrorMessageBytes(session));
        }

        /// <summary>Sends a negotiated protocol cancellation for one active request.</summary>
        /// <param name="requestId">The non-zero request identifier to cancel.</param>
        /// <param name="reason">The stable client-side cancellation reason.</param>
        public void SendCancelAsync(long requestId, ProtocolV2CancelReason reason)
        {
            var runtimeSession = GetRuntimeSession(session);
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
                (GetRuntimeSession(session).NegotiatedCapabilities &
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

        public void SendPingAsync()
            => SendTimestampFrame(session, ProtocolV2FrameType.Ping, Stopwatch.GetTimestamp());

        public void SendPongAsync(long timestamp)
            => SendTimestampFrame(session, ProtocolV2FrameType.Pong, timestamp);

        /// <summary>Sends a protocol-level health request on a negotiated session.</summary>
        /// <param name="session">The negotiated session that owns the send queue.</param>
        /// <param name="requestId">The non-zero health request identifier.</param>
        public void SendHealthCheck(long requestId)
            => session.SendPacketAsync(
                ProtocolV2FrameType.HealthCheck,
                ProtocolV2FrameFlags.None,
                requestId);

        /// <summary>Sends a fixed-width protocol health response.</summary>
        /// <param name="session">The negotiated session that owns the send queue.</param>
        /// <param name="requestId">The request identifier being answered.</param>
        /// <param name="status">The current server readiness state.</param>
        public void SendHealthResponse(long requestId, SharpLinkHealthStatus status)
        {
            var writer = GetRuntimeSession(session).RentFrameWriter();
            var ownsWriter = true;
            try
            {
                using (writer.BeginPacketScope(
                           ProtocolV2FrameType.HealthResponse,
                           ProtocolV2FrameFlags.None,
                           unchecked((ulong)requestId)))
                {
                    ProtocolV2PayloadCodec.WriteHealthResponse(writer, status);
                }
                ownsWriter = false;
                GetRuntimeSession(session).SendPacket(writer);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        public async ValueTask SendStreamChunkAsync<T>(
            long requestId,
            ushort streamId,
            T item,
            CancellationToken cancellationToken = default)
        {
            var writer = GetRuntimeSession(session).RentFrameWriter();
            var ownsWriter = true;
            try
            {
                using (writer.BeginPacketScope(
                           ProtocolV2FrameType.StreamData,
                           ProtocolV2FrameFlags.None,
                           unchecked((ulong)requestId)))
                {
                    var idSpan = writer.GetSpan(sizeof(ushort));
                    BinaryPrimitives.WriteUInt16LittleEndian(idSpan, streamId);
                    writer.Advance(sizeof(ushort));
                    session.RuntimeContext.Codecs.GetCodec<T>().Serialize(item, writer);
                }
                var encodedBytes = Math.Max(
                    1,
                    writer.WrittenCount - ProtocolV2Constants.HeaderBytes - sizeof(ushort));
                var runtimeSession = GetRuntimeSession(session);
                await runtimeSession.AcquireStreamSendCreditAsync(
                    requestId,
                    streamId,
                    encodedBytes,
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    ownsWriter = false;
                    runtimeSession.SendPacket(writer);
                }
                catch
                {
                    runtimeSession.ReturnUnsentStreamCredit(requestId, streamId, encodedBytes);
                    throw;
                }
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        public void SendStreamCompleteAsync(long requestId, ushort streamId)
        {
            var writer = GetRuntimeSession(session).RentFrameWriter();
            var ownsWriter = true;
            try
            {
                using (writer.BeginPacketScope(
                           ProtocolV2FrameType.StreamComplete,
                           ProtocolV2FrameFlags.None,
                           unchecked((ulong)requestId)))
                {
                    var idSpan = writer.GetSpan(sizeof(ushort));
                    BinaryPrimitives.WriteUInt16LittleEndian(idSpan, streamId);
                    writer.Advance(sizeof(ushort));
                }
                ownsWriter = false;
                var runtimeSession = GetRuntimeSession(session);
                runtimeSession.SendPacket(writer);
                runtimeSession.CompleteSendStream(requestId, streamId);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        public void SendStreamErrorAsync(
            long requestId,
            ushort streamId,
            Exception exception,
            long contractId = 0,
            long methodId = 0)
        {
            ArgumentNullException.ThrowIfNull(exception);
            exception = GetRuntimeSession(session).MapServiceException(
                requestId, contractId, methodId, exception);
            var writer = GetRuntimeSession(session).RentFrameWriter();
            var ownsWriter = true;
            try
            {
                var token = writer.BeginPacket(
                    ProtocolV2FrameType.StreamComplete,
                    ProtocolV2FrameFlags.Error,
                    unchecked((ulong)requestId));
                var idSpan = writer.GetSpan(sizeof(ushort));
                BinaryPrimitives.WriteUInt16LittleEndian(idSpan, streamId);
                writer.Advance(sizeof(ushort));
                var code = exception is SharpLinkException sharpLinkException
                    ? sharpLinkException.Code
                    : SharpLinkErrorCode.Internal;
                var message = exception is SharpLinkException ? exception.Message : "Internal stream error.";
                ProtocolV2PayloadCodec.WriteError(
                    writer, code, message, GetMaxErrorMessageBytes(session), out var truncated);
                writer.EndPacket(token);
                if (truncated)
                    SetTruncatedFlag(writer, token);
                ownsWriter = false;
                var runtimeSession = GetRuntimeSession(session);
                runtimeSession.SendPacket(writer);
                runtimeSession.CompleteSendStream(requestId, streamId, exception);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        public async ValueTask SendGoAwayAsync(
            long lastAcceptedRequestId,
            SharpLinkErrorCode code,
            string? message,
            CancellationToken cancellationToken = default)
        {
            var writer = GetRuntimeSession(session).RentFrameWriter();
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
                await GetRuntimeSession(session)
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
            var writer = GetRuntimeSession(session).RentFrameWriter();
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
                GetRuntimeSession(session).SendPacket(writer);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }
    }

    private static void SendTimestampFrame(IRpcSession session, ProtocolV2FrameType type, long timestamp)
    {
        var writer = GetRuntimeSession(session).RentFrameWriter();
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
            GetRuntimeSession(session).SendPacket(writer);
        }
        finally
        {
            if (ownsWriter)
                session.RuntimeContext.Buffers.Return(writer);
        }
    }

    private static void SendErrorFrame(
        IRpcSession session,
        ProtocolV2FrameType frameType,
        long requestId,
        SharpLinkErrorCode code,
        string? message,
        int maxMessageBytes)
    {
        var writer = GetRuntimeSession(session).RentFrameWriter();
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
            GetRuntimeSession(session).SendPacket(writer);
        }
        finally
        {
            if (ownsWriter)
                session.RuntimeContext.Buffers.Return(writer);
        }
    }

    private static async ValueTask SendErrorFrameAndFlushAsync(
        IRpcSession session,
        ProtocolV2FrameType frameType,
        long requestId,
        SharpLinkErrorCode code,
        string? message,
        int maxMessageBytes,
        CancellationToken cancellationToken)
    {
        var writer = GetRuntimeSession(session).RentFrameWriter();
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
            await GetRuntimeSession(session)
                .SendPacketAndFlushAsync(writer, cancellationToken)
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

    private static int GetMaxErrorMessageBytes(IRpcSession session)
        => GetRuntimeSession(session).RuntimeContext.Protocol.MaxErrorMessageBytes;

    private static RpcSession GetRuntimeSession(IRpcSession session)
        => session as RpcSession ?? throw new InvalidOperationException(
            "SharpLink generated stubs require the built-in runtime session implementation.");
}
