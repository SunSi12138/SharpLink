using System.Diagnostics;
using System.Diagnostics.Metrics;
using SharpLink.Client;
using static SharpLink.UnitTests.Client.ClientStreamWaitTestSupport;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class ClientStreamWaitTelemetryTests
{
    private static readonly RpcMethodDescriptor Method = new(
        0x735, 0x742, RpcMethodKind.ServerStreaming, HasResponsePayload: true,
        HasClientStreams: false, HasMethodTimeout: false, MethodTimeout: null);

    [Test]
    [Arguments("success", false)]
    [Arguments("fault", false)]
    [Arguments("canceled", false)]
    [Arguments("abandoned", false)]
    [Arguments("success", true)]
    [Arguments("fault", true)]
    [Arguments("canceled", true)]
    [Arguments("abandoned", true)]
    public async Task RealTelemetryCompletesOnceAfterSuspendedRead(string terminal, bool nested)
    {
        var completed = 0L;
        var failed = 0L;
        var active = 0L;
        var activeEvents = 0;
        var abandoned = 0L;
        var stopped = 0;
        Activity? observed = null;
        using var activities = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "SharpLink.Client",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("rpc.sharplink.method_id")?.ToString() != Method.MethodId.ToString()) return;
                observed = activity;
                stopped++;
            }
        };
        ActivitySource.AddActivityListener(activities);
        using var metrics = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == "SharpLink" && instrument.Name.StartsWith("sharplink.calls.", StringComparison.Ordinal))
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        metrics.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            switch (instrument.Name)
            {
                case "sharplink.calls.completed": completed += value; break;
                case "sharplink.calls.failed": failed += value; break;
                case "sharplink.calls.active": active += value; activeEvents++; break;
                case "sharplink.calls.abandoned": abandoned += value; break;
            }
        });
        metrics.Start();
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception expected = terminal == "canceled"
            ? new OperationCanceledException("telemetry cancellation", cancellation.Token)
            : new InvalidOperationException("telemetry fault");
        var source = new PendingStream();
        var scope = SharpLinkTelemetry.StartClientCall(Method);
        Require(SharpLinkTelemetry.ClientCallsEnabled && scope.IsEnabled, "real telemetry listeners were not enabled");
        var reader = Wrap(client, source, nested ? "both" : "telemetry", scope);
        var disposed = false;
        try
        {
            var move = reader.MoveNextAsync();
            Require(!move.IsCompleted && active == 1, "telemetry must cover a genuinely suspended read");
            if (terminal == "abandoned")
            {
                await reader.DisposeAsync();
                disposed = true;
            }
            if (terminal is "fault" or "canceled") source.Fail(expected);
            else source.Finish(false);
            Require(move.IsCompleted, "telemetry read did not complete inline");
            Exception? failure = null;
            try { Require(!move.GetAwaiter().GetResult(), "terminal result invented DATA"); }
            catch (Exception error) { failure = error; }
            Require(terminal is "fault" or "canceled" ? ReferenceEquals(failure, expected) : failure is null,
                "telemetry changed the original terminal result");
        }
        finally
        {
            source.ReleaseForCleanup();
            if (!disposed) await reader.DisposeAsync();
        }
        Require(source.ConsumptionCount == 1 && source.DisposeCount == 1, "telemetry duplicated source ownership");
        Require(completed == (terminal == "success" ? 1 : 0) && failed == (terminal == "success" ? 0 : 1),
            "call success/failure metrics did not complete exactly once");
        Require(active == 0 && activeEvents == 2, "call active metrics must contain exactly one increment and decrement");
        Require(abandoned == (terminal == "abandoned" ? 1 : 0), "abandonment metric changed");
        Require(stopped == 1 && observed?.Status == (terminal == "success" ? ActivityStatusCode.Ok : ActivityStatusCode.Error),
            "Activity did not stop exactly once with its correct terminal status");
        if (nested)
            Require(((ISharpLinkClientDrainInspector)client).ActiveCallCount == 0, "nested logical accounting did not finish");
    }
}
