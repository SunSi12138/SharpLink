namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private HeartbeatConfigurationGeneration? _heartbeatConfiguration;

    public SharpLinkHeartbeatConfigurationSnapshot GetHeartbeatConfigurationSnapshot()
    {
        var current = CaptureHeartbeatConfiguration();
        return new SharpLinkHeartbeatConfigurationSnapshot(
            current.Generation,
            current.Interval,
            current.Timeout);
    }

    public void UpdateHeartbeat(TimeSpan interval, TimeSpan timeout)
    {
        ValidateHeartbeatConfiguration(interval, timeout);
        HeartbeatConfigurationGeneration? previous;
        lock (_stateGate)
        {
            EnsureHeartbeatPublicationAllowed();
            previous = PublishHeartbeatConfigurationLocked(interval, timeout);
        }
        previous?.SignalChanged();
    }

    public void UpdateHeartbeatInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        HeartbeatConfigurationGeneration? previous;
        lock (_stateGate)
        {
            EnsureHeartbeatPublicationAllowed();
            var current = CaptureHeartbeatConfiguration();
            ValidateHeartbeatConfiguration(interval, current.Timeout);
            previous = PublishHeartbeatConfigurationLocked(interval, current.Timeout);
        }
        previous?.SignalChanged();
    }

    public void UpdateHeartbeatTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        HeartbeatConfigurationGeneration? previous;
        lock (_stateGate)
        {
            EnsureHeartbeatPublicationAllowed();
            var current = CaptureHeartbeatConfiguration();
            ValidateHeartbeatConfiguration(current.Interval, timeout);
            previous = PublishHeartbeatConfigurationLocked(current.Interval, timeout);
        }
        previous?.SignalChanged();
    }

    private HeartbeatConfigurationGeneration CaptureHeartbeatConfiguration()
    {
        var current = Volatile.Read(ref _heartbeatConfiguration);
        if (current is not null)
            return current;

        var initial = new HeartbeatConfigurationGeneration(
            generation: 0,
            _heartbeatInterval,
            _heartbeatTimeout);
        return Interlocked.CompareExchange(ref _heartbeatConfiguration, initial, null) ?? initial;
    }

    private HeartbeatConfigurationGeneration? PublishHeartbeatConfigurationLocked(
        TimeSpan interval,
        TimeSpan timeout)
    {
        var current = CaptureHeartbeatConfiguration();
        if (current.Interval == interval && current.Timeout == timeout)
            return null;
        if (current.Generation == ulong.MaxValue)
            throw new InvalidOperationException("The heartbeat configuration generation is exhausted.");

        var candidate = new HeartbeatConfigurationGeneration(
            current.Generation + 1,
            interval,
            timeout);
        Volatile.Write(ref _heartbeatConfiguration, candidate);
        return current;
    }

    private void EnsureHeartbeatPublicationAllowed()
    {
        var state = State;
        if (Volatile.Read(ref _stopStarted) != 0 ||
            state is SharpLinkConnectionState.Draining or
                SharpLinkConnectionState.Stopped or
                SharpLinkConnectionState.Faulted)
        {
            throw new InvalidOperationException(
                $"Client state '{state}' does not accept heartbeat configuration updates.");
        }
    }

    private static void ValidateHeartbeatConfiguration(TimeSpan interval, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (timeout <= interval)
            throw new ArgumentException("Heartbeat timeout must be greater than interval.");
    }

    private sealed class HeartbeatConfigurationGeneration
    {
        private readonly CancellationTokenSource _changed = new();

        internal HeartbeatConfigurationGeneration(
            ulong generation,
            TimeSpan interval,
            TimeSpan timeout)
        {
            Generation = generation;
            Interval = interval;
            Timeout = timeout;
        }

        internal ulong Generation { get; }
        internal TimeSpan Interval { get; }
        internal TimeSpan Timeout { get; }
        internal CancellationToken ChangedToken => _changed.Token;

        internal void SignalChanged()
        {
            try
            {
                _changed.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
