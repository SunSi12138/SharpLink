namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    internal ValueTask<bool> SendStreamChunkWithProgressAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        Func<long, bool> tryCommitProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(tryCommitProgress);

        if (codec is IRpcSizedCodec<T> sizedCodec &&
            sizedCodec.CanExactSize &&
            sizedCodec.TryGetEncodedSize(item, out var knownEncodedBytes, out var sizedSnapshot))
        {
            return SendKnownSizeStreamChunkWithProgressAsync(
                requestId,
                streamId,
                item,
                sizedCodec,
                knownEncodedBytes,
                sizedSnapshot,
                tryCommitProgress,
                cancellationToken);
        }

        return SendUnsizedStreamChunkWithProgressAsync(
            requestId,
            streamId,
            item,
            codec,
            tryCommitProgress,
            cancellationToken);
    }

    private async ValueTask<bool> SendKnownSizeStreamChunkWithProgressAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcSizedCodec<T> sizedCodec,
        int encodedBytes,
        IRpcSizedCodecSnapshot? sizedSnapshot,
        Func<long, bool> tryCommitProgress,
        CancellationToken cancellationToken)
    {
        var creditBytes = Math.Max(1, encodedBytes);
        var creditAcquired = false;
        var sent = false;
        IRpcByteBufferWriter? writer = null;
        var ownsWriter = true;
        try
        {
            await AcquireStreamSendCreditAsync(
                requestId,
                streamId,
                creditBytes,
                cancellationToken).ConfigureAwait(false);
            creditAcquired = true;

            writer = RuntimeContext.Buffers.Rent(
                checked(ProtocolV2Constants.HeaderBytes + NegotiatedMaxFramePayloadBytes + 4));
            using (writer.BeginPacketScope(
                       ProtocolV2FrameType.StreamData,
                       ProtocolV2FrameFlags.None,
                       unchecked((ulong)requestId)))
            {
                writer.GetSpan(sizeof(ushort) + encodedBytes + 4);
                writer.Advance(0);
                var idSpan = writer.GetSpan(sizeof(ushort));
                BinaryPrimitives.WriteUInt16LittleEndian(idSpan, streamId);
                writer.Advance(sizeof(ushort));
                sizedCodec.SerializeSized(item, writer, encodedBytes, sizedSnapshot);
                if (sizedSnapshot is not null)
                {
                    sizedCodec.ReleaseSnapshot(sizedSnapshot);
                    sizedSnapshot = null;
                }
            }

            var actualEncodedBytes = writer.WrittenCount - ProtocolV2Constants.HeaderBytes - sizeof(ushort);
            if (actualEncodedBytes != encodedBytes)
            {
                throw new InvalidOperationException(
                    "Generated stream item size differed after credit was acquired.");
            }

            if (!tryCommitProgress(requestId))
                return false;

            ownsWriter = false;
            SendPacket(writer);
            sent = true;
            return true;
        }
        finally
        {
            if (creditAcquired && !sent)
                ReturnUnsentStreamCredit(requestId, streamId, creditBytes);
            if (sizedSnapshot is not null)
                sizedCodec.ReleaseSnapshot(sizedSnapshot);
            if (ownsWriter && writer is not null)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    private ValueTask<bool> SendUnsizedStreamChunkWithProgressAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        Func<long, bool> tryCommitProgress,
        CancellationToken cancellationToken)
    {
        IRpcByteBufferWriter? writer = null;
        var ownsWriter = true;
        var creditAcquired = false;
        var sent = false;
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
                TryAcquireStreamSendCredit(requestId, streamId, encodedBytes))
            {
                creditAcquired = HasStreamFlowControl;
                if (!tryCommitProgress(requestId))
                    return ValueTask.FromResult(false);

                ownsWriter = false;
                SendPacket(writer);
                sent = true;
                return ValueTask.FromResult(true);
            }

            var budget = GetOrCreatePreCreditSerializedBudget();
            var pendingBudget = budget.AcquireAsync(
                requestId,
                streamId,
                encodedBytes,
                cancellationToken);
            ownsWriter = false;
            return AwaitPreCreditBudgetAndFlowCreditWithProgressAsync(
                pendingBudget,
                writer,
                requestId,
                streamId,
                encodedBytes,
                budget,
                tryCommitProgress,
                cancellationToken);
        }
        finally
        {
            if (creditAcquired && !sent)
                ReturnUnsentStreamCredit(requestId, streamId, Math.Max(
                    1,
                    (writer?.WrittenCount ?? ProtocolV2Constants.HeaderBytes + sizeof(ushort)) -
                    ProtocolV2Constants.HeaderBytes - sizeof(ushort)));
            if (ownsWriter && writer is not null)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    private async ValueTask<bool> AwaitPreCreditBudgetAndFlowCreditWithProgressAsync(
        ValueTask pendingBudget,
        IRpcByteBufferWriter writer,
        long requestId,
        ushort streamId,
        int encodedBytes,
        PreCreditSerializedBudget budget,
        Func<long, bool> tryCommitProgress,
        CancellationToken cancellationToken)
    {
        var ownsWriter = true;
        var ownsBudget = false;
        var creditAcquired = false;
        var sent = false;
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

            if (!tryCommitProgress(requestId))
                return false;

            ownsWriter = false;
            SendPacket(writer);
            sent = true;
            return true;
        }
        finally
        {
            if (creditAcquired && !sent)
                ReturnUnsentStreamCredit(requestId, streamId, encodedBytes);
            if (ownsBudget)
                budget.Release(encodedBytes);
            if (ownsWriter)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    internal async ValueTask PumpGeneratedOutboundStreamWithProgressAsync<T>(
        long requestId,
        ushort streamId,
        IAsyncEnumerable<T> stream,
        IRpcCodec<T> codec,
        bool payloadNullable,
        Func<long, bool> tryCommitProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(tryCommitProgress);

        var callContext = SharpLinkCallContext.Current;
        var deadline = callContext?.LocalRpcDeadline ?? default;
        var deadlineTimeProvider = callContext?.DeadlineTimeProvider;
        using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enumerator = stream.GetAsyncEnumerator(lifetimeCancellation.Token);
        var deadlineWon = false;
        try
        {
            while (true)
            {
                var moveNext = enumerator.MoveNextAsync();
                bool hasNext;
                if (!deadline.HasValue || deadlineTimeProvider is null || moveNext.IsCompletedSuccessfully)
                {
                    hasNext = await moveNext.ConfigureAwait(false);
                }
                else
                {
                    var moveNextTask = moveNext.AsTask();
                    if (!await SharpLinkTimer.WaitAsync(
                            moveNextTask,
                            deadline,
                            deadlineTimeProvider,
                            lifetimeCancellation.Token).ConfigureAwait(false))
                    {
                        _ = tryCommitProgress(requestId);
                        deadlineWon = true;
                        lifetimeCancellation.Cancel();
                        _ = ObserveAbandonedGeneratedMoveNextAsync(moveNextTask);
                        cancellationToken.ThrowIfCancellationRequested();
                        throw CreateGeneratedStreamDeadlineExceededException();
                    }
                    hasNext = await moveNextTask.ConfigureAwait(false);
                }

                if (!hasNext)
                {
                    if (!tryCommitProgress(requestId))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw CreateGeneratedStreamDeadlineExceededException();
                    }
                    SendStreamCompleteAsync(requestId, streamId);
                    break;
                }

                var item = enumerator.Current;
                if (!payloadNullable && default(T) is null && item is null)
                {
                    throw new SharpLinkException(
                        SharpLinkErrorCode.Internal,
                        "A non-nullable RPC stream response was null.");
                }

                var send = SendStreamChunkWithProgressAsync(
                    requestId,
                    streamId,
                    item,
                    codec,
                    tryCommitProgress,
                    lifetimeCancellation.Token);
                bool sent;
                if (!deadline.HasValue || deadlineTimeProvider is null || send.IsCompletedSuccessfully)
                {
                    sent = await send.ConfigureAwait(false);
                }
                else
                {
                    var sendTask = send.AsTask();
                    if (!await SharpLinkTimer.WaitAsync(
                            sendTask,
                            deadline,
                            deadlineTimeProvider,
                            lifetimeCancellation.Token).ConfigureAwait(false))
                    {
                        _ = tryCommitProgress(requestId);
                        deadlineWon = true;
                        lifetimeCancellation.Cancel();
                        _ = ObserveAbandonedGeneratedSendAsync(sendTask);
                        cancellationToken.ThrowIfCancellationRequested();
                        throw CreateGeneratedStreamDeadlineExceededException();
                    }
                    sent = await sendTask.ConfigureAwait(false);
                }

                if (!sent)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw CreateGeneratedStreamDeadlineExceededException();
                }
            }
        }
        finally
        {
            lifetimeCancellation.Cancel();
            try
            {
                var dispose = enumerator.DisposeAsync();
                if (deadlineWon && !dispose.IsCompletedSuccessfully)
                    _ = ObserveAbandonedGeneratedDisposeAsync(dispose);
                else
                    await dispose.ConfigureAwait(false);
            }
            catch when (deadlineWon)
            {
                // The terminal contender already won; an enumerator that ignores cancellation
                // cannot delay call teardown while its disposal completes.
            }
        }
    }
}
