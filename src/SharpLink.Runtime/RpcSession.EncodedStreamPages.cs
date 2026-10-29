namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    // Lazy and stream-only: unary calls never initialize or consult a page budget.
    private EncodedStreamPageBudget? _encodedStreamPageBudget;

    internal long EncodedStreamPageBytesOwned
        => Volatile.Read(ref _encodedStreamPageBudget)?.BytesOwned ?? 0;

    private EncodedStreamPageWriter? TryCreateEncodedStreamPageWriter<T>(
        IRpcCodec<T> codec,
        out int encodedBytes)
    {
        encodedBytes = 0;
        if (codec is not IStreamPageFixedSizeCodec fixedCodec ||
            _transport is not IStreamPageTransport ||
            ((IRpcRuntimeContext)RuntimeContext).Buffers is not SharpLinkBufferWriterPool ||
            Volatile.Read(ref _protocolState).Options is not { CompressionBinding: null })
        {
            return null;
        }

        var maxBytes = Math.Min(RuntimeContext.FlowControl.MaxSendQueueBytes, 1024 * 1024);
        if (maxBytes < EncodedStreamPageBudget.PageBytes)
            return null;
        var size = fixedCodec.EncodedBytes;
        if (size <= 0 || size > EncodedStreamPageBudget.PageBytes - ProtocolV2Constants.HeaderBytes - sizeof(ushort))
            return null;

        var budget = Volatile.Read(ref _encodedStreamPageBudget);
        if (budget is null)
        {
            var created = new EncodedStreamPageBudget(maxBytes);
            budget = Interlocked.CompareExchange(ref _encodedStreamPageBudget, created, null) ?? created;
        }
        encodedBytes = size;
        return new EncodedStreamPageWriter(budget);
    }

    private async ValueTask SendEncodedStreamPageChunkAsync<T>(
        long requestId,
        ushort streamId,
        T item,
        IRpcCodec<T> codec,
        EncodedStreamPageWriter writer,
        int encodedBytes,
        RpcDeadline deadline,
        TimeProvider? deadlineTimeProvider,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _terminal) is { } terminal)
            throw terminal.Exception;
        cancellationToken.ThrowIfCancellationRequested();
        if (!writer.TryBeginFrame(checked(ProtocolV2Constants.HeaderBytes + sizeof(ushort) + encodedBytes)))
        {
            // No page waiters and no altered fallback codec semantics. In particular, page
            // exhaustion cannot hold a stream's credit while it waits for another producer.
            await SendUnsizedStreamChunkAsync(
                requestId, streamId, item, codec, cancellationToken, deadline, deadlineTimeProvider)
                .ConfigureAwait(false);
            return;
        }

        var creditAcquired = false;
        try
        {
            await AcquireStreamSendCreditAsync(requestId, streamId, encodedBytes, cancellationToken)
                .ConfigureAwait(false);
            creditAcquired = true;
            using (writer.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, unchecked((ulong)requestId)))
            {
                BinaryPrimitives.WriteUInt16LittleEndian(writer.GetSpan(sizeof(ushort)), streamId);
                writer.Advance(sizeof(ushort));
                codec.Serialize(item, writer);
            }
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
            // Commit gives the slice one Q reference. Publication consumes it on entry on
            // EVERY outcome; the producer must never release Q again in its catch/finally.
            PublishStreamPageFrame(writer.CommitFrame());
        }
        catch
        {
            if (creditAcquired)
                ReturnUnsentStreamCredit(requestId, streamId, encodedBytes);
            throw;
        }
        finally
        {
            writer.AbandonFrame();
        }
    }

    private bool TrySendEncodedStreamPageComplete(
        long requestId,
        ushort streamId,
        EncodedStreamPageWriter writer,
        RpcDeadline deadline,
        TimeProvider? deadlineTimeProvider)
    {
        if (!writer.TryBeginFrame(ProtocolV2Constants.HeaderBytes + sizeof(ushort)))
            return false;
        try
        {
            using (writer.BeginPacketScope(ProtocolV2FrameType.StreamComplete, ProtocolV2FrameFlags.None, unchecked((ulong)requestId)))
            {
                BinaryPrimitives.WriteUInt16LittleEndian(writer.GetSpan(sizeof(ushort)), streamId);
                writer.Advance(sizeof(ushort));
            }
            ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
            var frame = writer.CommitFrame();
            try { PublishStreamPageFrame(frame); }
            finally { CompleteSendStream(requestId, streamId); }
            return true;
        }
        finally
        {
            writer.AbandonFrame();
        }
    }

    /// <summary>
    /// Consumes an already-held page slice reference immediately. Validation and pump
    /// construction failures release it here; TryEnqueue consumes it on every normal
    /// result, including Full and Closed. This is deliberately NOT the backpressure
    /// retry API whose Full result retains a caller-owned frame.
    /// </summary>
    internal void PublishStreamPageFrame(OwnedFrame frame)
    {
        var page = frame.Page ?? throw new ArgumentException("A page-backed frame is required.", nameof(frame));
        SendPump pump;
        try
        {
            ValidateOutboundFrame(frame.Memory.Span, allowEmpty: false);
            if (Volatile.Read(ref _protocolState).Options?.CompressionBinding is not null)
                throw new InvalidOperationException("Encoded stream pages require an uncompressed session.");
            pump = GetOrCreatePump();
        }
        catch
        {
            page.Release();
            throw;
        }

        var result = pump.TryEnqueue(frame);
        if (result != SendEnqueueResult.Accepted)
            throw CreateSendEnqueueFailure(result);
    }
}
