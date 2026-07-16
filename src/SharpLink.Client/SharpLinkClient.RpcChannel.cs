


namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private static readonly Action<object?> SRequestCancelCallback = static state =>
    {
        var cancelState = (RequestCancelState)state!;
        if (!cancelState.TryBeginInvocation())
            return;

        try
        {
            cancelState.Client.OnRequestCancel(cancelState);
        }
        finally
        {
            cancelState.ReturnAfterInvocation();
        }
    };

    private static readonly Action<object?> SStreamCancelCallback = static state =>
    {
        var cancelState = (StreamCancelState)state!;
        if (!cancelState.TryBeginInvocation())
            return;

        try
        {
            cancelState.Client.OnStreamCancel(cancelState);
        }
        finally
        {
            cancelState.ReturnAfterInvocation();
        }
    };

    private static readonly Action<object?> SRequestTimeoutCallback = static state =>
    {
        var timeoutState = (RequestTimeoutState)state!;
        if (!timeoutState.TryBeginInvocation())
            return;

        try
        {
            timeoutState.Client.OnRequestTimeout(timeoutState);
        }
        finally
        {
            timeoutState.ReturnAfterInvocation();
        }
    };

    private static readonly Action<object?> SStreamTimeoutCallback = static state =>
    {
        var timeoutState = (StreamTimeoutState)state!;
        if (!timeoutState.TryBeginInvocation())
            return;

        try
        {
            timeoutState.Client.OnStreamTimeout(timeoutState);
        }
        finally
        {
            timeoutState.ReturnAfterInvocation();
        }
    };

    public ValueTask<T> InvokeAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, false, true, false, null);

    public ValueTask<T> InvokeNoPayloadAsync<T>(long interfaceHash, long methodHash)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, null, null, false, true, false, null);

    public ValueTask<T> InvokeNoReturnAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, false, false, false, null);

    public ValueTask<T> InvokeNoReturnNoPayloadAsync<T>(long interfaceHash, long methodHash)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, null, null, false, false, false, null);

    public ValueTask<T> InvokeCancellableAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, false, true, cancellationToken, false, null);

    public ValueTask<T> InvokeCancellableNoPayloadAsync<T>(long interfaceHash, long methodHash, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, null, false, true, cancellationToken, false, null);

    public ValueTask<T> InvokeCancellableWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, false, true, cancellationToken, true, null);

    public ValueTask<T> InvokeCancellableWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, null, false, true, cancellationToken, true, null);

    public ValueTask<T> InvokeCancellableWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, TimeSpan timeout, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, false, true, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public ValueTask<T> InvokeCancellableWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, TimeSpan timeout, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, null, false, true, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public ValueTask<T> InvokeCancellableNoReturnAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, false, false, cancellationToken, false, null);

    public ValueTask<T> InvokeCancellableNoReturnNoPayloadAsync<T>(long interfaceHash, long methodHash, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, null, false, false, cancellationToken, false, null);

    public ValueTask<T> InvokeCancellableNoReturnWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, false, false, cancellationToken, true, null);

    public ValueTask<T> InvokeCancellableNoReturnWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, null, false, false, cancellationToken, true, null);

    public ValueTask<T> InvokeCancellableNoReturnWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, TimeSpan timeout, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, false, false, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public ValueTask<T> InvokeCancellableNoReturnWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, TimeSpan timeout, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, null, false, false, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public async ValueTask InvokeOneWayAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, null, true, false, false, null);

    public async ValueTask InvokeOneWayNoPayloadAsync(long interfaceHash, long methodHash)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, null, null, true, false, false, null);

    public async ValueTask InvokeOneWayClientStreamAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, streamSender, true, false, false, null);

    public async ValueTask InvokeOneWayClientStreamNoPayloadAsync(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, null, streamSender, true, false, false, null);

    public async ValueTask InvokeCancellableOneWayAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, null, true, false, cancellationToken, false, null);

    public async ValueTask InvokeCancellableOneWayNoPayloadAsync(long interfaceHash, long methodHash, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, null, null, true, false, cancellationToken, false, null);

    public async ValueTask InvokeCancellableOneWayWithDefaultTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, null, true, false, cancellationToken, true, null);

    public async ValueTask InvokeCancellableOneWayWithDefaultTimeoutNoPayloadAsync(long interfaceHash, long methodHash, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, null, null, true, false, cancellationToken, true, null);

    public async ValueTask InvokeCancellableOneWayWithTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, TimeSpan timeout, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, null, true, false, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public async ValueTask InvokeCancellableOneWayWithTimeoutNoPayloadAsync(long interfaceHash, long methodHash, TimeSpan timeout, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, null, null, true, false, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public async ValueTask InvokeCancellableOneWayClientStreamAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, streamSender, true, false, cancellationToken, false, null);

    public async ValueTask InvokeCancellableOneWayClientStreamNoPayloadAsync(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, null, streamSender, true, false, cancellationToken, false, null);

    public async ValueTask InvokeCancellableOneWayClientStreamWithDefaultTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, streamSender, true, false, cancellationToken, true, null);

    public async ValueTask InvokeCancellableOneWayClientStreamWithDefaultTimeoutNoPayloadAsync(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, null, streamSender, true, false, cancellationToken, true, null);

    public async ValueTask InvokeCancellableOneWayClientStreamWithTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, streamSender, true, false, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public async ValueTask InvokeCancellableOneWayClientStreamWithTimeoutNoPayloadAsync(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, null, streamSender, true, false, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public ValueTask<T> InvokeClientStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, false, true, false, null);

    public ValueTask<T> InvokeClientStreamNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, null, streamSender, false, true, false, null);

    public ValueTask<T> InvokeClientStreamNoReturnAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, false, false, false, null);

    public ValueTask<T> InvokeClientStreamNoReturnNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, null, streamSender, false, false, false, null);

    public ValueTask<T> InvokeCancellableClientStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, false, true, cancellationToken, false, null);

    public ValueTask<T> InvokeCancellableClientStreamNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, streamSender, false, true, cancellationToken, false, null);

    public ValueTask<T> InvokeCancellableClientStreamWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, false, true, cancellationToken, true, null);

    public ValueTask<T> InvokeCancellableClientStreamWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, streamSender, false, true, cancellationToken, true, null);

    public ValueTask<T> InvokeCancellableClientStreamWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, false, true, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public ValueTask<T> InvokeCancellableClientStreamWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, streamSender, false, true, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public ValueTask<T> InvokeCancellableClientStreamNoReturnAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, false, false, cancellationToken, false, null);

    public ValueTask<T> InvokeCancellableClientStreamNoReturnNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, streamSender, false, false, cancellationToken, false, null);

    public ValueTask<T> InvokeCancellableClientStreamNoReturnWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, false, false, cancellationToken, true, null);

    public ValueTask<T> InvokeCancellableClientStreamNoReturnWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, streamSender, false, false, cancellationToken, true, null);

    public ValueTask<T> InvokeCancellableClientStreamNoReturnWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, false, false, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public ValueTask<T> InvokeCancellableClientStreamNoReturnWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, streamSender, false, false, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public IAsyncEnumerable<T> InvokeServerStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter)
        => InvokeServerStreamCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, false, null);

    public IAsyncEnumerable<T> InvokeServerStreamNoPayloadAsync<T>(long interfaceHash, long methodHash)
        => InvokeServerStreamCoreAsync<T>(interfaceHash, methodHash, null, null, false, null);

    public IAsyncEnumerable<T> InvokeCancellableServerStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default)
        => InvokeCancellableServerStreamCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, cancellationToken, false, null);

    public IAsyncEnumerable<T> InvokeCancellableServerStreamNoPayloadAsync<T>(long interfaceHash, long methodHash, CancellationToken cancellationToken = default)
        => InvokeCancellableServerStreamCoreAsync<T>(interfaceHash, methodHash, null, null, cancellationToken, false, null);

    public IAsyncEnumerable<T> InvokeCancellableServerStreamWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default)
        => InvokeCancellableServerStreamCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, cancellationToken, true, null);

    public IAsyncEnumerable<T> InvokeCancellableServerStreamWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, CancellationToken cancellationToken = default)
        => InvokeCancellableServerStreamCoreAsync<T>(interfaceHash, methodHash, null, null, cancellationToken, true, null);

    public IAsyncEnumerable<T> InvokeCancellableServerStreamWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, TimeSpan timeout, CancellationToken cancellationToken = default)
        => InvokeCancellableServerStreamCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public IAsyncEnumerable<T> InvokeCancellableServerStreamWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, TimeSpan timeout, CancellationToken cancellationToken = default)
        => InvokeCancellableServerStreamCoreAsync<T>(interfaceHash, methodHash, null, null, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public IAsyncEnumerable<T> InvokeDuplexStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender)
        => InvokeServerStreamCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, false, null);

    public IAsyncEnumerable<T> InvokeDuplexStreamNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender)
        => InvokeServerStreamCoreAsync<T>(interfaceHash, methodHash, null, streamSender, false, null);

    public IAsyncEnumerable<T> InvokeCancellableDuplexStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableServerStreamCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, cancellationToken, false, null);

    public IAsyncEnumerable<T> InvokeCancellableDuplexStreamNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableServerStreamCoreAsync<T>(interfaceHash, methodHash, null, streamSender, cancellationToken, false, null);

    public IAsyncEnumerable<T> InvokeCancellableDuplexStreamWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableServerStreamCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, cancellationToken, true, null);

    public IAsyncEnumerable<T> InvokeCancellableDuplexStreamWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableServerStreamCoreAsync<T>(interfaceHash, methodHash, null, streamSender, cancellationToken, true, null);

    public IAsyncEnumerable<T> InvokeCancellableDuplexStreamWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout, CancellationToken cancellationToken = default)
        => InvokeCancellableServerStreamCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public IAsyncEnumerable<T> InvokeCancellableDuplexStreamWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout, CancellationToken cancellationToken = default)
        => InvokeCancellableServerStreamCoreAsync<T>(interfaceHash, methodHash, null, streamSender, cancellationToken, false, EnsurePositiveTimeout(timeout));

    private async ValueTask<T> InvokeCoreAsync<T>(
        long interfaceHash,
        long methodHash,
        Action<IBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender,
        bool isOneWay,
        bool hasReturnPayload,
        bool useDefaultTimeout,
        TimeSpan? timeoutOverride)
    {
        var applyRequestTimeout = !isOneWay || streamSender is not null;
        var hasTimeout = TryResolveRequestTimeout(
            applyRequestTimeout, includeClientDefault: true, useDefaultTimeout, timeoutOverride, out var timeout);
        var selectedSession = GetReadySession();
        DateTimeOffset? absoluteDeadline = hasTimeout ? AddTimeout(DateTimeOffset.UtcNow, timeout) : null;

        var requestId = isOneWay ? _requestManager.AllocateRequestId() : 0;
        RpcRequestOperation<T>? op = null;
        if (!isOneWay)
        {
            op = _requestManager.Rent<T>(out requestId);
        }
        try
        {
            if (!isOneWay || streamSender is not null)
                BindRequestToSession(requestId, selectedSession);
        }
        catch when (op is not null)
        {
            return await op.AsValueTask().ConfigureAwait(false);
        }

        if (!hasTimeout)
        {
            var fastFlags = isOneWay ? ProtocolV2FrameFlags.OneWay : ProtocolV2FrameFlags.None;
            if (!isOneWay && hasReturnPayload)
                fastFlags |= ProtocolV2FrameFlags.HasReturn;
            try
            {
                SendRpcCall(
                    selectedSession, interfaceHash, methodHash, requestId, fastFlags, payloadWriter, absoluteDeadline);
            }
            catch (Exception exception)
            {
                TryUnbindRequest(requestId, out _);
                if (op is null)
                    throw;
                _requestManager.DispatchError(requestId, exception);
                return await op.AsValueTask();
            }

            if (streamSender is not null)
            {
                if (isOneWay)
                {
                    try
                    {
                        await streamSender(requestId, CancellationToken.None);
                    }
                    finally
                    {
                        TryUnbindRequest(requestId, out _);
                    }
                }
                else
                    TrackBackgroundTask(RunStreamSenderAsync(streamSender, requestId, CancellationToken.None));
            }

            if (isOneWay)
            {
                TryUnbindRequest(requestId, out _);
                return default!;
            }

            return await op!.AsValueTask();
        }

        var packetFlags = isOneWay ? ProtocolV2FrameFlags.OneWay : ProtocolV2FrameFlags.None;
        if (!isOneWay && hasReturnPayload)
            packetFlags |= ProtocolV2FrameFlags.HasReturn;
        if (streamSender is not null)
            packetFlags |= ProtocolV2FrameFlags.Cancellable;

        using var timeoutRegistration = RegisterRequestTimeout(
            absoluteDeadline,
            requestId,
            isOneWay);
        try
        {
            SendRpcCall(
                selectedSession, interfaceHash, methodHash, requestId, packetFlags, payloadWriter, absoluteDeadline);
        }
        catch (Exception exception)
        {
            TryUnbindRequest(requestId, out _);
            if (op is null)
                throw;
            _requestManager.DispatchError(requestId, exception);
            return await op.AsValueTask();
        }

        if (streamSender is not null)
        {
            if (isOneWay)
            {
                try
                {
                    await streamSender(requestId, CancellationToken.None);
                }
                finally
                {
                    TryUnbindRequest(requestId, out _);
                }
            }
            else
                TrackBackgroundTask(RunStreamSenderAsync(streamSender, requestId, CancellationToken.None));
        }

        if (isOneWay)
        {
            TryUnbindRequest(requestId, out _);
            return default!;
        }

        return await op!.AsValueTask();
    }

    private async ValueTask<T> InvokeCancellableCoreAsync<T>(
        long interfaceHash,
        long methodHash,
        Action<IBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender,
        bool isOneWay,
        bool hasReturnPayload,
        CancellationToken ct,
        bool useDefaultTimeout,
        TimeSpan? timeoutOverride)
    {
        ct.ThrowIfCancellationRequested();

        var applyRequestTimeout = !isOneWay || streamSender is not null;
        var hasTimeout = TryResolveRequestTimeout(
            applyRequestTimeout, includeClientDefault: true, useDefaultTimeout, timeoutOverride, out var timeout);
        var selectedSession = GetReadySession();
        DateTimeOffset? absoluteDeadline = hasTimeout ? AddTimeout(DateTimeOffset.UtcNow, timeout) : null;

        var requestId = isOneWay ? _requestManager.AllocateRequestId() : 0;
        RpcRequestOperation<T>? op = null;
        if (!isOneWay)
        {
            op = _requestManager.Rent<T>(out requestId);
        }
        try
        {
            if (!isOneWay || streamSender is not null)
                BindRequestToSession(requestId, selectedSession);
        }
        catch when (op is not null)
        {
            return await op.AsValueTask().ConfigureAwait(false);
        }

        var packetFlags = isOneWay ? ProtocolV2FrameFlags.OneWay : ProtocolV2FrameFlags.None;
        if (!isOneWay && hasReturnPayload)
            packetFlags |= ProtocolV2FrameFlags.HasReturn;
        if (ct.CanBeCanceled || hasTimeout)
            packetFlags |= ProtocolV2FrameFlags.Cancellable;

        using var timeoutRegistration = RegisterRequestTimeout(
            absoluteDeadline,
            requestId,
            isOneWay);
        await using var cancelRegistration = RegisterCancel(
            ct,
            requestId,
            isOneWay,
            ct);
        try
        {
            SendRpcCall(
                selectedSession, interfaceHash, methodHash, requestId, packetFlags, payloadWriter, absoluteDeadline);
        }
        catch (Exception exception)
        {
            TryUnbindRequest(requestId, out _);
            if (op is null)
                throw;
            _requestManager.DispatchError(requestId, exception);
            return await op.AsValueTask();
        }

        if (streamSender is not null)
        {
            if (isOneWay)
            {
                try
                {
                    await streamSender(requestId, ct);
                }
                finally
                {
                    TryUnbindRequest(requestId, out _);
                }
            }
            else
                TrackBackgroundTask(RunStreamSenderAsync(streamSender, requestId, ct));
        }

        if (isOneWay)
        {
            TryUnbindRequest(requestId, out _);
            return default!;
        }

        return await op!.AsValueTask();
    }

    private IAsyncEnumerable<T> InvokeServerStreamCoreAsync<T>(
        long interfaceHash,
        long methodHash,
        Action<IBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender,
        bool useDefaultTimeout,
        TimeSpan? timeoutOverride)
    {
        var session = GetReadySession();
        var requestId = _requestManager.AllocateRequestId();
        _serverStreamRequestIds.Add(requestId);
        var streamDispatcher = PooledAsyncStreamDispatcher<T>.Rent(
            codecProvider: _runtimeContext.Codecs);
        session.StreamManager.Register(requestId, 0, streamDispatcher);
        BindRequestToSession(requestId, session);
        TrackBackgroundTask(StartServerStreamRequestAsync(
            session,
            interfaceHash,
            methodHash,
            requestId,
            payloadWriter,
            streamSender,
            useDefaultTimeout,
            timeoutOverride));

        return streamDispatcher;
    }

    private IAsyncEnumerable<T> InvokeCancellableServerStreamCoreAsync<T>(
        long interfaceHash,
        long methodHash,
        Action<IBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender,
        CancellationToken cancellationToken,
        bool useDefaultTimeout,
        TimeSpan? timeoutOverride)
    {
        if (!cancellationToken.CanBeCanceled)
            return InvokeServerStreamCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, useDefaultTimeout, timeoutOverride);

        var session = GetReadySession();
        var requestId = _requestManager.AllocateRequestId();
        _serverStreamRequestIds.Add(requestId);
        var streamDispatcher = PooledAsyncStreamDispatcher<T>.Rent(
            cancellationToken,
            _runtimeContext.Codecs);
        session.StreamManager.Register(requestId, 0, streamDispatcher);
        BindRequestToSession(requestId, session);
        TrackBackgroundTask(StartCancellableServerStreamRequestAsync(
            session,
            interfaceHash,
            methodHash,
            requestId,
            payloadWriter,
            streamSender,
            cancellationToken,
            useDefaultTimeout,
            timeoutOverride));

        return streamDispatcher;
    }

    private async Task StartServerStreamRequestAsync(
        RpcSession session,
        long interfaceHash,
        long methodHash,
        long requestId,
        Action<IBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender,
        bool useDefaultTimeout,
        TimeSpan? timeoutOverride)
    {
        var hasTimeout = TryResolveRequestTimeout(
            shouldApply: true, includeClientDefault: false, useDefaultTimeout, timeoutOverride, out var timeout);
        DateTimeOffset? absoluteDeadline = hasTimeout ? AddTimeout(DateTimeOffset.UtcNow, timeout) : null;
        try
        {
            if (!hasTimeout)
            {
                SendRpcCall(session, interfaceHash, methodHash, requestId, ProtocolV2FrameFlags.None, payloadWriter);
                if (streamSender is not null)
                {
                    await streamSender(requestId, CancellationToken.None);
                }
                return;
            }

            var timeoutRegistration = RegisterStreamTimeout(absoluteDeadline, requestId);
            var lifetime = new StreamCallLifetime(timeoutRegistration, default);
            if (!_streamCallLifetimes.TryAdd(requestId, lifetime))
            {
                lifetime.Dispose();
                throw new InvalidOperationException("A stream lifetime is already registered for this request.");
            }
            const ProtocolV2FrameFlags packetFlags = ProtocolV2FrameFlags.Cancellable;
            SendRpcCall(
                session, interfaceHash, methodHash, requestId, packetFlags, payloadWriter, absoluteDeadline);

            if (streamSender is not null)
            {
                await streamSender(requestId, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _serverStreamRequestIds.Remove(requestId);
            TryUnbindRequest(requestId, out _);
            CompleteStreamLifetime(requestId);
            session.StreamManager.CompleteStream(requestId, ex);
        }
    }

    private async Task StartCancellableServerStreamRequestAsync(
        RpcSession session,
        long interfaceHash,
        long methodHash,
        long requestId,
        Action<IBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender,
        CancellationToken cancellationToken,
        bool useDefaultTimeout,
        TimeSpan? timeoutOverride)
    {
        var hasTimeout = TryResolveRequestTimeout(
            shouldApply: true, includeClientDefault: false, useDefaultTimeout, timeoutOverride, out var timeout);
        DateTimeOffset? absoluteDeadline = hasTimeout ? AddTimeout(DateTimeOffset.UtcNow, timeout) : null;

        try
        {
            var timeoutRegistration = RegisterStreamTimeout(absoluteDeadline, requestId);
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
            var packetFlags = (cancellationToken.CanBeCanceled || hasTimeout)
                ? ProtocolV2FrameFlags.Cancellable
                : ProtocolV2FrameFlags.None;
            SendRpcCall(
                session, interfaceHash, methodHash, requestId, packetFlags, payloadWriter, absoluteDeadline);

            if (streamSender is not null)
            {
                await streamSender(requestId, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _serverStreamRequestIds.Remove(requestId);
            TryUnbindRequest(requestId, out _);
            CompleteStreamLifetime(requestId);
            session.StreamManager.CompleteStream(requestId, ex);
        }
    }

    private PooledCancellationRegistration RegisterCancel(
        CancellationToken ct,
        long requestId,
        bool isOneWay,
        CancellationToken userToken)
    {
        if (!ct.CanBeCanceled)
            return default;

        var state = RequestCancelState.Rent(this, requestId, isOneWay, userToken);
        var registration = ct.UnsafeRegister(SRequestCancelCallback, state);
        return new PooledCancellationRegistration(registration, state);
    }

    private PooledCancellationRegistration RegisterStreamCancel(
        CancellationToken ct,
        long requestId,
        CancellationToken userToken)
    {
        if (!ct.CanBeCanceled)
            return default;

        var state = StreamCancelState.Rent(this, requestId, userToken);
        var registration = ct.UnsafeRegister(SStreamCancelCallback, state);
        return new PooledCancellationRegistration(registration, state);
    }

    private static OperationCanceledException CreateCancellationException(CancellationToken userToken)
    {
        return userToken.CanBeCanceled
            ? new OperationCanceledException(userToken)
            : new OperationCanceledException();
    }

    private bool TryResolveRequestTimeout(
        bool shouldApply,
        bool includeClientDefault,
        bool useDefaultTimeout,
        TimeSpan? timeoutOverride,
        out TimeSpan timeout)
    {
        timeout = TimeSpan.Zero;
        if (!shouldApply)
            return false;

        var hasTimeout = false;
        if (timeoutOverride is { } overrideTimeout)
        {
            timeout = overrideTimeout;
            hasTimeout = true;
        }

        if ((includeClientDefault || useDefaultTimeout) && _hasRequestTimeout &&
            (!hasTimeout || _requestTimeoutValue < timeout))
        {
            timeout = _requestTimeoutValue;
            hasTimeout = true;
        }
        return hasTimeout;

    }

    private static TimeSpan EnsurePositiveTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        return timeout;
    }

    private void SendRpcCall(
        RpcSession session,
        long interfaceHash,
        long methodHash,
        long requestId,
        ProtocolV2FrameFlags flags,
        Action<IBufferWriter<byte>>? payloadWriter,
        DateTimeOffset? deadline = null,
        SharpLinkMetadata? metadata = null)
    {
        var hasMetadata = metadata is { Count: > 0 };
        var metadataLength = 0;
        if (deadline is not null)
            flags |= ProtocolV2FrameFlags.HasDeadline;
        if (hasMetadata)
        {
            if ((session.NegotiatedCapabilities & ProtocolV2Capabilities.Metadata) == 0)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.Unimplemented,
                    "The connected server did not negotiate request metadata support.");
            }
            metadataLength = ProtocolV2PayloadCodec.GetMetadataPayloadLength(metadata!);
            if (metadataLength > _protocolOptions.MaxMetadataBytes)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ResourceExhausted,
                    $"Request metadata exceeds {_protocolOptions.MaxMetadataBytes} bytes.");
            }
            flags |= ProtocolV2FrameFlags.HasMetadata;
        }

        var writer = _runtimeContext.Buffers.Rent();
        var ownsWriter = true;
        try
        {
            using (writer.BeginPacketScope(
                       ProtocolV2FrameType.Request, flags, unchecked((ulong)requestId)))
            {
                var span = writer.GetSpan(ProtocolV2Constants.RequestPrefixBytes);
                BinaryPrimitives.WriteInt64LittleEndian(span, interfaceHash);
                BinaryPrimitives.WriteInt64LittleEndian(span[8..], methodHash);
                writer.Advance(ProtocolV2Constants.RequestPrefixBytes);
                if (deadline is { } absoluteDeadline)
                {
                    var deadlineSpan = writer.GetSpan(sizeof(long));
                    BinaryPrimitives.WriteInt64LittleEndian(
                        deadlineSpan,
                        absoluteDeadline.ToUnixTimeMilliseconds());
                    writer.Advance(sizeof(long));
                }
                if (hasMetadata)
                {
                    ProtocolV2PayloadCodec.WriteVarUInt32(writer, checked((uint)metadataLength));
                    ProtocolV2PayloadCodec.WriteMetadata(writer, metadata!);
                }
                payloadWriter?.Invoke(writer);
            }

            // SendPacket takes ownership even when enqueueing detects a terminal session.
            ownsWriter = false;
            session.SendPacket(writer);
        }
        finally
        {
            if (ownsWriter)
                _runtimeContext.Buffers.Return(writer);
        }
    }

    public async Task SendClientStreamAsync<T>(long requestId, ushort streamId, IAsyncEnumerable<T> stream, CancellationToken cancellationToken = default)
    {
        if (!_requestSessions.TryGetValue(requestId, out var session))
            session = GetReadySession();
        try
        {
            await foreach (var item in stream.WithCancellation(cancellationToken))
            {
                await session.SendStreamChunkAsync(
                    requestId,
                    streamId,
                    item,
                    cancellationToken).ConfigureAwait(false);
            }

            session.SendStreamCompleteAsync(requestId, streamId);
        }
        catch (Exception ex)
        {
            try
            {
                session.SendStreamErrorAsync(requestId, streamId, ex);
            }
            catch (SharpLinkException sendException) when (sendException.Code == SharpLinkErrorCode.ConnectionClosed)
            {
            }
            throw;
        }
    }

    private async Task RunStreamSenderAsync(Func<long, CancellationToken, Task> streamSender, long requestId, CancellationToken ct)
    {
        try
        {
            await streamSender(requestId, ct);
        }
        catch (Exception ex)
        {
            TryUnbindRequest(requestId, out _);
            _requestManager.DispatchError(requestId, ex);
        }
    }

    private static ValueTask DispatchStreamChunkAsync(IRpcSession session, long requestId, ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out short streamIdBits))
            throw CreateProtocolViolationException("StreamData stream ID is truncated.");
        var streamId = unchecked((ushort)streamIdBits);
        var streamPayload = payload.Slice(sizeof(ushort));
        return session.StreamManager.DispatchChunkAsync(requestId, streamId, streamPayload);
    }

    private void DispatchStreamComplete(
        IRpcSession session,
        long requestId,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        SharpLinkProtocolOptions limits)
    {
        var streamId = TryReadStreamId(ref payload);
        if ((flags & ProtocolV2FrameFlags.Error) == 0)
        {
            session.StreamManager.CompleteStream(requestId, streamId, exception: null);
            if (streamId == 0)
            {
                _serverStreamRequestIds.Remove(requestId);
                TryUnbindRequest(requestId, out _);
                CompleteStreamLifetime(requestId);
            }
            return;
        }
        var error = ProtocolV2PayloadCodec.ReadError(payload, flags, limits.MaxErrorMessageBytes);
        session.StreamManager.CompleteStream(
            requestId,
            streamId,
            new SharpLinkException(error.Code, error.Message));
        if (streamId == 0)
        {
            _serverStreamRequestIds.Remove(requestId);
            TryUnbindRequest(requestId, out _);
            CompleteStreamLifetime(requestId);
        }
    }

    private static ushort TryReadStreamId(ref ReadOnlySequence<byte> payload)
    {
        var firstSpan = payload.FirstSpan;
        ushort streamId;
        if (firstSpan.Length >= sizeof(ushort))
        {
            streamId = BinaryPrimitives.ReadUInt16LittleEndian(firstSpan);
        }
        else
        {
            var reader = new SequenceReader<byte>(payload);
            if (!reader.TryReadLittleEndian(out short streamIdBits))
                throw CreateProtocolViolationException("StreamComplete stream ID is truncated.");
            streamId = unchecked((ushort)streamIdBits);
        }

        payload = payload.Slice(sizeof(ushort));
        return streamId;
    }

    private void HandleDisconnected(RpcSession session, Exception ex)
    {
        if (!RemoveReadySession(session, out var sessionCts))
            return;

        using var sessionScope = BeginSessionLogScope(_logger, session.Id);
        LogClientDisconnectedWithError(_logger, ex);

        if (sessionCts is not null)
        {
            sessionCts.Cancel();
            sessionCts.Dispose();
        }
        FailRequestsForSession(session, ex);
        session.StreamManager.CompleteAll(ex);
        TrackBackgroundTask(DisposeDisconnectedSessionAsync(session));

        if (_shutdownCts.IsCancellationRequested ||
            State is SharpLinkConnectionState.Stopped)
            return;

        if (ReadyConnectionCount != 0)
        {
            TransitionTo(SharpLinkConnectionState.Ready);
            EnsureReconnectLoop();
            return;
        }

        ResetReadySignal();
        var stableTicks = Stopwatch.GetTimestamp() - Volatile.Read(ref _readyTimestamp);
        if (stableTicks >= 30L * Stopwatch.Frequency)
            Volatile.Write(ref _reconnectDelayMilliseconds, 100);
        TransitionTo(SharpLinkConnectionState.Reconnecting);
        EnsureReconnectLoop();
    }

    private void EnsureReconnectLoop()
    {
        lock (_stateGate)
        {
            if (_shutdownCts.IsCancellationRequested || _reconnectTask is { IsCompleted: false })
                return;
            _reconnectTask = ReconnectLoopAsync();
        }
    }

    private async Task ReconnectLoopAsync()
    {
        while (!_shutdownCts.IsCancellationRequested)
        {
            if (ReadyConnectionCount >= _connectionPoolOptions.MinConnections)
                return;
            var baseDelay = Volatile.Read(ref _reconnectDelayMilliseconds);
            var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
            var delay = TimeSpan.FromMilliseconds(baseDelay * jitter);
            try
            {
                await Task.Delay(delay, _shutdownCts.Token).ConfigureAwait(false);
                await ConnectOneAsync(_shutdownCts.Token).ConfigureAwait(false);
                PublishReadyState();
                if (ReadyConnectionCount >= _connectionPoolOptions.MinConnections)
                    return;
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                using var scope = BeginSessionLogScope(_logger, "reconnect");
                LogClientBackgroundLoopUnhandledException(_logger, nameof(ReconnectLoopAsync), ex);
                var nextDelay = Math.Min(baseDelay * 2, 5000);
                Volatile.Write(ref _reconnectDelayMilliseconds, nextDelay);
                TransitionTo(SharpLinkConnectionState.Reconnecting);
            }
        }
    }

    private RpcSession GetReadySession()
    {
        var sessions = Volatile.Read(ref _readySessions);
        if (State == SharpLinkConnectionState.Ready && sessions.Length != 0)
        {
            RpcSession selected;
            if (sessions.Length == 1)
            {
                selected = sessions[0];
            }
            else
            {
                var first = Random.Shared.Next(sessions.Length);
                var second = Random.Shared.Next(sessions.Length - 1);
                if (second >= first)
                    second++;
                selected = SelectLeastLoaded(sessions, first, second);
            }

            if (selected.CanAcceptCalls)
            {
                if (selected.ActiveRequestCount != 0)
                    EnsureExpansion();
                return selected;
            }
        }
        if (State is SharpLinkConnectionState.Draining or SharpLinkConnectionState.Stopped)
            throw CreateConnectionClosedException("Client is not accepting new calls.");
        throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "No SharpLink connection is ready.");
    }

    internal static RpcSession SelectLeastLoaded(RpcSession[] sessions, int first, int second)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfNegative(second);
        if ((uint)first >= (uint)sessions.Length || (uint)second >= (uint)sessions.Length)
            throw new ArgumentOutOfRangeException(nameof(first));
        var firstSession = sessions[first];
        var secondSession = sessions[second];
        return firstSession.ActiveRequestCount <= secondSession.ActiveRequestCount
            ? firstSession
            : secondSession;
    }

    private bool RemoveReadySession(
        RpcSession session,
        out CancellationTokenSource? sessionCancellation)
    {
        lock (_poolGate)
        {
            if (!_sessionCancellations.Remove(session, out sessionCancellation))
                return false;
            PublishReadySnapshotLocked();
            return true;
        }
    }

    private void MarkSessionDraining(RpcSession session)
    {
        session.MarkDraining();
        lock (_poolGate)
            PublishReadySnapshotLocked();

        RetireDrainingSessionIfIdle(session);

        if (ReadyConnectionCount != 0)
        {
            EnsureReconnectLoop();
            return;
        }
        ResetReadySignal();
        TransitionTo(SharpLinkConnectionState.Draining);
        EnsureReconnectLoop();
    }

    private void RetireDrainingSessionIfIdle(RpcSession session)
    {
        if (!session.IsDraining || session.ActiveRequestCount != 0 ||
            !RemoveReadySession(session, out var sessionCancellation))
        {
            return;
        }

        sessionCancellation?.Cancel();
        sessionCancellation?.Dispose();
        TrackBackgroundTask(DisposeDisconnectedSessionAsync(session));
    }

    private void EnsureExpansion()
    {
        lock (_stateGate)
        {
            if (_shutdownCts.IsCancellationRequested ||
                ReadyConnectionCount >= _connectionPoolOptions.MaxConnections ||
                _expansionTask is { IsCompleted: false })
            {
                return;
            }
            _expansionTask = ExpandOneAsync();
        }
    }

    private async Task ExpandOneAsync()
    {
        try
        {
            if (ReadyConnectionCount >= _connectionPoolOptions.MaxConnections)
                return;
            await ConnectOneAsync(_shutdownCts.Token).ConfigureAwait(false);
            PublishReadyState();
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            using var scope = BeginSessionLogScope(_logger, "pool-expand");
            LogClientBackgroundLoopUnhandledException(_logger, nameof(ExpandOneAsync), ex);
        }
    }

    private void FailRequestsForSession(RpcSession session, Exception exception)
    {
        foreach (var pair in _requestSessions)
        {
            if (!ReferenceEquals(pair.Value, session) || !TryUnbindRequest(pair.Key, out _))
                continue;
            _requestManager.DispatchError(pair.Key, exception);
            _serverStreamRequestIds.Remove(pair.Key);
            _locallyCanceledRequestIds.Remove(pair.Key);
            CompleteStreamLifetime(pair.Key);
        }
    }

    private void OnRequestCancel(RequestCancelState state)
    {
        if (!state.IsOneWay && !_locallyCanceledRequestIds.Add(state.RequestId))
            return;

        TrySendCancel(state.RequestId);

        if (state.IsOneWay) return;
        var ex = CreateCancellationException(state.UserToken);
        _requestManager.DispatchError(state.RequestId, ex);
        TryUnbindRequest(state.RequestId, out _);
    }

    private void OnStreamCancel(StreamCancelState state)
    {
        if (!_locallyCanceledRequestIds.Add(state.RequestId))
            return;

        TrySendCancel(state.RequestId);
        var ex = CreateCancellationException(state.UserToken);
        if (_requestSessions.TryGetValue(state.RequestId, out var session))
            session.StreamManager.CompleteStream(state.RequestId, ex);
        _serverStreamRequestIds.Remove(state.RequestId);
        TryUnbindRequest(state.RequestId, out _);
    }

    private void OnRequestTimeout(RequestTimeoutState state)
    {
        if (!state.IsOneWay && !_locallyCanceledRequestIds.Add(state.RequestId))
            return;

        TrySendCancel(state.RequestId);
        if (!state.IsOneWay)
        {
            _requestManager.DispatchError(state.RequestId, new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "Request deadline exceeded."));
            TryUnbindRequest(state.RequestId, out _);
        }
    }

    private void OnStreamTimeout(StreamTimeoutState state)
    {
        if (!_locallyCanceledRequestIds.Add(state.RequestId))
            return;

        TrySendCancel(state.RequestId);
        if (_requestSessions.TryGetValue(state.RequestId, out var session))
        {
            session.StreamManager.CompleteStream(state.RequestId, new SharpLinkException(
                SharpLinkErrorCode.DeadlineExceeded,
                "Request deadline exceeded."));
        }
        _serverStreamRequestIds.Remove(state.RequestId);
        TryUnbindRequest(state.RequestId, out _);
    }

    private void TrySendCancel(long requestId)
    {
        try
        {
            if (_requestSessions.TryGetValue(requestId, out var requestSession))
                requestSession.SendCancelAsync(requestId);
            else
                _session?.SendCancelAsync(requestId);
        }
        catch (SharpLinkException ex) when (ex.Code is
            SharpLinkErrorCode.ConnectionClosed or
            SharpLinkErrorCode.ResourceExhausted or
            SharpLinkErrorCode.Unavailable)
        {
        }
    }

    private static async Task DisposeDisconnectedSessionAsync(IRpcSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or SocketException)
        {
        }
    }

    private TimeoutRegistration RegisterRequestTimeout(DateTimeOffset? deadline, long requestId, bool isOneWay)
    {
        if (deadline is not { } absoluteDeadline)
            return default;

        var state = RequestTimeoutState.Rent(this, requestId, isOneWay);
        return _requestTimeoutScheduler.Schedule(requestId, absoluteDeadline, SRequestTimeoutCallback, state);
    }

    private TimeoutRegistration RegisterStreamTimeout(DateTimeOffset? deadline, long requestId)
    {
        if (deadline is not { } absoluteDeadline)
            return default;

        var state = StreamTimeoutState.Rent(this, requestId);
        return _requestTimeoutScheduler.Schedule(requestId, absoluteDeadline, SStreamTimeoutCallback, state);
    }

}
