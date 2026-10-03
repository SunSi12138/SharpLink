// Client logical-invocation lifetime: static 1:1 call-shape specialization.
// of the client logical-invocation accountant
// (src/SharpLink.Client/SharpLinkClient.Invokers.cs, CompleteLogicalInvocation).
//
// Shape rule:
//   simple 1:1 shape -> the single physical attempt releases the logical invocation through the
//                       pending table's already-existing exactly-once completion hook
//                       (IPendingCallCompletionObserver), so a truly suspended unary call creates
//                       no outer `async` wrapper and allocates nothing for logical lifetime
//                       accounting.
//   every other shape -> the pre-existing outer wrapper is kept byte-for-byte unchanged:
//                       retry generation enabled, telemetry scope, interceptor pipeline,
//                       endpoint admission policy occupying the completion-observer slot,
//                       streaming, OneWay.
//
namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    // ---------------------------------------------------------------------------------------------
    // Exactly-once release of one logical invocation. _activeLogicalInvocations has no underflow
    // guard and drives SharpLinkMultiClusterClient.WaitForActiveCallsToDrainAsync, so every caller
    // of this member must be provably exactly-once.
    //
    // ---------------------------------------------------------------------------------------------
    /// <summary>
    /// Releases one logical invocation.
    /// </summary>
    internal void EndLogicalInvocation() => Interlocked.Decrement(ref _activeLogicalInvocations);

    // ---------------------------------------------------------------------------------------------
    // One cached observer instance per client, created lazily on the first specialized call. The
    // observer is stateless apart from its client reference, so the single instance is safely shared
    // by every connection, shard and concurrent attempt of the client. The steady-state lookup is a
    // single volatile read: no allocation, no lock, no per-call delegate or closure (the observer is
    // handed to the pending table directly as an interface reference), and no Interlocked on a shared
    // static. Only the very first specialized call per client takes the gate, so the observer is
    // created exactly once per client.
    // ---------------------------------------------------------------------------------------------
    private IPendingCallCompletionObserver? _logicalShapeObserver;
    private readonly object _logicalShapeObserverGate = new();

    private IPendingCallCompletionObserver GetOrCreateLogicalShapeObserver()
    {
        var observer = Volatile.Read(ref _logicalShapeObserver);
        if (observer is not null)
            return observer;

        lock (_logicalShapeObserverGate)
        {
            return _logicalShapeObserver ??= new LogicalShapeCompletionObserver(this);
        }
    }

    /// <summary>
    /// The one static call-shape predicate: it
    /// decides, for a unary call, whether this logical call consists of exactly one physical attempt
    /// and therefore may ride that attempt's completion hook instead of an outer wrapper.
    /// </summary>
    /// <remarks>
    /// It is a pure function of two per-call immutable values - the method descriptor (a readonly
    /// record struct) and the resolved call control (a readonly record struct copied per call) - so
    /// the plain entry point that skips the wrapper and the retry entry point that arms the observer
    /// evaluate it on identical inputs and cannot disagree.
    /// <list type="bullet">
    /// <item><description><c>method.Kind != RpcMethodKind.Unary || !method.IsIdempotent</c>: the
    /// retry entry point never retries such a call, so it is always one physical attempt.</description></item>
    /// <item><description><c>control.RetryGeneration is { Enabled: false }</c>: retry generation is
    /// captured for every unary + idempotent call by CaptureRetryGenerationForInvocation, and a
    /// disabled generation makes the retry entry point take its single-attempt core path.</description></item>
    /// </list>
    /// A null <c>RetryGeneration</c> is deliberately NOT specialized: it means the retry entry point
    /// would capture live client state itself, and this predicate must not guess that answer.
    /// <c>_endpointAdmissionPolicy</c> is deliberately NOT part of this predicate: it is a volatile
    /// field that runtime configuration and the circuit breaker replace at will, so a second read
    /// could contradict the wrapper decision already taken by the caller. The core method decides
    /// observer arming from the single value it reads when it registers the attempt, and reproduces
    /// the skipped wrapper when the slot is not free.
    /// </remarks>
    private static bool IsSimpleOneToOneUnaryShape(in RpcMethodDescriptor method, in ResolvedCallControl control)
        => method.Kind != RpcMethodKind.Unary
           || !method.IsIdempotent
           || control.RetryGeneration is { Enabled: false };

    /// <summary>
    /// Entry point for a plain (no telemetry scope,
    /// no interceptor pipeline) unary call whose static shape is a single physical attempt.
    /// </summary>
    /// <remarks>
    /// This is the only place that arms the cached shape observer (<c>specializeLogicalShape:
    /// true</c>), and it deliberately does not apply the outer logical wrapper: the release of the
    /// logical invocation belongs to that attempt's exactly-once completion observer, or - when
    /// nothing was ever registered - to the inline release in the core method's catch. The caller
    /// (InvokeUnaryAsync) increments <c>_activeLogicalInvocations</c> before calling here and must
    /// therefore skip <c>CompleteLogicalInvocation</c> for this shape.
    ///
    /// This is equivalent by construction to what InvokeUnaryWithOptionalRetryAsync would do for the
    /// same method and control: whenever the predicate above is true, that entry point returns
    /// exactly this core call (the reverse is not exact only for a null RetryGeneration, which the
    /// predicate deliberately refuses to specialize); this method merely also carries the arming
    /// decision that the outer call site already took.
    ///
    /// Note for audit: the completion of this shape is deliberately NOT detected with
    /// <c>invocation.IsCompleted</c>. An attempt can complete (and fire its observer) before this
    /// method even returns - a synchronous send failure or a concurrent connection close both do -
    /// so a completed ValueTask here means "the observer already released this logical call", not
    /// "nothing was registered". Decrementing inline on IsCompleted would double-release exactly
    /// those calls. The registered/not-registered boundary (attemptRegistered) is the only sound
    /// signal and is what the core method uses.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ValueTask<TResponse> InvokeUnarySimpleShapeAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        ResolvedCallControl control,
        CancellationToken cancellationToken)
        => InvokeUnaryCoreAsync(
            method,
            request,
            requestCodec,
            responseCodec,
            control,
            cancellationToken,
            specializeLogicalShape: true);

    /// <summary>
    /// Converts a failure of the specialized shape
    /// into the exception the call reports, without releasing the logical invocation and without
    /// throwing.
    /// </summary>
    /// <remarks>
    /// The caller decides separately who owns the release; an escape from here would reach
    /// InvokeUnaryAsync's catch, which would release the same logical invocation in addition to that
    /// owner. Arbitration and admission diagnostics are therefore contained here, the same way the
    /// pending table contains the diagnostics of its terminal path.
    /// </remarks>
    private static Exception ArbitrateShapeLogicalFailure(
        in ResolvedCallControl control,
        Exception exception,
        AttemptOutcomeState? outcome)
    {
        Exception reported;
        try
        {
            reported = ArbitrateLogicalCallFailure(control, exception);
        }
        catch (Exception arbitrationFailure)
        {
            // the failure is still reported to the caller as a
            // faulted ValueTask; a broken arbiter is reported instead of escaping, because the
            // release of the logical invocation was already decided by the caller.
            reported = arbitrationFailure;
        }

        if (outcome is not null)
        {
            try
            {
                outcome.CompleteLocalFailure(reported);
            }
            catch (Exception)
            {
                // admission diagnostics must never interrupt the
                // logical-invocation accounting that the caller already committed.
            }
        }

        return reported;
    }

    /// <summary>
    /// The cached observer that rides the pending
    /// table's exactly-once terminal transition (PendingRequestTable.CompleteTakenCall fires
    /// CompletionObserver exactly once per physical attempt, before the caller's ValueTask completes
    /// and before the PendingCall returns to its pool).
    /// </summary>
    private sealed class LogicalShapeCompletionObserver(SharpLinkClient client) : IPendingCallCompletionObserver
    {
        public void OnResponseObserved()
        {
            // never raised for Unary attempts - PendingRequestTable
            // only calls it for ServerStreaming/DuplexStreaming kinds, which never take this shape.
            // Kept as a no-op so this signal can never release a logical invocation.
        }

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
            => client.EndLogicalInvocation();
    }
}

