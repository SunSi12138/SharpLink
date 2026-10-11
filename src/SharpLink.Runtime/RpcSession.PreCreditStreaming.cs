namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private readonly Lock _preCreditSerializedBudgetGate = new();
    private PreCreditSerializedBudget? _preCreditSerializedBudget;

    internal ValueTask SendUnsizedStreamChunkAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        CancellationToken cancellationToken,
        RpcDeadline deadline = default,
        TimeProvider? deadlineTimeProvider = null,
        StreamFlowController.SendCreditLease? grantLease = null)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return SerializeUnsizedStreamChunk(
            requestId,
            streamId,
            item,
            codec,
            cancellationToken,
            deadline,
            deadlineTimeProvider,
            grantLease);
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

    private ValueTask SerializeUnsizedStreamChunk<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        CancellationToken cancellationToken,
        RpcDeadline deadline,
        TimeProvider? deadlineTimeProvider,
        StreamFlowController.SendCreditLease? grantLease)
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
            cancellationToken.ThrowIfCancellationRequested();

            if (!HasStreamFlowControl ||
                (grantLease is null
                    ? TryAcquireStreamSendCredit(requestId, streamId, encodedBytes)
                    : grantLease.TryAcquire(encodedBytes, cancellationToken)))
            {
                creditAcquired = HasStreamFlowControl;
                try
                {
                    ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
                    ownsWriter = false;
                    SendPacket(writer);
                }
                catch
                {
                    if (creditAcquired)
                    {
                        if (grantLease is null)
                            ReturnUnsentStreamCredit(requestId, streamId, encodedBytes);
                        else
                            grantLease.ReturnUnsent(encodedBytes);
                    }
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
                cancellationToken,
                deadline,
                deadlineTimeProvider,
                grantLease);
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
        CancellationToken cancellationToken,
        RpcDeadline deadline,
        TimeProvider? deadlineTimeProvider,
        StreamFlowController.SendCreditLease? grantLease)
    {
        var ownsWriter = true;
        var ownsBudget = false;
        var creditAcquired = false;
        try
        {
            await pendingBudget.ConfigureAwait(false);
            ownsBudget = true;

            var admission = grantLease is null
                ? AcquireStreamSendCreditAsync(requestId, streamId, encodedBytes, cancellationToken)
                : grantLease.AcquireAsync(encodedBytes, cancellationToken);
            await admission.ConfigureAwait(false);
            creditAcquired = true;

            budget.Release(encodedBytes);
            ownsBudget = false;

            ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
            ownsWriter = false;
            SendPacket(writer);
        }
        catch
        {
            if (creditAcquired)
            {
                if (grantLease is null)
                    ReturnUnsentStreamCredit(requestId, streamId, encodedBytes);
                else
                    grantLease.ReturnUnsent(encodedBytes);
            }
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

            // This is a local process-memory policy. It must not depend on either the configured
            // or negotiated connection receive window, which controls peer-visible wire credit.
            var maxBytes = RuntimeContext.FlowControl.MaxPreCreditSerializedBytes;

            // A queued budget waiter already owns a serialized writer, so waiter count is part of
            // the memory envelope. Bound queued backing to roughly one additional configured
            // budget worth of max-size frames (or one frame when the local budget is smaller than
            // a legal frame). Oversized sole-owner semantics remain enforced by the byte budget.
            var maxFrameBytes = Math.Max(1, NegotiatedMaxFramePayloadBytes);
            var derivedWaiters = Math.Max(1L, (long)maxBytes / maxFrameBytes);
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
