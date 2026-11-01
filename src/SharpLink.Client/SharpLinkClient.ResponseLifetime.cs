namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    internal sealed partial class ContractCallSite
    {
        internal interface IResponseInvocationLifetime
        {
            ValueTask<SharpLinkClientInvocationResult> AwaitInvocationAsync(ValueTask<SharpLinkClientInvocationResult> invocation);
        }

        // The existing intercepted enumerable is also the fixed-slot physical response carrier.
        // Only the dynamic module caller enables this path; static intercepted calls retain their
        // existing contract and are not silently given an unproven module-lifetime guarantee.
        internal sealed partial class InterceptedAsyncEnumerable<T>
        {
            private Task<SharpLinkClientInvocationResult>? _pendingInvocation;
            private Task<SharpLinkClientInvocationResult>? _pendingChain;
            private bool _chainAssigned;
            private bool _chainConsumed;
            private Task<bool>? _pendingMove;
            private IAsyncEnumerable<T>? _resultStream;
            private IAsyncEnumerator<T>? _resultEnumerator;
            private bool _enumeratorAcquisitionAttempted;
            private CancellationTokenSource? _responseCancellation;
            private CancellationTokenRegistration _enumerationCancellationRegistration;
            private CancellationTokenSource? _physicalSource;
            private SharpLinkClientStreamModuleLeaseOwner? _physicalProducerLogicalOwner;
            private PooledAsyncStreamDispatcher<T>? _coreSink;
            private long _coreLease;
            private Task? _coreOrigin;
            private bool _originSupervised;
            private FrameworkTaskRegistration? _originAttachmentFailureReceipt;
            private bool _detached;
            private bool _cleanupDone;
            private bool _invocationResultConsumed;
            private SharpLinkDynamicModuleLease _physicalLease;
            private Task? _worker;
            private TaskCompletionSource? _physicalCompletion;

            internal void InitializeCancellation()
                => _responseCancellation = CancellationTokenSource.CreateLinkedTokenSource(invocationCancellation);
            internal CancellationToken ResponseCancellationToken => _responseCancellation!.Token;

            internal void CaptureCore(PooledAsyncStreamDispatcher<T> sink)
            {
                if (_coreSink is not null) throw new InvalidOperationException("An interceptor terminal may be invoked at most once.");
                _coreLease = sink.CaptureOriginalLease();
                _coreSink = sink;
            }

            internal void CaptureOrigin(Task origin, bool supervised)
            {
                if (_coreOrigin is not null) throw new InvalidOperationException("A response has one original startup operation.");
                _coreOrigin = origin;
                _originSupervised = supervised;
            }

            internal void MarkOriginSupervised(FrameworkTaskRegistration? attachmentFailureReceipt = null)
            {
                _originSupervised = true;
                _originAttachmentFailureReceipt = attachmentFailureReceipt;
            }

            public async ValueTask<SharpLinkClientInvocationResult> AwaitInvocationAsync(
                ValueTask<SharpLinkClientInvocationResult> source)
            {
                if (!deadline.HasValue)
                {
                    var direct = await source.ConfigureAwait(false);
                    CaptureResult(direct); // Before any validation, deadline or telemetry callback.
                    return direct;
                }
                if (source.IsCompletedSuccessfully)
                {
                    var ready = source.Result;
                    CaptureResult(ready);
                    CheckOwnedProgress();
                    return ready;
                }
                var actual = source.AsTask();
                _pendingInvocation = actual;
                try
                {
                    if (!await SharpLinkTimer.WaitForCompletionAsync(actual, deadline, timeProvider).ConfigureAwait(false))
                    {
                        logicalCall?.TryClaimDeadline();
                        throw CreateDeadlineExceededException();
                    }
                }
                catch { _detached = true; throw; }
                // Successful readiness includes physical timer teardown. Only now does the
                // logical caller exclusively consume the source outcome (including raw OCE).
                _pendingInvocation = null;
                var result = await actual.ConfigureAwait(false);
                CaptureResult(result);
                CheckOwnedProgress();
                return result;
            }

            private void CheckOwnedProgress()
            {
                try { ThrowIfDeadlineExpired(); }
                catch { _detached = true; throw; }
            }

            private void CaptureResult(SharpLinkClientInvocationResult result)
            {
                _invocationResultConsumed = true;
                _resultStream = result.Value as IAsyncEnumerable<T>;
            }

            private async IAsyncEnumerator<T> GetOwnedEnumerator(CancellationToken cancellationToken)
            {
                if (Interlocked.Exchange(ref _enumerated, 1) != 0)
                    throw new InvalidOperationException("An intercepted RPC stream can only be enumerated once.");
                Exception? primary = null;
                try
                {
                    try
                    {
                        // The chain performs the sole invocation deadline arbitration. Its raw
                        // result was captured before post-result validation/telemetry can fail.
                        _chainConsumed = true;
                        var result = await invocation.ConfigureAwait(false);
                        if (!_invocationResultConsumed) CaptureResult(result);
                        _resultStream = result.GetValue<IAsyncEnumerable<T>>();
                        if (cancellationToken.CanBeCanceled)
                            _enumerationCancellationRegistration = cancellationToken.UnsafeRegister(
                                static state => ((CancellationTokenSource)state!).Cancel(), _responseCancellation!);
                        CheckOwnedProgress();
                        _enumeratorAcquisitionAttempted = true; // A failed acquisition is a consumed logical outcome, never retried.
                        _resultEnumerator = _resultStream.GetAsyncEnumerator(_responseCancellation!.Token);
                    }
                    catch (Exception error) { primary = error; throw; }
                    while (true)
                    {
                        bool next;
                        try
                        {
                            CheckOwnedProgress();
                            var move = _resultEnumerator.MoveNextAsync();
                            if (!deadline.HasValue)
                                next = await move.ConfigureAwait(false); // Preserve direct no-deadline outcome semantics.
                            else if (move.IsCompletedSuccessfully)
                            {
                                next = move.Result;
                                CheckOwnedProgress();
                            }
                            else
                            {
                                var actual = move.AsTask();
                                _pendingMove = actual;
                                try
                                {
                                    if (!await SharpLinkTimer.WaitForCompletionAsync(actual, deadline, timeProvider,
                                            _responseCancellation!.Token).ConfigureAwait(false))
                                    {
                                        logicalCall?.TryClaimDeadline();
                                        throw CreateDeadlineExceededException();
                                    }
                                }
                                catch { _detached = true; throw; }
                                _pendingMove = null;
                                next = await actual.ConfigureAwait(false);
                            }
                        }
                        catch (Exception error) { primary = error; throw; }
                        if (!next) yield break;
                        T item;
                        try
                        {
                            item = _resultEnumerator.Current;
                            if (!responseNullable && default(T) is null && item is null)
                                throw new InvalidCastException("A non-nullable intercepted RPC stream response was null.");
                        }
                        catch (Exception error) { primary = error; throw; }
                        yield return item;
                    }
                }
                finally
                {
                    // Never start arbitrary Dispose while an abandoned MoveNext is still active.
                    // A detached path returns only to its module caller, which must hand off the
                    // SAME lease before this logical operation can complete to application code.
                    if (!_detached)
                    {
                        var cleanup = await CleanupResponseAsync().ConfigureAwait(false);
                        if (cleanup is not null)
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                                PhysicalTaskFailureException.CombineRaw(primary, cleanup)!).Throw();
                    }
                }
            }

            internal ValueTask FinishModuleResponse(ref SharpLinkDynamicModuleLease originalLease,
                ref CancellationTokenSource? originalSource, ref SharpLinkClientStreamModuleLeaseOwner? producerLogicalOwner)
            {
                if (_cleanupDone) return ValueTask.CompletedTask;
                // A telemetry/outer GetEnumerator construction failure may prevent the carrier
                // iterator from consuming its chain at all. Claim that exact ValueTask ONCE.
                // Wait for chain exit before inspecting its inner invocation slots; never race
                // two owners against the same readiness/source transition.
                if (_chainAssigned && !_chainConsumed)
                {
                    _chainConsumed = true;
                    if (invocation.IsCompletedSuccessfully) CaptureResult(invocation.Result);
                    else { _pendingChain = invocation.AsTask(); _detached = true; }
                }
                if (!_detached) return FinishInlineAsync();

                // These objects/registration exist ONLY for an actual detached physical tail.
                // Registration roots this existing carrier while the caller still owns its lease.
                TaskCompletionSource? completion = null;
                TaskCompletionSource start;
                FrameworkTaskRegistration? receipt = null;
                try
                {
                    completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _physicalCompletion = completion;
                    _client.TrackOwnedResponseTail(completion.Task, this, ref receipt);
                    start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                catch (Exception registrationFailure)
                {
                    Exception? failure = registrationFailure;
                    completion?.TrySetResult(); // No transferred batch: never report it twice.
                    if (receipt is not null)
                    {
                        try { _client.CompleteAcceptedStreamSupervision(receipt); }
                        catch (Exception cleanup) { failure = PhysicalTaskFailureException.CombineRaw(failure, cleanup); }
                    }
                    _physicalCompletion = null;
                    return FinishInlineAsync(failure); // Caller keeps lease until actual join.
                }

                _physicalLease = originalLease;
                _physicalSource = originalSource;
                _physicalProducerLogicalOwner = producerLogicalOwner;
                originalSource = null;
                producerLogicalOwner = null;
                originalLease = default; // Definitive one-time move, never another TryAcquire.
                Task worker;
                try
                {
                    worker = RunDetachedAsync(start.Task);
                    _worker = worker; // Strong assignment precedes worker release/completion.
                    _ = worker.ContinueWith(static (finished, state) =>
                        ((InterceptedAsyncEnumerable<T>)state!).OnWorkerExited(finished), this,
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                catch (Exception setupFailure)
                {
                    // If attachment failed, caller joins the actual worker and owns its outcome.
                    // A partial accepted anchor is retired successfully after that fallback join.
                    start.TrySetResult();
                    return FinishWorkerSetupFailureAsync(setupFailure, receipt!);
                }
                start.TrySetResult();
                return ValueTask.CompletedTask;
            }

            private async ValueTask FinishInlineAsync(Exception? first = null)
            {
                var cleanup = await CleanupResponseAsync().ConfigureAwait(false);
                var failure = PhysicalTaskFailureException.CombineRaw(first, cleanup);
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }

            private async Task RunDetachedAsync(Task start)
            {
                await start.ConfigureAwait(false);
                Exception? failure = null;
                try { failure = await CleanupResponseAsync().ConfigureAwait(false); }
                catch (Exception unexpected) { failure = PhysicalTaskFailureException.CombineRaw(failure, unexpected); }
                finally
                {
                    failure = ReleaseTransferredOwnership(failure);
                }
                if (failure is not null) throw PhysicalTaskFailureException.From(failure);
            }

            private Exception? ReleaseTransferredOwnership(Exception? failure)
            {
                var producer = _physicalProducerLogicalOwner; _physicalProducerLogicalOwner = null;
                var source = _physicalSource; _physicalSource = null;
                var lease = _physicalLease; _physicalLease = default;
                try { producer?.Dispose(); }
                catch (Exception cleanup) { failure = PhysicalTaskFailureException.CombineRaw(failure, cleanup); }
                try { source?.Dispose(); }
                catch (Exception cleanup) { failure = PhysicalTaskFailureException.CombineRaw(failure, cleanup); }
                try { lease.Dispose(); }
                catch (Exception cleanup) { failure = PhysicalTaskFailureException.CombineRaw(failure, cleanup); }
                return failure;
            }

            private void OnWorkerExited(Task worker)
            {
                var completion = _physicalCompletion!;
                _worker = null;
                _physicalCompletion = null;
                // RunDetached always marks raw outcomes before async OCE conversion.
                if (worker.Exception is { } aggregate)
                {
                    var failure = aggregate.InnerExceptions.Count == 1 ? aggregate.InnerExceptions[0] : aggregate;
                    completion.TrySetException(failure);
                }
                else if (worker.IsCanceled)
                    completion.TrySetException(PhysicalTaskFailureException.From(
                        new InvalidOperationException("An owned response cleanup bypassed its raw-outcome envelope.")));
                else completion.TrySetResult();
            }

            private async ValueTask FinishWorkerSetupFailureAsync(Exception first, FrameworkTaskRegistration receipt)
            {
                Exception? failure = first;
                try
                {
                    if (_worker is { } worker)
                    {
                        try { await worker.ConfigureAwait(false); }
                        catch (Exception cleanup) { failure = PhysicalTaskFailureException.CombineRaw(failure, PhysicalTaskFailureException.GetRaw(cleanup)); }
                    }
                    else
                    {
                        try { failure = PhysicalTaskFailureException.CombineRaw(failure, await CleanupResponseAsync().ConfigureAwait(false)); }
                        finally
                        {
                            failure = ReleaseTransferredOwnership(failure);
                        }
                    }
                }
                finally
                {
                    _worker = null;
                    _physicalCompletion?.TrySetResult();
                    _physicalCompletion = null;
                    try { _client.CompleteAcceptedStreamSupervision(receipt); }
                    catch (Exception cleanup) { failure = PhysicalTaskFailureException.CombineRaw(failure, cleanup); }
                }
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure!).Throw();
            }

            private async ValueTask<Exception?> CleanupResponseAsync()
            {
                if (_cleanupDone) return null;
                Exception? failure = null;
                try { _responseCancellation?.Cancel(); }
                catch (Exception error) { failure = error; }
                if (_pendingChain is { } pendingChain)
                {
                    _pendingChain = null;
                    try { CaptureResult(await pendingChain.ConfigureAwait(false)); }
                    catch (Exception error) { failure = PhysicalTaskFailureException.CombineRaw(failure, error); }
                }
                if (_pendingInvocation is { } pendingInvocation)
                {
                    _pendingInvocation = null;
                    try { CaptureResult(await pendingInvocation.ConfigureAwait(false)); }
                    catch (Exception error) { failure = PhysicalTaskFailureException.CombineRaw(failure, error); }
                }
                if (_pendingMove is { } pendingMove)
                {
                    _pendingMove = null;
                    try { _ = await pendingMove.ConfigureAwait(false); }
                    catch (Exception error) { failure = PhysicalTaskFailureException.CombineRaw(failure, error); }
                }
                var enumerator = _resultEnumerator;
                if (!_enumeratorAcquisitionAttempted && enumerator is null && _resultStream is { } lateStream)
                {
                    try
                    {
                        _enumeratorAcquisitionAttempted = true;
                        enumerator = lateStream.GetAsyncEnumerator(_responseCancellation?.Token ?? invocationCancellation);
                    }
                    catch (Exception error) { failure = PhysicalTaskFailureException.CombineRaw(failure, error); }
                }
                if (enumerator is not null)
                {
                    try { await enumerator.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error) { failure = PhysicalTaskFailureException.CombineRaw(failure, error); }
                }
                // Always reconcile the original terminal, even if an interceptor returned a
                // replacement/nonforwarding enumerable or GetAsyncEnumerator/Dispose threw.
                if (_coreSink is { } sink)
                {
                    var claimed = false;
                    try { await sink.DisposeOriginalLeaseAsync(_coreLease, ref claimed).ConfigureAwait(false); }
                    catch (Exception error) { failure = PhysicalTaskFailureException.CombineRaw(failure, PhysicalTaskFailureException.GetRaw(error)); }
                }
                if (_coreOrigin is { } origin)
                {
                    if (_originSupervised)
                        await origin.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); // Completion-only, no duplicate Duplex/legacy origin reporting.
                    else
                    {
                        try { await origin.ConfigureAwait(false); }
                        catch (Exception error) { failure = PhysicalTaskFailureException.CombineRaw(failure, PhysicalTaskFailureException.GetRaw(error)); }
                    }
                }
                // Acceptance is not proof that ContinueWith attached. Only a tracking throw
                // retains this exact receipt; after actual origin exit the fallback performs
                // the supervisor's sole outcome classification and exact-identity removal.
                var failedAttachment = _originAttachmentFailureReceipt;
                _originAttachmentFailureReceipt = null;
                if (failedAttachment is not null)
                {
                    try { _client.CompleteAcceptedStreamSupervision(failedAttachment); }
                    catch (Exception error) { failure = PhysicalTaskFailureException.CombineRaw(failure, error); }
                }
                try { _enumerationCancellationRegistration.Dispose(); }
                catch (Exception error) { failure = PhysicalTaskFailureException.CombineRaw(failure, error); }
                _enumerationCancellationRegistration = default;
                try { _responseCancellation?.Dispose(); }
                catch (Exception error) { failure = PhysicalTaskFailureException.CombineRaw(failure, error); }
                _pendingInvocation = null; _pendingChain = null; _pendingMove = null; _resultEnumerator = null;
                _resultStream = null; _coreSink = null; _coreOrigin = null; _responseCancellation = null;
                _cleanupDone = true;
                return failure;
            }
        }
    }
}
