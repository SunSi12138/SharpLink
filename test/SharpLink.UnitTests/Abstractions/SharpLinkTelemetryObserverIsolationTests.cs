using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;

namespace SharpLink.UnitTests.Abstractions;

[NotInParallel]
public sealed class SharpLinkTelemetryObserverIsolationTests
{
    private static readonly RpcMethodDescriptor Method = new(
        0x581,
        0x1,
        RpcMethodKind.Unary,
        HasResponsePayload: true,
        HasClientStreams: false,
        HasMethodTimeout: false,
        MethodTimeout: null);

    [Test]
    public void ThrowingMeterListenersShouldNotEscapeCallStartOrCompletion()
    {
        using var listener = CreateThrowingMeterListener();

        var success = SharpLinkTelemetry.StartClientCall(Method);
        success.Complete();

        var failure = SharpLinkTelemetry.StartServerCall(Method, requestId: 7);
        failure.Complete(new SharpLinkException(
            SharpLinkErrorCode.ResourceExhausted,
            "injected business failure"));

        SharpLinkTelemetry.RecordAdmissionRejected("method", "concurrency");
        SharpLinkTelemetry.RecordAdmissionQueueDuration(TimeSpan.FromMilliseconds(1));
        SharpLinkTelemetry.RecordClientRetry();
        SharpLinkTelemetry.RecordSharedMemoryWait("data");
        SharpLinkTelemetry.RecordMultiClusterMutation("add", "success", TimeSpan.FromMilliseconds(1));
        SharpLinkTelemetry.RecordAbandonedCall("client", "consumer_abandoned");
    }

