using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel("dispatcher-pool")]
public sealed class PooledAsyncStreamDispatcherResolvedRetentionTests
{
    [Test]
    [Arguments("_resolvedBytesConsumed")]
    [Arguments("_receiveCreditLease")]
    [Arguments("_localAbortResolvedBytesConsumed")]
    [Arguments("_localAbortReceiveCreditLease")]
    public async Task RetentionOracleShouldDetectEachResolvedReferenceIndependently(string fieldName)
    {
        PooledAsyncStreamDispatcher<string?>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<string?>.Rent(default, StringCodec.Instance);
        var controller = new StreamFlowController(16, 16, 1024, 1);
        var receiveLease = controller.ResolveReceiveCreditLease(42, 1);
        var marker = new object();
        void Consumed(in StreamFlowController.ResolvedReceiveCreditLease lease, int bytes)
        {
            GC.KeepAlive(marker);
            _ = controller.RecordConsumed(in lease, bytes);
        }

        var field = typeof(PooledAsyncStreamDispatcher<string?>).GetField(
            fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing retention field {fieldName}.");
        try
        {
            dispatcher.Complete(exception: null);
            await dispatcher.DisposeAsync();
            Require(PooledAsyncStreamDispatcher<string?>.RetainedCountForTests == 1 &&
                    !dispatcher.HasRetainedReferencesForTests,
                "the oracle control must begin with one clean static-pool entry");

            // Inject exactly one reference into an otherwise clean pooled object. This
            // fails if the oracle forgets that field, even when all other cleanup is correct.
            object retained = field.FieldType == typeof(ResolvedStreamBytesCallback)
                ? (ResolvedStreamBytesCallback)Consumed
                : receiveLease;
            field.SetValue(dispatcher, retained);
            Require(dispatcher.HasRetainedReferencesForTests,
                $"retention oracle ignored {fieldName}");
            field.SetValue(dispatcher, null);
            Require(!dispatcher.HasRetainedReferencesForTests,
                $"clearing {fieldName} must restore the clean-pool oracle");
        }
        finally
        {
            field.SetValue(dispatcher, null);
            dispatcher.Complete(exception: null);
            await dispatcher.DisposeAsync();
            _ = controller.FlushConsumed(in receiveLease);
            PooledAsyncStreamDispatcher<string?>.ClearPoolForTests();
        }
    }

    [Test]
    public async Task LocalAbortPoolReturnShouldClearResolvedCreditSnapshot()
    {
        PooledAsyncStreamDispatcher<string?>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<string?>.Rent(default, StringCodec.Instance);
        var controller = new StreamFlowController(16, 16, 1024, 1);
        var receiveLease = controller.ResolveReceiveCreditLease(43, 1);
        var marker = new object();
        var creditedBytes = 0;
        void Consumed(in StreamFlowController.ResolvedReceiveCreditLease lease, int bytes)
        {
            GC.KeepAlive(marker);
            Require(lease.Generation == receiveLease.Generation,
                "local-abort credit must target the captured receive generation");
            _ = controller.RecordConsumed(in lease, bytes);
            creditedBytes += bytes;
        }

        var localAbort = (IStreamLocalAbortDispatcher)dispatcher;
        try
        {
            Require(dispatcher.TrySetResolvedBytesConsumedCallback(Consumed, in receiveLease),
                "the local-abort fixture must install resolved receive ownership");
            var packet = new ArrayBufferWriter<byte>();
            var value = "retention";
            StringCodec.Instance.Serialize(in value, packet);
            controller.AcceptReceived(in receiveLease, packet.WrittenCount);
            await dispatcher.DispatchAsync(new ReadOnlySequence<byte>(packet.WrittenMemory));

            localAbort.CompleteLocalAbort(new OperationCanceledException("local-abort retention control"));
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var callback = typeof(PooledAsyncStreamDispatcher<string?>).GetField(
                "_localAbortResolvedBytesConsumed", flags)!.GetValue(dispatcher);
            var captured = (StreamFlowController.ResolvedReceiveCreditLease)typeof(PooledAsyncStreamDispatcher<string?>)
                .GetField("_localAbortReceiveCreditLease", flags)!.GetValue(dispatcher)!;
            Require(callback is ResolvedStreamBytesCallback &&
                    ReferenceEquals(captured.Owner, controller) &&
                    ReferenceEquals(captured.State, receiveLease.State),
                "the local-abort snapshot must actually hold both strong references before retirement");

            // Normal callback detachment must not erase the outstanding abort snapshot.
            var emptyLease = default(StreamFlowController.ResolvedReceiveCreditLease);
            _ = dispatcher.TrySetResolvedBytesConsumedCallback(null, in emptyLease);
            localAbort.RetireLocalAbortBuffer();
            Require(creditedBytes == packet.WrittenCount,
                "the retained snapshot must return discarded credit exactly once");
            await dispatcher.DisposeAsync();
            Require(PooledAsyncStreamDispatcher<string?>.RetainedCountForTests == 1 &&
                    !dispatcher.HasRetainedReferencesForTests,
                "local-abort pool return must clear both resolved snapshots and every other root");
        }
        finally
        {
            localAbort.RetireLocalAbortBuffer();
            dispatcher.Complete(exception: null);
            await dispatcher.DisposeAsync();
            _ = controller.FlushConsumed(in receiveLease);
            PooledAsyncStreamDispatcher<string?>.ClearPoolForTests();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
