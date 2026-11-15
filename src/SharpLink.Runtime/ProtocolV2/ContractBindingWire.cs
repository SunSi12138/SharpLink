namespace SharpLink.Runtime;

internal enum ContractBindStatus : long
{
    Success, ContractMissing, HashMismatch, DynamicUnsupported, Capacity, Unavailable,
    UnknownBinding, UnknownMethod, UnsupportedShape, Retired, NoBoundRoute, ReceiptMismatch
}

internal readonly record struct ContractBindRequest(long ContractId, RpcHash128 Hash,
    RpcRegistrationRequirement Requirement, RpcRegistrationReceipt? Receipt);
internal readonly record struct ContractBindAck(ContractBindStatus Status, long ContractId,
    ulong Token, long RegistrationId, long PublicationGeneration);

internal static class ContractBindingWire
{
    internal static bool IsToken(ulong token)
        => (uint)(token >> 32) != 0 && (uint)token is >= 1 and <= 64;

    internal static byte[] WriteRequest(ContractBindRequest request)
    {
        var bytes = new byte[request.Receipt.HasValue ? 112 : 40];
        var span = bytes.AsSpan();
        Write(span, 0, request.ContractId); Write(span, 8, unchecked((long)request.Hash.High));
        Write(span, 16, unchecked((long)request.Hash.Low)); Write(span, 24, (long)request.Requirement);
        Write(span, 32, request.Receipt.HasValue ? 1 : 0);
        if (request.Receipt is { } r)
        {
            r.ServerInstanceId.TryWriteBytes(span.Slice(40, 16)); r.RegistryEpoch.TryWriteBytes(span.Slice(56, 16));
            Write(span, 72, r.Generation); Write(span, 80, r.ContractId);
            Write(span, 88, unchecked((long)r.RpcAssemblyHash.High)); Write(span, 96, unchecked((long)r.RpcAssemblyHash.Low));
            Write(span, 104, r.RegistrationId);
        }
        return bytes;
    }

    internal static ContractBindRequest ReadRequest(ReadOnlySequence<byte> payload)
    {
        if (payload.Length is not (40 or 112)) throw Violation("BindContract length is invalid.");
        Span<byte> bytes = stackalloc byte[112]; payload.CopyTo(bytes);
        var id = Read(bytes, 0); var hash = new RpcHash128(unchecked((ulong)Read(bytes, 8)), unchecked((ulong)Read(bytes, 16)));
        var requirement = Read(bytes, 24); var hasReceipt = Read(bytes, 32);
        if (id == 0 || hash.IsEmpty || requirement is < 0 or > 2 || hasReceipt is < 0 or > 1 ||
            payload.Length != (hasReceipt == 0 ? 40 : 112) || hasReceipt == 0 && requirement != 0)
            throw Violation("BindContract fields are invalid.");
        RpcRegistrationReceipt? receipt = null;
        if (hasReceipt != 0)
        {
            var r = new RpcRegistrationReceipt(new Guid(bytes.Slice(40, 16)), new Guid(bytes.Slice(56, 16)),
                Read(bytes, 72), Read(bytes, 80),
                new RpcHash128(unchecked((ulong)Read(bytes, 88)), unchecked((ulong)Read(bytes, 96))), Read(bytes, 104));
            if (r.ServerInstanceId == Guid.Empty || r.RegistryEpoch == Guid.Empty || r.Generation < 0 ||
                r.RegistrationId <= 0 || r.ContractId != id || r.RpcAssemblyHash != hash)
                throw Violation("BindContract receipt is invalid.");
            receipt = r;
        }
        return new(id, hash, (RpcRegistrationRequirement)requirement, receipt);
    }

