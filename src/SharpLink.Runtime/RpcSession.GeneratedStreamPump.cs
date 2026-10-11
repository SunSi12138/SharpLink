namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    internal async ValueTask PumpGeneratedOutboundStreamAsync<T>(
        long requestId,
        ushort streamId,
        IAsyncEnumerable<T> stream,
        IRpcCodec<T> codec,
        bool payloadNullable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(codec);

        var callContext = SharpLinkCallContext.Current;
        var deadline = callContext?.LocalRpcDeadline ?? default;
        var deadlineTimeProvider = callContext?.DeadlineTimeProvider;
        using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enumerator = stream.GetAsyncEnumerator(lifetimeCancellation.Token);
        var deadlineWon = false;
        var sendCreditLease = default(StreamFlowController.ResolvedSendCreditLease);
        var writerCancellation = default(CancellationTokenRegistration);
        var writerCancellationRegistered = false;
        try
        {
            var exactSizeCodec = codec as IRpcSizedCodec<T>;
            if (exactSizeCodec is not null && !exactSizeCodec.CanExactSize)
                exactSizeCodec = null;
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
                        deadlineWon = true;
                        TryCancelGeneratedLifetime(lifetimeCancellation);
                        _ = ObserveAbandonedGeneratedMoveNextAsync(moveNextTask);
                        throw CreateGeneratedStreamDeadlineExceededException();
                    }
                    hasNext = await moveNextTask.ConfigureAwait(false);
                }

                if (!hasNext)
                    break;

                var item = enumerator.Current;
                if (!payloadNullable && default(T) is null && item is null)
                {
                    throw new SharpLinkException(
                        SharpLinkErrorCode.Internal,
                        "A non-nullable RPC stream response was null.");
                }

                var send = SendGeneratedStreamChunkAsync(
                    requestId,
                    streamId,
                    item,
                    codec,
                    exactSizeCodec,
                    deadline,
                    deadlineTimeProvider,
                    lifetimeCancellation.Token,
                    sendCreditLease);
                if (!deadline.HasValue || deadlineTimeProvider is null || send.IsCompletedSuccessfully)
                {
                    sendCreditLease = await send.ConfigureAwait(false);
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
                        deadlineWon = true;
                        TryCancelGeneratedLifetime(lifetimeCancellation);
                        _ = ObserveAbandonedGeneratedSendAsync(sendTask);
                        throw CreateGeneratedStreamDeadlineExceededException();
                    }
                    sendCreditLease = await sendTask.ConfigureAwait(false);
                }
                if (UsesWriterOwnedStreams && !writerCancellationRegistered)
                {
                    writerCancellation = RegisterWriterOwnedStreamCancellation(sendCreditLease, cancellationToken);
                    writerCancellationRegistered = true;
                }
            }

            ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
            var complete = SendGeneratedStreamCompleteAsync(requestId, streamId, deadline,
                deadlineTimeProvider, lifetimeCancellation.Token);
            if (!deadline.HasValue || deadlineTimeProvider is null || complete.IsCompletedSuccessfully)
            {
                await complete.ConfigureAwait(false);
            }
            else
            {
                // Covers the WHOLE terminal operation, including an empty stream waiting
                // for state capacity or a client-role Request predecessor, not just its drain.
                var completion = complete.AsTask();
                try
                {
                    if (!await SharpLinkTimer.WaitAsync(completion, deadline, deadlineTimeProvider,
                        lifetimeCancellation.Token).ConfigureAwait(false))
                    {
                        deadlineWon = true;
                        TryCancelGeneratedLifetime(lifetimeCancellation);
                        throw CreateGeneratedStreamDeadlineExceededException();
                    }
                    await completion.ConfigureAwait(false);
                }
                catch
                {
                    _ = ObserveAbandonedGeneratedSendAsync(completion);
                    throw;
                }
            }
        }
        catch (SharpLinkException error) when (UsesWriterOwnedStreams &&
            error.Code == SharpLinkErrorCode.DeadlineExceeded && deadlineTimeProvider is not null &&
            deadline.IsExpired(deadlineTimeProvider))
        {
            // The frozen framework deadline can now win in EOF drain. A service error with
            // the same error code BEFORE that boundary remains an ordinary business failure.
            deadlineWon = true;
            throw;
        }
        finally
        {
            writerCancellation.Dispose();
            if (UsesWriterOwnedStreams && (deadlineWon || cancellationToken.IsCancellationRequested))
                AbortWriterOwnedLease(sendCreditLease, deadlineWon
                    ? CreateGeneratedStreamDeadlineExceededException()
                    : new OperationCanceledException(cancellationToken));
            TryCancelGeneratedLifetime(lifetimeCancellation);
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
                // Cleanup cannot replace a deadline that already owns the call terminal.
            }
        }
    }
}
