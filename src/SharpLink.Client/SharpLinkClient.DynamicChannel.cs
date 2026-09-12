namespace SharpLink.Client;

internal sealed class SharpLinkModuleRpcChannel(SharpLinkClient inner, SharpLinkDynamicModule module) : IRpcChannel
{
    public IRpcRuntimeContext RuntimeContext => inner.RuntimeContext;

    public ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(RpcMethodDescriptor method, in TRequest request,
        IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec, SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
    {
        if (!module.TryAcquire(false, out var lease))
            return ValueTask.FromException<TResponse>(Draining());
        var combined = Combine(cancellationToken, module.ForcedCancellation);
        try
        {
            var call = inner.InvokeUnaryAsync(method, request, requestCodec, responseCodec, metadata, combined.Token);
            if (call.IsCompletedSuccessfully)
            {
                lease.Dispose();
                combined.Dispose();
                return call;
            }
            return AwaitAsync(call, lease, combined);
        }
        catch { lease.Dispose(); combined.Dispose(); throw; }
    }

    public ValueTask InvokeOneWayAsync<TRequest, TStreams>(RpcMethodDescriptor method, in TRequest request,
        IRpcCodec<TRequest> requestCodec, in TStreams streams, SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default) where TStreams : struct, IRpcClientStreamWriter
    {
        if (!module.TryAcquire(method.HasClientStreams, out var lease))
            return ValueTask.FromException(Draining());
        var combined = Combine(cancellationToken, module.ForcedCancellation);
        try
        {
            var call = inner.InvokeOneWayAsync(method, request, requestCodec, streams, metadata, combined.Token);
            if (call.IsCompletedSuccessfully)
            {
                lease.Dispose();
                combined.Dispose();
                return call;
            }
            return AwaitAsync(call, lease, combined);
        }
        catch { lease.Dispose(); combined.Dispose(); throw; }
    }

    public ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        in TStreams streams, SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
    {
        if (!module.TryAcquire(true, out var lease))
            return ValueTask.FromException<TResponse>(Draining());
        if (!module.TryAcquire(true, out var producerLease))
        {
            lease.Dispose();
            return ValueTask.FromException<TResponse>(Draining());
        }
        var producerLifetime = new SharpLinkClientStreamModuleLeaseOwner(producerLease);
        var combined = Combine(cancellationToken, module.ForcedCancellation);
        try
        {
            ValueTask<TResponse> call;
            using (SharpLinkClientStreamModuleLeaseContext.Push(producerLifetime))
            {
                call = inner.InvokeClientStreamingAsync(
                    method, request, requestCodec, responseCodec, streams, metadata, combined.Token);
            }
            if (call.IsCompletedSuccessfully)
            {
                lease.Dispose();
                producerLifetime.Dispose();
                combined.Dispose();
                return call;
            }
            return AwaitAsync(call, lease, combined, producerLifetime);
        }
        catch { lease.Dispose(); producerLifetime.Dispose(); combined.Dispose(); throw; }
    }

    public IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
    {
        var requestValue = request;
        var control = inner.ResolveCallControlForInvocation(method, metadata, includeClientDefault: false);
        return InvokeServerStreamingDeferred(
            method, requestValue, requestCodec, responseCodec, control, cancellationToken);
    }

    public IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        in TStreams streams, SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var requestValue = request;
        var streamsValue = streams;
        var control = inner.ResolveCallControlForInvocation(method, metadata, includeClientDefault: false);
        return InvokeDuplexStreamingDeferred(
            method, requestValue, requestCodec, responseCodec, streamsValue, control, cancellationToken);
    }

    public Task SendClientStreamAsync<T>(long requestId, ushort streamId, IAsyncEnumerable<T> stream,
        IRpcCodec<T> codec, CancellationToken cancellationToken = default)
        => inner.SendClientStreamAsync(requestId, streamId, stream, codec, cancellationToken);

    private static async ValueTask<T> AwaitAsync<T>(ValueTask<T> call, SharpLinkDynamicModuleLease lease,
        CombinedCancellation combined)
    {
        try { return await call.ConfigureAwait(false); }
        finally { lease.Dispose(); combined.Dispose(); }
    }

    private static async ValueTask<T> AwaitAsync<T>(
        ValueTask<T> call,
        SharpLinkDynamicModuleLease lease,
        CombinedCancellation combined,
        SharpLinkClientStreamModuleLeaseOwner producerLifetime)
    {
        try { return await call.ConfigureAwait(false); }
        finally { lease.Dispose(); producerLifetime.Dispose(); combined.Dispose(); }
    }

