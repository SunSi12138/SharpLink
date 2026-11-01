namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    internal IAsyncEnumerable<T> CreateGeneratedInboundStream<T>(
        long requestId,
        ushort streamId,
        IRpcCodec<T> codec,
        bool payloadNullable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(codec);
        var dispatcher = PooledAsyncStreamDispatcher<T>.Rent(
            cancellationToken,
            codec,
            payloadNullable);
        try
        {
            StreamManager.Register(requestId, streamId, dispatcher);
            return dispatcher;
        }
        catch (Exception registrationException)
        {
            dispatcher.Complete(registrationException);
            SharpLinkAsyncCleanup.DisposeSynchronously(dispatcher);
            throw;
        }
    }

    internal async ValueTask PumpGeneratedOutboundStreamAsync<T>(
        long requestId,
        ushort streamId,
        IAsyncEnumerable<T> stream,
        IRpcCodec<T> codec,
        bool payloadNullable,
        CancellationToken cancellationToken,
        IGeneratedStreamPhysicalOwner? physicalOwner = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(codec);

        var callContext = SharpLinkCallContext.Current;
        var deadline = callContext?.LocalRpcDeadline ?? default;
        var deadlineTimeProvider = callContext?.DeadlineTimeProvider;
        var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IAsyncEnumerator<T>? enumerator = null;
        Task? pendingOperation = null;
        var terminalSelected = false;
        Exception? invocationFailure = null;
        try
        {
            enumerator = stream.GetAsyncEnumerator(lifetimeCancellation.Token);
            var exactSizeCodec = codec as IRpcSizedCodec<T>;
            if (exactSizeCodec is not null && !exactSizeCodec.CanExactSize)
                exactSizeCodec = null;

            while (true)
            {
                var moveNext = enumerator.MoveNextAsync();
                bool hasNext;
                if (moveNext.IsCompletedSuccessfully ||
                    ((!deadline.HasValue || deadlineTimeProvider is null) && !cancellationToken.CanBeCanceled))
                {
                    hasNext = await moveNext.ConfigureAwait(false);
                }
                else
                {
                    var moveNextTask = moveNext.AsTask();
                    pendingOperation = moveNextTask;
                    if (deadline.HasValue && deadlineTimeProvider is not null)
                    {
                        if (!await SharpLinkTimer.WaitForCompletionAsync(moveNextTask, deadline, deadlineTimeProvider,
                                lifetimeCancellation.Token).ConfigureAwait(false))
                        {
                            terminalSelected = true;
                            throw CreateGeneratedStreamDeadlineExceededException();
                        }
                    }
                    else
                    {
                        // Incomplete cancellable operations without a deadline now need a
                        // Task conversion/wait so cancellation can leave a physical tail.
                        // Wait forwarding is suppressed in THIS existing state machine; no
                        // completion adapter Task. Source-ready wins at arbitration, otherwise
                        // caller cancellation wins while the original source stays pending.
                        await ((Task)moveNextTask).WaitAsync(lifetimeCancellation.Token)
                            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                        if (!moveNextTask.IsCompleted)
                        {
                            lifetimeCancellation.Token.ThrowIfCancellationRequested();
                            throw new InvalidOperationException("Generated source wait ended without completion or caller cancellation.");
                        }
                    }
                    // Wait and all timer cleanup succeeded without consuming the source.
                    // Transfer its outcome to this invocation BEFORE the direct await can throw.
                    pendingOperation = null;
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
                    lifetimeCancellation.Token);
                if (send.IsCompletedSuccessfully ||
                    ((!deadline.HasValue || deadlineTimeProvider is null) && !cancellationToken.CanBeCanceled))
                {
                    await send.ConfigureAwait(false);
                }
                else
                {
                    var sendTask = send.AsTask();
                    pendingOperation = sendTask;
                    if (deadline.HasValue && deadlineTimeProvider is not null)
                    {
                        if (!await SharpLinkTimer.WaitForCompletionAsync(sendTask, deadline, deadlineTimeProvider,
                                lifetimeCancellation.Token).ConfigureAwait(false))
                        {
                            terminalSelected = true;
                            throw CreateGeneratedStreamDeadlineExceededException();
                        }
                    }
                    else
                    {
                        // Incomplete cancellable operations without a deadline now need a
                        // Task conversion/wait so cancellation can leave a physical tail.
                        // Wait forwarding is suppressed in THIS existing state machine; no
                        // completion adapter Task. Source-ready wins at arbitration, otherwise
                        // caller cancellation wins while the original source stays pending.
                        await sendTask.WaitAsync(lifetimeCancellation.Token)
                            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                        if (!sendTask.IsCompleted)
                        {
                            lifetimeCancellation.Token.ThrowIfCancellationRequested();
                            throw new InvalidOperationException("Generated source wait ended without completion or caller cancellation.");
                        }
                    }
                    pendingOperation = null;
                    await sendTask.ConfigureAwait(false);
                }
            }

            ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
            SendGeneratedStreamComplete(requestId, streamId, deadline, deadlineTimeProvider);
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.DeadlineExceeded)
        {
            terminalSelected = true;
            invocationFailure = exception;
            throw;
        }
        catch (OperationCanceledException exception) when (lifetimeCancellation.IsCancellationRequested)
        {
            terminalSelected = true;
            invocationFailure = exception;
            throw;
        }
        catch (Exception exception)
        {
            invocationFailure = exception;
            throw;
        }
        finally
        {
            Exception? residual = null;
            Exception? lifetimeFailure = null;
            try { lifetimeCancellation.Cancel(); }
            catch (Exception error) { lifetimeFailure = error; }
            if (enumerator is null)
                DisposeGeneratedLifetime(lifetimeCancellation, physicalOwner, lifetimeFailure, ref residual);
            else if (!terminalSelected && pendingOperation is null)
            {
                // Ordinary completion remains in this existing state machine: no tail Task,
                // registration or helper state machine on the normal-success path.
                try { await enumerator.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error)
                {
                    ReportGeneratedPhysicalFailure(physicalOwner, GeneratedPhysicalFailureStage.EnumeratorDispose,
                        error, ref residual);
                }
                DisposeGeneratedLifetime(lifetimeCancellation, physicalOwner, lifetimeFailure, ref residual);
            }
            else
            {
                try
                {
                    await FinishGeneratedEnumeratorAsync(enumerator, pendingOperation, lifetimeCancellation,
                        physicalOwner, terminalSelected, lifetimeFailure).ConfigureAwait(false);
                }
                catch (Exception error) { residual = PhysicalTaskFailureException.GetRaw(error); }
            }
            if (residual is not null)
            {
                // A later untransferred infrastructure error cannot mask an earlier invocation
                // failure. Accepted permit outcomes are NOT copied into this caller-owned residual.
                if (invocationFailure is not null)
                    residual = PhysicalTaskFailureException.CombineRaw(PhysicalTaskFailureException.GetRaw(invocationFailure), residual);
                throw PhysicalTaskFailureException.From(residual!);
            }
        }
    }

    private void SendGeneratedStreamComplete(
        long requestId,
        ushort streamId,
        RpcDeadline deadline,
        TimeProvider? deadlineTimeProvider)
    {
        var writer = RentFrameWriter();
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

            // The enqueue is the publication commit. Re-check after frame construction so clean
            // EOF cannot publish after the frozen RPC deadline merely because the pump checked
            // before building the terminal frame.
            ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
            ownsWriter = false;
            try
            {
                SendPacket(writer);
            }
            finally
            {
                CompleteSendStream(requestId, streamId);
            }
        }
        finally
        {
            if (ownsWriter)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    private static void ThrowIfGeneratedStreamDeadlineExpired(
        RpcDeadline deadline,
        TimeProvider? timeProvider)
    {
        if (timeProvider is not null && deadline.IsExpired(timeProvider))
            throw CreateGeneratedStreamDeadlineExceededException();
    }

    private static SharpLinkException CreateGeneratedStreamDeadlineExceededException()
        => new(
            SharpLinkErrorCode.DeadlineExceeded,
            "RPC deadline exceeded during server stream production.");

    // Keep the generated-server path concrete and codec-bound. Exact-size codecs retain the
    // credit-before-serialize path; only the universal unsized fallback enters the session-owned
    // pre-credit serialized-memory admission helper.
    private ValueTask SendGeneratedStreamChunkAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        IRpcSizedCodec<T>? exactSizeCodec,
        RpcDeadline deadline,
        TimeProvider? deadlineTimeProvider,
        CancellationToken cancellationToken)
    {
        if (exactSizeCodec is not null &&
            exactSizeCodec.TryGetEncodedSize(item, out var knownEncodedBytes, out var sizedSnapshot))
        {
            return SendStreamChunkKnownSizeAsync(
                requestId,
                streamId,
                item,
                exactSizeCodec,
                knownEncodedBytes,
                sizedSnapshot,
                cancellationToken,
                deadline,
                deadlineTimeProvider);
        }

        return SendUnsizedStreamChunkAsync(
            requestId,
            streamId,
            item,
            codec,
            cancellationToken,
            deadline,
            deadlineTimeProvider);
    }

    internal async ValueTask SendStreamChunkKnownSizeAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcSizedCodec<T> sizedCodec,
        int encodedBytes,
        IRpcSizedCodecSnapshot? sizedSnapshot,
        CancellationToken cancellationToken,
        RpcDeadline deadline = default,
        TimeProvider? deadlineTimeProvider = null)
    {
        var creditBytes = Math.Max(1, encodedBytes);
        var creditAcquired = false;
        IRpcByteBufferWriter? writer = null;
        var ownsWriter = true;
        Exception? operationError = null;
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
                    var snapshotToRelease = sizedSnapshot;
                    sizedSnapshot = null;
                    sizedCodec.ReleaseSnapshot(snapshotToRelease);
                }
            }

            var actualEncodedBytes = writer.WrittenCount - ProtocolV2Constants.HeaderBytes - sizeof(ushort);
            if (actualEncodedBytes != encodedBytes)
            {
                throw new InvalidOperationException(
                    "Generated stream item size differed after credit was acquired.");
            }

            ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
            ownsWriter = false;
            SendPacket(writer);
        }
        catch (Exception exception)
        {
            operationError = exception;
            if (creditAcquired)
                ReturnUnsentStreamCredit(requestId, streamId, creditBytes);
            throw;
        }
        finally
        {
            try
            {
                if (sizedSnapshot is not null)
                {
                    var snapshotToRelease = sizedSnapshot;
                    sizedSnapshot = null;
                    try
                    {
                        sizedCodec.ReleaseSnapshot(snapshotToRelease);
                    }
                    catch when (operationError is not null)
                    {
                        // Preserve the operation failure when snapshot cleanup also fails.
                    }
                }
            }
            finally
            {
                if (ownsWriter && writer is not null)
                    RuntimeContext.Buffers.Return(writer);
            }
        }
    }
}

