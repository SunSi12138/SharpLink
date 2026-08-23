from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text()
    count = text.count(old)
    assert count == 1, f"{path}: expected one match, found {count}"
    p.write_text(text.replace(old, new, 1))

# Keep the P1 emission observer in repository whitespace style.
replace_once(
    "src/SharpLink.Client/SharpLinkClient.Invokers.cs",
    """            var deadlineExceeded = exception is SharpLinkException
                { Code: SharpLinkErrorCode.DeadlineExceeded };""",
    """            var deadlineExceeded = exception is SharpLinkException
            { Code: SharpLinkErrorCode.DeadlineExceeded };""")

# Every terminal source is arbitrated against the monotonic boundary under CompletionGate.
replace_once(
    "src/SharpLink.Client/PendingRequestTable.cs",
    """        if ((reason is PendingCallCompletionReason.RemoteError or
             PendingCallCompletionReason.RemoteStreamComplete) &&
            deadlineExpired)
        {
            reason = PendingCallCompletionReason.DeadlineExceeded;
            exception = null;
        }""",
    """        if (deadlineExpired && reason != PendingCallCompletionReason.DeadlineExceeded)
        {
            reason = PendingCallCompletionReason.DeadlineExceeded;
            exception = null;
        }""")

replace_once(
    "src/SharpLink.Client/PendingRequestTable.cs",
    """            if (!TryTakeCallAtIndex(index, out var call))
                continue;

            var payload = ReadOnlySequence<byte>.Empty;
            CompleteTakenCall(
                call!,
                PendingCallCompletionReason.ConnectionClosed,
                exception,
                ref payload);""",
    """            if (!TryTakeCallAtIndex(index, out var call, out var deadlineExpired))
                continue;

            var payload = ReadOnlySequence<byte>.Empty;
            CompleteTakenCall(
                call!,
                deadlineExpired
                    ? PendingCallCompletionReason.DeadlineExceeded
                    : PendingCallCompletionReason.ConnectionClosed,
                deadlineExpired ? null : exception,
                ref payload);""")

replace_once(
    "src/SharpLink.Client/PendingRequestTable.cs",
    """    private bool TryTakeCallAtIndex(int index, out PendingCall? call)
    {
        var slots = Volatile.Read(ref _slots)!;
        while (true)
        {
            var current = Volatile.Read(ref slots[index]);
            if (current is null)
            {
                call = null;
                return false;
            }

            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref slots[index]), current))
                    continue;

                if (!ReferenceEquals(Interlocked.CompareExchange(ref slots[index], null, current), current))
                    continue;

                current.WaitUntilRegistered();
                call = current;
                return true;
            }
        }
    }""",
    """    private bool TryTakeCallAtIndex(
        int index,
        out PendingCall? call,
        out bool deadlineExpired)
    {
        var slots = Volatile.Read(ref _slots)!;
        while (true)
        {
            var current = Volatile.Read(ref slots[index]);
            if (current is null)
            {
                call = null;
                deadlineExpired = false;
                return false;
            }

            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref slots[index]), current))
                    continue;

                deadlineExpired = current.Deadline.IsExpired(_timeProvider);
                if (!ReferenceEquals(Interlocked.CompareExchange(ref slots[index], null, current), current))
                    continue;

                current.WaitUntilRegistered();
                call = current;
                return true;
            }
        }
    }""")

# Producer-side progress uses the same timer-independent boundary.
replace_once(
    "src/SharpLink.Client/PendingRequestTable.cs",
    """    public bool Contains(long id)
    {""",
    """    public bool TryAcceptProducerProgress(long id)
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is null)
            return false;

        var index = (int)(id & _indexMask);
        var current = Volatile.Read(ref slots[index]);
        if (current is null || current.Id != id ||
            current.Kind is not (PendingCallKind.OneWay or
                                 PendingCallKind.ClientStreaming or
                                 PendingCallKind.DuplexStreaming))
        {
            return false;
        }

        PendingCall? expiredCall = null;
        lock (current.CompletionGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref slots[index]), current) || current.Id != id)
                return false;
            if (!current.Deadline.IsExpired(_timeProvider))
                return true;
            if (!ReferenceEquals(Interlocked.CompareExchange(ref slots[index], null, current), current))
                return false;
            current.WaitUntilRegistered();
            expiredCall = current;
        }

        var emptyPayload = ReadOnlySequence<byte>.Empty;
        CompleteTakenCall(
            expiredCall!, PendingCallCompletionReason.DeadlineExceeded, exception: null, ref emptyPayload);
        return false;
    }

    public bool Contains(long id)
    {""")

