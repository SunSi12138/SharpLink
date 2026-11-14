namespace SharpLink.Client;
#if SHARPLINK_775_STATIC_SLICE
using CombinedCancellation = SharpLink.Client.SharpLinkClientModuleCancellation;
internal sealed class SharpLinkModuleRpcChannel(SharpLinkClient inner, StaticBindingCallSite site, SharpLinkDynamicModule module) : IRpcContractChannel
#else
internal sealed class SharpLinkModuleRpcChannel(SharpLinkClient inner, SharpLinkDynamicModule module) : IRpcChannel
#endif
{
    public IRpcRuntimeContext RuntimeContext => inner.RuntimeContext;
#if SHARPLINK_775_STATIC_SLICE
    public ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => site.PrepareAsync(timeout, cancellationToken);
#endif

    public ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(RpcMethodDescriptor method, in TRequest request,
        IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec, SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
    {
        if (!module.TryAcquire(false, out var lease))
            return ValueTask.FromException<TResponse>(Draining());
        var combined = Combine(cancellationToken, module.ForcedCancellation);
        try
        {
            var call =
#if SHARPLINK_775_STATIC_SLICE
                inner.InvokeStaticUnaryAsync(site,
#else
                inner.InvokeUnaryAsync(
#endif
                method, request, requestCodec, responseCodec, metadata, combined.Token);
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
            var call =
#if SHARPLINK_775_STATIC_SLICE
                inner.InvokeStaticOneWayAsync(site,
#else
                inner.InvokeOneWayAsync(
#endif
                method, request, requestCodec, streams, metadata, combined.Token);
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
#if SHARPLINK_775_STATIC_SLICE
        var combined = Combine(cancellationToken, module.ForcedCancellation);
        var producerLifetime = new SharpLinkClientStreamModuleLeaseOwner(producerLease, combined.Retain());
#else
        var producerLifetime = new SharpLinkClientStreamModuleLeaseOwner(producerLease);
        var combined = Combine(cancellationToken, module.ForcedCancellation);
#endif
        try
        {
            ValueTask<TResponse> call;
            using (SharpLinkClientStreamModuleLeaseContext.Push(producerLifetime))
            {
                call =
#if SHARPLINK_775_STATIC_SLICE
                    inner.InvokeStaticClientStreamingAsync(site,
#else
                    inner.InvokeClientStreamingAsync(
#endif
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
        var interceptors = inner.CaptureInterceptorGenerationForInvocation();
        var control = inner.ResolveCallControlForInvocation(
            method, metadata, includeClientDefault: false, interceptors);
#if SHARPLINK_775_STATIC_SLICE
        control = control with { BindingSite = site };
#endif
        return InvokeServerStreamingDeferred(
            method, requestValue, requestCodec, responseCodec, interceptors, control, cancellationToken);
    }

    public IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        in TStreams streams, SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var requestValue = request;
        var streamsValue = streams;
        var interceptors = inner.CaptureInterceptorGenerationForInvocation();
        var control = inner.ResolveCallControlForInvocation(
            method, metadata, includeClientDefault: false, interceptors);
#if SHARPLINK_775_STATIC_SLICE
        control = control with { BindingSite = site };
#endif
        return InvokeDuplexStreamingDeferred(
            method, requestValue, requestCodec, responseCodec, streamsValue, interceptors, control, cancellationToken);
    }

    private static async ValueTask<T> AwaitAsync<T>(ValueTask<T> call, SharpLinkDynamicModuleLease lease,
        CombinedCancellation combined)
    {
        try { return await call.ConfigureAwait(false); }
#if SHARPLINK_775_STATIC_SLICE
        finally { combined.Dispose(); lease.Dispose(); }
#else
        finally { lease.Dispose(); combined.Dispose(); }
#endif
    }

    private static async ValueTask<T> AwaitAsync<T>(
        ValueTask<T> call,
        SharpLinkDynamicModuleLease lease,
        CombinedCancellation combined,
        SharpLinkClientStreamModuleLeaseOwner producerLifetime)
    {
        try { return await call.ConfigureAwait(false); }
#if SHARPLINK_775_STATIC_SLICE
        finally { producerLifetime.Dispose(); combined.Dispose(); lease.Dispose(); }
#else
        finally { lease.Dispose(); producerLifetime.Dispose(); combined.Dispose(); }
#endif
    }

    private static async ValueTask AwaitAsync(ValueTask call, SharpLinkDynamicModuleLease lease,
        CombinedCancellation combined)
    {
        try { await call.ConfigureAwait(false); }
#if SHARPLINK_775_STATIC_SLICE
        finally { combined.Dispose(); lease.Dispose(); }
#else
        finally { lease.Dispose(); combined.Dispose(); }
#endif
    }

    private async IAsyncEnumerable<TResponse> InvokeServerStreamingDeferred<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkClient.ClientInterceptorGeneration interceptors,
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
#if SHARPLINK_775_STATIC_SLICE
        var callOwner = new SharpLinkClientModuleCallOwner(lease, combined);
        using var callScope = SharpLinkClientModuleCallContext.Push(callOwner);
#endif
        try
        {
            SharpLinkClient.EnsureLogicalCallProgress(control);
            var stream = inner.InvokeServerStreamingResolved(
                method, request, requestCodec, responseCodec, interceptors, control, combined.Token);
            await foreach (var item in stream.WithCancellation(enumerationCancellation).ConfigureAwait(false))
                yield return item;
        }
#if SHARPLINK_775_STATIC_SLICE
        finally { callOwner.Dispose(); }
#else
        finally { lease.Dispose(); combined.Dispose(); }
#endif
    }

    private async IAsyncEnumerable<TResponse> InvokeDuplexStreamingDeferred<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        TStreams streams,
        SharpLinkClient.ClientInterceptorGeneration interceptors,
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

#if SHARPLINK_775_STATIC_SLICE
        var combined = Combine(callCancellation, module.ForcedCancellation);
        var producerLifetime = new SharpLinkClientStreamModuleLeaseOwner(producerLease, combined.Retain());
#else
        var producerLifetime = new SharpLinkClientStreamModuleLeaseOwner(producerLease);
        var combined = Combine(callCancellation, module.ForcedCancellation);
#endif
#if SHARPLINK_775_STATIC_SLICE
        var callOwner = new SharpLinkClientModuleCallOwner(lease, combined);
        using var callScope = SharpLinkClientModuleCallContext.Push(callOwner);
#endif
        try
        {
            using (SharpLinkClientStreamModuleLeaseContext.Push(producerLifetime))
            {
                SharpLinkClient.EnsureLogicalCallProgress(control);
                var stream = inner.InvokeDuplexStreamingResolved(
                    method, request, requestCodec, responseCodec, streams, interceptors, control, combined.Token);
                await foreach (var item in stream.WithCancellation(enumerationCancellation).ConfigureAwait(false))
                    yield return item;
            }
        }
#if SHARPLINK_775_STATIC_SLICE
        finally { producerLifetime.Dispose(); callOwner.Dispose(); }
#else
        finally { lease.Dispose(); producerLifetime.Dispose(); combined.Dispose(); }
#endif
    }

    private static SharpLinkException Draining() => new(SharpLinkErrorCode.Unavailable, "RPC module is draining");

    private static CombinedCancellation Combine(CancellationToken caller, CancellationToken moduleToken)
    {
        if (!caller.CanBeCanceled)
            return new CombinedCancellation(moduleToken, null);
        var source = CancellationTokenSource.CreateLinkedTokenSource(caller, moduleToken);
        return new CombinedCancellation(source.Token, source);
    }

#if !SHARPLINK_775_STATIC_SLICE
    private readonly struct CombinedCancellation(CancellationToken token, CancellationTokenSource? source) : IDisposable
    {
        internal CancellationToken Token { get; } = token;
        public void Dispose() => source?.Dispose();
    }
#endif
}

internal sealed class SharpLinkClientStreamModuleLeaseOwner(SharpLinkDynamicModuleLease lease) : IDisposable
{
    private int _claimed;

#if SHARPLINK_775_STATIC_SLICE
    private IDisposable? _cancellation;
    internal SharpLinkClientStreamModuleLeaseOwner(SharpLinkDynamicModuleLease lease, IDisposable cancellation) : this(lease)
        => _cancellation = cancellation;
    internal SharpLinkClientStreamPhysicalLease TakeLease()
    {
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
            throw new InvalidOperationException("The dynamic client-stream producer lease was already claimed.");
        return new(lease, Interlocked.Exchange(ref _cancellation, null));
    }
#else
    internal SharpLinkDynamicModuleLease TakeLease()
    {
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
            throw new InvalidOperationException("The dynamic client-stream producer lease was already claimed.");
        return lease;
    }
#endif

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _claimed, 2, 0) == 0)
#if SHARPLINK_775_STATIC_SLICE
        { try { Interlocked.Exchange(ref _cancellation, null)?.Dispose(); } finally { lease.Dispose(); } }
#else
            lease.Dispose();
#endif
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
