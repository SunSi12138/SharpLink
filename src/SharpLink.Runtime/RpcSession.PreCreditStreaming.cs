namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private readonly Lock _preCreditSerializedBudgetGate = new();
    private PreCreditSerializedBudget? _preCreditSerializedBudget;

    /// <summary>
    /// Instance member intentionally owns stream-item dispatch so the exact-size path remains the
    /// first branch while the universal unsized fallback can use session-scoped pre-credit admission.
    /// </summary>
    internal ValueTask SendStreamChunkAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var codec = RuntimeContext.Codecs.GetCodec<T>();
            if (codec is IRpcSizedCodec<T> sizedCodec &&
                sizedCodec.CanExactSize &&
                sizedCodec.TryGetEncodedSize(item, out var knownEncodedBytes, out var sizedSnapshot))
            {
                return SendStreamChunkKnownSizeAsync(
                    requestId,
                    streamId,
                    item,
                    sizedCodec,
                    knownEncodedBytes,
                    sizedSnapshot,
                    cancellationToken);
            }

            return SendUnsizedStreamChunkAsync(
                requestId,
                streamId,
                item,
                codec,
                cancellationToken);
        }
        catch (Exception exception)
        {
            // The previous extension implementation was async ValueTask, so synchronous codec,
            // sizing, compression, and SendPacket failures were surfaced through the returned
            // ValueTask rather than escaping the call site. Preserve that contract without adding
            // an async state machine to the successful fast path.
            return ValueTask.FromException(exception);
        }
    }

    internal ValueTask SendUnsizedStreamChunkAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(codec);

        if (!HasStreamFlowControl)
        {
            return SerializeUnsizedStreamChunkAfterReservation(
                requestId,
                streamId,
                item,
                codec,
                budget: null,
                reservedBytes: 0,
                cancellationToken);
        }

        var budget = GetOrCreatePreCreditSerializedBudget();
        var reservedBytes = GetPreCreditSerializationReservationBytes(budget);
        var pendingReservation = budget.AcquireAsync(
            requestId,
            streamId,
            reservedBytes,
            cancellationToken);
        if (!pendingReservation.IsCompletedSuccessfully)
        {
            return AwaitPreCreditReservationAndSerializeAsync(
                pendingReservation,
                requestId,
                streamId,
                item,
                codec,
                budget,
                reservedBytes,
                cancellationToken);
        }

        pendingReservation.GetAwaiter().GetResult();
        return SerializeUnsizedStreamChunkAfterReservation(
            requestId,
            streamId,
            item,
            codec,
            budget,
            reservedBytes,
            cancellationToken);
    }

    internal long PreCreditSerializedBytes
        => Volatile.Read(ref _preCreditSerializedBudget)?.ReservedBytes ?? 0;

    internal long PreCreditSerializedByteLimit
        => Volatile.Read(ref _preCreditSerializedBudget)?.MaxBytes ?? 0;

    internal int PreCreditSerializedWaiterCount
        => Volatile.Read(ref _preCreditSerializedBudget)?.WaiterCount ?? 0;

    internal void CompletePreCreditSendStream(
        long requestId,
        ushort streamId,
        Exception? exception = null)
        => Volatile.Read(ref _preCreditSerializedBudget)?
            .CompleteStream(requestId, streamId, exception);

    internal void AbortPreCreditSendStreams(long requestId, Exception exception)
        => Volatile.Read(ref _preCreditSerializedBudget)?.AbortRequest(requestId, exception);

    private async ValueTask AwaitPreCreditReservationAndSerializeAsync<T>(
        ValueTask pendingReservation,
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        PreCreditSerializedBudget budget,
        int reservedBytes,
        CancellationToken cancellationToken)
    {
        await pendingReservation.ConfigureAwait(false);
        await SerializeUnsizedStreamChunkAfterReservation(
            requestId,
            streamId,
            item,
            codec,
            budget,
            reservedBytes,
            cancellationToken).ConfigureAwait(false);
    }

    private ValueTask SerializeUnsizedStreamChunkAfterReservation<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        PreCreditSerializedBudget? budget,
        int reservedBytes,
        CancellationToken cancellationToken)
    {
        IRpcByteBufferWriter? writer = null;
        var ownsWriter = true;
        var ownsReservation = budget is not null;
        try
        {
            if (Volatile.Read(ref _terminal) is { } terminal)
                throw terminal.Exception;
            cancellationToken.ThrowIfCancellationRequested();

            writer = RentFrameWriter();
            using (writer.BeginPacketScope(
                       ProtocolV2FrameType.StreamData,
                       ProtocolV2FrameFlags.None,
                       unchecked((ulong)requestId)))
            {
                var idSpan = writer.GetSpan(sizeof(ushort));
                BinaryPrimitives.WriteUInt16LittleEndian(idSpan, streamId);
                writer.Advance(sizeof(ushort));
                codec.Serialize(item, writer);
            }

            var encodedBytes = Math.Max(
                1,
                writer.WrittenCount - ProtocolV2Constants.HeaderBytes - sizeof(ushort));
            if (budget is not null)
            {
                budget.ResizeReservation(reservedBytes, encodedBytes);
                reservedBytes = encodedBytes;
            }

            var pendingCredit = AcquireStreamSendCreditAsync(
                requestId,
                streamId,
                encodedBytes,
                cancellationToken);
            if (!pendingCredit.IsCompletedSuccessfully)
            {
                ownsWriter = false;
                ownsReservation = false;
                return AwaitUnsizedStreamCreditAndSendAsync(
                    pendingCredit,
                    writer,
                    requestId,
                    streamId,
                    encodedBytes,
                    budget,
                    reservedBytes);
            }

            pendingCredit.GetAwaiter().GetResult();
            if (budget is not null)
            {
                budget.Release(reservedBytes);
                ownsReservation = false;
            }

            try
            {
                ownsWriter = false;
                SendPacket(writer);
            }
            catch
            {
                ReturnUnsentStreamCredit(requestId, streamId, encodedBytes);
                throw;
            }
            return ValueTask.CompletedTask;
        }
        finally
        {
            if (ownsReservation)
                budget!.Release(reservedBytes);
            if (ownsWriter && writer is not null)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    private async ValueTask AwaitUnsizedStreamCreditAndSendAsync(
        ValueTask pendingCredit,
        IRpcByteBufferWriter writer,
        long requestId,
        ushort streamId,
        int encodedBytes,
        PreCreditSerializedBudget? budget,
        int reservedBytes)
    {
        var ownsWriter = true;
        var ownsReservation = budget is not null;
        var creditAcquired = false;
        try
        {
            await pendingCredit.ConfigureAwait(false);
            creditAcquired = true;
            if (budget is not null)
            {
                budget.Release(reservedBytes);
                ownsReservation = false;
            }

            ownsWriter = false;
            SendPacket(writer);
        }
        catch
        {
            if (creditAcquired)
                ReturnUnsentStreamCredit(requestId, streamId, encodedBytes);
            throw;
        }
        finally
        {
            if (ownsReservation)
                budget!.Release(reservedBytes);
            if (ownsWriter)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    private PreCreditSerializedBudget GetOrCreatePreCreditSerializedBudget()
    {
        var budget = Volatile.Read(ref _preCreditSerializedBudget);
        if (budget is not null)
            return budget;

        lock (_preCreditSerializedBudgetGate)
        {
            budget = _preCreditSerializedBudget;
            if (budget is not null)
                return budget;

            var negotiated = NegotiatedOptions;
            var maxBytes = Math.Max(
                1,
                negotiated?.ConnectionReceiveWindowBytes ??
                RuntimeContext.FlowControl.ConnectionReceiveWindowBytes);
            budget = new PreCreditSerializedBudget(
                maxBytes,
                RuntimeContext.Protocol.MaxConcurrentStreamsPerConnection);
            Volatile.Write(ref _preCreditSerializedBudget, budget);

            _ = _lifetimeToken.UnsafeRegister(
                static state =>
                {
                    var session = (RpcSession)state!;
                    Volatile.Read(ref session._preCreditSerializedBudget)?
                        .Complete(session.GetTerminalException());
                },
                this);
            return budget;
        }
    }

    private int GetPreCreditSerializationReservationBytes(PreCreditSerializedBudget budget)
    {
        var maxEncodedItemBytes = Math.Max(
            1,
            NegotiatedMaxFramePayloadBytes - sizeof(ushort));
        return checked((int)Math.Min((long)maxEncodedItemBytes, budget.MaxBytes));
    }
}
