using System.Diagnostics.Metrics;
using System.Reflection;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public partial class PendingRequestTableTests
{
    private static readonly AsyncLocal<long?> PendingMetricProbeMeasurement = new();

    [Test]
    [NotInParallel]
    public async Task DeadlineScanCanCompleteAReusedPendingCallGeneration()
    {
        DrainPendingCallPool();
        using var timeProvider = new DeadlineReuseRaceTimeProvider();
        using var manager = CreateTable(1, owner: NoopPendingCallOwner.Instance, timeProvider: timeProvider);
        var oldOperation = manager.Rent(
            new Int32Codec(),
            PendingCallKind.Unary,
            RpcDeadline.FromTimestamp(10),
            CancellationToken.None,
            out var oldRequestId);
        var oldCall = GetOnlyPendingCall(manager);

        timeProvider.ArmOneExpiredRead();
        var scan = LongRunningTestWorker.Run(() => InvokeDeadlineScan(manager));
        RpcRequestOperation<int>? reusedOperation = null;
        long reusedRequestId = 0;
        try
        {
            Ensure(timeProvider.ExpiredReadEntered.Wait(RaceCoordinationTimeout),
                "deadline scan should stop inside the stale generation's expiry check");

            Ensure(manager.TryComplete(
                    oldRequestId,
                    PendingCallCompletionReason.ConnectionClosed,
                    new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "old generation completed")),
                "old generation should complete while the scan is paused");
            var oldFailure = await CaptureExceptionAsync(oldOperation.AsValueTask().AsTask());
            Ensure(oldFailure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
                "old generation should leave through the non-deadline completion path");

            // This test owns the process-wide PendingCall pool while NotInParallel. Starting from an
            // empty pool makes A the sole returned instance, so the next Rent must create B by
            // reusing the exact same pooled object rather than relying on a probabilistic rotation.
            reusedOperation = manager.Rent(
                new Int32Codec(),
                PendingCallKind.Unary,
                RpcDeadline.FromTimestamp(10_000),
                CancellationToken.None,
                out reusedRequestId);

            Ensure(ReferenceEquals(GetOnlyPendingCall(manager), oldCall),
                "request B should reuse the exact PendingCall instance captured by the stale scan");
            Ensure(reusedRequestId != oldRequestId, "reused object must represent a new request generation");
            Ensure(manager.Count == 1, "new generation should still be pending before the stale scan resumes");

            timeProvider.ReleaseExpiredRead.Set();
            await LongRunningTestWorker.JoinAsync(scan, RaceCoordinationTimeout);

            var reusedFailure = await CaptureExceptionAsync(reusedOperation.AsValueTask().AsTask());
            Ensure(reusedFailure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                "current scan should demonstrate the ABA bug by timing out the reused, non-expired generation");
            Ensure(manager.Count == 0, "the stale scan currently removes the reused generation");
        }
        finally
        {
            timeProvider.ReleaseExpiredRead.Set();
            await LongRunningTestWorker.JoinAsync(scan, RaceCoordinationTimeout);
            if (reusedOperation is not null && manager.Count != 0)
            {
                manager.TryComplete(reusedRequestId, PendingCallCompletionReason.ConnectionClosed);
                _ = await CaptureExceptionAsync(reusedOperation.AsValueTask().AsTask());
            }
        }
    }

    [Test]
    [NotInParallel]
    public async Task ThrowingPendingMetricOnRegistrationLeavesPublishedCallUnregistered()
    {
        using var listener = CreateThrowingPendingMetricListener();
        using var manager = CreateTable(1, owner: NoopPendingCallOwner.Instance);

        var failure = CapturePendingMetricException(1, () =>
        {
            _ = manager.Rent<int>(out _);
        });

        Ensure(failure is PendingMetricProbeException { Measurement: 1 },
            "the listener exception should escape the pending +1 measurement");
        Ensure(manager.Count == 1, "the slot is already published when the +1 callback throws");
        Ensure(manager.ActiveCount == 1, "the published registration still owns capacity");

        var call = GetOnlyPendingCall(manager);
        Ensure(GetPendingCallRegisteredState(call) == 0,
            "MarkRegistered is skipped after the metric callback throws");
        var requestId = GetPendingCallId(call);
        var operation = GetPendingInt32Operation(call);

        // Repair only the test fixture so disposal cannot enter WaitUntilRegistered forever.
        listener.Dispose();
        MarkPendingCallRegistered(call);
        Ensure(manager.TryComplete(requestId, PendingCallCompletionReason.ConnectionClosed),
            "repaired evidence fixture should cleanly remove the stranded slot");
        var completion = await CaptureExceptionAsync(operation.AsValueTask().AsTask());
        Ensure(completion is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
            "cleanup should complete the operation after repairing registration state");
        Ensure(manager.ActiveCount == 0, "cleanup should restore capacity after the listener is removed");
    }

    [Test]
    [NotInParallel]
    public async Task ThrowingPendingMetricOnReleaseLeaksCapacityAfterSlotRemoval()
    {
        using var listener = CreateThrowingPendingMetricListener();
        using var manager = CreateTable(1, owner: NoopPendingCallOwner.Instance);
        var operation = manager.Rent<int>(out var requestId);

        var failure = CapturePendingMetricException(-1, () => manager.TryComplete(
            requestId,
            PendingCallCompletionReason.ConnectionClosed,
            new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "release probe")));

        Ensure(failure is PendingMetricProbeException { Measurement: -1 },
            "the listener exception should escape the pending -1 measurement");
        Ensure(manager.Count == 0, "terminal completion removes the slot before capacity release");
        Ensure(manager.ActiveCount == 1, "the -1 metric exception skips ReleaseCapacity");
        ExpectResourceExhausted(() => manager.Rent<int>(out _));

        var completion = await CaptureExceptionAsync(operation.AsValueTask().AsTask());
        Ensure(completion is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
            "the operation itself is already terminal despite the leaked table capacity");

        // Repair only the test fixture so the evidence test does not leak accounting into disposal.
        listener.Dispose();
        InvokeReleaseCapacity(manager);
        Ensure(manager.ActiveCount == 0, "test cleanup should repair leaked capacity");
    }

    private static Exception? CapturePendingMetricException(long measurement, Action action)
    {
        var prior = PendingMetricProbeMeasurement.Value;
        PendingMetricProbeMeasurement.Value = measurement;
        try
        {
            return CaptureException(action);
        }
        finally
        {
            PendingMetricProbeMeasurement.Value = prior;
        }
    }

    private static MeterListener CreateThrowingPendingMetricListener()
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, currentListener) =>
        {
            if (instrument.Meter.Name == "SharpLink" && instrument.Name == "sharplink.requests.pending")
                currentListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>(static (instrument, measurement, _, _) =>
        {
            if (instrument.Meter.Name == "SharpLink" &&
                instrument.Name == "sharplink.requests.pending" &&
                PendingMetricProbeMeasurement.Value == measurement)
            {
                throw new PendingMetricProbeException(measurement);
            }
        });
        listener.Start();
        return listener;
    }

    private static void DrainPendingCallPool()
    {
        var pendingCallType = typeof(PendingRequestTable).GetNestedType("PendingCall", BindingFlags.NonPublic)
            ?? throw new Exception("cannot find PendingCall nested type");
        var pool = pendingCallType.GetField("Pool", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            ?? throw new Exception("cannot find PendingCall.Pool");
        var tryDequeue = pool.GetType().GetMethod("TryDequeue", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new Exception("cannot find PendingCall pool TryDequeue");
        var retainedCount = pendingCallType.GetField("s_retainedCount", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find PendingCall.s_retainedCount");

        while (true)
        {
            object?[] arguments = [null];
            if (!(bool)tryDequeue.Invoke(pool, arguments)!)
                break;
        }
        retainedCount.SetValue(null, 0);
    }

    private static object GetOnlyPendingCall(PendingRequestTable manager)
    {
        var slotsField = typeof(PendingRequestTable).GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find _slots field");
        var slots = (Array?)slotsField.GetValue(manager)
            ?? throw new Exception("pending slots have not been materialized");
        var call = slots.GetValue(0);
        return call ?? throw new Exception("expected one pending call in capacity-1 table");
    }

    private static int GetPendingCallRegisteredState(object call)
        => (int)(call.GetType().GetField("_registered", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(call) ?? throw new Exception("cannot read PendingCall._registered"));

    private static long GetPendingCallId(object call)
        => (long)(call.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(call) ?? throw new Exception("cannot read PendingCall.Id"));

    private static RpcRequestOperation<int> GetPendingInt32Operation(object call)
        => (RpcRequestOperation<int>)(call.GetType().GetProperty("Operation", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(call) ?? throw new Exception("cannot read PendingCall.Operation"));

    private static void MarkPendingCallRegistered(object call)
        => (call.GetType().GetMethod("MarkRegistered", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new Exception("cannot find PendingCall.MarkRegistered"))
            .Invoke(call, null);

    private static void InvokeDeadlineScan(PendingRequestTable manager)
        => (typeof(PendingRequestTable).GetMethod("ScanExpiredDeadlines", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find ScanExpiredDeadlines"))
            .Invoke(manager, null);

    private static void InvokeReleaseCapacity(PendingRequestTable manager)
        => (typeof(PendingRequestTable).GetMethod("ReleaseCapacity", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find ReleaseCapacity"))
            .Invoke(manager, null);

    private sealed class PendingMetricProbeException(long measurement)
        : Exception($"pending metric probe {measurement}")
    {
        public long Measurement { get; } = measurement;
    }

    private sealed class NoopPendingCallOwner : IPendingCallOwner
    {
        internal static NoopPendingCallOwner Instance { get; } = new();

        public void OnPendingCallRegistered()
        {
        }

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
        }

        public void OnProducerCancellationCallbackFailed(Exception exception)
        {
        }
    }

    private sealed class DeadlineReuseRaceTimeProvider : TimeProvider, IDisposable
    {
        private int _armed;
        private int _blocked;

        internal ManualResetEventSlim ExpiredReadEntered { get; } = new(initialState: false);
        internal ManualResetEventSlim ReleaseExpiredRead { get; } = new(initialState: false);

        public override long TimestampFrequency => 1;

        internal void ArmOneExpiredRead()
        {
            Volatile.Write(ref _armed, 1);
            Volatile.Write(ref _blocked, 0);
            ExpiredReadEntered.Reset();
            ReleaseExpiredRead.Reset();
        }

        public override long GetTimestamp()
        {
            if (Volatile.Read(ref _armed) != 0 &&
                Interlocked.CompareExchange(ref _blocked, 1, 0) == 0)
            {
                ExpiredReadEntered.Set();
                if (!ReleaseExpiredRead.Wait(RaceCoordinationTimeout))
                    throw new TimeoutException("deadline race probe was not released");
                return 20;
            }

            return 0;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
            => NoopTimer.Instance;

        public void Dispose()
        {
            ReleaseExpiredRead.Set();
            ExpiredReadEntered.Dispose();
            ReleaseExpiredRead.Dispose();
        }

        private sealed class NoopTimer : ITimer
        {
            internal static NoopTimer Instance { get; } = new();

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose()
            {
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
