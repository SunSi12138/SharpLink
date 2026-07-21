using System.Collections.Concurrent;

namespace SharpLink.Client;

/// <summary>Allows dynamic topology owners to release per-generation policy state deterministically.</summary>
internal interface ISharpLinkEndpointAdmissionLifecycle
{
    void Retire(in SharpLinkEndpointCandidate endpoint);
}

/// <summary>
/// Built-in, lazy endpoint-generation breaker. It uses monotonic timestamps and has no timer or
/// topology writer lock on its Closed path. The bounded sample ring is allocated once per active
/// endpoint generation and released when that generation retires.
/// </summary>
internal sealed class SharpLinkCircuitBreaker : ISharpLinkEndpointAdmissionPolicy, ISharpLinkEndpointAdmissionLifecycle
{
    private readonly SharpLinkCircuitBreakerOptions _options;
    private readonly ConcurrentDictionary<CircuitKey, CircuitState> _states = new();

    public SharpLinkCircuitBreaker(SharpLinkCircuitBreakerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public SharpLinkEndpointAdmissionDecision TryAcquire(
        in SharpLinkEndpointCandidate endpoint,
        in RpcMethodDescriptor method)
    {
        var key = new CircuitKey(endpoint.Endpoint.Id, endpoint.Generation);
        var state = _states.GetOrAdd(key, static (_, options) => new CircuitState(options), _options);
        var decision = state.TryAcquire(Stopwatch.GetTimestamp());
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
        state.Report(Stopwatch.GetTimestamp(), Classify(outcome), token);
    }

    public void Retire(in SharpLinkEndpointCandidate endpoint)
        => _states.TryRemove(new CircuitKey(endpoint.Endpoint.Id, endpoint.Generation), out _);

    /// <summary>Records an endpoint-level infrastructure failure that has no call admission token.</summary>
    internal void ReportInfrastructureFailure(in SharpLinkEndpointCandidate endpoint)
    {
        var key = new CircuitKey(endpoint.Endpoint.Id, endpoint.Generation);
        var state = _states.GetOrAdd(key, static (_, options) => new CircuitState(options), _options);
        state.ReportInfrastructureFailure(Stopwatch.GetTimestamp());
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
            return outcome.ErrorCode == SharpLinkErrorCode.ResourceExhausted
                ? CircuitSample.Ignore
                : CircuitSample.Failure;
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

        private readonly SharpLinkCircuitBreakerOptions _options;
        private readonly object _samplesGate = new();
        private readonly long[] _timestamps;
        private readonly bool[] _failures;
        private readonly long _samplingTicks;
        private readonly long _breakTicks;
        private int _state;
        private long _openUntil;
        private int _halfOpenInFlight;
        private long _halfOpenEpoch;
        private int _head;
        private int _count;
        private int _failureCount;

        public CircuitState(SharpLinkCircuitBreakerOptions options)
        {
            _options = options;
            var capacity = Math.Max(options.MinimumThroughput * 4, 64);
            _timestamps = new long[capacity];
            _failures = new bool[capacity];
            _samplingTicks = ToStopwatchTicks(options.SamplingDuration);
            _breakTicks = ToStopwatchTicks(options.BreakDuration);
        }

        public SharpLinkEndpointAdmissionDecision TryAcquire(long now)
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
                            retryAfter = TimeSpan.FromSeconds((double)(openUntil - now) / Stopwatch.Frequency);
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
                    if (_halfOpenInFlight >= _options.HalfOpenMaxCalls)
                        return new SharpLinkEndpointAdmissionDecision(false, Token: 0, TimeSpan.Zero);
                    _halfOpenInFlight++;
                    return new SharpLinkEndpointAdmissionDecision(true, _halfOpenEpoch, RetryAfter: null);
                }
            }
        }

        public void Report(long now, CircuitSample sample, long token)
        {
            if (token != 0)
            {
                ReportHalfOpen(now, sample, token);
                return;
            }

            if (sample == CircuitSample.Ignore || Volatile.Read(ref _state) != Closed)
                return;

            lock (_samplesGate)
            {
                if (Volatile.Read(ref _state) != Closed)
                    return;
                Prune(now);
                Add(now, sample == CircuitSample.Failure);
                if (_count >= _options.MinimumThroughput &&
                    (double)_failureCount / _count >= _options.FailureRatio)
                {
                    OpenCircuitLocked(now);
                }
            }
        }

        public void ReportInfrastructureFailure(long now)
        {
            lock (_samplesGate)
            {
                var state = Volatile.Read(ref _state);
                if (state == Open)
                    return;
                if (state == HalfOpen)
                {
                    OpenCircuitLocked(now);
                    return;
                }

                Prune(now);
                Add(now, failure: true);
                if (_count >= _options.MinimumThroughput &&
                    (double)_failureCount / _count >= _options.FailureRatio)
                {
                    OpenCircuitLocked(now);
                }
            }
        }

        private void ReportHalfOpen(long now, CircuitSample sample, long token)
        {
            lock (_samplesGate)
            {
                if (Volatile.Read(ref _state) != HalfOpen || token != _halfOpenEpoch || _halfOpenInFlight == 0)
                    return;

                _halfOpenInFlight--;
                if (sample == CircuitSample.Failure)
                {
                    OpenCircuitLocked(now);
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

        private void OpenCircuitLocked(long now)
        {
            _openUntil = SaturatingAdd(now, _breakTicks);
            _halfOpenInFlight = 0;
            _halfOpenEpoch = NextHalfOpenEpoch();
            Volatile.Write(ref _state, Open);
        }

        private long NextHalfOpenEpoch()
            => _halfOpenEpoch == long.MaxValue ? 1 : _halfOpenEpoch + 1;

        private void Prune(long now)
        {
            var minimum = now - _samplingTicks;
            while (_count != 0 && _timestamps[_head] < minimum)
            {
                if (_failures[_head])
                    _failureCount--;
                _head = (_head + 1) % _timestamps.Length;
                _count--;
            }
        }

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

        private static long ToStopwatchTicks(TimeSpan value)
        {
            var ticks = value.TotalSeconds * Stopwatch.Frequency;
            return ticks >= long.MaxValue ? long.MaxValue : Math.Max(1, (long)Math.Ceiling(ticks));
        }

        private static long SaturatingAdd(long value, long add)
            => add >= long.MaxValue - value ? long.MaxValue : value + add;
    }
}
