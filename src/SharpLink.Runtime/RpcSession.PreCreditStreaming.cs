namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private readonly Lock _preCreditSerializedBudgetGate = new();
    private PreCreditSerializedBudget? _preCreditSerializedBudget;

    /// <summary>
    /// Instance member owns stream-item dispatch so the exact-size path remains the first branch
    /// while the universal unsized fallback can use session-scoped pre-credit admission.
    /// </summary>
    internal async ValueTask SendStreamChunkAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        CancellationToken cancellationToken = default)
    {
        var codec = RuntimeContext.Codecs.GetCodec<T>();
        if (codec is IRpcSizedCodec<T> sizedCodec &&
            sizedCodec.CanExactSize &&
            sizedCodec.TryGetEncodedSize(item, out var knownEncodedBytes, out var sizedSnapshot))
        {
            await SendStreamChunkKnownSizeAsync(
                requestId,
                streamId,
                item,
                sizedCodec,
                knownEncodedBytes,
                sizedSnapshot,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendUnsizedStreamChunkAsync(
            requestId,
            streamId,
            item,
            codec,
            cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask SendUnsizedStreamChunkAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return SerializeUnsizedStreamChunk(
            requestId,
            streamId,
            item,
            codec,
            cancellationToken);
    }

    internal long PreCreditSerializedBytes
        => Volatile.Read(ref _preCreditSerializedBudget)?.ReservedBytes ?? 0;

    internal long PreCreditSerializedByteLimit
        => Volatile.Read(ref _preCreditSerializedBudget)?.MaxBytes ?? 0;

    internal int PreCreditSerializedWaiterCount
        => Volatile.Read(ref _preCreditSerializedBudget)?.WaiterCount ?? 0;

    // Kept as diagnostics for the benchmark harness while the previous two-layer prototype is
    // compared with the final flow-credit-probe design. The final design has no serializer gate.
    internal int PreCreditSerializationPermitLimit => 0;

    internal int PreCreditActiveSerializerCount => 0;

    internal void CompletePreCreditSendStream(
        long requestId,
        ushort streamId,
        Exception? exception = null)
        => Volatile.Read(ref _preCreditSerializedBudget)?
            .CompleteStream(requestId, streamId, exception);

    internal void AbortPreCreditSendStreams(long requestId, Exception exception)
        => Volatile.Read(ref _preCreditSerializedBudget)?.AbortRequest(requestId, exception);

    private ValueTask SerializeUnsizedStreamChunk<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        CancellationToken cancellationToken)
    {
        IRpcByteBufferWriter? writer = null;
        var ownsWriter = true;
        var creditAcquired = false;
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

            if (!HasStreamFlowControl ||
                TryAcquireStreamSendCredit(requestId, streamId, encodedBytes))
            {
                creditAcquired = HasStreamFlowControl;
                try
                {
                    ownsWriter = false;
                    SendPacket(writer);
                }
                catch
                {
                    if (creditAcquired)
                        ReturnUnsentStreamCredit(requestId, streamId, encodedBytes);
                    throw;
                }
                return ValueTask.CompletedTask;
            }

            // Only writers that are known to require asynchronous flow-credit admission are
            // charged to the pre-credit budget. Fast consumers therefore execute the same single
            // flow-controller lock/reserve as dev and never touch the byte budget.
            var budget = GetOrCreatePreCreditSerializedBudget();
            var pendingBudget = budget.AcquireAsync(
                requestId,
                streamId,
                encodedBytes,
                cancellationToken);
            ownsWriter = false;
            return AwaitPreCreditBudgetAndFlowCreditAsync(
                pendingBudget,
                writer,
                requestId,
                streamId,
                encodedBytes,
                budget,
                cancellationToken);
        }
        finally
        {
            if (ownsWriter && writer is not null)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    private async ValueTask AwaitPreCreditBudgetAndFlowCreditAsync(
        ValueTask pendingBudget,
        IRpcByteBufferWriter writer,
        long requestId,
        ushort streamId,
        int encodedBytes,
        PreCreditSerializedBudget budget,
        CancellationToken cancellationToken)
    {
        var ownsWriter = true;
        var ownsBudget = false;
        var creditAcquired = false;
        try
        {
            await pendingBudget.ConfigureAwait(false);
            ownsBudget = true;

            await AcquireStreamSendCreditAsync(
                requestId,
                streamId,
                encodedBytes,
                cancellationToken).ConfigureAwait(false);
            creditAcquired = true;

            budget.Release(encodedBytes);
            ownsBudget = false;

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
            if (ownsBudget)
                budget.Release(encodedBytes);
            if (ownsWriter)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    private bool TryAcquireStreamSendCredit(long requestId, ushort streamId, int encodedBytes)
    {
        var controller = Volatile.Read(ref _protocolState).FlowController;
        return controller is null || controller.TryAcquireSendCredit(requestId, streamId, encodedBytes);
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

            // A queued budget waiter already owns a serialized writer, so waiter count is part of
            // the hard memory envelope. Keep worst-case queued backing bounded to at most roughly
            // one additional connection-window worth of max-size frames (or one frame when the
            // negotiated window is smaller than a legal frame).
            var maxFrameBytes = Math.Max(1, NegotiatedMaxFramePayloadBytes);
            var derivedWaiters = Math.Max(1L, maxBytes / maxFrameBytes);
            var maxWaiters = checked((int)Math.Min(
                RuntimeContext.Protocol.MaxConcurrentStreamsPerConnection,
                derivedWaiters));

            budget = new PreCreditSerializedBudget(maxBytes, maxWaiters);
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
}