    [Test]
    public void ThrowingCompletionMeterListenerShouldNotSkipRemainingAccounting()
    {
        var measurements = new List<MetricMeasurement>();
        using (var listener = CreateSelectiveThrowingMeterListener(
                   "sharplink.calls.completed",
                   measurements))
        {
            var success = SharpLinkTelemetry.StartClientCall(Method);
            measurements.Clear();
            success.Complete();

            Ensure(measurements.Any(static measurement =>
                    measurement.Name == "sharplink.calls.completed"),
                "successful completion callback should be exercised");
            Ensure(measurements.Any(static measurement =>
                    measurement.Name == "sharplink.calls.active" && measurement.Value < 0),
                "active-call decrement must still run after completed metric observer failure");
            Ensure(measurements.Any(static measurement =>
                    measurement.Name == "sharplink.calls.duration"),
                "duration recording must still run after completed metric observer failure");
        }

        measurements.Clear();
        using (var listener = CreateSelectiveThrowingMeterListener(
                   "sharplink.calls.failed",
                   measurements))
        {
            var failure = SharpLinkTelemetry.StartServerCall(Method, requestId: 9);
            measurements.Clear();
            failure.Complete(new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                "injected business failure"));

            Ensure(measurements.Any(static measurement =>
                    measurement.Name == "sharplink.calls.failed"),
                "failed completion callback should be exercised");
            Ensure(measurements.Any(static measurement =>
                    measurement.Name == "sharplink.resource_exhausted"),
                "resource-exhaustion accounting must still run after failed metric observer failure");
            Ensure(measurements.Any(static measurement =>
                    measurement.Name == "sharplink.calls.active" && measurement.Value < 0),
                "active-call decrement must still run after failed metric observer failure");
            Ensure(measurements.Any(static measurement =>
                    measurement.Name == "sharplink.calls.duration"),
                "duration recording must still run after failed metric observer failure");
        }
    }

    [Test]
    public void ThrowingActivitySamplerShouldNotEscapeLogicalOrAttemptStart()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source =>
                ReferenceEquals(source, SharpLinkTelemetry.ClientActivitySource) ||
                ReferenceEquals(source, SharpLinkTelemetry.ServerActivitySource),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                throw new InvalidOperationException("injected activity sampler failure"),
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                throw new InvalidOperationException("injected activity sampler failure")
        };
        ActivitySource.AddActivityListener(listener);

        var client = SharpLinkTelemetry.StartClientCall(Method);
        client.Complete();
        var server = SharpLinkTelemetry.StartServerCall(Method, requestId: 8);
        server.Complete();
        var attempt = SharpLinkTelemetry.StartClientAttempt(Method, attempt: 1);
        attempt.Complete();
    }

    [Test]
    public void ThrowingActivityStartedCallbackShouldNotEscapeAndShouldRestoreAmbientParent()
    {
        using var parentSource = new ActivitySource("SharpLink.UnitTests.TelemetryObserverIsolation.Parent.Started");
        using var parentListener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, parentSource),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.PropagationData
        };
        ActivitySource.AddActivityListener(parentListener);
        using var parent = parentSource.StartActivity("parent");

        var started = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source =>
                ReferenceEquals(source, SharpLinkTelemetry.ClientActivitySource) ||
                ReferenceEquals(source, SharpLinkTelemetry.ServerActivitySource),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _ =>
            {
                Interlocked.Increment(ref started);
                throw new InvalidOperationException("injected activity started failure");
            }
        };
        ActivitySource.AddActivityListener(listener);

        var logical = SharpLinkTelemetry.StartClientCall(Method);
        Ensure(started == 1, "logical ActivityStarted callback should be exercised");
        EnsureCurrent(parent, "activity started failure must restore ambient parent");
        logical.Complete();
        EnsureCurrent(parent, "logical completion must preserve ambient parent");

        var attempt = SharpLinkTelemetry.StartClientAttempt(Method, attempt: 2);
        Ensure(started == 2, "attempt ActivityStarted callback should be exercised");
        EnsureCurrent(parent, "attempt activity started failure must restore ambient parent");
        attempt.Complete(new SharpLinkException(SharpLinkErrorCode.Unavailable, "attempt failure"));
        EnsureCurrent(parent, "attempt completion must preserve ambient parent");
    }

    [Test]
    public void ThrowingActivityStartedListenerShouldStillPairEarlierObserverStartAndStop()
    {
        var observedStarted = 0;
        var observedStopped = 0;
        using var observer = new ActivityListener
        {
            ShouldListenTo = static source =>
                ReferenceEquals(source, SharpLinkTelemetry.ClientActivitySource),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _ => Interlocked.Increment(ref observedStarted),
            ActivityStopped = _ => Interlocked.Increment(ref observedStopped)
        };
        ActivitySource.AddActivityListener(observer);

        var throwingStarted = 0;
        using var thrower = new ActivityListener
        {
            ShouldListenTo = static source =>
                ReferenceEquals(source, SharpLinkTelemetry.ClientActivitySource),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _ =>
            {
                Interlocked.Increment(ref throwingStarted);
                throw new InvalidOperationException("injected later ActivityStarted listener failure");
            }
        };
        ActivitySource.AddActivityListener(thrower);

        var previous = Activity.Current;

        var logical = SharpLinkTelemetry.StartClientCall(Method);
        Ensure(throwingStarted == 1, "throwing logical ActivityStarted listener should be exercised");
        Ensure(observedStarted == 1, "earlier observer should receive logical ActivityStarted");
        Ensure(observedStopped == 1,
            "earlier observer should receive matching logical ActivityStopped after later start failure");
        EnsureCurrent(previous, "logical start fault cleanup must restore ambient activity");
        logical.Complete();
        Ensure(observedStopped == 1,
            "logical scope completion must not emit a duplicate stop after start fault cleanup");

        var attempt = SharpLinkTelemetry.StartClientAttempt(Method, attempt: 4);
        Ensure(throwingStarted == 2, "throwing attempt ActivityStarted listener should be exercised");
        Ensure(observedStarted == 2, "earlier observer should receive attempt ActivityStarted");
        Ensure(observedStopped == 2,
            "earlier observer should receive matching attempt ActivityStopped after later start failure");
        EnsureCurrent(previous, "attempt start fault cleanup must restore ambient activity");
        attempt.Complete(new SharpLinkException(SharpLinkErrorCode.Unavailable, "attempt failure"));
        Ensure(observedStopped == 2,
            "attempt scope completion must not emit a duplicate stop after start fault cleanup");
    }

    [Test]
    public void ThrowingActivityStoppedCallbackShouldNotEscapeAndShouldRestoreAmbientParent()
    {
        using var parentSource = new ActivitySource("SharpLink.UnitTests.TelemetryObserverIsolation.Parent.Stopped");
        using var parentListener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, parentSource),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.PropagationData
        };
        ActivitySource.AddActivityListener(parentListener);
        using var parent = parentSource.StartActivity("parent");

        var stopped = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source =>
                ReferenceEquals(source, SharpLinkTelemetry.ClientActivitySource) ||
                ReferenceEquals(source, SharpLinkTelemetry.ServerActivitySource),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _ =>
            {
                Interlocked.Increment(ref stopped);
                throw new InvalidOperationException("injected activity stopped failure");
            }
        };
        ActivitySource.AddActivityListener(listener);

        var logical = SharpLinkTelemetry.StartClientCall(Method);
        Ensure(!ReferenceEquals(Activity.Current, parent),
            "logical activity should become ambient before completion");
        logical.Complete();
        Ensure(stopped == 1, "logical ActivityStopped callback should be exercised");
        EnsureCurrent(parent, "logical stopped callback failure must restore ambient parent");

        var attempt = SharpLinkTelemetry.StartClientAttempt(Method, attempt: 3);
        Ensure(!ReferenceEquals(Activity.Current, parent),
            "attempt activity should become ambient before completion");
        attempt.Complete(new SharpLinkException(SharpLinkErrorCode.Unavailable, "attempt failure"));
        Ensure(stopped == 2, "attempt ActivityStopped callback should be exercised");
        EnsureCurrent(parent, "attempt stopped callback failure must restore ambient parent");
    }

    private static MeterListener CreateThrowingMeterListener()
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, SharpLinkTelemetry.Meter))
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>(static (_, _, _, _) =>
            throw new InvalidOperationException("injected meter listener failure"));
        listener.SetMeasurementEventCallback<double>(static (_, _, _, _) =>
            throw new InvalidOperationException("injected meter listener failure"));
        listener.Start();
        return listener;
    }

    private static MeterListener CreateSelectiveThrowingMeterListener(
        string throwInstrument,
        List<MetricMeasurement> measurements)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meterListener) =>
        {
            if (!ReferenceEquals(instrument.Meter, SharpLinkTelemetry.Meter))
                return;
            if (instrument.Name is
                "sharplink.calls.completed" or
                "sharplink.calls.failed" or
                "sharplink.calls.active" or
                "sharplink.calls.duration" or
                "sharplink.resource_exhausted")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            measurements.Add(new MetricMeasurement(instrument.Name, measurement));
            if (string.Equals(instrument.Name, throwInstrument, StringComparison.Ordinal))
                throw new InvalidOperationException("injected completion metric listener failure");
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            measurements.Add(new MetricMeasurement(instrument.Name, measurement));
            if (string.Equals(instrument.Name, throwInstrument, StringComparison.Ordinal))
                throw new InvalidOperationException("injected completion metric listener failure");
        });
        listener.Start();
        return listener;
    }

    private static void EnsureCurrent(Activity? expected, string message)
    {
        if (!ReferenceEquals(Activity.Current, expected))
            throw new Exception($"assert failed: {message}");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
    }

    private readonly record struct MetricMeasurement(string Name, double Value);
}
