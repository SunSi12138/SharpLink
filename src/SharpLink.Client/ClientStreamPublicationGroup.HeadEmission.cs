using System.Threading.Tasks.Sources;

namespace SharpLink.Client;

internal sealed partial class ClientStreamPublicationGroup
{
    private enum HeadEmissionOwner : byte { Empty, Prepared, Group, Caller, Closed }
    private Task<bool>? _headEmissionSource;
    private HeadEmissionOwner _headEmissionOwner;

    internal enum HeadEmissionKind : byte { NoObservation, SourceConsumed, PendingTerminalSelected }
    internal readonly record struct HeadEmissionDecision(HeadEmissionKind Kind, Task? Source, Exception? Failure);

    internal void PrepareHeadEmission(RpcSession session, long id, Task<bool> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            if (!IsAdopted || !HasProducerLifetime || !ReferenceEquals(Session, session) || RequestId != id ||
                _headEmissionOwner != HeadEmissionOwner.Empty || _finished)
                throw new InvalidOperationException("Head observation requires its exact live adopted publication owner.");
            _headEmissionSource = source;
            _headEmissionOwner = HeadEmissionOwner.Prepared;
        }
    }

    // Preparation already validated single ownership, before the irrevocable queue transfer.
    // Caller holds PendingCall.CompletionGate. No factories, validation or callbacks here.
    internal void CommitHeadEmissionUnderCompletionGate()
    {
        lock (_gate) _headEmissionOwner = HeadEmissionOwner.Group;
    }

    internal void RetireUnacceptedHeadEmission()
    {
        lock (_gate)
        {
            // Never clear an incomplete original even when finalization threw. FinishAsync
            // will retain/join that exact obligation rather than refund an uncertain owner.
            if (_headEmissionOwner != HeadEmissionOwner.Prepared || _headEmissionSource is not { IsCompleted: true } source)
                return;
            _ = source.Exception; // Exact refusal/abandon finalizer already owns its outcome.
            _headEmissionSource = null;
            _headEmissionOwner = HeadEmissionOwner.Closed;
        }
    }

    internal async ValueTask<HeadEmissionDecision> ObserveHeadEmissionAsync()
    {
        Task<bool> source;
        lock (_gate)
        {
            if (_headEmissionOwner is HeadEmissionOwner.Empty or HeadEmissionOwner.Closed)
                return default;
            if (_headEmissionOwner != HeadEmissionOwner.Group)
                throw new InvalidOperationException("Only an accepted head has emission readiness.");
            source = _headEmissionSource ?? throw new InvalidOperationException("The accepted head lost its original source.");
        }
        // This forwarding waiter conveys readiness only. The original source remains rooted;
        // observing waiter cancellation cannot turn it into an independent publication failure.
        await ((Task)source).WaitAsync(ProducerToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        lock (_gate)
        {
            if ((_logicalReceiptState & ReceiptMask) != ReceiptOpen)
                return new(HeadEmissionKind.PendingTerminalSelected, null, null);
        }
        if (!source.IsCompleted)
            throw new InvalidOperationException("Head readiness ended without a source outcome or selected pending terminal.");
        var outcome = await ReadHeadOutcomeAsync(source).ConfigureAwait(false);
        lock (_gate)
        {
            // Linearizes with TrySelectTerminal, including Selected before publication finishes.
            if ((_logicalReceiptState & ReceiptMask) != ReceiptOpen)
                return new(HeadEmissionKind.PendingTerminalSelected, null, null);
            if (_headEmissionOwner != HeadEmissionOwner.Group)
                throw new InvalidOperationException("The original head outcome was consumed twice.");
            _headEmissionOwner = HeadEmissionOwner.Caller;
            _headEmissionSource = null;
        }
        return new(HeadEmissionKind.SourceConsumed, source, outcome);
    }

    private static async ValueTask<Exception?> ReadHeadOutcomeAsync(Task source)
    {
        await source.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (source.IsFaulted)
        {
            var graph = source.Exception!;
            // Task.Exception adds an accessor envelope even for one actual cause. Preserve
            // that exact cause (including an intentional AggregateException); retain the
            // ordered envelope only when the original Task really has multiple siblings.
            return graph.InnerExceptions.Count == 1 ? graph.InnerExceptions[0] : graph;
        }
        if (!source.IsCanceled) return null;
        try { await source.ConfigureAwait(false); }
        catch (Exception cancellation) { return cancellation; } // Actual original Task, never its wait wrapper.
        throw new InvalidOperationException("A canceled original emission had no cancellation outcome.");
    }

    private async ValueTask<Exception?> JoinOwnedHeadEmissionAsync()
    {
        Task? source;
        lock (_gate)
        {
            source = _headEmissionOwner is HeadEmissionOwner.Group or HeadEmissionOwner.Prepared
                ? _headEmissionSource : null;
        }
        if (source is null) return null;
        var outcome = await ReadHeadOutcomeAsync(source).ConfigureAwait(false);
        lock (_gate)
        {
            _headEmissionSource = null;
            _headEmissionOwner = HeadEmissionOwner.Closed;
        }
        return outcome;
    }

    // One exceptional-only, nonpooled receipt. No field is added to a pooled operation and no
    // source Task/exception-reference comparison is used to infer error propagation ownership.
    internal abstract class HeadFailureReceipt
    {
        protected HeadFailureReceipt(IRpcOperation operation, long requestId, Exception failure, Task? consumedSource)
        {
            Operation = operation;
            RequestId = requestId;
            Failure = failure;
            ConsumedSource = consumedSource;
        }
        private IRpcOperation Operation { get; }
        private long RequestId { get; }
        protected Exception Failure { get; }
        // A strong actual-source receipt survives until original pending consumption completes.
        protected Task? ConsumedSource { get; }
        protected bool Selected { get; private set; }
        protected bool Published { get; private set; }
        protected Exception? TerminalFailure { get; private set; }

        internal void RecordSelection(IRpcOperation? operation, long id)
            => Selected = id == RequestId && ReferenceEquals(operation, Operation);

        internal void RecordPublication(IRpcOperation operation, long id)
            => Published = Selected && id == RequestId && ReferenceEquals(operation, Operation);

        internal void CompletePending(ClientConnection connection, ClientStreamPublicationGroup group)
        {
            var reason = Failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }
                ? PendingCallCompletionReason.DeadlineExceeded : PendingCallCompletionReason.SendFailure;
            try { connection.PendingCalls.CompleteProducingSetupFailure(group, Failure, reason, this); }
            catch (Exception independent) { TerminalFailure = independent; }
        }
    }

    internal sealed class HeadFailureReceipt<T> : HeadFailureReceipt, IValueTaskSource
    {
        private readonly RpcRequestOperation<T> _operation;
        private readonly short _version;
        private bool _consumptionStarted;
        private RpcRequestOperation<T>.FailureConsumption _consumption;

        internal HeadFailureReceipt(RpcRequestOperation<T> operation, long requestId, Exception failure, Task? consumedSource)
            : base(operation, requestId, failure, consumedSource)
        {
            _operation = operation;
            _version = operation.FailureObservationVersion;
        }

        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _operation.GetStatus(token);
        void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _operation.OnCompleted(continuation, state, token, flags);
        void IValueTaskSource.GetResult(short token)
        {
            if (_consumptionStarted)
                throw new InvalidOperationException("The original pending operation has one consumption owner.");
            _consumptionStarted = true;
            _consumption = _operation.ConsumeFailure(token);
        }

        internal async ValueTask<T> ObserveFailureAsync()
        {
            Exception? readinessFailure = null;
            try { await new ValueTask(this, _version).ConfigureAwait(false); }
            catch (Exception independent) { readinessFailure = independent; }
            Exception? failure;
            if (Published && _consumption.Consumed && _consumption.CoreFailure is not null &&
                _consumption.Status is ValueTaskSourceStatus.Faulted or ValueTaskSourceStatus.Canceled)
            {
                // This selected origin was actually published and this same core was consumed.
                // Its propagation is the caller result ONCE; pool-return is a different stage.
                failure = _consumption.CoreFailure;
            }
            else
            {
                failure = AddPhysical(Failure, _consumption.CoreFailure);
                if (Published && _consumption.Consumed && _consumption.CoreFailure is null)
                    failure = AddPhysical(failure, new InvalidOperationException("A published head failure lost its original pending outcome."));
            }
            failure = AddPhysical(failure, TerminalFailure);
            failure = AddPhysical(failure, readinessFailure);
            failure = AddPhysical(failure, _consumption.ReturnFailure);
            // Keep this exact source alive through actual original consumption, including a
            // throwing return action. No mutable receipt is left in the reusable operation.
            GC.KeepAlive(ConsumedSource);
            return await ValueTask.FromException<T>(failure!).ConfigureAwait(false);
        }
    }
}
