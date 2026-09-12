using System.Collections.Concurrent;

namespace SharpLink.Client;

/// <summary>Allows dynamic topology owners to release per-generation policy state deterministically.</summary>
internal interface ISharpLinkEndpointAdmissionLifecycle
{
    void Retire(in SharpLinkEndpointCandidate endpoint);
}

/// <summary>
/// Built-in, lazy endpoint-generation breaker. It uses monotonic timestamps and has no timer or
/// topology writer lock on its Closed path. Runtime option replacement preserves each endpoint-
/// generation state object and its live sample history.
/// </summary>
internal sealed class SharpLinkCircuitBreaker : ISharpLinkEndpointAdmissionPolicy, ISharpLinkEndpointAdmissionLifecycle
{
    private CircuitBreakerConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<CircuitKey, CircuitState> _states = new();

    public SharpLinkCircuitBreaker(SharpLinkCircuitBreakerOptions options)
        : this(options, TimeProvider.System)
    {
    }

    internal SharpLinkCircuitBreaker(
        ISharpLinkCircuitBreakerOptions options,
        TimeProvider timeProvider)
    {
        _configuration = CircuitBreakerConfiguration.CopyValidated(options);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    internal CircuitBreakerConfiguration CaptureConfiguration()
        => Volatile.Read(ref _configuration);

    internal void UpdateConfiguration(ISharpLinkCircuitBreakerOptions options)
        => Volatile.Write(ref _configuration, CircuitBreakerConfiguration.CopyValidated(options));

    public SharpLinkEndpointAdmissionDecision TryAcquire(
        in SharpLinkEndpointCandidate endpoint,
        in RpcMethodDescriptor method)
    {
        var configuration = Volatile.Read(ref _configuration);
        var key = new CircuitKey(endpoint.Endpoint.Id, endpoint.Generation);
        var state = _states.GetOrAdd(
            key,
            static (_, factory) => new CircuitState(factory.Configuration, factory.TimeProvider),
            (Configuration: configuration, TimeProvider: _timeProvider));
        var decision = state.TryAcquire(_timeProvider.GetTimestamp(), configuration);
        if (!decision.IsAllowed)
        {
            SharpLinkTelemetry.RecordEndpointAdmissionRejected("breaker_open");
            SharpLinkTelemetry.RecordBreakerOpen();
        }
        return decision;
    }

    public void Report(in SharpLinkEndpointOutcome outcome, long token)
    {
        var key = new CircuitKey(outcome.Endpoint.Endpoint.Id, outcome.Endpoint.Generation);
        if (!_states.TryGetValue(key, out var state))
            return;
        state.Report(
            _timeProvider.GetTimestamp(),
            Classify(outcome),
            token,
            Volatile.Read(ref _configuration));
    }

    public void Retire(in SharpLinkEndpointCandidate endpoint)
        => _states.TryRemove(new CircuitKey(endpoint.Endpoint.Id, endpoint.Generation), out _);

    /// <summary>Records an endpoint-level infrastructure failure that has no call admission token.</summary>
    internal void ReportInfrastructureFailure(in SharpLinkEndpointCandidate endpoint)
    {
        var configuration = Volatile.Read(ref _configuration);
        var key = new CircuitKey(endpoint.Endpoint.Id, endpoint.Generation);
        var state = _states.GetOrAdd(
            key,
            static (_, factory) => new CircuitState(factory.Configuration, factory.TimeProvider),
            (Configuration: configuration, TimeProvider: _timeProvider));
        state.ReportInfrastructureFailure(
            _timeProvider.GetTimestamp(),
            configuration);
    }

    private static CircuitSample Classify(in SharpLinkEndpointOutcome outcome)
    {
        if (outcome.Kind is SharpLinkEndpointOutcomeKind.Cancelled or SharpLinkEndpointOutcomeKind.DeadlineExceeded)
            return CircuitSample.Ignore;
        if (outcome.Kind is SharpLinkEndpointOutcomeKind.ConnectionClosed or
            SharpLinkEndpointOutcomeKind.GoAway)
        {
            return CircuitSample.Failure;
        }
        if (outcome.Kind == SharpLinkEndpointOutcomeKind.SendFailure)
        {
            return outcome.ErrorCode is SharpLinkErrorCode.Unavailable or
                SharpLinkErrorCode.ConnectionClosed or
                SharpLinkErrorCode.DataLoss or
                SharpLinkErrorCode.Internal
                ? CircuitSample.Failure
                : CircuitSample.Ignore;
        }
        if (outcome.Kind == SharpLinkEndpointOutcomeKind.RemoteError)
        {
            return outcome.ErrorCode is SharpLinkErrorCode.Unavailable or
                SharpLinkErrorCode.ConnectionClosed or
                SharpLinkErrorCode.ResourceExhausted or
                SharpLinkErrorCode.DataLoss or
                SharpLinkErrorCode.Internal
                ? CircuitSample.Failure
                : CircuitSample.Success;
        }
        return CircuitSample.Success;
    }

    internal sealed class CircuitBreakerConfiguration
    {
        private CircuitBreakerConfiguration(
            int minimumThroughput,
            double failureRatio,
            TimeSpan samplingDuration,
            TimeSpan breakDuration,
            int halfOpenMaxCalls)
        {
            MinimumThroughput = minimumThroughput;
            FailureRatio = failureRatio;
            SamplingDuration = samplingDuration;
            BreakDuration = breakDuration;
            HalfOpenMaxCalls = halfOpenMaxCalls;
        }

        internal int MinimumThroughput { get; }
        internal double FailureRatio { get; }
        internal TimeSpan SamplingDuration { get; }
        internal TimeSpan BreakDuration { get; }
        internal int HalfOpenMaxCalls { get; }

        internal static CircuitBreakerConfiguration CopyValidated(ISharpLinkCircuitBreakerOptions options)
        {
            var frozen = SharpLinkCircuitBreakerOptions.CopyValidated(options);
            return new CircuitBreakerConfiguration(
                frozen.MinimumThroughput,
                frozen.FailureRatio,
                frozen.SamplingDuration,
                frozen.BreakDuration,
                frozen.HalfOpenMaxCalls);
        }
    }

    private readonly record struct CircuitKey(string EndpointId, long Generation);

    private enum CircuitSample : byte
    {
        Ignore,
        Success,
        Failure
    }

    private sealed class CircuitState
    {
        private const int Closed = 0;
        private const int Open = 1;
        private const int HalfOpen = 2;

        private readonly TimeProvider _timeProvider;
        private readonly object _samplesGate = new();
        private long[] _timestamps;
        private bool[] _failures;
        private int _state;
        private long _openUntil;
        private int _halfOpenInFlight;
        private long _halfOpenEpoch;
        private int _head;
        private int _count;
        private int _failureCount;

        public CircuitState(
            CircuitBreakerConfiguration configuration,
            TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
            var capacity = GetRequiredCapacity(configuration.MinimumThroughput);
            _timestamps = new long[capacity];
            _failures = new bool[capacity];
        }

        public SharpLinkEndpointAdmissionDecision TryAcquire(
            long now,
            CircuitBreakerConfiguration configuration)
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);
                if (state == Closed)
                    return new SharpLinkEndpointAdmissionDecision(true, Token: 0, RetryAfter: null);

                if (state == Open)
                {
                    TimeSpan? retryAfter = null;
                    lock (_samplesGate)
                    {
                        if (Volatile.Read(ref _state) != Open)
                            continue;
                        var openUntil = _openUntil;
                        if (now < openUntil)
                        {
                            retryAfter = SharpLinkTime.GetRemaining(
                                openUntil,
                                now,
                                _timeProvider.TimestampFrequency);
                        }
                        else
                        {
                            BeginHalfOpenLocked();
                        }
                    }
                    if (retryAfter is { } remaining)
                        return new SharpLinkEndpointAdmissionDecision(false, Token: 0, remaining);
                    continue;
                }

                lock (_samplesGate)
                {
                    if (Volatile.Read(ref _state) != HalfOpen)
                        continue;
                    if (_halfOpenInFlight >= configuration.HalfOpenMaxCalls)
                        return new SharpLinkEndpointAdmissionDecision(false, Token: 0, TimeSpan.Zero);
                    _halfOpenInFlight++;
                    return new SharpLinkEndpointAdmissionDecision(true, _halfOpenEpoch, RetryAfter: null);
                }
            }
        }

        public void Report(
            long now,
            CircuitSample sample,
            long token,
            CircuitBreakerConfiguration configuration)
        {
            if (token != 0)
            {
                ReportHalfOpen(now, sample, token, configuration);
                return;
            }

            if (sample == CircuitSample.Ignore || Volatile.Read(ref _state) != Closed)
                return;

            lock (_samplesGate)
            {
                if (Volatile.Read(ref _state) != Closed)
                    return;
                Prune(now, configuration.SamplingDuration);
                EnsureCapacity(configuration.MinimumThroughput);
                Add(now, sample == CircuitSample.Failure);
                if (_count >= configuration.MinimumThroughput &&
                    (double)_failureCount / _count >= configuration.FailureRatio)
                {
                    OpenCircuitLocked(now, configuration.BreakDuration);
                }
            }
        }

        public void ReportInfrastructureFailure(
            long now,
            CircuitBreakerConfiguration configuration)
        {
            lock (_samplesGate)
            {
                var state = Volatile.Read(ref _state);
                if (state == Open)
                    return;
                if (state == HalfOpen)
                {
                    OpenCircuitLocked(now, configuration.BreakDuration);
                    return;
                }

                Prune(now, configuration.SamplingDuration);
                EnsureCapacity(configuration.MinimumThroughput);
                Add(now, failure: true);
                if (_count >= configuration.MinimumThroughput &&
                    (double)_failureCount / _count >= configuration.FailureRatio)
                {
                    OpenCircuitLocked(now, configuration.BreakDuration);
                }
            }
        }

        private void ReportHalfOpen(
            long now,
            CircuitSample sample,
            long token,
            CircuitBreakerConfiguration configuration)
        {
            lock (_samplesGate)
            {
                if (Volatile.Read(ref _state) != HalfOpen || token != _halfOpenEpoch || _halfOpenInFlight == 0)
                    return;

                _halfOpenInFlight--;
                if (sample == CircuitSample.Failure)
                {
                    OpenCircuitLocked(now, configuration.BreakDuration);
                    return;
                }

                if (sample == CircuitSample.Success && _halfOpenInFlight == 0)
                {
                    _head = 0;
                    _count = 0;
                    _failureCount = 0;
                    Volatile.Write(ref _state, Closed);
                }
            }
        }

        private void BeginHalfOpenLocked()
        {
            _halfOpenEpoch = NextHalfOpenEpoch();
            _halfOpenInFlight = 0;
            Volatile.Write(ref _state, HalfOpen);
        }

        private void OpenCircuitLocked(long now, TimeSpan breakDuration)
        {
            _openUntil = SharpLinkTime.AddDuration(
                now,
                breakDuration,
                _timeProvider.TimestampFrequency);
            _halfOpenInFlight = 0;
            _halfOpenEpoch = NextHalfOpenEpoch();
            Volatile.Write(ref _state, Open);
        }

        private long NextHalfOpenEpoch()
            => _halfOpenEpoch == long.MaxValue ? 1 : _halfOpenEpoch + 1;

        private void Prune(long now, TimeSpan samplingDuration)
        {
            while (_count != 0 &&
                   _timeProvider.GetElapsedTime(_timestamps[_head], now) > samplingDuration)
            {
                if (_failures[_head])
                    _failureCount--;
                _head = (_head + 1) % _timestamps.Length;
                _count--;
            }
        }

        private void EnsureCapacity(int minimumThroughput)
        {
            var required = GetRequiredCapacity(minimumThroughput);
            if (required <= _timestamps.Length)
                return;

            var timestamps = new long[required];
            var failures = new bool[required];
            for (var index = 0; index < _count; index++)
            {
                var source = (_head + index) % _timestamps.Length;
                timestamps[index] = _timestamps[source];
                failures[index] = _failures[source];
            }

            _timestamps = timestamps;
            _failures = failures;
            _head = 0;
        }

        private static int GetRequiredCapacity(int minimumThroughput)
            => Math.Max(minimumThroughput * 4, 64);

        private void Add(long timestamp, bool failure)
        {
            if (_count == _timestamps.Length)
            {
                if (_failures[_head])
                    _failureCount--;
                _timestamps[_head] = timestamp;
                _failures[_head] = failure;
                if (failure)
                    _failureCount++;
                _head = (_head + 1) % _timestamps.Length;
                return;
            }

            var index = (_head + _count) % _timestamps.Length;
            _timestamps[index] = timestamp;
            _failures[index] = failure;
            _count++;
            if (failure)
                _failureCount++;
        }
    }
}
