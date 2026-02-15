


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
        => InvokeCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, false, false, null);

    public ValueTask<T> InvokeNoPayloadAsync<T>(long interfaceHash, long methodHash)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, null, null, false, false, null);

    public ValueTask<T> InvokeWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, false, true, null);

    public ValueTask<T> InvokeWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, null, null, false, true, null);

    public ValueTask<T> InvokeWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, TimeSpan timeout)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, false, false, EnsurePositiveTimeout(timeout));

    public ValueTask<T> InvokeWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, TimeSpan timeout)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, null, null, false, false, EnsurePositiveTimeout(timeout));

    public ValueTask<T> InvokeCancellableAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, false, cancellationToken, false, null);

    public ValueTask<T> InvokeCancellableNoPayloadAsync<T>(long interfaceHash, long methodHash, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, null, false, cancellationToken, false, null);

    public ValueTask<T> InvokeCancellableWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, false, cancellationToken, true, null);

    public ValueTask<T> InvokeCancellableWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, null, false, cancellationToken, true, null);

    public ValueTask<T> InvokeCancellableWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, TimeSpan timeout, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, false, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public ValueTask<T> InvokeCancellableWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, TimeSpan timeout, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, null, false, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public async ValueTask InvokeOneWayAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, null, true, false, null);

    public async ValueTask InvokeOneWayNoPayloadAsync(long interfaceHash, long methodHash)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, null, null, true, false, null);

    public async ValueTask InvokeOneWayWithDefaultTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, null, true, true, null);

    public async ValueTask InvokeOneWayWithDefaultTimeoutNoPayloadAsync(long interfaceHash, long methodHash)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, null, null, true, true, null);

    public async ValueTask InvokeOneWayWithTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, TimeSpan timeout)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, null, true, false, EnsurePositiveTimeout(timeout));

    public async ValueTask InvokeOneWayWithTimeoutNoPayloadAsync(long interfaceHash, long methodHash, TimeSpan timeout)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, null, null, true, false, EnsurePositiveTimeout(timeout));

    public async ValueTask InvokeOneWayClientStreamAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, streamSender, true, false, null);

    public async ValueTask InvokeOneWayClientStreamNoPayloadAsync(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, null, streamSender, true, false, null);

    public async ValueTask InvokeOneWayClientStreamWithDefaultTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, streamSender, true, true, null);

    public async ValueTask InvokeOneWayClientStreamWithDefaultTimeoutNoPayloadAsync(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, null, streamSender, true, true, null);

    public async ValueTask InvokeOneWayClientStreamWithTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, streamSender, true, false, EnsurePositiveTimeout(timeout));

    public async ValueTask InvokeOneWayClientStreamWithTimeoutNoPayloadAsync(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout)
        => await InvokeCoreAsync<byte>(interfaceHash, methodHash, null, streamSender, true, false, EnsurePositiveTimeout(timeout));

    public async ValueTask InvokeCancellableOneWayAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, null, true, cancellationToken, false, null);

    public async ValueTask InvokeCancellableOneWayNoPayloadAsync(long interfaceHash, long methodHash, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, null, null, true, cancellationToken, false, null);

    public async ValueTask InvokeCancellableOneWayWithDefaultTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, null, true, cancellationToken, true, null);

    public async ValueTask InvokeCancellableOneWayWithDefaultTimeoutNoPayloadAsync(long interfaceHash, long methodHash, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, null, null, true, cancellationToken, true, null);

    public async ValueTask InvokeCancellableOneWayWithTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, TimeSpan timeout, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, null, true, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public async ValueTask InvokeCancellableOneWayWithTimeoutNoPayloadAsync(long interfaceHash, long methodHash, TimeSpan timeout, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, null, null, true, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public async ValueTask InvokeCancellableOneWayClientStreamAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, streamSender, true, cancellationToken, false, null);

    public async ValueTask InvokeCancellableOneWayClientStreamNoPayloadAsync(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, null, streamSender, true, cancellationToken, false, null);

    public async ValueTask InvokeCancellableOneWayClientStreamWithDefaultTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, streamSender, true, cancellationToken, true, null);

    public async ValueTask InvokeCancellableOneWayClientStreamWithDefaultTimeoutNoPayloadAsync(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, null, streamSender, true, cancellationToken, true, null);

    public async ValueTask InvokeCancellableOneWayClientStreamWithTimeoutAsync(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, payloadWriter, streamSender, true, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public async ValueTask InvokeCancellableOneWayClientStreamWithTimeoutNoPayloadAsync(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout, CancellationToken cancellationToken = default)
        => await InvokeCancellableCoreAsync<byte>(interfaceHash, methodHash, null, streamSender, true, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public ValueTask<T> InvokeClientStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, false, false, null);

    public ValueTask<T> InvokeClientStreamNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, null, streamSender, false, false, null);

    public ValueTask<T> InvokeClientStreamWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, false, true, null);

    public ValueTask<T> InvokeClientStreamWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, null, streamSender, false, true, null);

    public ValueTask<T> InvokeClientStreamWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, false, false, EnsurePositiveTimeout(timeout));

    public ValueTask<T> InvokeClientStreamWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout)
        => InvokeCoreAsync<T>(interfaceHash, methodHash, null, streamSender, false, false, EnsurePositiveTimeout(timeout));

    public ValueTask<T> InvokeCancellableClientStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, false, cancellationToken, false, null);

    public ValueTask<T> InvokeCancellableClientStreamNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, streamSender, false, cancellationToken, false, null);

    public ValueTask<T> InvokeCancellableClientStreamWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, false, cancellationToken, true, null);

    public ValueTask<T> InvokeCancellableClientStreamWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, streamSender, false, cancellationToken, true, null);

    public ValueTask<T> InvokeCancellableClientStreamWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, false, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public ValueTask<T> InvokeCancellableClientStreamWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout, CancellationToken cancellationToken = default)
        => InvokeCancellableCoreAsync<T>(interfaceHash, methodHash, null, streamSender, false, cancellationToken, false, EnsurePositiveTimeout(timeout));

    public IAsyncEnumerable<T> InvokeServerStreamAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter)
        => InvokeServerStreamCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, false, null);

    public IAsyncEnumerable<T> InvokeServerStreamNoPayloadAsync<T>(long interfaceHash, long methodHash)
        => InvokeServerStreamCoreAsync<T>(interfaceHash, methodHash, null, null, false, null);

    public IAsyncEnumerable<T> InvokeServerStreamWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter)
        => InvokeServerStreamCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, true, null);

    public IAsyncEnumerable<T> InvokeServerStreamWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash)
        => InvokeServerStreamCoreAsync<T>(interfaceHash, methodHash, null, null, true, null);

    public IAsyncEnumerable<T> InvokeServerStreamWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, TimeSpan timeout)
        => InvokeServerStreamCoreAsync<T>(interfaceHash, methodHash, payloadWriter, null, false, EnsurePositiveTimeout(timeout));

    public IAsyncEnumerable<T> InvokeServerStreamWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, TimeSpan timeout)
        => InvokeServerStreamCoreAsync<T>(interfaceHash, methodHash, null, null, false, EnsurePositiveTimeout(timeout));

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

    public IAsyncEnumerable<T> InvokeDuplexStreamWithDefaultTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender)
        => InvokeServerStreamCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, true, null);

    public IAsyncEnumerable<T> InvokeDuplexStreamWithDefaultTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender)
        => InvokeServerStreamCoreAsync<T>(interfaceHash, methodHash, null, streamSender, true, null);

    public IAsyncEnumerable<T> InvokeDuplexStreamWithTimeoutAsync<T>(long interfaceHash, long methodHash, Action<IBufferWriter<byte>> payloadWriter, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout)
        => InvokeServerStreamCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, false, EnsurePositiveTimeout(timeout));

    public IAsyncEnumerable<T> InvokeDuplexStreamWithTimeoutNoPayloadAsync<T>(long interfaceHash, long methodHash, Func<long, CancellationToken, Task> streamSender, TimeSpan timeout)
        => InvokeServerStreamCoreAsync<T>(interfaceHash, methodHash, null, streamSender, false, EnsurePositiveTimeout(timeout));

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
        bool useDefaultTimeout,
        TimeSpan? timeoutOverride)
    {
        var applyRequestTimeout = !isOneWay || streamSender is not null;
        var hasTimeout = TryResolveRequestTimeout(applyRequestTimeout, useDefaultTimeout, timeoutOverride, out var timeout);

        var requestId = isOneWay ? _requestManager.AllocateRequestId() : 0;
        RpcRequestOperation<T>? op = null;
        if (!isOneWay)
        {
            op = _requestManager.Rent<T>(out requestId);
        }

        if (!hasTimeout)
        {
            var fastFlags = isOneWay ? PacketFlags.IsOneWay : PacketFlags.None;
            SendRpcCall(interfaceHash, methodHash, requestId, fastFlags, payloadWriter);

            if (streamSender is not null)
                _ = RunStreamSenderAsync(streamSender, requestId, CancellationToken.None);

            if (isOneWay)
                return default!;

            return await op!.AsValueTask();
        }

        var packetFlags = isOneWay ? PacketFlags.IsOneWay : PacketFlags.IsCancellable;

        using var timeoutRegistration = RegisterRequestTimeout(
            hasTimeout,
            timeout,
            requestId,
            isOneWay);
        SendRpcCall(interfaceHash, methodHash, requestId, packetFlags, payloadWriter);

        if (streamSender is not null)
            _ = RunStreamSenderAsync(streamSender, requestId, CancellationToken.None);

        if (isOneWay)
            return default!;

        return await op!.AsValueTask();
    }

    private async ValueTask<T> InvokeCancellableCoreAsync<T>(
        long interfaceHash,
        long methodHash,
        Action<IBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender,
        bool isOneWay,
        CancellationToken ct,
        bool useDefaultTimeout,
        TimeSpan? timeoutOverride)
    {
        if (!ct.CanBeCanceled)
            return await InvokeCoreAsync<T>(interfaceHash, methodHash, payloadWriter, streamSender, isOneWay, useDefaultTimeout, timeoutOverride);

        var applyRequestTimeout = !isOneWay || streamSender is not null;
        var hasTimeout = TryResolveRequestTimeout(applyRequestTimeout, useDefaultTimeout, timeoutOverride, out var timeout);

        var requestId = isOneWay ? _requestManager.AllocateRequestId() : 0;
        RpcRequestOperation<T>? op = null;
        if (!isOneWay)
        {
            op = _requestManager.Rent<T>(out requestId);
        }

        var packetFlags = isOneWay ? PacketFlags.IsOneWay : PacketFlags.None;
        if (ct.CanBeCanceled || hasTimeout)
            packetFlags |= PacketFlags.IsCancellable;

        using var timeoutRegistration = RegisterRequestTimeout(
            hasTimeout,
            timeout,
            requestId,
            isOneWay);
        await using var cancelRegistration = RegisterCancel(
            ct,
            requestId,
            isOneWay,
            ct);
        SendRpcCall(interfaceHash, methodHash, requestId, packetFlags, payloadWriter);

        if (streamSender is not null)
            _ = RunStreamSenderAsync(streamSender, requestId, ct);

        if (isOneWay)
            return default!;

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
        var requestId = _requestManager.AllocateRequestId();
        _serverStreamRequestIds.Add(requestId);
        var channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        _session!.StreamManager.Register(requestId, 0, new TypedStreamDispatcher<T>(channel.Writer, serializer));
        _ = StartServerStreamRequestAsync(
            interfaceHash,
            methodHash,
            requestId,
            payloadWriter,
            streamSender,
            useDefaultTimeout,
            timeoutOverride);

        return channel.Reader.ReadAllAsync();
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

        var requestId = _requestManager.AllocateRequestId();
        _serverStreamRequestIds.Add(requestId);
        var channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        _session!.StreamManager.Register(requestId, 0, new TypedStreamDispatcher<T>(channel.Writer, serializer));
        _ = StartCancellableServerStreamRequestAsync(
            interfaceHash,
            methodHash,
            requestId,
            payloadWriter,
            streamSender,
            cancellationToken,
            useDefaultTimeout,
            timeoutOverride);

        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    private async Task StartServerStreamRequestAsync(
        long interfaceHash,
        long methodHash,
        long requestId,
        Action<IBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender,
        bool useDefaultTimeout,
        TimeSpan? timeoutOverride)
    {
        var hasTimeout = TryResolveRequestTimeout(true, useDefaultTimeout, timeoutOverride, out var timeout);
        if (!hasTimeout)
        {
            SendRpcCall(interfaceHash, methodHash, requestId, PacketFlags.None, payloadWriter);
            if (streamSender is not null)
                await streamSender(requestId, CancellationToken.None);
            return;
        }

        using var timeoutRegistration = RegisterStreamTimeout(hasTimeout, timeout, requestId);
        try
        {
            var packetFlags = PacketFlags.IsCancellable;
            SendRpcCall(interfaceHash, methodHash, requestId, packetFlags, payloadWriter);

            if (streamSender is not null)
                await streamSender(requestId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _serverStreamRequestIds.Remove(requestId);
            _session!.StreamManager.CompleteStream(requestId, 0, true, ex.Message);
        }
    }

    private async Task StartCancellableServerStreamRequestAsync(
        long interfaceHash,
        long methodHash,
        long requestId,
        Action<IBufferWriter<byte>>? payloadWriter,
        Func<long, CancellationToken, Task>? streamSender,
        CancellationToken cancellationToken,
        bool useDefaultTimeout,
        TimeSpan? timeoutOverride)
    {
        var hasTimeout = TryResolveRequestTimeout(true, useDefaultTimeout, timeoutOverride, out var timeout);

        using var timeoutRegistration = RegisterStreamTimeout(hasTimeout, timeout, requestId);
        try
        {
            await using var cancelRegistration = RegisterStreamCancel(
                cancellationToken,
                requestId,
                cancellationToken);
            var packetFlags = (cancellationToken.CanBeCanceled || hasTimeout)
                ? PacketFlags.IsCancellable
                : PacketFlags.None;
            SendRpcCall(interfaceHash, methodHash, requestId, packetFlags, payloadWriter);

            if (streamSender is not null)
                await streamSender(requestId, cancellationToken);
        }
        catch (Exception ex)
        {
            _serverStreamRequestIds.Remove(requestId);
            _session!.StreamManager.CompleteStream(requestId, 0, true, ex.Message);
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

    private bool TryResolveRequestTimeout(bool shouldApply, bool useDefaultTimeout, TimeSpan? timeoutOverride, out TimeSpan timeout)
    {
        timeout = TimeSpan.Zero;
        if (!shouldApply)
            return false;

        if (timeoutOverride is { } overrideTimeout)
        {
            timeout = overrideTimeout;
            return true;
        }

        if (!useDefaultTimeout || !_hasRequestTimeout) return false;
        timeout = _requestTimeoutValue;
        return true;

    }

    private static TimeSpan EnsurePositiveTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        return timeout;
    }

    private void SendRpcCall(long interfaceHash,
        long methodHash,
        long requestId,
        PacketFlags flags,
        Action<IBufferWriter<byte>>? payloadWriter)
    {
        var writer = BufferWriterPool.Get();
        using (writer.BeginPacketScope(PacketType.RpcCall, flags, requestId))
        {
            var span = writer.GetSpan(ProtocolConstants.RequestHeaderLength);
            BinaryPrimitives.WriteInt64LittleEndian(span, interfaceHash);
            BinaryPrimitives.WriteInt64LittleEndian(span[8..], methodHash);
            writer.Advance(ProtocolConstants.RequestHeaderLength);
            payloadWriter?.Invoke(writer);
        }

        _session!.SendPacket(writer);
    }

    public async Task SendClientStreamAsync<T>(long requestId, sbyte streamId, IAsyncEnumerable<T> stream, CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var item in stream.WithCancellation(cancellationToken))
            {
                _session!.SendStreamChunkAsync(requestId, streamId, item);
            }

            _session!.SendStreamCompleteAsync(requestId, streamId);
        }
        catch (Exception ex)
        {
            _session!.SendStreamErrorAsync(requestId, streamId, ex.Message);
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
            _requestManager.DispatchError(requestId, ex);
        }
    }

    private static ValueTask DispatchStreamChunkAsync(IRpcSession session, long requestId, ReadOnlySequence<byte> payload)
    {
        if(payload.IsEmpty)
            return session.StreamManager.DispatchChunkAsync(requestId, payload);
        
        var reader = new SequenceReader<byte>(payload);
        
        if (!reader.TryRead(out var streamIdRaw))
            return session.StreamManager.DispatchChunkAsync(requestId, payload);
        
        var streamId = unchecked((sbyte)streamIdRaw);
        var streamPayload = payload.Slice(sizeof(sbyte));
        return session.StreamManager.DispatchChunkAsync(requestId, streamId, streamPayload);
    }

    private static void DispatchStreamComplete(IRpcSession session, long requestId, ReadOnlySequence<byte> payload)
    {
        var streamId = TryReadStreamId(ref payload);
        session.StreamManager.CompleteStream(requestId, streamId, false, null);
    }

    private static void DispatchStreamError(IRpcSession session, long requestId, ReadOnlySequence<byte> payload)
    {
        var streamId = TryReadStreamId(ref payload);
        var message = payload.Length > 0 ? Encoding.UTF8.GetString(payload) : "Remote Error";
        session.StreamManager.CompleteStream(requestId, streamId, true, message);
    }

    private static sbyte TryReadStreamId(ref ReadOnlySequence<byte> payload)
    {
        var firstSpan = payload.FirstSpan;
        sbyte streamId;
        if (firstSpan.Length > 0)
        {
            streamId = unchecked((sbyte)firstSpan[0]);
        }
        else
        {
            var reader = new SequenceReader<byte>(payload);
            if (!reader.TryRead(out var streamIdRaw))
                return 0;

            streamId = unchecked((sbyte)streamIdRaw);
        }

        payload = payload.Slice(sizeof(sbyte));
        return streamId;
    }

    private void HandleDisconnected(Exception ex)
    {
        if (Interlocked.Exchange(ref _disconnectHandled, true))
            return;

        if (IsEnabled(LogLevel.Information))
            _logger.LogInformation(ex, "Client disconnected.");

        _requestManager.FailAllPendingRequests(ex);
        _session?.StreamManager.CompleteAll(true, ex.Message);
        _serverStreamRequestIds.Clear();
        _locallyCanceledRequestIds.Clear();
    }

    private void OnRequestCancel(RequestCancelState state)
    {
        if (!state.IsOneWay && !_locallyCanceledRequestIds.Add(state.RequestId))
            return;

        _session?.SendCancelAsync(state.RequestId);

        if (state.IsOneWay) return;
        var ex = CreateCancellationException(state.UserToken);
        _requestManager.DispatchError(state.RequestId, ex);
    }

    private void OnStreamCancel(StreamCancelState state)
    {
        if (!_locallyCanceledRequestIds.Add(state.RequestId))
            return;

        _session?.SendCancelAsync(state.RequestId);
        var ex = CreateCancellationException(state.UserToken);
        _session?.StreamManager.CompleteStream(state.RequestId, 0, true, ex.Message);
    }

    private void OnRequestTimeout(RequestTimeoutState state)
    {
        if (!state.IsOneWay && !_locallyCanceledRequestIds.Add(state.RequestId))
            return;

        _session?.SendCancelAsync(state.RequestId);
        if (!state.IsOneWay)
            _requestManager.DispatchError(state.RequestId, new TimeoutException("Request timed out."));
    }

    private void OnStreamTimeout(StreamTimeoutState state)
    {
        if (!_locallyCanceledRequestIds.Add(state.RequestId))
            return;

        _session?.SendCancelAsync(state.RequestId);
        _session?.StreamManager.CompleteStream(state.RequestId, 0, true, "Request timed out.");
    }

    private TimeoutRegistration RegisterRequestTimeout(bool enabled, TimeSpan timeout, long requestId, bool isOneWay)
    {
        if (!enabled)
            return default;

        var state = RequestTimeoutState.Rent(this, requestId, isOneWay);
        return _requestTimeoutScheduler.Schedule(timeout, SRequestTimeoutCallback, state);
    }

    private TimeoutRegistration RegisterStreamTimeout(bool enabled, TimeSpan timeout, long requestId)
    {
        if (!enabled)
            return default;

        var state = StreamTimeoutState.Rent(this, requestId);
        return _requestTimeoutScheduler.Schedule(timeout, SStreamTimeoutCallback, state);
    }

    private bool IsEnabled(LogLevel level) => level >= _minimumLogLevel && _logger.IsEnabled(level);
}
