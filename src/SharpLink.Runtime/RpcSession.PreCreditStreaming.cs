namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private readonly Lock _preCreditSerializedBudgetGate = new();
    private readonly Lock _preCreditSerializationPermitGate = new();
    private PreCreditSerializedBudget? _preCreditSerializedBudget;
    private PreCreditSerializedBudget? _preCreditSerializationPermits;

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

        if (!HasStreamFlowControl)
        {
            return SerializeUnsizedStreamChunkAfterPermit(
                requestId,
                streamId,
                item,
                codec,
                permits: null,
                cancellationToken);
        }

        var permits = GetOrCreatePreCreditSerializationPermits();
        var pendingPermit = permits.AcquireAsync(
            requestId,
            streamId,
            bytes: 1,
            cancellationToken);
        if (!pendingPermit.IsCompletedSuccessfully)
        {
            return AwaitPreCreditSerializationPermitAndSendAsync(
                pendingPermit,
                requestId,
                streamId,
                item,
                codec,
                permits,
                cancellationToken);
        }

        pendingPermit.GetAwaiter().GetResult();
        return SerializeUnsizedStreamChunkAfterPermit(
            requestId,
            streamId,
            item,
            codec,
            permits,
            cancellationToken);
    }

    internal long PreCreditSerializedBytes
        => Volatile.Read(ref _preCreditSerializedBudget)?.ReservedBytes ?? 0;

    internal long PreCreditSerializedByteLimit
        => Volatile.Read(ref _preCreditSerializedBudget)?.MaxBytes ?? 0;

    internal int PreCreditSerializedWaiterCount
        => (Volatile.Read(ref _preCreditSerializedBudget)?.WaiterCount ?? 0) +
           (Volatile.Read(ref _preCreditSerializationPermits)?.WaiterCount ?? 0);

    internal int PreCreditSerializationPermitLimit
        => checked((int)(Volatile.Read(ref _preCreditSerializationPermits)?.MaxBytes ?? 0));

    internal int PreCreditActiveSerializerCount
        => checked((int)(Volatile.Read(ref _preCreditSerializationPermits)?.ReservedBytes ?? 0));

    internal void CompletePreCreditSendStream(
        long requestId,
        ushort streamId,
        Exception? exception = null)
    {
        Volatile.Read(ref _preCreditSerializedBudget)?
            .CompleteStream(requestId, streamId, exception);
        Volatile.Read(ref _preCreditSerializationPermits)?
            .CompleteStream(requestId, streamId, exception);
    }

    internal void AbortPreCreditSendStreams(long requestId, Exception exception)
    {
        Volatile.Read(ref _preCreditSerializedBudget)?.AbortRequest(requestId, exception);
        Volatile.Read(ref _preCreditSerializationPermits)?.AbortRequest(requestId, exception);
    }

    private async ValueTask AwaitPreCreditSerializationPermitAndSendAsync<T>(
        ValueTask pendingPermit,
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        PreCreditSerializedBudget permits,
        CancellationToken cancellationToken)
    {
        await pendingPermit.ConfigureAwait(false);
        await SerializeUnsizedStreamChunkAfterPermit(
            requestId,
            streamId,
            item,
            codec,
            permits,
            cancellationToken).ConfigureAwait(false);
    }

    private ValueTask SerializeUnsizedStreamChunkAfterPermit<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        PreCreditSerializedBudget? permits,
        CancellationToken cancellationToken)
    {
        IRpcByteBufferWriter? writer = null;
        PreCreditSerializedBudget? budget = null;
        var ownsWriter = true;
        var ownsPermit = permits is not null;
        var ownsBudget = false;
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

            if (permits is not null)
            {
                budget = GetOrCreatePreCreditSerializedBudget();
                var pendingBudget = budget.AcquireAsync(
                    requestId,
                    streamId,
                    encodedBytes,
                    cancellationToken);
                if (!pendingBudget.IsCompletedSuccessfully)
                {
                    ownsWriter = false;
                    ownsPermit = false;
                    return AwaitPreCreditByteBudgetAndSendAsync(
                        pendingBudget,
                        writer,
                        requestId,
                        streamId,
                        encodedBytes,
                        budget,
                        permits,
                        cancellationToken);
                }

                pendingBudget.GetAwaiter().GetResult();
                ownsBudget = true;
            }

            var pendingCredit = AcquireStreamSendCreditAsync(
                requestId,
                streamId,
                encodedBytes,
                cancellationToken);
            if (!pendingCredit.IsCompletedSuccessfully)
            {
                ownsWriter = false;
                ownsPermit = false;
                ownsBudget = false;
                return AwaitUnsizedStreamCreditAndSendAsync(
                    pendingCredit,
                    writer,
                    requestId,
                    streamId,
                    encodedBytes,
                    budget,
                    permits);
            }

            pendingCredit.GetAwaiter().GetResult();
            if (budget is not null)
            {
                budget.Release(encodedBytes);
                ownsBudget = false;
            }
            if (permits is not null)
            {
                permits.Release(1);
                ownsPermit = false;
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
            if (ownsBudget)
                budget!.Release(Math.Max(
                    1,
                    writer!.WrittenCount - ProtocolV2Constants.HeaderBytes - sizeof(ushort)));
            if (ownsPermit)
                permits!.Release(1);
            if (ownsWriter && writer is not null)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    private async ValueTask AwaitPreCreditByteBudgetAndSendAsync(
        ValueTask pendingBudget,
        IRpcByteBufferWriter writer,
        long requestId,
        ushort streamId,
        int encodedBytes,
        PreCreditSerializedBudget budget,
        PreCreditSerializedBudget permits,
        CancellationToken cancellationToken)
    {
        var ownsWriter = true;
        var ownsPermit = true;
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
            permits.Release(1);
            ownsPermit = false;

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
            if (ownsPermit)
                permits.Release(1);
            if (ownsWriter)
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
        PreCreditSerializedBudget? permits)
    {
        var ownsWriter = true;
        var ownsPermit = permits is not null;
        var ownsBudget = budget is not null;
        var creditAcquired = false;
        try
        {
            await pendingCredit.ConfigureAwait(false);
            creditAcquired = true;
            if (budget is not null)
            {
                budget.Release(encodedBytes);
                ownsBudget = false;
            }
            if (permits is not null)
            {
                permits.Release(1);
                ownsPermit = false;
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
            if (ownsBudget)
                budget!.Release(encodedBytes);
            if (ownsPermit)
                permits!.Release(1);
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

    private PreCreditSerializedBudget GetOrCreatePreCreditSerializationPermits()
    {
        var permits = Volatile.Read(ref _preCreditSerializationPermits);
        if (permits is not null)
            return permits;

        lock (_preCreditSerializationPermitGate)
        {
            permits = _preCreditSerializationPermits;
            if (permits is not null)
                return permits;

            var processorParallelism = Math.Clamp(Environment.ProcessorCount, 4, 16);
            var maxPermits = Math.Min(
                RuntimeContext.Protocol.MaxConcurrentStreamsPerConnection,
                processorParallelism);
            permits = new PreCreditSerializedBudget(
                maxPermits,
                RuntimeContext.Protocol.MaxConcurrentStreamsPerConnection);
            Volatile.Write(ref _preCreditSerializationPermits, permits);

            _ = _lifetimeToken.UnsafeRegister(
                static state =>
                {
                    var session = (RpcSession)state!;
                    Volatile.Read(ref session._preCreditSerializationPermits)?
                        .Complete(session.GetTerminalException());
                },
                this);
            return permits;
        }
    }
}
