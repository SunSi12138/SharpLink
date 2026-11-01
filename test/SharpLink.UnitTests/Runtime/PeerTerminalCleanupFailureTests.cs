using System.Diagnostics.Metrics;

namespace SharpLink.UnitTests.Runtime;

public sealed class PeerTerminalCleanupFailureTests
{
    [Test]
    public void RemovedPeerTerminalAttemptsEveryOwnerDespiteTelemetryAndCompleteFailures()
    {
        var scope = new AsyncLocal<bool>();
        using var listener = ThrowOnRefund(scope);
        var terminalCalls = 0;
        var manager = new StreamManager(new RuntimeConcurrencyOptions(), null, null,
            (_, _) => { terminalCalls++; throw new InvalidOperationException("receive terminal failure"); });
        var child = new ThrowingChild();
        manager.Register(51, 1, child);
        scope.Value = true;
        Exception? failure = null;
        try { manager.CompletePeerStream(51, 1, null); }
        catch (Exception error) { failure = error; }
        finally { scope.Value = false; }
        Ensure(failure is AggregateException && manager.ActiveStreamCount == 0 && terminalCalls == 1 &&
            child.CompleteCount == 1 && child.DrainedCount == 1 && child.State!.IsDetached,
            "removed exact owner independently refunds scalar, metrics, Complete, terminal and detach");
        manager.CompleteAll(null);
        Ensure(manager.ActiveStreamCount == 0 && child.CompleteCount == 1, "later Session drain cannot double-clean removed owner");
    }

    [Test]
    public void UnregisterRefundAndTerminalSurviveThrowingConsumptionCallbackAndTelemetry()
    {
        var scope = new AsyncLocal<bool>();
        var telemetryHits = 0;
        using var listener = ThrowOnRefund(scope, () => Interlocked.Increment(ref telemetryHits));
        var terminalCalls = 0;
        var manager = new StreamManager(new RuntimeConcurrencyOptions(), null, null, (_, _) => terminalCalls++);
        var child = new ThrowingChild();
        manager.Register(52, 1, child);
        var callbackFailure = new InvalidOperationException("consumption callback clear failure");
        child.CallbackClearFailure = callbackFailure;
        scope.Value = true;
        Exception? failure = null;
        try { manager.Unregister(52, 1); }
        catch (Exception error) { failure = error; }
        finally { scope.Value = false; }
        // Telemetry observer failures are deliberately isolated by SharpLinkTelemetry.
        // Unregister does not Complete the dispatcher: the one callback-clear failure must
        // therefore retain its exact identity, not acquire a fabricated AggregateException.
        var observed = $"failure={failure?.GetType().FullName ?? "<null>"}; " +
            $"sameCallbackFailure={ReferenceEquals(failure, callbackFailure)}; " +
            $"telemetryHits={telemetryHits}; callbackClearAttempts={child.FailingCallbackClearAttempts}; " +
            $"active={manager.ActiveStreamCount}; terminals={terminalCalls}; " +
            $"drained={child.DrainedCount}; detached={child.State?.IsDetached}";
        if (!ReferenceEquals(failure, callbackFailure))
            throw new InvalidOperationException("Unregister must preserve only the exact callback-clear failure. " + observed, failure);
        Ensure(telemetryHits == 1 && child.FailingCallbackClearAttempts == 1,
            "Both throwing callbacks must actually be attempted exactly once. " + observed);
        Ensure(manager.ActiveStreamCount == 0 && terminalCalls == 1 &&
            child.DrainedCount == 1 && child.State is { IsDetached: true },
            "Unregister has no telemetry/callback early-return hole. " + observed);
    }

    [Test]
    public void RetainedChildCompletionRunsOutsideRouteMapGateAndStillPublishesPeerTerminalOnThrow()
    {
        var terminalCalls = 0;
        var manager = new StreamManager(new RuntimeConcurrencyOptions(), null, null, (_, _) => terminalCalls++);
        Ensure(manager.TryReservePreAdmissionStreams(53, 1, RpcSessionTestFixture.RuntimeContext.Buffers,
            static _ => true, static _ => { }, static () => { }, retainUntilLocalCompletion: true), "route reserved");
        var child = new ThrowingChild
        {
            BeforeComplete = () => Task.Run(async () =>
                await manager.DispatchChunkAsync(53, 99, ReadOnlySequence<byte>.Empty))
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()
        };
        manager.Register(53, 1, child);
        Exception? failure = null;
        try { manager.CompletePeerStream(53, 1, null); }
        catch (Exception error) { failure = error; }
        Ensure(failure is InvalidOperationException && terminalCalls == 1 && manager.ActiveStreamCount == 1,
            "cross-thread exact map query completes outside map gate; retained failure still publishes terminal");
        // Indexed retained owner remains discoverable by Session termination, not stranded.
        manager.CompleteAll(null);
        Ensure(manager.ActiveStreamCount == 0, "Session termination refunds retained route");
    }

    private static MeterListener ThrowOnRefund(AsyncLocal<bool> scope, Action? onRefund = null)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "SharpLink" && instrument.Name == "sharplink.streams.active")
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            if (scope.Value && value < 0)
            {
                onRefund?.Invoke();
                throw new InvalidOperationException("active-stream refund telemetry failure");
            }
        });
        listener.Start();
        return listener;
    }
    private sealed class ThrowingChild : IStreamConsumptionAwareDispatcher, IStreamDispatchLease
    {
        internal IStreamDispatchState? State;
        internal int CompleteCount;
        internal int DrainedCount;
        internal Exception? CallbackClearFailure;
        internal int FailingCallbackClearAttempts;
        internal Action? BeforeComplete;
        public void BindDispatchState(IStreamDispatchState state) => State = state;
        public ValueTask DispatchAcquiredAsync(ReadOnlySequence<byte> payload, int encodedByteCount) => ValueTask.CompletedTask;
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount) => ValueTask.CompletedTask;
        public void SetBytesConsumedCallback(Action<long, ushort, int>? callback, long requestId, ushort streamId)
        {
            if (CallbackClearFailure is { } failure && callback is null)
            {
                FailingCallbackClearAttempts++;
                throw failure;
            }
        }
        public void Complete(bool isError, string? errorMessage) => Complete((Exception?)null);
        public void Complete(Exception? exception)
        {
            CompleteCount++;
            BeforeComplete?.Invoke();
            throw new InvalidOperationException("typed child completion failure");
        }
        public void OnDispatchesDrained() => DrainedCount++;
    }
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
