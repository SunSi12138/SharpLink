namespace SharpLink.Client;

internal sealed class SharpLinkModuleRpcChannel(SharpLinkClient.ContractCallSite inner, SharpLinkDynamicModule module) : IRpcContractChannel
{
    public IRpcRuntimeContext RuntimeContext => inner.RuntimeContext;

    public ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => inner.PrepareAsync(timeout, cancellationToken);

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
        CombinedCancellation combined = default;
        SharpLinkClientStreamModuleLeaseOwner? producerLifetime = null;
        try
        {
            combined = Combine(cancellationToken, module.ForcedCancellation);
            if (method.HasClientStreams)
            {
                // OneWay already owns one module lease. Transfer it; do not acquire a second.
                producerLifetime = new SharpLinkClientStreamModuleLeaseOwner(lease, combined.Source, keepLeaseUntilLogicalCompletion: true);
                lease = default;
                combined = new CombinedCancellation(combined.Token, null);
            }
            ValueTask call;
            if (producerLifetime is null)
                call = inner.InvokeOneWayAsync(method, request, requestCodec, streams, metadata, combined.Token);
            else
            {
                using (SharpLinkClientStreamModuleLeaseContext.Push(producerLifetime))
                    call = inner.InvokeOneWayAsync(method, request, requestCodec, streams, metadata, combined.Token);
            }
            if (call.IsCompletedSuccessfully)
            {
                CleanupLogicalOwnership(ref lease, ref producerLifetime, ref combined);
                return call;
            }
            return AwaitOneWayAsync(call, lease, combined, producerLifetime);
        }
        catch (Exception failure)
        {
            CleanupLogicalOwnership(ref lease, ref producerLifetime, ref combined, failure);
            throw;
        }
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
        SharpLinkClientStreamModuleLeaseOwner? producerLifetime = null;
        CombinedCancellation combined = default;
        try
        {
            combined = Combine(cancellationToken, module.ForcedCancellation);
            producerLifetime = new SharpLinkClientStreamModuleLeaseOwner(producerLease, combined.Source);
            producerLease = default;
            combined = new CombinedCancellation(combined.Token, null);
            ValueTask<TResponse> call;
            using (SharpLinkClientStreamModuleLeaseContext.Push(producerLifetime))
            {
                call = inner.InvokeClientStreamingAsync(
                    method, request, requestCodec, responseCodec, streams, metadata, combined.Token);
            }
            if (call.IsCompletedSuccessfully)
            {
                CleanupLogicalOwnership(ref lease, ref producerLifetime, ref combined);
                return call;
            }
            return AwaitAsync(call, lease, combined, producerLifetime);
        }
        catch (Exception failure)
        {
            try { producerLease.Dispose(); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup)!; }
            CleanupLogicalOwnership(ref lease, ref producerLifetime, ref combined, failure);
            throw;
        }
    }

    public IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
    {
        var requestValue = request;
        var interceptors = inner.CaptureInterceptorGenerationForInvocation();
        var control = inner.ResolveCallControlForInvocation(
            method, metadata, includeClientDefault: false, interceptors);
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
        return InvokeDuplexStreamingDeferred(
            method, requestValue, requestCodec, responseCodec, streamsValue, interceptors, control, cancellationToken);
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
        SharpLinkClientStreamModuleLeaseOwner? producerLifetime)
    {
        T result = default!;
        Exception? failure = null;
        try { result = await call.ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        CleanupLogicalOwnership(ref lease, ref producerLifetime, ref combined, failure);
        return result;
    }

    private static async ValueTask AwaitOneWayAsync(ValueTask call, SharpLinkDynamicModuleLease lease,
        CombinedCancellation combined, SharpLinkClientStreamModuleLeaseOwner? producerLifetime)
    {
        Exception? failure = null;
        try { await call.ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        CleanupLogicalOwnership(ref lease, ref producerLifetime, ref combined, failure);
    }

    private static void CleanupLogicalOwnership(ref SharpLinkDynamicModuleLease lease,
        ref SharpLinkClientStreamModuleLeaseOwner? producerLifetime, ref CombinedCancellation combined, Exception? failure = null)
    {
        var ownedLease = lease;
        var ownedProducer = producerLifetime;
        var ownedCombined = combined;
        lease = default;
        producerLifetime = null;
        combined = default;
        try { ownedLease.Dispose(); } catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
        try { ownedProducer?.Dispose(); } catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
        try { ownedCombined.Dispose(); } catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
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
        CombinedCancellation combined = default;
        SharpLinkClientStreamModuleLeaseOwner? noProducer = null;
        SharpLinkClient.ContractCallSite.InterceptedAsyncEnumerable<TResponse>? responseLifetime = null;
        Exception? failure = null;
        try
        {
            IAsyncEnumerator<TResponse> enumerator;
            try
            {
                combined = Combine(callCancellation, module.ForcedCancellation);
                SharpLinkClient.EnsureLogicalCallProgress(control);
                var stream = inner.InvokeServerStreamingResolved(
                    method, request, requestCodec, responseCodec, interceptors, control, combined.Token,
                    ownResponse: true, out responseLifetime);
                enumerator = stream.GetAsyncEnumerator(enumerationCancellation);
            }
            catch (Exception error) { failure = error; throw; }
            try
            {
                while (true)
                {
                    bool next;
                    try { next = await enumerator.MoveNextAsync().ConfigureAwait(false); }
                    catch (Exception error) { failure = error; throw; }
                    if (!next) break;
                    TResponse item;
                    try { item = enumerator.Current; }
                    catch (Exception error) { failure = error; throw; }
                    yield return item;
                }
            }
            finally
            {
                try { await enumerator.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
            }
        }
        finally
        {
            if (responseLifetime is not null)
            {
                var source = combined.Source;
                try { await responseLifetime.FinishModuleResponse(ref lease, ref source, ref noProducer).ConfigureAwait(false); }
                catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
                finally { combined = new CombinedCancellation(combined.Token, source); }
            }
            CleanupLogicalOwnership(ref lease, ref noProducer, ref combined, failure);
        }
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

        SharpLinkClientStreamModuleLeaseOwner? producerLifetime = null;
        CombinedCancellation combined = default;
        Exception? invocationFailure = null;
        SharpLinkClient.ContractCallSite.InterceptedAsyncEnumerable<TResponse>? responseLifetime = null;
        try
        {
            try
            {
                combined = Combine(callCancellation, module.ForcedCancellation);
                producerLifetime = new SharpLinkClientStreamModuleLeaseOwner(producerLease, combined.Source);
                producerLease = default;
                combined = new CombinedCancellation(combined.Token, null);
            }
            catch (Exception error) { invocationFailure = error; throw; }
            using (SharpLinkClientStreamModuleLeaseContext.Push(producerLifetime))
            {
                IAsyncEnumerator<TResponse> enumerator;
                try
                {
                    SharpLinkClient.EnsureLogicalCallProgress(control);
                    var stream = inner.InvokeDuplexStreamingResolved(
                        method, request, requestCodec, responseCodec, streams, interceptors, control, combined.Token,
                        ownResponse: true, out responseLifetime);
                    enumerator = stream.GetAsyncEnumerator(enumerationCancellation);
                }
                catch (Exception error) { invocationFailure = error; throw; }
                try
                {
                    while (true)
                    {
                        bool next;
                        try { next = await enumerator.MoveNextAsync().ConfigureAwait(false); }
                        catch (Exception error) { invocationFailure = error; throw; }
                        if (!next) break;
                        TResponse current;
                        try { current = enumerator.Current; }
                        catch (Exception error) { invocationFailure = error; throw; }
                        yield return current;
                    }
                }
                finally
                {
                    try { await enumerator.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error) { invocationFailure = RpcPublicationErrors.Combine(invocationFailure, error); }
                }
            }
        }
        finally
        {
            if (responseLifetime is not null)
            {
                var source = combined.Source;
                try { await responseLifetime.FinishModuleResponse(ref lease, ref source, ref producerLifetime).ConfigureAwait(false); }
                catch (Exception error) { invocationFailure = RpcPublicationErrors.Combine(invocationFailure, error); }
                finally { combined = new CombinedCancellation(combined.Token, source); }
            }
            try { producerLease.Dispose(); }
            catch (Exception error) { invocationFailure = RpcPublicationErrors.Combine(invocationFailure, error); }
            CleanupLogicalOwnership(ref lease, ref producerLifetime, ref combined, invocationFailure);
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
        internal CancellationTokenSource? Source { get; } = source;
        public void Dispose() => Source?.Dispose();
    }
}

// Existing producer owner now holds the linked source too. Only this per-stream-call
// owner synchronizes logical/physical exit; no candidate-wide or ordinary unary count.
internal sealed class SharpLinkClientStreamModuleLeaseOwner : IDisposable
{
    private readonly Lock _gate = new();
    private SharpLinkDynamicModuleLease _lease;
    private CancellationTokenSource? _source;
    private readonly bool _keepLeaseUntilLogicalCompletion;
    private bool _claimed;
    private bool _physicalCompleted;
    private bool _logicalCompleted;
    private bool _leaseReleased;

    internal SharpLinkClientStreamModuleLeaseOwner(SharpLinkDynamicModuleLease lease,
        CancellationTokenSource? source, bool keepLeaseUntilLogicalCompletion = false)
    {
        _lease = lease;
        _source = source;
        _keepLeaseUntilLogicalCompletion = keepLeaseUntilLogicalCompletion;
    }

    internal bool TryClaimPhysical()
    {
        lock (_gate)
        {
            if (_claimed || _logicalCompleted) return false;
            _claimed = true;
            return true;
        }
    }

    internal void CompletePhysical() => Complete(logical: false);
    public void Dispose() => Complete(logical: true);

    private void Complete(bool logical)
    {
        SharpLinkDynamicModuleLease lease = default;
        CancellationTokenSource? source = null;
        lock (_gate)
        {
            if (logical) _logicalCompleted = true;
            else
            {
                if (!_claimed) throw new InvalidOperationException("Unclaimed dynamic producer physical completion.");
                _physicalCompleted = true;
            }
            var physicalDone = !_claimed || _physicalCompleted;
            if (!_leaseReleased && physicalDone &&
                (_logicalCompleted || !_keepLeaseUntilLogicalCompletion && _claimed))
            {
                _leaseReleased = true;
                lease = _lease;
                _lease = default;
            }
            if (_logicalCompleted && physicalDone)
            {
                source = _source;
                _source = null;
            }
        }
        Exception? failure = null;
        try { source?.Dispose(); } catch (Exception error) { failure = error; }
        try { lease.Dispose(); } catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
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