    private static async ValueTask AwaitAsync(ValueTask call, SharpLinkDynamicModuleLease lease,
        CombinedCancellation combined)
    {
        try { await call.ConfigureAwait(false); }
        finally { lease.Dispose(); combined.Dispose(); }
    }

    private async IAsyncEnumerable<TResponse> InvokeServerStreamingDeferred<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkClient.ResolvedCallControl control,
        CancellationToken callCancellation,
        [EnumeratorCancellation] CancellationToken enumerationCancellation = default)
    {
        SharpLinkClient.EnsureLogicalCallProgress(control);
        if (!module.TryAcquire(true, out var lease))
        {
            SharpLinkClient.EnsureLogicalCallProgress(control);
            throw Draining();
        }
        var combined = Combine(callCancellation, module.ForcedCancellation);
        try
        {
            SharpLinkClient.EnsureLogicalCallProgress(control);
            var stream = inner.InvokeServerStreamingResolved(
                method, request, requestCodec, responseCodec, control, combined.Token);
            await foreach (var item in stream.WithCancellation(enumerationCancellation).ConfigureAwait(false))
                yield return item;
        }
        finally { lease.Dispose(); combined.Dispose(); }
    }

    private async IAsyncEnumerable<TResponse> InvokeDuplexStreamingDeferred<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        TStreams streams,
        SharpLinkClient.ResolvedCallControl control,
        CancellationToken callCancellation,
        [EnumeratorCancellation] CancellationToken enumerationCancellation = default)
        where TStreams : struct, IRpcClientStreamWriter
    {
        SharpLinkClient.EnsureLogicalCallProgress(control);
        if (!module.TryAcquire(true, out var lease))
        {
            SharpLinkClient.EnsureLogicalCallProgress(control);
            throw Draining();
        }

        SharpLinkDynamicModuleLease producerLease;
        try
        {
            SharpLinkClient.EnsureLogicalCallProgress(control);
            if (!module.TryAcquire(true, out producerLease))
            {
                SharpLinkClient.EnsureLogicalCallProgress(control);
                throw Draining();
            }
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        var producerLifetime = new SharpLinkClientStreamModuleLeaseOwner(producerLease);
        var combined = Combine(callCancellation, module.ForcedCancellation);
        try
        {
            using (SharpLinkClientStreamModuleLeaseContext.Push(producerLifetime))
            {
                SharpLinkClient.EnsureLogicalCallProgress(control);
                var stream = inner.InvokeDuplexStreamingResolved(
                    method, request, requestCodec, responseCodec, streams, control, combined.Token);
                await foreach (var item in stream.WithCancellation(enumerationCancellation).ConfigureAwait(false))
                    yield return item;
            }
        }
        finally { lease.Dispose(); producerLifetime.Dispose(); combined.Dispose(); }
    }

    private static SharpLinkException Draining() => new(SharpLinkErrorCode.Unavailable, "RPC module is draining");

    private static CombinedCancellation Combine(CancellationToken caller, CancellationToken moduleToken)
    {
        if (!caller.CanBeCanceled)
            return new CombinedCancellation(moduleToken, null);
        var source = CancellationTokenSource.CreateLinkedTokenSource(caller, moduleToken);
        return new CombinedCancellation(source.Token, source);
    }

    private readonly struct CombinedCancellation(CancellationToken token, CancellationTokenSource? source) : IDisposable
    {
        internal CancellationToken Token { get; } = token;
        public void Dispose() => source?.Dispose();
    }
}

internal sealed class SharpLinkClientStreamModuleLeaseOwner(SharpLinkDynamicModuleLease lease) : IDisposable
{
    private int _claimed;

    internal SharpLinkDynamicModuleLease TakeLease()
    {
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
            throw new InvalidOperationException("The dynamic client-stream producer lease was already claimed.");
        return lease;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _claimed, 2, 0) == 0)
            lease.Dispose();
    }
}

internal static class SharpLinkClientStreamModuleLeaseContext
{
    private static readonly AsyncLocal<SharpLinkClientStreamModuleLeaseOwner?> CurrentOwner = new();

    internal static SharpLinkClientStreamModuleLeaseOwner? Current => CurrentOwner.Value;

    internal static Scope Push(SharpLinkClientStreamModuleLeaseOwner owner)
    {
        var previous = CurrentOwner.Value;
        CurrentOwner.Value = owner;
        return new Scope(previous);
    }

    internal readonly struct Scope(SharpLinkClientStreamModuleLeaseOwner? previous) : IDisposable
    {
        public void Dispose() => CurrentOwner.Value = previous;
    }
}
