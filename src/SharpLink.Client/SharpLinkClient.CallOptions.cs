namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    public async ValueTask<T> InvokeWithCallOptionsAsync<T>(
        long interfaceHash,
        long methodHash,
        Action<IBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender,
        bool isOneWay,
        bool hasReturnPayload,
        SharpLinkCallOptions options,
        bool hasMethodTimeout,
        TimeSpan? methodTimeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var control = ResolveCallControl(
            options,
            includeClientDefault: !isOneWay,
            hasMethodTimeout,
            methodTimeout);
        var session = await GetReadySessionAsync(
            control.WaitForReady,
            control.Deadline,
            cancellationToken).ConfigureAwait(false);
        var requestId = isOneWay ? _requestManager.AllocateRequestId() : 0;
        RpcRequestOperation<T>? operation = null;
        if (!isOneWay)
        {
            var lease = await _requestManager.RentAsync<T>(
                control.WaitForReady,
                control.Deadline,
                cancellationToken).ConfigureAwait(false);
            requestId = lease.Id;
            operation = lease.Operation;
        }

        var flags = isOneWay ? ProtocolV2FrameFlags.OneWay : ProtocolV2FrameFlags.None;
        if (hasReturnPayload)
            flags |= ProtocolV2FrameFlags.HasReturn;
        if (cancellationToken.CanBeCanceled || control.Deadline is not null)
            flags |= ProtocolV2FrameFlags.Cancellable;

        using var timeoutRegistration = RegisterRequestTimeout(
            control.Deadline,
            requestId,
            isOneWay);
        await using var cancelRegistration = RegisterCancel(
            cancellationToken,
            requestId,
            isOneWay,
            cancellationToken);

        try
        {
            if (!isOneWay || streamSender is not null)
                BindRequestToSession(requestId, session);
            SendRpcCall(
                session,
                interfaceHash,
                methodHash,
                requestId,
                flags,
                payloadWriter,
                control.Deadline,
                control.Metadata);
        }
        catch (Exception exception)
        {
            TryUnbindRequest(requestId, out _);
            if (operation is null)
                throw;
            _requestManager.DispatchError(requestId, exception);
            return await operation.AsValueTask().ConfigureAwait(false);
        }

        if (streamSender is not null)
        {
            if (isOneWay)
            {
                try
                {
                    await streamSender(requestId, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    TryUnbindRequest(requestId, out _);
                }
            }
            else
                TrackBackgroundTask(RunStreamSenderAsync(streamSender, requestId, cancellationToken));
        }

        if (isOneWay)
        {
            TryUnbindRequest(requestId, out _);
            return default!;
        }
        return await operation!.AsValueTask().ConfigureAwait(false);
    }

    public IAsyncEnumerable<T> InvokeStreamingWithCallOptionsAsync<T>(
        long interfaceHash,
        long methodHash,
        Action<IBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender,
        SharpLinkCallOptions options,
        bool hasMethodTimeout,
        TimeSpan? methodTimeout,
        CancellationToken cancellationToken = default)
    {
        var control = ResolveCallControl(
            options,
            includeClientDefault: false,
            hasMethodTimeout,
            methodTimeout);
        var requestId = _requestManager.AllocateRequestId();
        _serverStreamRequestIds.Add(requestId);
        var dispatcher = PooledAsyncStreamDispatcher<T>.Rent(
            cancellationToken,
            _runtimeContext.Codecs);
        TrackBackgroundTask(StartStreamingWithCallOptionsAsync(
            dispatcher,
            interfaceHash,
            methodHash,
            requestId,
            payloadWriter,
            streamSender,
            control,
            cancellationToken));
        return dispatcher;
    }

    private async Task StartStreamingWithCallOptionsAsync<T>(
        PooledAsyncStreamDispatcher<T> dispatcher,
        long interfaceHash,
        long methodHash,
        long requestId,
        Action<IBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
    {
        try
        {
            var session = await GetReadySessionAsync(
                control.WaitForReady,
                control.Deadline,
                cancellationToken).ConfigureAwait(false);
            var timeoutRegistration = RegisterStreamTimeout(
                control.Deadline,
                requestId);
            var cancelRegistration = RegisterStreamCancel(
                cancellationToken,
                requestId,
                cancellationToken);
            var lifetime = new StreamCallLifetime(timeoutRegistration, cancelRegistration);
            if (!_streamCallLifetimes.TryAdd(requestId, lifetime))
            {
                lifetime.Dispose();
                throw new InvalidOperationException("A stream lifetime is already registered for this request.");
            }

            session.StreamManager.Register(requestId, 0, dispatcher);
            BindRequestToSession(requestId, session);
            var flags = cancellationToken.CanBeCanceled || control.Deadline is not null
                ? ProtocolV2FrameFlags.Cancellable
                : ProtocolV2FrameFlags.None;
            SendRpcCall(
                session,
                interfaceHash,
                methodHash,
                requestId,
                flags,
                payloadWriter,
                control.Deadline,
                control.Metadata);
            if (streamSender is not null)
                await streamSender(requestId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _serverStreamRequestIds.Remove(requestId);
            TryUnbindRequest(requestId, out _);
            CompleteStreamLifetime(requestId);
            dispatcher.Complete(exception);
        }
    }

    private ResolvedCallControl ResolveCallControl(
        SharpLinkCallOptions options,
        bool includeClientDefault,
        bool hasMethodTimeout,
        TimeSpan? methodTimeout)
    {
        if (options.Timeout is { } optionTimeout)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(optionTimeout, TimeSpan.Zero);
        if (methodTimeout is { } configuredMethodTimeout)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(configuredMethodTimeout, TimeSpan.Zero);
        if (options.EnableCompression)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.Unimplemented,
                "Request compression is not available until a compression capability is registered.");
        }

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? deadline = null;
        AddDeadlineCandidate(ref deadline, options.Deadline);
        if (options.Timeout is { } timeout)
            AddDeadlineCandidate(ref deadline, AddTimeout(now, timeout));
        if (methodTimeout is { } explicitMethodTimeout)
            AddDeadlineCandidate(ref deadline, AddTimeout(now, explicitMethodTimeout));
        if ((includeClientDefault || hasMethodTimeout) && _hasRequestTimeout)
            AddDeadlineCandidate(ref deadline, AddTimeout(now, _requestTimeoutValue));

        if (deadline is { } expired && expired <= now)
            throw CreateDeadlineExceededException();
        return new ResolvedCallControl(
            deadline,
            options.Metadata is { Count: > 0 } ? options.Metadata : null,
            options.WaitForReady);
    }

    private async ValueTask<RpcSession> GetReadySessionAsync(
        bool waitForReady,
        DateTimeOffset? deadline,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (State == SharpLinkConnectionState.Ready && ReadyConnectionCount != 0)
                return GetReadySession();

            if (!waitForReady)
                return GetReadySession();
            if (State == SharpLinkConnectionState.Stopped || _shutdownCts.IsCancellationRequested)
                throw CreateConnectionClosedException("Client has stopped.");

            var signal = Volatile.Read(ref _readySignal).Task;
            if (deadline is not { } absoluteDeadline)
            {
                await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var remaining = absoluteDeadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw CreateDeadlineExceededException();
            try
            {
                await signal.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw CreateDeadlineExceededException();
            }
        }
    }

    private static void AddDeadlineCandidate(
        ref DateTimeOffset? deadline,
        DateTimeOffset? candidate)
    {
        if (candidate is { } value && (deadline is null || value < deadline.Value))
            deadline = value;
    }

    private static DateTimeOffset AddTimeout(DateTimeOffset now, TimeSpan timeout)
    {
        try
        {
            return now.Add(timeout);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, exception.Message);
        }
    }

    private static SharpLinkException CreateDeadlineExceededException()
        => new(SharpLinkErrorCode.DeadlineExceeded, "Request deadline exceeded.");

    private void CompleteStreamLifetime(long requestId)
    {
        if (_streamCallLifetimes.TryRemove(requestId, out var lifetime))
            lifetime.Dispose();
    }

    private sealed class StreamCallLifetime(
        TimeoutRegistration timeoutRegistration,
        PooledCancellationRegistration cancellationRegistration) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            timeoutRegistration.Dispose();
            cancellationRegistration.Dispose();
        }
    }

    private readonly record struct ResolvedCallControl(
        DateTimeOffset? Deadline,
        SharpLinkMetadata? Metadata,
        bool WaitForReady);
}
