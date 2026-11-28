namespace SharpLink.Client;

internal static class SharpLinkModuleRpcChannel
{
    internal static ValueTask<TResponse> InvokeUnaryWithModule<TRequest, TResponse>(SharpLinkClient inner,
        SharpLinkDynamicModule module, RpcMethodDescriptor method, in TRequest request,
        IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec, SharpLinkMetadata? metadata,
        CancellationToken cancellationToken, SharpLinkClient.NStudyPreparedSite prepared)
    {
        if (!module.TryAcquire(false, out var lease))
            return ValueTask.FromException<TResponse>(Draining());
        var combined = Combine(cancellationToken, module.ForcedCancellation);
        try
        {
            var call = inner.InvokeUnary(method, request, requestCodec, responseCodec, metadata, combined.Token, prepared);
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

    internal static ValueTask InvokeOneWayWithModule<TRequest, TStreams>(SharpLinkClient inner,
        SharpLinkDynamicModule module, RpcMethodDescriptor method, in TRequest request,
        IRpcCodec<TRequest> requestCodec, in TStreams streams, SharpLinkMetadata? metadata,
        CancellationToken cancellationToken, SharpLinkClient.NStudyPreparedSite prepared)
        where TStreams : struct, IRpcClientStreamWriter
    {
        if (!module.TryAcquire(method.HasClientStreams, out var lease))
            return ValueTask.FromException(Draining());
        var combined = Combine(cancellationToken, module.ForcedCancellation);
        try
        {
            var call = inner.InvokeOneWay(method, request, requestCodec, streams, metadata, combined.Token, prepared);
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

    internal static ValueTask<TResponse> InvokeClientStreamingWithModule<TRequest, TResponse, TStreams>(
        SharpLinkClient inner, SharpLinkDynamicModule module, RpcMethodDescriptor method, in TRequest request,
        IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec, in TStreams streams,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken, SharpLinkClient.NStudyPreparedSite prepared)
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
            var call = inner.InvokeClientStreaming(method, request, requestCodec, responseCodec, streams,
                metadata, combined.Token, prepared, producerLifetime);
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

    internal static IAsyncEnumerable<TResponse> InvokeServerStreamingDeferred<TRequest, TResponse>(
        SharpLinkClient inner, SharpLinkDynamicModule? module, RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken, SharpLinkClient.NStudyPreparedSite prepared)
    {
        var interceptors = inner.CaptureInterceptorGenerationForInvocation();
        var control = inner.ResolveCallControlForInvocation(
            method, metadata, includeClientDefault: false, interceptors) with
        { PreparedSite = prepared };
        return module is null
            ? new StaticServerStreamingInvocation<TRequest, TResponse>(inner, method, request, requestCodec,
                responseCodec, interceptors, control, cancellationToken)
            : EnumerateModuleServerStream(inner, module, method, request, requestCodec, responseCodec,
                interceptors, control, cancellationToken);
    }

    internal static IAsyncEnumerable<TResponse> InvokeDuplexStreamingDeferred<TRequest, TResponse, TStreams>(
        SharpLinkClient inner, SharpLinkDynamicModule? module, RpcMethodDescriptor method, in TRequest request,
        IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec, in TStreams streams,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken, SharpLinkClient.NStudyPreparedSite prepared)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var interceptors = inner.CaptureInterceptorGenerationForInvocation();
        var control = inner.ResolveCallControlForInvocation(
            method, metadata, includeClientDefault: false, interceptors) with
        { PreparedSite = prepared };
        var invocation = new DuplexInvocation<TRequest, TResponse, TStreams>(inner, module,
            method, request, requestCodec, responseCodec, streams, interceptors, control, cancellationToken);
        return module is null ? invocation : invocation.EnumerateAsync();
    }

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

    private static void ClaimStaticOutputEnumeration(ref int enumerated)
    {
        if (Interlocked.Exchange(ref enumerated, 1) != 0)
            throw new InvalidOperationException("A logical RPC stream can only be enumerated once.");
    }

    private sealed class StaticServerStreamingInvocation<TRequest, TResponse>(
        SharpLinkClient inner, RpcMethodDescriptor method, TRequest request,
        IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkClient.ClientInterceptorGeneration interceptors,
        SharpLinkClient.ResolvedCallControl control,
        CancellationToken callCancellation) : IAsyncEnumerable<TResponse>
    {
        private int _enumerated;

        public IAsyncEnumerator<TResponse> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            ClaimStaticOutputEnumeration(ref _enumerated);
            return EnumerateAsync(cancellationToken);
        }

        private async IAsyncEnumerator<TResponse> EnumerateAsync(CancellationToken enumerationCancellation)
        {
            SharpLinkClient.EnsureLogicalCallProgress(control);
            var stream = inner.InvokeServerStreamingResolved(method, request, requestCodec, responseCodec,
                interceptors, control, callCancellation);
            await foreach (var item in stream.WithCancellation(enumerationCancellation).ConfigureAwait(false))
                yield return item;
        }
    }

    private static async IAsyncEnumerable<TResponse> EnumerateModuleServerStream<TRequest, TResponse>(
        SharpLinkClient inner, SharpLinkDynamicModule module, RpcMethodDescriptor method,
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
        var lifetime = new NStudyServerOutputLifetime(lease, combined.Source);
        try
        {
            SharpLinkClient.EnsureLogicalCallProgress(control);
            var stream = inner.InvokeServerStreamingResolved(
                method, request, requestCodec, responseCodec, interceptors,
                control with { OutputLifetime = lifetime }, combined.Token);
            await foreach (var item in stream.WithCancellation(enumerationCancellation).ConfigureAwait(false))
                yield return item;
        }
        finally { lifetime.CloseConsumer(); }
    }

    // Capture call-time arguments once instead of copying them into the
    // iterator's initial and per-enumeration fields.
    private sealed class DuplexInvocation<TRequest, TResponse, TStreams>(
        SharpLinkClient inner,
        SharpLinkDynamicModule? module,
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        TStreams streams,
        SharpLinkClient.ClientInterceptorGeneration interceptors,
        SharpLinkClient.ResolvedCallControl control,
        CancellationToken callCancellation) : IAsyncEnumerable<TResponse>
        where TStreams : struct, IRpcClientStreamWriter
    {
        private int _enumerated;

        public IAsyncEnumerator<TResponse> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            ClaimStaticOutputEnumeration(ref _enumerated);
            return EnumerateAsync().GetAsyncEnumerator(cancellationToken);
        }

        internal async IAsyncEnumerable<TResponse> EnumerateAsync(
            [EnumeratorCancellation] CancellationToken enumerationCancellation = default)
        {
            SharpLinkClient.EnsureLogicalCallProgress(control);
            SharpLinkDynamicModuleLease lease = default;
            if (module is not null && !module.TryAcquire(true, out lease))
            {
                SharpLinkClient.EnsureLogicalCallProgress(control);
                throw Draining();
            }

            SharpLinkDynamicModuleLease producerLease = default;
            try
            {
                SharpLinkClient.EnsureLogicalCallProgress(control);
                if (module is not null && !module.TryAcquire(true, out producerLease))
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

            var producerLifetime = module is null ? null : new SharpLinkClientStreamModuleLeaseOwner(producerLease);
            var combined = module is null ? new CombinedCancellation(callCancellation, null) :
                Combine(callCancellation, module.ForcedCancellation);
            var outputLifetime = module is not null
                ? new NStudyServerOutputLifetime(lease, combined.Source)
                : null;
            try
            {
                SharpLinkClient.EnsureLogicalCallProgress(control);
                var stream = inner.BeginDuplexStreamingResolved(
                    method, request, requestCodec, responseCodec, streams, interceptors,
                    control with { InputLifetime = producerLifetime, OutputLifetime = outputLifetime }, combined.Token);
                var ownsLogicalCall = true;
                IAsyncEnumerator<TResponse>? enumerator = null;
                try
                {
                    enumerator = stream.GetAsyncEnumerator(enumerationCancellation);
                    while (true)
                    {
                        bool hasNext;
                        try { hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false); }
                        catch { ReleaseLogicalCall(ref ownsLogicalCall); throw; }
                        if (!hasNext)
                        {
                            // Logical completion precedes any physical wait in DisposeAsync.
                            ReleaseLogicalCall(ref ownsLogicalCall);
                            break;
                        }
                        yield return enumerator.Current;
                    }
                }
                finally
                {
                    try
                    {
                        if (enumerator is not null)
                            await enumerator.DisposeAsync().ConfigureAwait(false);
                    }
                    finally { ReleaseLogicalCall(ref ownsLogicalCall); }
                }
            }
            finally
            {
                if (outputLifetime is not null)
                {
                    try { outputLifetime.CloseConsumer(); }
                    finally { producerLifetime?.Dispose(); }
                }
                else
                {
                    lease.Dispose();
                    producerLifetime?.Dispose();
                    combined.Dispose();
                }
            }
        }

        private void ReleaseLogicalCall(ref bool owned)
        {
            if (!owned)
                return;
            owned = false;
            inner.ReleaseDirectLogicalInvocation();
        }
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
        internal CancellationTokenSource? Source => source;
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
