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
            var writer = session.RuntimeContext.Buffers.Rent();
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
            var writer = session.RuntimeContext.Buffers.Rent();
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
            var writer = session.RuntimeContext.Buffers.Rent();
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

        public void SendCancelAsync(long requestId)
            => session.SendPacketAsync(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, requestId);

        public void SendPingAsync()
            => SendTimestampFrame(session, ProtocolV2FrameType.Ping, Stopwatch.GetTimestamp());

        public void SendPongAsync(long timestamp)
            => SendTimestampFrame(session, ProtocolV2FrameType.Pong, timestamp);

        public void SendStreamChunkAsync<T>(long requestId, ushort streamId, T item)
        {
            var writer = session.RuntimeContext.Buffers.Rent();
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
                ownsWriter = false;
                GetRuntimeSession(session).SendPacket(writer);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        public void SendStreamCompleteAsync(long requestId, ushort streamId)
        {
            var writer = session.RuntimeContext.Buffers.Rent();
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
                GetRuntimeSession(session).SendPacket(writer);
            }
            finally
            {
                if (ownsWriter)
                    session.RuntimeContext.Buffers.Return(writer);
            }
        }

        public void SendStreamErrorAsync(long requestId, ushort streamId, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            var writer = session.RuntimeContext.Buffers.Rent();
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
                GetRuntimeSession(session).SendPacket(writer);
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
            var writer = session.RuntimeContext.Buffers.Rent();
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
    }

    private static void SendTimestampFrame(IRpcSession session, ProtocolV2FrameType type, long timestamp)
    {
        var writer = session.RuntimeContext.Buffers.Rent();
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
        var writer = session.RuntimeContext.Buffers.Rent();
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
        var writer = session.RuntimeContext.Buffers.Rent();
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