replace_once(
    "src/SharpLink.Client/ClientConnection.cs",
    """        try
        {
            await foreach (var item in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await Session.SendStreamChunkAsync(
                    requestId,
                    streamId,
                    item,
                    cancellationToken).ConfigureAwait(false);
            }

            Session.SendStreamCompleteAsync(requestId, streamId);""",
    """        try
        {
            await foreach (var item in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (!PendingCalls.TryAcceptProducerProgress(requestId))
                    throw new SharpLinkException(
                        SharpLinkErrorCode.DeadlineExceeded,
                        \"RPC deadline exceeded during client stream production.\");
                await Session.SendStreamChunkAsync(
                    requestId,
                    streamId,
                    item,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!PendingCalls.TryAcceptProducerProgress(requestId))
                throw new SharpLinkException(
                    SharpLinkErrorCode.DeadlineExceeded,
                    \"RPC deadline exceeded before client stream completion.\");
            Session.SendStreamCompleteAsync(requestId, streamId);""")

# Anchor the child clock before sampling inherited remaining lifetime.
replace_once(
    "src/SharpLink.Abstractions/RpcDeadline.cs",
    """    internal static RpcDeadline FromTimestamp(long timestamp)
        => new(true, timestamp);""",
    """    internal static RpcDeadline Create(
        TimeSpan timeBudget,
        long timestampNow,
        long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeBudget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timestampFrequency, 0);
        return new(
            true,
            SharpLinkTime.AddDuration(timestampNow, timeBudget, timestampFrequency));
    }

    internal static RpcDeadline FromTimestamp(long timestamp)
        => new(true, timestamp);""")

replace_once(
    "src/SharpLink.Client/SharpLinkClient.CallOptions.cs",
    """        TimeSpan? selectedTimeout = methodHasTimeout
            ? methodTimeout
            : allowDefaultTimeout
                ? _requestTimeout
                : null;

        var ambientCall = SharpLinkCallContext.Current;""",
    """        TimeSpan? selectedTimeout = methodHasTimeout
            ? methodTimeout
            : allowDefaultTimeout
                ? _requestTimeout
                : null;

        var timeProvider = _runtimeContext.TimeProvider;
        var deadlineAnchor = timeProvider.GetTimestamp();
        var ambientCall = SharpLinkCallContext.Current;""")

replace_once(
    "src/SharpLink.Client/SharpLinkClient.CallOptions.cs",
    """        var timeProvider = _runtimeContext.TimeProvider;
        var deadline = selectedTimeout is { } timeout
            ? RpcDeadline.Create(timeout, timeProvider)
            : default;""",
    """        var deadline = selectedTimeout is { } timeout
            ? RpcDeadline.Create(timeout, deadlineAnchor, timeProvider.TimestampFrequency)
            : default;""")

# Client response StreamData is gated before decompression and again by the existing post-decode gate.
replace_once(
    "src/SharpLink.Client/SharpLinkClient.Lifecycle.cs",
    """                    IRpcByteBufferWriter? decodedOwner = null;
                    try
                    {
                        payload = session.DecodeInboundPayload(""",
    """                    IRpcByteBufferWriter? decodedOwner = null;
                    if (header.Type == ProtocolV2FrameType.StreamData &&
                        !connection.PendingCalls.TryAcceptStreamData(unchecked((long)header.RequestId)))
                    {
                        continue;
                    }
                    try
                    {
                        payload = session.DecodeInboundPayload(""")

replace_once(
    "src/SharpLink.Client/SharpLinkClient.Lifecycle.cs",
    """                            if (header.Type == ProtocolV2FrameType.StreamData)
                            {
                                var streamId = RpcSession.ReadCompressedStreamId(payload);""",
    """                            if (header.Type == ProtocolV2FrameType.StreamData)
                            {
                                if (!connection.PendingCalls.TryAcceptStreamData(requestId))
                                    continue;
                                var streamId = RpcSession.ReadCompressedStreamId(payload);""")

# Server stream progress (data or completion) is deadline-gated once state exists.
replace_once(
    "src/SharpLink.Server/SharpLinkServer.RequestLoop.cs",
    """internal sealed partial class SharpLinkServer
{
    private async Task ProcessRequestLoop(ServerConnectionState connection)""",
    """internal sealed partial class SharpLinkServer
{
    private static bool TryAcceptInboundStreamProgress(
        StripedLongMap<ServerCallCancellationState> requestCancellationMap,
        long requestId)
    {
        if (!requestCancellationMap.TryCapture(
                requestId,
                static (id, state) => state.CaptureLease(id),
                out var callLease))
        {
            // No owning state yet: preserve pre-admission buffering.
            return true;
        }
        if (!callLease.TryAcquire())
            return false;
        try
        {
            return callLease.State.TryAcceptStreamData();
        }
        finally
        {
            callLease.ReleaseUse();
        }
    }

    private async Task ProcessRequestLoop(ServerConnectionState connection)""")

