namespace SharpLink.Runtime;

/// <summary>Writes Protocol v2 control, response, and streaming frames through an RPC session.</summary>
public static class RpcSessionExtensions
{
    extension(IRpcSession session)
    {
        /// <summary>Sends and flushes a client handshake request.</summary>
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

        /// <summary>Sends and flushes a successful server handshake response.</summary>
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

        /// <summary>Sends and flushes a bounded handshake rejection.</summary>
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

        /// <summary>Queues a payload-free protocol frame for sending.</summary>
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

        internal async ValueTask SendPacketWithBackpressureAsync(
            ProtocolV2FrameType frameType,
            ProtocolV2FrameFlags flags,
            long requestId,
            CancellationToken cancellationToken = default)
        {
            var writer = GetRuntimeSession(session).RentFrameWriter();
            var ownsWriter = true;
            try
            {
                writer.WritePacket(frameType, flags, unchecked((ulong)requestId));
                ownsWriter = false;
                await GetRuntimeSession(session)
                    .SendPacketWithBackpressureAsync(writer, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        /// <summary>Sends a bounded structured RPC response error.</summary>
        public void SendRpcErrorAsync(long requestId, SharpLinkException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            SendErrorFrame(
                session,
                ProtocolV2FrameType.Response,
                requestId,
                exception.Code,
                exception.Message,
                GetMaxErrorMessageBytes(session));
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
                exception.Message,
                GetMaxErrorMessageBytes(session),
                cancellationToken);
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

        /// <summary>Sends a ping containing the current monotonic timestamp.</summary>
        public void SendPingAsync()
            => SendTimestampFrame(session, ProtocolV2FrameType.Ping, Stopwatch.GetTimestamp());

        internal ValueTask SendPingWithBackpressureAsync(CancellationToken cancellationToken = default)
            => SendTimestampFrameWithBackpressureAsync(
                session,
                ProtocolV2FrameType.Ping,
                Stopwatch.GetTimestamp(),
                cancellationToken);

        /// <summary>Sends a pong that echoes a received ping timestamp.</summary>
        /// <param name="timestamp">The monotonic timestamp from the ping frame.</param>
        public void SendPongAsync(long timestamp)
            => SendTimestampFrame(session, ProtocolV2FrameType.Pong, timestamp);

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
        public void SendHealthCheck(long requestId)
            => session.SendPacketAsync(
                ProtocolV2FrameType.HealthCheck,
                ProtocolV2FrameFlags.None,
                requestId);

        /// <summary>Sends a fixed-width protocol health response.</summary>
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

        internal async ValueTask SendHealthResponseWithBackpressureAsync(
            long requestId,
            SharpLinkHealthStatus status,
            CancellationToken cancellationToken = default)
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
                await GetRuntimeSession(session)
                    .SendPacketWithBackpressureAsync(writer, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        /// <summary>Serializes and sends one flow-controlled stream item.</summary>
        /// <typeparam name="T">The stream item type.</typeparam>
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

        /// <summary>Sends successful completion for one request stream.</summary>
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
                try
                {
                    runtimeSession.SendPacket(writer);
                }
                finally
                {
                    runtimeSession.CompleteSendStream(requestId, streamId);
                }
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        /// <summary>Sends a structured terminal failure for one request stream.</summary>
        public void SendStreamErrorAsync(
            long requestId,
            ushort streamId,
            SharpLinkException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
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
                ProtocolV2PayloadCodec.WriteError(
                    writer,
                    exception.Code,
                    exception.Message,
                    GetMaxErrorMessageBytes(session),
                    out var truncated);
                writer.EndPacket(token);
                if (truncated)
                    SetTruncatedFlag(writer, token);
                ownsWriter = false;
                var runtimeSession = GetRuntimeSession(session);
                try
                {
                    runtimeSession.SendPacket(writer);
                }
                finally
                {
                    runtimeSession.CompleteSendStream(requestId, streamId, exception);
                }
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        /// <summary>Sends and flushes a connection-drain frame with the last accepted request.</summary>
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

    private static async ValueTask SendTimestampFrameWithBackpressureAsync(
        IRpcSession session,
        ProtocolV2FrameType type,
        long timestamp,
        CancellationToken cancellationToken)
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
            await GetRuntimeSession(session)
                .SendPacketWithBackpressureAsync(writer, cancellationToken)
                .ConfigureAwait(false);
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

    private static async ValueTask SendErrorFrameWithBackpressureAsync(
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

    private static int GetMaxErrorMessageBytes(IRpcSession session)
        => GetRuntimeSession(session).RuntimeContext.Protocol.MaxErrorMessageBytes;

    private static RpcSession GetRuntimeSession(IRpcSession session)
        => session as RpcSession ?? throw new InvalidOperationException(
            "SharpLink generated stubs require the built-in runtime session implementation.");
}
