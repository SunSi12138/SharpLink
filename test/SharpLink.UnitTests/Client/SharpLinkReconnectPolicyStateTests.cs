using System.Runtime.CompilerServices;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkReconnectPolicyStateTests
{
    [Test]
    public void LiveBackoffShouldPreserveOrClampAgainstReplacementBounds()
    {
        var stored = TimeSpan.FromSeconds(2);

        var lowerInitial = Policy(500, 30_000, 3d, 1d, 1d, 0);
        Ensure(
            SharpLinkClient.ResolveReconnectDelay(stored.Ticks, lowerInitial) == stored,
            "decreasing InitialDelay must not move an active failure streak backwards");

        var higherInitial = Policy(3_000, 30_000, 3d, 1d, 1d, 0);
        Ensure(
            SharpLinkClient.ResolveReconnectDelay(stored.Ticks, higherInitial) == TimeSpan.FromSeconds(3),
            "an active position below the new InitialDelay must clamp to the new lower bound");

        var lowerMaximum = Policy(500, 1_500, 3d, 1d, 1d, 0);
        Ensure(
            SharpLinkClient.ResolveReconnectDelay(stored.Ticks, lowerMaximum) == TimeSpan.FromMilliseconds(1_500),
            "decreasing MaxBackoff must clamp an active position immediately");

        var higherMaximum = Policy(500, 30_000, 3d, 1d, 1d, 0);
        Ensure(
            SharpLinkClient.ResolveReconnectDelay(stored.Ticks, higherMaximum) == stored,
            "increasing MaxBackoff must preserve the active position");
        Ensure(
            SharpLinkClient.NextReconnectDelay(stored, higherMaximum) == TimeSpan.FromSeconds(6),
            "the replacement multiplier must advance from the preserved active position");
    }

    [Test]
    public async Task ArmedActiveBackoffShouldSurvivePublicationAndUseReplacementMultiplier()
    {
        var time = new ManualTimeProvider();
        var factory = new FailCountThenBlockTransportFactory(failuresBeforeBlock: 3);
        var client = BuildDynamicClient(
            time,
            factory,
            Policy(1_000, 8_000, 2d, 1d, 1d, 0),
            port: 5601);

        try
        {
            await EnsureInitialConnectFailureAsync(client);
            await time.WaitForCreatedTimerCountAsync(1);

            time.Advance(TimeSpan.FromSeconds(1));
            await time.WaitForCreatedTimerCountAsync(2);
            Ensure(factory.ConnectCount == 2,
                "the first reconnect failure must advance the live position to two seconds");

            client.UpdateReconnectPolicy(Policy(500, 10_000, 3d, 1d, 1d, 0));
            await time.WaitForCreatedTimerCountAsync(3);

            time.Advance(TimeSpan.FromMilliseconds(1_999));
            await YieldAsync();
            Ensure(factory.ConnectCount == 2,
                "publication must not reset the active two-second backoff to the replacement InitialDelay");

            time.Advance(TimeSpan.FromMilliseconds(1));
            await WaitForConnectCountAsync(factory, 3);
            await time.WaitForCreatedTimerCountAsync(4);

            time.Advance(TimeSpan.FromMilliseconds(5_999));
            await YieldAsync();
            Ensure(factory.ConnectCount == 3,
                "the replacement multiplier must advance the preserved two-second position to six seconds");

            time.Advance(TimeSpan.FromMilliseconds(1));
            await factory.BlockedAttemptEntered.WaitAsync(TimeSpan.FromSeconds(3));
            Ensure(factory.ConnectCount == 4,
                "exactly one reconnect owner must start after the six-second replacement backoff");
        }
        finally
        {
            await client.StopAsync();
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task MaxDecreaseShouldClampAnAlreadyArmedBackoff()
    {
        var time = new ManualTimeProvider();
        var factory = new FailCountThenBlockTransportFactory(failuresBeforeBlock: 2);
        var client = BuildDynamicClient(
            time,
            factory,
            Policy(1_000, 8_000, 2d, 1d, 1d, 0),
            port: 5602);

        try
        {
            await EnsureInitialConnectFailureAsync(client);
            await time.WaitForCreatedTimerCountAsync(1);
            time.Advance(TimeSpan.FromSeconds(1));
            await time.WaitForCreatedTimerCountAsync(2);
            Ensure(factory.ConnectCount == 2, "the active backoff must have reached two seconds");

            client.UpdateReconnectPolicy(Policy(500, 1_500, 2d, 1d, 1d, 0));
            await time.WaitForCreatedTimerCountAsync(3);

            time.Advance(TimeSpan.FromMilliseconds(1_499));
            await YieldAsync();
            Ensure(factory.ConnectCount == 2,
                "the replacement wait must remain armed until the reduced maximum is reached");

            time.Advance(TimeSpan.FromMilliseconds(1));
            await factory.BlockedAttemptEntered.WaitAsync(TimeSpan.FromSeconds(3));
            Ensure(factory.ConnectCount == 3,
                "the active two-second position must clamp to the new 1.5-second maximum");
        }
        finally
        {
            await client.StopAsync();
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task JitterUpdateShouldRescheduleTheArmedWaitDeterministically()
    {
        var time = new ManualTimeProvider();
        var factory = new FailCountThenBlockTransportFactory(failuresBeforeBlock: 1);
        var client = BuildDynamicClient(
            time,
            factory,
            Policy(1_000, 8_000, 2d, 1d, 1d, 0),
            port: 5603);

        try
        {
            await EnsureInitialConnectFailureAsync(client);
            await time.WaitForCreatedTimerCountAsync(1);

            client.UpdateReconnectPolicy(Policy(1_000, 8_000, 2d, 0.5d, 0.5d, 0));
            await time.WaitForCreatedTimerCountAsync(2);

            time.Advance(TimeSpan.FromMilliseconds(499));
            await YieldAsync();
            Ensure(factory.ConnectCount == 1,
                "the deterministically jittered replacement wait must not fire early");

            time.Advance(TimeSpan.FromMilliseconds(1));
            await factory.BlockedAttemptEntered.WaitAsync(TimeSpan.FromSeconds(3));
            Ensure(factory.ConnectCount == 2,
                "the new jitter policy must apply to the replacement armed wait without a duplicate dial");
        }
        finally
        {
            await client.StopAsync();
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task InFlightReconnectFailureShouldUseTheNewestPolicyAfterRapidUpdates()
    {
        var time = new ManualTimeProvider();
        var factory = new InFlightFailureTransportFactory();
        var client = BuildDynamicClient(
            time,
            factory,
            Policy(1_000, 8_000, 2d, 1d, 1d, 0),
            port: 5604);

        try
        {
            await EnsureInitialConnectFailureAsync(client);
            await time.WaitForCreatedTimerCountAsync(1);
            time.Advance(TimeSpan.FromSeconds(1));
            await factory.InFlightAttemptEntered.WaitAsync(TimeSpan.FromSeconds(3));
            Ensure(factory.ConnectCount == 2, "the second dial must be the existing reconnect owner");

            client.UpdateReconnectPolicy(Policy(500, 10_000, 3d, 1d, 1d, 0));
            client.UpdateReconnectPolicy(Policy(500, 10_000, 4d, 1d, 1d, 0));
            await YieldAsync();
            Ensure(factory.ConnectCount == 2,
                "rapid policy publication must not restart the connection attempt already in progress");

            factory.ReleaseInFlightFailure();
            await time.WaitForCreatedTimerCountAsync(2);

            time.Advance(TimeSpan.FromMilliseconds(3_999));
            await YieldAsync();
            Ensure(factory.ConnectCount == 2,
                "the completed old-generation attempt must advance under the newest x4 multiplier");

            time.Advance(TimeSpan.FromMilliseconds(1));
            await factory.FinalAttemptEntered.WaitAsync(TimeSpan.FromSeconds(3));
            Ensure(factory.ConnectCount == 3,
                "the subsequent wait must use the newest complete policy after the in-flight failure");
        }
        finally
        {
            await client.StopAsync();
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task StableResetWindowShouldEvaluatePreservedHistoryAgainstTheLatestThreshold()
    {
        var time = new ManualTimeProvider();
        await using var client = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseTimeProvider(time)
            .UseReconnectPolicy(Policy(1_000, 8_000, 2d, 1d, 1d, 10_000))
            .UseTransport(new NeverUsedTransportFactory())
            .Build();
        var implementation = (SharpLinkClient)client;
        var readyTimestamp = time.GetTimestamp();
        Ensure(readyTimestamp == 0, "the regression requires a valid zero monotonic timestamp");
        time.Advance(TimeSpan.FromSeconds(5));

        client.UpdateReconnectPolicy(Policy(1_000, 8_000, 2d, 1d, 1d, 4_000));
        Ensure(
            implementation.HasReachedReconnectStableWindow(
                readyTimestamp, hasReadyTimestamp: true, client.GetReconnectPolicy()),
            "decreasing StableResetWindow must evaluate a preserved Ready timestamp even when its valid value is zero");

        client.UpdateReconnectPolicy(Policy(1_000, 8_000, 2d, 1d, 1d, 6_000));
        Ensure(
            !implementation.HasReachedReconnectStableWindow(
                readyTimestamp, hasReadyTimestamp: true, client.GetReconnectPolicy()),
            "increasing StableResetWindow must evaluate the same zero-valued Ready history against the new threshold");
        Ensure(
            !implementation.HasReachedReconnectStableWindow(
                readyTimestamp, hasReadyTimestamp: false, client.GetReconnectPolicy()),
            "history absence must be represented separately from the monotonic timestamp value");
    }

    [Test]
    public void EndpointReadyHistoryShouldNotReuseZeroAsAnUninitializedSentinel()
    {
        var configuration = new StaticEndpointConfiguration(
            Endpoint("zero-ready", 5699),
            new NeverUsedTransportFactory());
        var staticState = new StaticClientRuntimeEndpointState(configuration, index: 0);
        staticState.MarkReadyTimestamp(0);
        staticState.MarkReadyTimestamp(TimeSpan.FromSeconds(5).Ticks);
        Ensure(staticState.HasReadyTimestamp && staticState.ReadyTimestamp == 0,
            "static endpoint Ready history must retain a valid zero timestamp across later Ready publications");
        staticState.ClearReadyTimestamp();
        Ensure(!staticState.HasReadyTimestamp,
            "static endpoint Ready-history presence must clear independently of the timestamp value");

        var dynamicState = new SharpLinkClient.DynamicEndpointState(configuration, generation: 1);
        dynamicState.MarkReadyTimestamp(0);
        dynamicState.MarkReadyTimestamp(TimeSpan.FromSeconds(5).Ticks);
        Ensure(dynamicState.HasReadyTimestamp && dynamicState.ReadyTimestamp == 0,
            "dynamic endpoint Ready history must retain a valid zero timestamp across later Ready publications");
        dynamicState.ClearReadyTimestamp();
        Ensure(!dynamicState.HasReadyTimestamp,
            "dynamic endpoint Ready-history presence must clear independently of the timestamp value");
    }

    [Test]
    public void ReconnectCompletionMustUseOneCompletePolicySnapshot()
    {
        var baseDelay = TimeSpan.FromSeconds(4);
        var immediateReset = Policy(500, 30_000, 2d, 1d, 1d, 0);
        var preserveUntilStable = Policy(2_000, 30_000, 3d, 1d, 1d, 10_000);

        Ensure(
            SharpLinkClient.ResolveReconnectCompletionDelay(baseDelay, reconnected: true, immediateReset) ==
            immediateReset.InitialDelay,
            "zero StableResetWindow must reset a successful reconnect using the same policy snapshot");
        Ensure(
            SharpLinkClient.ResolveReconnectCompletionDelay(baseDelay, reconnected: true, preserveUntilStable) == baseDelay,
            "a non-zero StableResetWindow must preserve the active backoff without mixing in another generation's InitialDelay");
        Ensure(
            SharpLinkClient.ResolveReconnectCompletionDelay(baseDelay, reconnected: false, preserveUntilStable) ==
            TimeSpan.FromSeconds(12),
            "a failed completion must advance using one complete replacement policy snapshot");
    }

    [Test]
    public async Task RapidUpdatesBeforeConnectivityShouldPublishOnlyTheLatestPolicyAndStopShouldSealPublication()
    {
        var time = new ManualTimeProvider();
        var client = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseTimeProvider(time)
            .UseTransport(new NeverUsedTransportFactory())
            .Build();

        SharpLinkReconnectPolicy? last = null;
        try
        {
            for (var index = 0; index < 64; index++)
            {
                last = Policy(
                    10 + index,
                    1_000 + index,
                    1d + index / 100d,
                    1d,
                    1d,
                    index);
                client.UpdateReconnectPolicy(last);
            }

            Ensure(last is not null && client.GetReconnectPolicy() == last,
                "rapid updates must atomically expose only the latest complete policy");
            Ensure(time.CreatedTimerCount == 0,
                "policy publication before connectivity must not manufacture reconnect timers");

            await client.StopAsync();
            EnsureThrows<InvalidOperationException>(() =>
                client.UpdateReconnectPolicy(Policy(25, 250, 2d, 1d, 1d, 0)));
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    private static ISharpLinkClient BuildDynamicClient(
        ManualTimeProvider time,
        IClientTransportFactory factory,
        SharpLinkReconnectPolicy policy,
        int port)
        => SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseTimeProvider(time)
            .UseReconnectPolicy(policy)
            .UseEndpointResolver(
                new FixedResolver(new SharpLinkEndpointSnapshot(1, [Endpoint("dynamic", port)])),
                _ => factory)
            .Build();

    private static async Task EnsureInitialConnectFailureAsync(ISharpLinkClient client)
    {
        try
        {
            await client.ConnectAsync();
            throw new Exception("expected initial dynamic connect failure");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable,
                "initial dynamic transport failure must surface as Unavailable");
        }
    }

    private static async Task WaitForConnectCountAsync(FailCountThenBlockTransportFactory factory, int target)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (factory.ConnectCount < target)
            await Task.Delay(1, timeout.Token).ConfigureAwait(false);
    }

    private static async Task YieldAsync()
    {
        for (var index = 0; index < 8; index++)
            await Task.Yield();
    }

    private static SharpLinkReconnectPolicy Policy(
        int initialMilliseconds,
        int maxMilliseconds,
        double multiplier,
        double jitterMinimum,
        double jitterMaximum,
        int stableResetMilliseconds)
        => new(
            TimeSpan.FromMilliseconds(initialMilliseconds),
            TimeSpan.FromMilliseconds(maxMilliseconds),
            multiplier,
            jitterMinimum,
            jitterMaximum,
            TimeSpan.FromMilliseconds(stableResetMilliseconds));

    private static SharpLinkEndpoint Endpoint(string id, int port) => new()
    {
        Id = id,
        Address = new SharpLinkTcpAddress("127.0.0.1", port)
    };

    private static void EnsureThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new Exception($"expected {typeof(TException).Name}");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class FixedResolver(SharpLinkEndpointSnapshot snapshot) : ISharpLinkEndpointResolver
    {
        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(snapshot);

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NeverUsedTransportFactory : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(
                new InvalidOperationException("transport must not be used by this test"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailCountThenBlockTransportFactory(int failuresBeforeBlock) : IClientTransportFactory
    {
        private readonly TaskCompletionSource _blockedAttemptEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;

        public int ConnectCount => Volatile.Read(ref _connectCount);
        public Task BlockedAttemptEntered => _blockedAttemptEntered.Task;

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _connectCount);
            if (attempt <= failuresBeforeBlock)
            {
                return ValueTask.FromException<ITransportConnection>(
                    new IOException($"test reconnect failure {attempt}"));
            }
            return new ValueTask<ITransportConnection>(BlockAsync(cancellationToken));
        }

        private async Task<ITransportConnection> BlockAsync(CancellationToken cancellationToken)
        {
            _blockedAttemptEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable blocked reconnect state");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InFlightFailureTransportFactory : IClientTransportFactory
    {
        private readonly TaskCompletionSource _inFlightAttemptEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseInFlightFailure =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finalAttemptEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;

        public int ConnectCount => Volatile.Read(ref _connectCount);
        public Task InFlightAttemptEntered => _inFlightAttemptEntered.Task;
        public Task FinalAttemptEntered => _finalAttemptEntered.Task;

        public void ReleaseInFlightFailure() => _releaseInFlightFailure.TrySetResult();

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _connectCount);
            return attempt switch
            {
                1 => ValueTask.FromException<ITransportConnection>(new IOException("initial test failure")),
                2 => new ValueTask<ITransportConnection>(FailInFlightAsync(cancellationToken)),
                _ => new ValueTask<ITransportConnection>(BlockFinalAsync(cancellationToken))
            };
        }

        private async Task<ITransportConnection> FailInFlightAsync(CancellationToken cancellationToken)
        {
            _inFlightAttemptEntered.TrySetResult();
            await _releaseInFlightFailure.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new IOException("released in-flight reconnect failure");
        }

        private async Task<ITransportConnection> BlockFinalAsync(CancellationToken cancellationToken)
        {
            _finalAttemptEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable final reconnect state");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly SemaphoreSlim _timerCreated = new(0);
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
        private long _timestamp;
        private int _createdTimerCount;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public int CreatedTimerCount => Volatile.Read(ref _createdTimerCount);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _utcNow;
        }

        public override long GetTimestamp()
        {
            lock (_gate)
                return _timestamp;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
                timer.ChangeLocked(dueTime, period);
                Interlocked.Increment(ref _createdTimerCount);
            }
            _timerCreated.Release();
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);
            lock (_gate)
            {
                _timestamp = checked(_timestamp + amount.Ticks);
                _utcNow += amount;
            }

            while (true)
            {
                List<(TimerCallback Callback, object? State)> due = [];
                lock (_gate)
                {
                    for (var index = 0; index < _timers.Count; index++)
                    {
                        var timer = _timers[index];
                        if (timer.TryClaimLocked(_timestamp, out var callback, out var state))
                            due.Add((callback, state));
                    }
                }
                if (due.Count == 0)
                    return;
                foreach (var item in due)
                    item.Callback(item.State);
            }
        }

        public async Task WaitForCreatedTimerCountAsync(int target)
        {
            while (Volatile.Read(ref _createdTimerCount) < target)
            {
                if (!await _timerCreated.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
                    throw new TimeoutException($"manual time provider did not create timer {target}");
            }
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private long _dueTimestamp = long.MaxValue;
            private long _periodTicks = Timeout.InfiniteTimeSpan.Ticks;
            private bool _disposed;

            internal ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_owner._gate)
                {
                    if (_disposed)
                        return false;
                    ChangeLocked(dueTime, period);
                    return true;
                }
            }

            internal void ChangeLocked(TimeSpan dueTime, TimeSpan period)
            {
                _periodTicks = period == Timeout.InfiniteTimeSpan ? long.MaxValue : period.Ticks;
                _dueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : checked(_owner._timestamp + Math.Max(0L, dueTime.Ticks));
            }

            internal bool TryClaimLocked(long now, out TimerCallback callback, out object? state)
            {
                callback = _callback;
                state = _state;
                if (_disposed || _dueTimestamp == long.MaxValue || _dueTimestamp > now)
                    return false;

                if (_periodTicks == long.MaxValue || _periodTicks <= 0)
                {
                    _dueTimestamp = long.MaxValue;
                }
                else
                {
                    var next = _dueTimestamp;
                    do
                        next = checked(next + _periodTicks);
                    while (next <= now);
                    _dueTimestamp = next;
                }
                return true;
            }

            public void Dispose()
            {
                lock (_owner._gate)
                {
                    if (_disposed)
                        return;
                    _disposed = true;
                    _dueTimestamp = long.MaxValue;
                    _owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