old_gate = """                        if (header.Type == ProtocolV2FrameType.StreamData)
                        {
                            var streamRequestId = unchecked((long)header.RequestId);
                            var acceptStreamData = true;
                            if (requestCancellationMap.TryCapture(
                                    streamRequestId,
                                    static (requestId, state) => state.CaptureLease(requestId),
                                    out var streamCallLease))
                            {
                                if (!streamCallLease.TryAcquire())
                                {
                                    // An owning call state existed but its generation is already
                                    // retiring. Do not let a stale chunk escape into StreamManager.
                                    acceptStreamData = false;
                                }
                                else
                                {
                                    try
                                    {
                                        acceptStreamData = streamCallLease.State.TryAcceptStreamData();
                                    }
                                    finally
                                    {
                                        streamCallLease.ReleaseUse();
                                    }
                                }
                            }

                            // No call state yet means this is pre-admission buffering. Once the
                            // request owns a state, every chunk (compressed or not) is gated here.
                            if (!acceptStreamData)
                                continue;
                        }"""
new_gate = """                        if (header.Type is ProtocolV2FrameType.StreamData or
                            ProtocolV2FrameType.StreamComplete)
                        {
                            var streamRequestId = unchecked((long)header.RequestId);
                            if (!TryAcceptInboundStreamProgress(requestCancellationMap, streamRequestId))
                                continue;
                        }"""
replace_once("src/SharpLink.Server/SharpLinkServer.RequestLoop.cs", old_gate, new_gate)

replace_once(
    "src/SharpLink.Server/SharpLinkServer.RequestLoop.cs",
    """                            else if (header.Type == ProtocolV2FrameType.StreamData)
                            {
                                session.StreamManager.CompleteStream(""",
    """                            else if (header.Type == ProtocolV2FrameType.StreamData)
                            {
                                if (!TryAcceptInboundStreamProgress(requestCancellationMap, failedRequestId))
                                    continue;
                                session.StreamManager.CompleteStream(""")

replace_once(
    "src/SharpLink.Server/SharpLinkServer.RequestLoop.cs",
    """                        // 3. 处理完整的消息 (这里不需要 await 阻塞网络读取，最好由 Task.Run 处理业务)
                        // 注意：messagePayload 在 Advance 之后就会失效，如果需要异步处理，必须 Copy
                        try
                        {""",
    """                        if (header.Type is ProtocolV2FrameType.StreamData or
                            ProtocolV2FrameType.StreamComplete)
                        {
                            var streamRequestId = unchecked((long)header.RequestId);
                            if (!TryAcceptInboundStreamProgress(requestCancellationMap, streamRequestId))
                                continue;
                        }

                        // 3. 处理完整的消息 (这里不需要 await 阻塞网络读取，最好由 Task.Run 处理业务)
                        // 注意：messagePayload 在 Advance 之后就会失效，如果需要异步处理，必须 Copy
                        try
                        {""")

# Admission-path decompression must not start business dispatch after the preserved deadline.
replace_once(
    "src/SharpLink.Server/SharpLinkServer.InvocationDispatch.cs",
    """        var supportsCooperativeCancellation =
            (isCancellable || serviceInfo.Module is not null) &&""",
    """        if (IsDeadlineExceeded(request.RpcDeadline))
        {
            session.ReturnDecodedPayload(decodedRequestOwner);
            decodedRequestOwner = null;
            var deadlineException = new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                \"Request deadline exceeded during admission/decompression.\");
            CompleteFailedRequestStreams(session, requestId, deadlineException);
            admittedCallState?.TryCancel(ServerCallCancellationReason.DeadlineExceeded);
            var responseSend = session.SendRpcErrorWithBackpressureAsync(
                requestId, deadlineException, connection.ConnectionToken);
            return ReleaseDispatchResourcesAfterResponseAsync(
                responseSend, admittedCallState, requestId, requestCancellationMap, connection);
        }

        var supportsCooperativeCancellation =
            (isCancellable || serviceInfo.Module is not null) &&""")

replace_once(
    "src/SharpLink.Server/SharpLinkServer.AdmissionDispatch.cs",
    """        var supportsCooperativeCancellation =
            (isCancellable || serviceInfo.Module is not null) &&""",
    """        if (IsDeadlineExceeded(request.RpcDeadline))
        {
            session.ReturnDecodedPayload(decodedRequestOwner);
            decodedRequestOwner = null;
            admittedCallState?.TryCancel(ServerCallCancellationReason.DeadlineExceeded);
            DrainFailedOneWayStreams(session, requestId, descriptor.ClientStreamCount);
            ReleaseOneWayDispatchResources(
                admittedCallState, requestId, requestCancellationMap, connection);
            return;
        }

        var supportsCooperativeCancellation =
            (isCancellable || serviceInfo.Module is not null) &&""")

print("review5 P2A source patch applied")
