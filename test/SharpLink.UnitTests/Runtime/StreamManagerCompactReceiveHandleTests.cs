using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel("route-footprint")]
public sealed class StreamManagerCompactReceiveHandleTests
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public void RouteEntryMustNotDuplicateTheFullReceiveLease()
    {
        var type = typeof(StreamManager).GetNestedType("DispatcherEntry", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing route entry type.");
        Require(type.GetFields(InstanceMembers).All(field =>
                field.FieldType != typeof(StreamFlowController.ResolvedReceiveCreditLease)),
            "route entry must not store duplicate full receive-lease key metadata");
    }

    [Test]
    [Arguments(long.MinValue, (ushort)0)]
    [Arguments(long.MaxValue, (ushort)7)]
    [Arguments(-17L, ushort.MaxValue)]
    public async Task RouteLeaseMustKeepFullWidthKeyAndOwnershipAfterCallbackDetachment(long requestId, ushort streamId)
    {
        var controller = new StreamFlowController(8, 8, 1024, 1);
        var expected = default(StreamFlowController.ResolvedReceiveCreditLease);
        var terminals = 0;
        var returnedCredit = 0;
        void Accept(in StreamFlowController.ResolvedReceiveCreditLease lease, int bytes)
        {
            CheckLease(lease, expected, requestId, streamId);
            controller.AcceptReceived(in lease, bytes);
        }
        void Consumed(in StreamFlowController.ResolvedReceiveCreditLease lease, int bytes)
            => returnedCredit += controller.RecordConsumed(in lease, bytes);
        void Terminal(StreamFlowController.ResolvedReceiveCreditLease lease)
        {
            CheckLease(lease, expected, requestId, streamId);
            terminals++;
            returnedCredit += controller.FlushConsumed(in lease);
        }
        var manager = new StreamManager(new RuntimeConcurrencyOptions(), null, null, null, 1, null,
            controller.ResolveReceiveCreditLease, Accept, Consumed, Terminal);
        var dispatcher = new CapturingDispatcher();
        try
        {
            manager.Register(requestId, streamId, dispatcher);
            expected = dispatcher.Lease;
            Require(expected.IsResolved, "registration must install receive ownership");
            await manager.DispatchChunkAsync(requestId, streamId,
                new ReadOnlySequence<byte>(new byte[] { 1 }));
            // The entry must not obtain its terminal identity from the mutable dispatcher.
            dispatcher.ClearCallback();
            CheckLease(ReadEntryLease(dispatcher.State, requestId, streamId), expected, requestId, streamId);
            manager.Unregister(requestId, streamId);
            Require(terminals == 1 && returnedCredit == 1 && manager.ActiveStreamCount == 0,
                "independent route ownership must publish the exact old key and final credit once");

            var next = controller.ResolveReceiveCreditLease(requestId, streamId);
            Require(next.Generation != expected.Generation,
                "same-key admission requires retirement of the old receive generation");
            controller.AcceptReceived(in next, 8);
            _ = controller.RecordConsumed(in next, 8);
            _ = controller.FlushConsumed(in next);
        }
        finally
        {
            manager.CompleteAll(new OperationCanceledException("fixture cleanup"));
            controller.Complete(new OperationCanceledException("fixture cleanup"));
        }
    }

    [Test]
    public void RouteRetainsOriginalGenerationAfterStateReuse()
    {
        var controller = new StreamFlowController(8, 8, 1024, 1);
        var manager = new StreamManager(new RuntimeConcurrencyOptions(), null, null, null, 1, null,
            controller.ResolveReceiveCreditLease, resolvedStreamCompleted:
            lease => { _ = controller.FlushConsumed(in lease); });
        var dispatcher = new CapturingDispatcher();
        try
        {
            manager.Register(9811, 7, dispatcher);
            var original = ReadEntryLease(dispatcher.State, 9811, 7);
            manager.Unregister(9811, 7);
            var replacement = controller.ResolveReceiveCreditLease(9812, 19);
            Require(ReferenceEquals(original.State, replacement.State) &&
                    original.Generation != replacement.Generation,
                "the control must reuse the same pooled receive state under another key");
            var retained = ReadEntryLease(dispatcher.State, 9811, 7);
            CheckLease(retained, original, 9811, 7);
            Require(controller.RecordConsumed(in retained, 1) == 0 && controller.FlushConsumed(in retained) == 0,
                "stale route bookkeeping cannot consume or close the replacement generation");
            Exception? rejected = null;
            try { controller.AcceptReceived(in retained, 1); }
            catch (Exception error) { rejected = error; }
            Require(rejected is SharpLinkException, "the old route cannot debit the reused state");
            controller.AcceptReceived(in replacement, 8);
            _ = controller.RecordConsumed(in replacement, 8);
            _ = controller.FlushConsumed(in replacement);
        }
        finally
        {
            manager.CompleteAll(new OperationCanceledException("fixture cleanup"));
            controller.Complete(new OperationCanceledException("fixture cleanup"));
        }
    }

    [Test]
    public void RouteHandleCannotLoseItsControllerIdentity()
    {
        var first = new StreamFlowController(8, 8, 1024, 1);
        var second = new StreamFlowController(8, 8, 1024, 1);
        var manager = new StreamManager(new RuntimeConcurrencyOptions(), null, null, null, 1, null,
            first.ResolveReceiveCreditLease, resolvedStreamCompleted:
            lease => { _ = first.FlushConsumed(in lease); });
        var dispatcher = new CapturingDispatcher();
        try
        {
            manager.Register(9813, 7, dispatcher);
            var captured = ReadEntryLease(dispatcher.State, 9813, 7);
            var other = second.ResolveReceiveCreditLease(9813, 7);
            Require(captured.Generation == other.Generation && ReferenceEquals(captured.Owner, first),
                "the foreign-controller control must share key and numeric generation");
            Exception? rejected = null;
            try { second.AcceptReceived(in captured, 1); }
            catch (Exception error) { rejected = error; }
            Require(rejected is SharpLinkException, "controller identity cannot be inferred from key or generation");
            second.AcceptReceived(in other, 8);
            _ = second.RecordConsumed(in other, 8);
            _ = second.FlushConsumed(in other);
            manager.Unregister(9813, 7);
        }
        finally
        {
            manager.CompleteAll(new OperationCanceledException("fixture cleanup"));
            first.Complete(new OperationCanceledException("fixture cleanup"));
            second.Complete(new OperationCanceledException("fixture cleanup"));
        }
    }

    [Test]
    public void UnresolvedRouteMustNotInventReceiveOwnership()
    {
        var manager = new StreamManager();
        var dispatcher = new CapturingDispatcher();
        manager.Register(9814, 7, dispatcher);
        try
        {
            var lease = ReadEntryLease(dispatcher.State, 9814, 7);
            Require(!lease.IsResolved && lease.Owner is null && lease.State is null && lease.Generation == 0,
                "routes without a controller must remain unresolved");
        }
        finally
        {
            manager.Unregister(9814, 7);
        }
    }

    [Test]
    [Arguments((ushort)0)]
    [Arguments((ushort)7)]
    public void FlowEnabledRouteLifecycleAllocationProbe(ushort streamId)
    {
        const int iterations = 10000;
        const long requestId = 9815;
        var controller = new StreamFlowController(8, 8, 1024, 1);
        void Consumed(in StreamFlowController.ResolvedReceiveCreditLease lease, int bytes)
            => _ = controller.RecordConsumed(in lease, bytes);
        var manager = new StreamManager(new RuntimeConcurrencyOptions(), null, null, null, 1, null,
            controller.ResolveReceiveCreditLease, resolvedBytesConsumed: Consumed,
            resolvedStreamCompleted: lease => { _ = controller.FlushConsumed(in lease); });
        var dispatcher = new StatelessConsumptionDispatcher();
        try
        {
            for (var i = 0; i < 2048; i++)
            {
                manager.Register(requestId, streamId, dispatcher);
                manager.Unregister(requestId, streamId);
            }
            var rows = new string[4];
            rows[0] = "repeat,bytes_per_route";
            for (var repeat = 0; repeat < 3; repeat++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < iterations; i++)
                {
                    manager.Register(requestId, streamId, dispatcher);
                    manager.Unregister(requestId, streamId);
                }
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Require(allocated > 0 && manager.ActiveStreamCount == 0,
                    "flow-enabled probe must measure complete real route lifecycles");
                rows[repeat + 1] = string.Create(CultureInfo.InvariantCulture,
                    $"{repeat},{allocated / (double)iterations:F6}");
            }
            var directory = Environment.GetEnvironmentVariable("SHARPLINK_ROUTE_FOOTPRINT_DIRECTORY");
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                File.WriteAllLines(Path.Combine(directory, $"flow-route-{streamId}.csv"), rows);
            }
            Console.WriteLine($"Flow-enabled route footprint {streamId}: {string.Join("; ", rows)}");
        }
        finally
        {
            manager.CompleteAll(new OperationCanceledException("fixture cleanup"));
            controller.Complete(new OperationCanceledException("fixture cleanup"));
        }
    }

    private static StreamFlowController.ResolvedReceiveCreditLease ReadEntryLease(
        IStreamDispatchState entry, long requestId, ushort streamId)
    {
        var type = entry.GetType();
        if (type.GetMethod("GetReceiveCreditLease", InstanceMembers) is { } get)
            return get.CreateDelegate<Func<long, ushort, StreamFlowController.ResolvedReceiveCreditLease>>(entry)
                (requestId, streamId);
        // The identical regression file also builds against the unchanged full-lease control.
        var property = type.GetProperty("ReceiveCreditLease", InstanceMembers)
            ?? throw new InvalidOperationException("Missing route receive identity accessor.");
        return (StreamFlowController.ResolvedReceiveCreditLease)property.GetValue(entry)!;
    }

    private static void CheckLease(StreamFlowController.ResolvedReceiveCreditLease actual,
        StreamFlowController.ResolvedReceiveCreditLease expected, long requestId, ushort streamId)
    {
        Require(ReferenceEquals(actual.Owner, expected.Owner) && ReferenceEquals(actual.State, expected.State) &&
                actual.Generation == expected.Generation && actual.RequestId == requestId && actual.StreamId == streamId,
            "route identity lost controller, state, generation or full-width key metadata");
    }

    private sealed class CapturingDispatcher : IStreamConsumptionAwareDispatcher, IStreamDispatchLease
    {
        private ResolvedStreamBytesCallback? _consumed;
        internal StreamFlowController.ResolvedReceiveCreditLease Lease;
        internal IStreamDispatchState State { get; private set; } = null!;
        public void BindDispatchState(IStreamDispatchState state) => State = state;
        public void OnDispatchesDrained() { }
        public ValueTask DispatchAcquiredAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
            => DispatchAsync(payload, encodedByteCount);
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => DispatchAsync(payload, (int)payload.Length);
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
        {
            _consumed?.Invoke(in Lease, encodedByteCount);
            return ValueTask.CompletedTask;
        }
        public void Complete(bool isError, string? errorMessage) { }
        public void Complete(Exception? exception) { }
        public void SetBytesConsumedCallback(Action<long, ushort, int>? callback, long requestId, ushort streamId) { }
        public bool TrySetResolvedBytesConsumedCallback(ResolvedStreamBytesCallback? callback,
            in StreamFlowController.ResolvedReceiveCreditLease lease)
        {
            _consumed = callback;
            Lease = lease;
            return true;
        }
        internal void ClearCallback()
        {
            _consumed = null;
            Lease = default;
        }
    }

    private sealed class StatelessConsumptionDispatcher : IStreamConsumptionAwareDispatcher
    {
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount) => ValueTask.CompletedTask;
        public void Complete(bool isError, string? errorMessage) { }
        public void Complete(Exception? exception) { }
        public void SetBytesConsumedCallback(Action<long, ushort, int>? callback, long requestId, ushort streamId) { }
        public bool TrySetResolvedBytesConsumedCallback(ResolvedStreamBytesCallback? callback,
            in StreamFlowController.ResolvedReceiveCreditLease lease) => true;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
