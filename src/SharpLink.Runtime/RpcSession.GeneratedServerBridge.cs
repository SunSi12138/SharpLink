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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(codec);

        var callContext = SharpLinkCallContext.Current;
        var deadline = callContext?.LocalRpcDeadline ?? default;
        var deadlineTimeProvider = callContext?.DeadlineTimeProvider;
        var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IAsyncEnumerator<T> enumerator;
        try
        {
            enumerator = stream.GetAsyncEnumerator(lifetimeCancellation.Token);
        }
        catch (Exception exception)
        {
            if (SharpLinkPhysicalWork.TransferLifetime(exception, lifetimeCancellation) is { } terminal)
                throw terminal;
            lifetimeCancellation.Dispose();
            throw;
        }
        var cleanupTransferred = false;
        EncodedStreamPageWriter? pageWriter = null;
        try
        {
            pageWriter = TryCreateEncodedStreamPageWriter(codec, out var pageEncodedBytes);
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
                    try
                    {
                        if (!await SharpLinkTimer.WaitAsync(
                                moveNextTask,
                                deadline,
                                deadlineTimeProvider,
                                lifetimeCancellation.Token).ConfigureAwait(false))
                        {
                            var terminal = CreateGeneratedStreamDeadlineExceededException();
                            cleanupTransferred = true;
                            var owner = SharpLinkPhysicalWork.CreateStreamOwner(terminal, lifetimeCancellation, pageWriter);
                            owner.Track(CompleteAbandonedGeneratedStreamAsync(moveNextTask, enumerator, lifetimeCancellation, owner));
                            throw terminal;
                        }
                    }
                    catch (OperationCanceledException exception) when (lifetimeCancellation.IsCancellationRequested)
                    {
                        // The ownership association must use a fresh framework terminal, never
                        // an exception instance that arbitrary user code could reuse elsewhere.
                        var terminal = SharpLinkPhysicalWork.CreateCancellationTerminal(exception);
                        cleanupTransferred = true;
                        var owner = SharpLinkPhysicalWork.CreateStreamCarrierOwner(terminal, exception, lifetimeCancellation, pageWriter);
                        owner.Track(CompleteAbandonedGeneratedStreamAsync(moveNextTask, enumerator, lifetimeCancellation, owner));
                        throw terminal;
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

                var send = pageWriter is not null
                    ? SendEncodedStreamPageChunkAsync(
                        requestId, streamId, item, codec, pageWriter, pageEncodedBytes,
                        deadline, deadlineTimeProvider, lifetimeCancellation.Token)
                    : SendGeneratedStreamChunkAsync(
                        requestId, streamId, item, codec, exactSizeCodec,
                        deadline, deadlineTimeProvider, lifetimeCancellation.Token);
                if (!deadline.HasValue || deadlineTimeProvider is null || send.IsCompletedSuccessfully)
                {
                    await send.ConfigureAwait(false);
                }
                else
                {
                    var sendTask = send.AsTask();
                    try
                    {
                        if (!await SharpLinkTimer.WaitAsync(
                                sendTask,
                                deadline,
                                deadlineTimeProvider,
                                lifetimeCancellation.Token).ConfigureAwait(false))
                        {
                            var terminal = CreateGeneratedStreamDeadlineExceededException();
                            cleanupTransferred = true;
                            var owner = SharpLinkPhysicalWork.CreateStreamOwner(terminal, lifetimeCancellation, pageWriter);
                            owner.Track(CompleteAbandonedGeneratedStreamAsync(sendTask, enumerator, lifetimeCancellation, owner));
                            throw terminal;
                        }
                    }
                    catch (OperationCanceledException exception) when (lifetimeCancellation.IsCancellationRequested)
                    {
                        // The ownership association must use a fresh framework terminal, never
                        // an exception instance that arbitrary user code could reuse elsewhere.
                        var terminal = SharpLinkPhysicalWork.CreateCancellationTerminal(exception);
                        cleanupTransferred = true;
                        var owner = SharpLinkPhysicalWork.CreateStreamCarrierOwner(terminal, exception, lifetimeCancellation, pageWriter);
                        owner.Track(CompleteAbandonedGeneratedStreamAsync(sendTask, enumerator, lifetimeCancellation, owner));
                        throw terminal;
                    }
                    await sendTask.ConfigureAwait(false);
                }
            }

            ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
            if (pageWriter is null || !TrySendEncodedStreamPageComplete(
                    requestId, streamId, pageWriter, deadline, deadlineTimeProvider))
                SendGeneratedStreamComplete(requestId, streamId, deadline, deadlineTimeProvider);
        }
        catch (SharpLinkException exception) when (
            !cleanupTransferred && exception.Code == SharpLinkErrorCode.DeadlineExceeded &&
            ((deadlineTimeProvider is not null && deadline.IsExpired(deadlineTimeProvider)) ||
                SharpLinkPhysicalWork.GetAbandonedUserWork(exception) is not null))
        {
            // The source or final publication check can select its deadline after its ValueTask
            // has been consumed. Retain any physical descendant before entering disposal.
            var terminal = new SharpLinkException(
                exception.Code, exception.DetailCode, exception.Message, exception);
            cleanupTransferred = true;
            var owner = SharpLinkPhysicalWork.CreateStreamCarrierOwner(terminal, exception, lifetimeCancellation, pageWriter);
            owner.Track(CompleteAbandonedGeneratedStreamAsync(
                Task.FromException(exception), enumerator, lifetimeCancellation, owner));
            throw terminal;
        }
        catch (OperationCanceledException exception) when (!cleanupTransferred &&
            (lifetimeCancellation.IsCancellationRequested || SharpLinkPhysicalWork.GetAbandonedUserWork(exception) is not null))
        {
            // Local call cancellation or an associated nested terminal can leave disposal
            // pending. The source has been consumed already; transfer only remaining cleanup.
            // An unassociated application cancellation retains ordinary disposal precedence.
            var terminal = SharpLinkPhysicalWork.CreateCancellationTerminal(exception);
            cleanupTransferred = true;
            var owner = SharpLinkPhysicalWork.CreateStreamCarrierOwner(terminal, exception, lifetimeCancellation, pageWriter);
            owner.Track(CompleteAbandonedGeneratedStreamAsync(
                Task.FromException(exception), enumerator, lifetimeCancellation, owner));
            throw terminal;
        }
        finally
        {
            if (!cleanupTransferred)
            {
                var disposalTransferred = false;
                try
                {
                    TryCancelGeneratedLifetime(lifetimeCancellation);
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // Disposal may carry unfinished nested work. Both the linked source and
                    // the producer page follow that descendant's full physical completion group.
                    if (SharpLinkPhysicalWork.TransferStreamLifetime(
                            exception, lifetimeCancellation, pageWriter) is { } terminal)
                    {
                        disposalTransferred = true;
                        throw terminal;
                    }
                    throw;
                }
                finally
                {
                    if (!disposalTransferred)
                    {
                        try { lifetimeCancellation.Dispose(); }
                        finally { pageWriter?.Dispose(); }
                    }
                }
            }
        }
    }

    private static async Task CompleteAbandonedGeneratedStreamAsync<T>(
        Task operation,
        IAsyncEnumerator<T> enumerator,
        CancellationTokenSource lifetimeCancellation,
        SharpLinkPhysicalWork.PhysicalOwner owner)
    {
        // Neither synchronous cancellation callbacks nor the synchronous prefix of DisposeAsync
        // may hold the selected public terminal. Never capture an application context here.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        TryCancelGeneratedLifetime(lifetimeCancellation);
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await owner.WaitForDescendantAsync(exception).ConfigureAwait(false);
        }
        try
        {
            // In particular, compiler-generated iterators reject concurrent MoveNext/Dispose.
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await owner.WaitForDescendantAsync(exception).ConfigureAwait(false);
        }
        // The owner releases the linked source only when all physical cleanup in its dependency
        // component has finished, including aliases and cross-recycled terminal exceptions.
    }

    private static void TryCancelGeneratedLifetime(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch
        {
            // Cancellation is cleanup after the call has already selected its terminal path.
            // User callbacks cannot replace that terminal outcome.
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
internal sealed class RpcSessionGeneratedServerBridge(RpcSession session) : IRpcGeneratedServerBridge
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
        long contractId,
        long methodId,
        CancellationToken cancellationToken)
        => session.PumpGeneratedOutboundStreamAsync(
            requestId,
            streamId,
            stream,
            codec,
            payloadNullable,
            cancellationToken);
}