    internal static byte[] WriteAck(ContractBindAck ack)
    {
        var bytes = new byte[40]; var span = bytes.AsSpan();
        Write(span, 0, (long)ack.Status); Write(span, 8, ack.ContractId); Write(span, 16, unchecked((long)ack.Token));
        Write(span, 24, ack.RegistrationId); Write(span, 32, ack.PublicationGeneration); return bytes;
    }
    internal static ContractBindAck ReadAck(ReadOnlySequence<byte> payload)
    {
        if (payload.Length != 40) throw Violation("BindAck length is invalid.");
        Span<byte> bytes = stackalloc byte[40]; payload.CopyTo(bytes);
        var status = Read(bytes, 0); var contract = Read(bytes, 8); var token = unchecked((ulong)Read(bytes, 16));
        var registration = Read(bytes, 24); var generation = Read(bytes, 32);
        if (status is < 0 or > 11 || contract == 0 ||
            (status == 0 ? !IsToken(token) || registration <= 0 || generation < 0 : token != 0 || registration != 0 || generation != 0))
            throw Violation("BindAck fields are invalid.");
        return new((ContractBindStatus)status, contract, token, registration, generation);
    }
    internal static ulong ReadRelease(ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (payload.Length != 8 || !reader.TryReadLittleEndian(out long token) || !IsToken(unchecked((ulong)token)))
            throw Violation("BindingRelease token is invalid.");
        return unchecked((ulong)token);
    }
    internal static ulong[] ReadInvalidated(ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long count) || count is < 1 or > 64 || payload.Length != 8 + count * 8)
            throw Violation("BindingInvalidated count is invalid.");
        var tokens = new ulong[count];
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!reader.TryReadLittleEndian(out long bits) || !IsToken(unchecked((ulong)bits))) throw Violation("Invalidated token is invalid.");
            tokens[i] = unchecked((ulong)bits);
            for (var j = 0; j < i; j++) if (tokens[i] == tokens[j]) throw Violation("Duplicate invalidated token.");
        }
        return tokens;
    }
    internal static byte[] WriteRelease(ulong token)
    { var bytes = new byte[8]; Write(bytes, 0, unchecked((long)token)); return bytes; }
    private static void Write(Span<byte> span, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(span[offset..], value);
    private static long Read(ReadOnlySpan<byte> span, int offset) => BinaryPrimitives.ReadInt64LittleEndian(span[offset..]);
    internal static SharpLinkException Violation(string message) => new(SharpLinkErrorCode.ProtocolViolation, message);
    internal static SharpLinkException Error(ContractBindStatus status)
    {
        var code = status switch
        {
            ContractBindStatus.ContractMissing or ContractBindStatus.UnknownMethod => SharpLinkErrorCode.Unimplemented,
            ContractBindStatus.Capacity => SharpLinkErrorCode.ResourceExhausted,
            ContractBindStatus.Unavailable or ContractBindStatus.Retired or ContractBindStatus.NoBoundRoute => SharpLinkErrorCode.Unavailable,
            _ => SharpLinkErrorCode.FailedPrecondition
        };
        var detail = status == ContractBindStatus.NoBoundRoute ? (ushort)60004 : checked((ushort)(60100 + (int)status));
        return new(code, detail, $"Contract binding rejected: {status}.");
    }
    internal static bool TrySend(RpcSession session, ProtocolV2FrameType type, ulong correlation, ReadOnlyMemory<byte> payload)
    {
        var writer = session.RentFrameWriter(); var owns = true;
        try
        {
            using (writer.BeginPacketScope(type, ProtocolV2FrameFlags.None, correlation))
            { payload.Span.CopyTo(writer.GetSpan(payload.Length)); writer.Advance(payload.Length); }
            owns = false;
            var prepared = session.PrepareOutboundFrame(writer, CancellationToken.None);
            if (session.TryEnqueuePreparedFrame(prepared, false, CancellationToken.None, null, out _, out var rejection)) return true;
            session.NotifyDisconnected(rejection); return false;
        }
        finally { if (owns) session.RuntimeContext.Buffers.Return(writer); }
    }
    internal static async ValueTask SendAndFlushWithinBudgetAsync(RpcSession session, ProtocolV2FrameType type, ulong correlation,
        ReadOnlyMemory<byte> payload, RpcDeadline deadline, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var send = SendAndFlushAsync(session, type, correlation, payload, sendCancellation.Token).AsTask();
        try
        {
            if (!await SharpLinkTimer.WaitAsync(send, deadline, timeProvider, cancellationToken).ConfigureAwait(false))
                throw new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "Binding control deadline exceeded.");
        }
        catch (Exception primary)
        {
            Exception failure = primary;
            // Publish the original control cause before cancellation/disposal callbacks can fail.
            session.NotifyDisconnected(primary);
            try { sendCancellation.Cancel(); } catch (Exception cleanup) { failure = new AggregateException(failure, cleanup); }
            try { await session.DisposeAsync().ConfigureAwait(false); } catch (Exception cleanup) { failure = new AggregateException(failure, cleanup); }
            try { await send.ConfigureAwait(false); } catch { /* The first failure already owns the result. */ }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
    internal static ValueTask SendAndFlushAsync(RpcSession session, ProtocolV2FrameType type, ulong correlation,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        => SendCoreAsync(session, type, correlation, payload, cancellationToken, true);
    internal static ValueTask SendAsync(RpcSession session, ProtocolV2FrameType type, ulong correlation,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        => SendCoreAsync(session, type, correlation, payload, cancellationToken, false);
    private static async ValueTask SendCoreAsync(RpcSession session, ProtocolV2FrameType type, ulong correlation,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken, bool flush)
    {
        var writer = session.RentFrameWriter(); var owns = true;
        try
        {
            using (writer.BeginPacketScope(type, ProtocolV2FrameFlags.None, correlation))
            { payload.Span.CopyTo(writer.GetSpan(payload.Length)); writer.Advance(payload.Length); }
            owns = false;
            if (flush) await session.SendPacketAndFlushAsync(writer, cancellationToken).ConfigureAwait(false);
            else await session.SendPacketWithBackpressureAsync(writer, cancellationToken).ConfigureAwait(false);
        }
        finally { if (owns) session.RuntimeContext.Buffers.Return(writer); }
    }
}

/// <summary>Endpoint-local ledger; reservations are returned only by their physical owner.</summary>
internal sealed class ContractBindingQuota
{
    private int _bindings, _operations, _protectedRequests;
    internal int Bindings => Volatile.Read(ref _bindings);
    internal int ProtectedRequests => Volatile.Read(ref _protectedRequests);
    internal int Operations => Volatile.Read(ref _operations);
    internal bool TryReserveProtectedRequest() => TryReserve(ref _protectedRequests, 65536);
    internal void ReturnProtectedRequest() => Interlocked.Decrement(ref _protectedRequests);
    internal bool TryReserveBinding() => TryReserve(ref _bindings, 65536);
    internal bool TryReserveOperation() => TryReserve(ref _operations, 1024);
    internal void ReturnBinding() => Interlocked.Decrement(ref _bindings);
    internal void ReturnOperation() => Interlocked.Decrement(ref _operations);
    private static bool TryReserve(ref int count, int limit)
    {
        var current = Volatile.Read(ref count);
        while (current < limit)
        { var prior = Interlocked.CompareExchange(ref count, current + 1, current); if (prior == current) return true; current = prior; }
        return false;
    }
}
