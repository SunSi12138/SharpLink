namespace SharpLink.UnitTests.Runtime;

[NotInParallel("dispatcher-pool")]
public class PooledAsyncStreamDispatcherPublicationReceiptTests
{
    [Test]
    public async Task WrongLeaseAndRecycledLeaseMustNotPublish()
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var original = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var originalLease = original.RetainForRegistration();
        var receipt = (IStreamSinkPublicationReceipt)original;
        Ensure(!receipt.TryRetainSinkPublication(originalLease + 4, out _),
            "a different generation must not acquire publication ownership");
        var disposition = StreamSinkPublicationDisposition.NotPublished;
        receipt.PublishSinkTerminal(new Exception("wrong lease"), false, originalLease + 4, ref disposition);
        Ensure(disposition == StreamSinkPublicationDisposition.NotPublished,
            "a wrong generation must not report terminal publication");
        original.Complete(exception: null);
        await original.DisposeAsync();
        original.ReleaseRegistrationRetention(originalLease);

        var recycled = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        Ensure(ReferenceEquals(original, recycled), "this test must exercise the same pooled object");
        var recycledLease = recycled.RetainForRegistration();
        Ensure(!receipt.TryRetainSinkPublication(originalLease, out _),
            "a stale lease must not retain a later rental of the same object");
        receipt.PublishSinkTerminal(new Exception("stale lease"), false, originalLease, ref disposition);
        Ensure(disposition == StreamSinkPublicationDisposition.NotPublished,
            "pooled object identity is not proof of delivery to its original consumer");
        PublishWithRetention(receipt, null, false, recycledLease, ref disposition);
        Ensure(disposition == StreamSinkPublicationDisposition.Published,
            "the stale attempt must not close the new generation");
        await recycled.DisposeAsync();
        recycled.ReleaseRegistrationRetention(recycledLease);
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
    }

    [Test]
    public async Task ReuseBetweenAcquireChecksMustRejectTheOriginalLease()
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var lease = dispatcher.RetainForRegistration();
        var receipt = (IStreamSinkPublicationReceipt)dispatcher;
        PooledAsyncStreamDispatcher<int>? recycled = null;
        long recycledLease = 0;
        dispatcher.SetBeforeProducerOperationAcquireForTests(() =>
        {
            dispatcher.SetBeforeProducerOperationAcquireForTests(null);
            dispatcher.Complete(exception: null);
            dispatcher.DisposeAsync().GetAwaiter().GetResult();
            dispatcher.ReleaseRegistrationRetention(lease);
            recycled = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
            recycledLease = recycled.RetainForRegistration();
        });
        Ensure(!receipt.TryRetainSinkPublication(lease, out _),
            "the post-increment generation check must reject an intervening rental");
        Ensure(ReferenceEquals(dispatcher, recycled), "the acquire race must reuse the original object");
        var disposition = StreamSinkPublicationDisposition.NotPublished;
        PublishWithRetention(receipt, null, false, recycledLease, ref disposition);
        Ensure(disposition == StreamSinkPublicationDisposition.Published,
            "failed old acquisition must not corrupt the new lease's operation count");
        await dispatcher.DisposeAsync();
        dispatcher.ReleaseRegistrationRetention(recycledLease);
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
    }

    [Test]
    public async Task DuplicateTerminalRequiresTheExactPrimaryAndTerminalKind()
    {
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var lease = dispatcher.RetainForRegistration();
        var receipt = (IStreamSinkPublicationReceipt)dispatcher;
        var primary = new Exception("primary");
        Ensure(receipt.TryRetainSinkPublication(lease, out var retained), "retain original lease");
        try
        {
            var disposition = StreamSinkPublicationDisposition.NotPublished;
            receipt.PublishSinkTerminal(primary, false, lease, ref disposition);
            Ensure(disposition == StreamSinkPublicationDisposition.Published, "first terminal must publish");
            disposition = StreamSinkPublicationDisposition.NotPublished;
            receipt.PublishSinkTerminal(primary, false, lease, ref disposition);
            Ensure(disposition == StreamSinkPublicationDisposition.Published, "exact duplicate can prove publication");
            receipt.PublishSinkTerminal(new Exception("primary"), false, lease, ref disposition);
            Ensure(disposition == StreamSinkPublicationDisposition.NotPublished, "equal text is not exact primary identity");
            receipt.PublishSinkTerminal(primary, true, lease, ref disposition);
            Ensure(disposition == StreamSinkPublicationDisposition.NotPublished, "another terminal kind is not an exact duplicate");
        }
        finally
        {
            receipt.ReleaseSinkPublication(retained);
            await dispatcher.DisposeAsync();
            dispatcher.ReleaseRegistrationRetention(lease);
        }
    }

    [Test]
    public async Task DuplicateLocalAbortMustNotEraseTheWinningCreditSnapshot()
    {
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var lease = dispatcher.RetainForRegistration();
        var receipt = (IStreamSinkPublicationReceipt)dispatcher;
        var primary = new Exception("local primary");
        var credit = 0;
        dispatcher.SetBytesConsumedCallback((_, _, bytes) => credit += bytes, 1, 0);
        await dispatcher.DispatchAsync(Encode(42));
        var disposition = StreamSinkPublicationDisposition.NotPublished;
        PublishWithRetention(receipt, primary, true, lease, ref disposition);
        PublishWithRetention(receipt, primary, true, lease, ref disposition);
        Ensure(disposition == StreamSinkPublicationDisposition.Published, "exact local duplicate must keep publication");
        ((IStreamLocalAbortDispatcher)dispatcher).RetireLocalAbortBuffer();
        Ensure(credit == 4, "a losing local claimant must not clear the first publisher's callback");
        await dispatcher.DisposeAsync();
        dispatcher.ReleaseRegistrationRetention(lease);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SignalFailureAfterTerminalStoreMustKeepPublished(bool localAbort)
    {
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var lease = dispatcher.RetainForRegistration();
        var receipt = (IStreamSinkPublicationReceipt)dispatcher;
        var signalFailure = new Exception("terminal Signal failure");
        dispatcher.SetBeforeTerminalSignalForTests(() => throw signalFailure);
        Ensure(receipt.TryRetainSinkPublication(lease, out var retained), "retain publication lease");
        var disposition = StreamSinkPublicationDisposition.NotPublished;
        Exception? observed = null;
        try
        {
            receipt.PublishSinkTerminal(new Exception("primary"), localAbort, lease, ref disposition);
        }
        catch (Exception exception)
        {
            observed = exception;
        }
        finally
        {
            dispatcher.SetBeforeTerminalSignalForTests(null);
            // Publication and local-abort cleanup each own an independently released retention.
            try
            {
                if (localAbort)
                    ((IStreamLocalAbortDispatcher)dispatcher).RetireLocalAbortBuffer();
            }
            finally
            {
                receipt.ReleaseSinkPublication(retained);
            }
        }
        Ensure(ReferenceEquals(observed, signalFailure), "notification failure must retain its original identity");
        Ensure(disposition == StreamSinkPublicationDisposition.Published,
            "a post-store Signal failure must not erase the caller-owned publication receipt");
        await dispatcher.DisposeAsync();
        dispatcher.ReleaseRegistrationRetention(lease);
    }

    [Test]
    public async Task DispatchCloseCallbackFailureMustKeepPublished()
    {
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var lease = dispatcher.RetainForRegistration();
        var receipt = (IStreamSinkPublicationReceipt)dispatcher;
        var callbackFailure = new Exception("dispatch-state Close callback");
        ((IStreamDispatchLease)dispatcher).BindDispatchState(new ThrowOnceCloseState(callbackFailure));
        Ensure(receipt.TryRetainSinkPublication(lease, out var retained), "retain publication lease");
        var disposition = StreamSinkPublicationDisposition.NotPublished;
        Exception? observed = null;
        try
        {
            receipt.PublishSinkTerminal(new Exception("primary"), false, lease, ref disposition);
        }
        catch (Exception exception)
        {
            observed = exception;
        }
        finally
        {
            receipt.ReleaseSinkPublication(retained);
        }
        Ensure(ReferenceEquals(observed, callbackFailure), "real dispatch-state callback failure must escape unchanged");
        Ensure(disposition == StreamSinkPublicationDisposition.Published,
            "a throwing dispatch-state Close follows the actual sink publication");
        await dispatcher.DisposeAsync();
        dispatcher.ReleaseRegistrationRetention(lease);
    }

    [Test]
    public async Task CreditFailureMustNotEraseReceiptOrStrandAbortRetention()
    {
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var lease = dispatcher.RetainForRegistration();
        var receipt = (IStreamSinkPublicationReceipt)dispatcher;
        var creditFailure = new Exception("credit callback");
        var returned = 0;
        dispatcher.SetAfterReturnTransitionForTests(() => returned++);
        dispatcher.SetBytesConsumedCallback((_, _, _) => throw creditFailure, 1, 0);
        await dispatcher.DispatchAsync(Encode(42));
        var disposition = StreamSinkPublicationDisposition.NotPublished;
        PublishWithRetention(receipt, new Exception("primary"), true, lease, ref disposition);
        Exception? observed = null;
        try
        {
            ((IStreamLocalAbortDispatcher)dispatcher).RetireLocalAbortBuffer();
        }
        catch (Exception exception)
        {
            observed = exception;
        }
        Ensure(ReferenceEquals(observed, creditFailure), "credit failure must remain independently reportable");
        Ensure(disposition == StreamSinkPublicationDisposition.Published, "credit cleanup is not sink delivery");
        await dispatcher.DisposeAsync();
        dispatcher.ReleaseRegistrationRetention(lease);
        Ensure(returned == 1, "a throwing credit callback must not strand the local-abort retention");
    }

    [Test]
    public async Task OriginalConsumerAbandonmentHasAnExplicitReceiptBeforeOwnerFailure()
    {
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var lease = dispatcher.RetainForRegistration();
        var receipt = (IStreamSinkPublicationReceipt)dispatcher;
        var disposition = StreamSinkPublicationDisposition.NotPublished;
        var ownerFailure = new Exception("abandonment owner callback");
        dispatcher.SetConsumerAbandonedCallback(_ =>
        {
            Ensure(receipt.TryRetainSinkPublication(lease, out var retained),
                "original Disposing lease must remain retainable inside abandonment callback");
            try
            {
                receipt.PublishSinkTerminal(new OperationCanceledException(), false, lease, ref disposition);
            }
            finally
            {
                receipt.ReleaseSinkPublication(retained);
            }
            throw ownerFailure;
        }, 1);
        Exception? observed = null;
        try
        {
            await dispatcher.DisposeAsync();
        }
        catch (Exception exception)
        {
            observed = exception;
        }
        Ensure(ReferenceEquals(observed, ownerFailure), "owner callback failure remains distinct from abandonment");
        Ensure(disposition == StreamSinkPublicationDisposition.PublishedConsumerAbandonment,
            "only an exact original abandoned consumer earns the abandonment disposition");
        dispatcher.ReleaseRegistrationRetention(lease);
        Ensure(!receipt.TryRetainSinkPublication(lease, out _), "returned lease cannot prove abandonment again");
    }

    private static void PublishWithRetention(
        IStreamSinkPublicationReceipt receipt,
        Exception? error,
        bool localAbort,
        long expectedLease,
        ref StreamSinkPublicationDisposition disposition)
    {
        Ensure(receipt.TryRetainSinkPublication(expectedLease, out var retained), "retain exact publication lease");
        try
        {
            receipt.PublishSinkTerminal(error, localAbort, expectedLease, ref disposition);
        }
        finally
        {
            receipt.ReleaseSinkPublication(retained);
        }
    }

    private sealed class ThrowOnceCloseState(Exception failure) : IStreamDispatchState
    {
        private bool _closed;

        public bool HasActiveDispatches => false;

        public bool IsDetached => true;

        public ValueTask WaitForDispatchesDrainedAsync() => ValueTask.CompletedTask;

        public ValueTask WaitForDetachedAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void Close()
        {
            if (_closed)
                return;
            _closed = true;
            throw failure;
        }
    }

    private static ReadOnlySequence<byte> Encode(int value)
    {
        var writer = new ArrayBufferWriter<byte>();
        Int32Codec.Instance.Serialize(in value, writer);
        return new ReadOnlySequence<byte>(writer.WrittenMemory);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
