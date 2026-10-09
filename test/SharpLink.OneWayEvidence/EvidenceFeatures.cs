using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.OneWayEvidence;

internal sealed class EvidenceFeatures : IDisposable
{
    private readonly ActivityListener? _activity;
    private readonly MeterListener? _meter;
    internal long Activities;
    internal long Measurements;
    internal long ClientInterceptions;
    internal long ServerInterceptions;
    internal long AdmissionAcquisitions;

    internal EvidenceFeatures(bool telemetry)
    {
        if (!telemetry)
            return;
        // Initialize sources before registering a listener that compares identities.
        // Otherwise a callback during type initialization can see a null property.
        _ = SharpLinkTelemetry.ClientActivitySource;
        _ = SharpLinkTelemetry.ServerActivitySource;
        _ = SharpLinkTelemetry.Meter;
        _activity = new ActivityListener
        {
            ShouldListenTo = static source => ReferenceEquals(source, SharpLinkTelemetry.ClientActivitySource)
                || ReferenceEquals(source, SharpLinkTelemetry.ServerActivitySource),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        _activity.ActivityStarted = _ => Interlocked.Increment(ref Activities);
        ActivitySource.AddActivityListener(_activity);
        _meter = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, SharpLinkTelemetry.Meter))
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _meter.SetMeasurementEventCallback<long>((_, _, _, _) => Interlocked.Increment(ref Measurements));
        _meter.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref Measurements));
        _meter.Start();
    }

    public void Dispose()
    {
        _activity?.Dispose();
        _meter?.Dispose();
    }

    internal sealed class ClientInterceptor(EvidenceFeatures owner) : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
        {
            Interlocked.Increment(ref owner.ClientInterceptions);
            return next(context);
        }
    }

    internal sealed class ServerInterceptor(EvidenceFeatures owner) : ISharpLinkServerInterceptor
    {
        public ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
        {
            Interlocked.Increment(ref owner.ServerInterceptions);
            return next(context);
        }
    }

    internal sealed class Admission(EvidenceFeatures owner) : ISharpLinkEndpointAdmissionPolicy
    {
        public SharpLinkEndpointAdmissionDecision TryAcquire(in SharpLinkEndpointCandidate endpoint, in RpcMethodDescriptor method)
        {
            Interlocked.Increment(ref owner.AdmissionAcquisitions);
            return new(true, Token: 1, RetryAfter: null);
        }
        public void Report(in SharpLinkEndpointOutcome outcome, long token) { }
    }
}
