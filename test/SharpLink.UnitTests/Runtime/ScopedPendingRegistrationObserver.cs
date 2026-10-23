using System.Diagnostics.Metrics;
using System.Runtime.ExceptionServices;

namespace SharpLink.UnitTests.Runtime;

// Test-only observation of the existing synchronous metric before PendingCall.MarkRegistered.
// The context marker scopes attribution to an explicit registration call, not all global metrics.
internal sealed class ScopedPendingRegistrationObserver : IDisposable
{
    private static readonly AsyncLocal<ScopedPendingRegistrationObserver?> Current = new();
    private readonly MeterListener _listener = new();
    private readonly Action _registered;
    private ExceptionDispatchInfo? _failure;

    internal ScopedPendingRegistrationObserver(Action registered)
    {
        _registered = registered;
        _listener.InstrumentPublished = static (instrument, listener) =>
        {
            if (instrument.Meter.Name == "SharpLink" && instrument.Name == "sharplink.requests.pending")
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, delta, _, _) =>
        {
            if (delta != 1 || !ReferenceEquals(Current.Value, this)) return;
            try { _registered(); }
            catch (Exception exception)
            {
                // Telemetry isolates callback exceptions. A test observation must not silently pass.
                Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(exception), null);
            }
        });
        _listener.Start();
    }

    internal T Observe<T>(Func<T> register)
    {
        var previous = Current.Value;
        Current.Value = this;
        try { return register(); }
        finally
        {
            Current.Value = previous;
            ThrowObservationFailure();
        }
    }

    private void ThrowObservationFailure() => Interlocked.Exchange(ref _failure, null)?.Throw();

    public void Dispose()
    {
        _listener.Dispose();
        ThrowObservationFailure();
    }
}