/// <summary>
/// Exposes only generated stream protocol operations. Business exception policy belongs to the
/// Server invocation bridge that composes this adapter.
/// </summary>
internal sealed class RpcSessionGeneratedServerBridge(
    RpcSession session,
    IGeneratedStreamPhysicalOwner? physicalOwner = null) : IRpcGeneratedServerBridge
{
    public void EnsureUserCodeEntry(long requestId)
    {
        if (SharpLinkCallContext.Current is { } context &&
            context.DeadlineTimeProvider is { } timeProvider &&
            context.LocalRpcDeadline.IsExpired(timeProvider))
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "Request deadline exceeded.");
        }
    }

    public IAsyncEnumerable<T> CreateInboundStream<T>(
        long requestId,
        ushort streamId,
        IRpcCodec<T> codec,
        bool payloadNullable,
        CancellationToken cancellationToken)
        => session.CreateGeneratedInboundStream(
            requestId,
            streamId,
            codec,
            payloadNullable,
            cancellationToken);

    public ValueTask PumpOutboundStreamAsync<T>(
        long requestId,
        ushort streamId,
        IAsyncEnumerable<T> stream,
        IRpcCodec<T> codec,
        bool payloadNullable,
        RpcMethodFacts method,
        CancellationToken cancellationToken)
        => session.PumpGeneratedOutboundStreamAsync(
            requestId,
            streamId,
            stream,
            codec,
            payloadNullable,
            cancellationToken,
            physicalOwner);
}
