using System.Runtime.CompilerServices;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkReconnectPolicyTests
{
    [Test]
    public async Task LegacyDefaultsShouldRemainTopologyCompatible()
    {
        await using var fixedClient = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseTransport(new NeverUsedTransportFactory())
            .Build();
        EnsurePolicy(
            fixedClient.GetReconnectPolicy(),
            initialMilliseconds: 100,
            maxMilliseconds: 5_000,
            multiplier: 2d,
            jitterMinimum: 0.8d,
            jitterMaximum: 1.2d,
            stableResetMilliseconds: 30_000,
            "fixed legacy reconnect policy");

        await using var singleStaticClient = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseEndpoint(Endpoint("single", 5101), _ => new NeverUsedTransportFactory())
            .Build();
        EnsurePolicy(
            singleStaticClient.GetReconnectPolicy(),
            100,
            5_000,
            2d,
            0.8d,
            1.2d,
            30_000,
            "single-static legacy reconnect policy");

        await using var staticClient = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseEndpoints(
                [Endpoint("static-a", 5102), Endpoint("static-b", 5103)],
                _ => new NeverUsedTransportFactory())
            .Build();
        EnsurePolicy(
            staticClient.GetReconnectPolicy(),
            100,
            5_000,
            2d,
            1d,
            1.25d,
            0,
            "multi-static legacy reconnect policy");

        await using var dynamicClient = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseEndpointResolver(
                new FixedResolver(new SharpLinkEndpointSnapshot(1, [Endpoint("dynamic", 5104)])),
                _ => new NeverUsedTransportFactory())
            .Build();
        EnsurePolicy(
            dynamicClient.GetReconnectPolicy(),
            100,
            5_000,
            2d,
            1d,
            1.25d,
            0,
            "dynamic legacy reconnect policy");
    }

    [Test]
    public async Task PolicyUpdateBeforeInitialConnectMustNotCreateAReconnectOwner()
    {
        var policy = Policy(25, 250, 2d, 1d, 1d, 0);

        var fixedTime = new ManualTimeProvider();
        var fixedCounter = new ConnectCounter();
        await using var fixedClient = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseTimeProvider(fixedTime)
            .UseTransport(new CountingTransportFactory(fixedCounter))
            .Build();
        fixedClient.UpdateReconnectPolicy(policy);

        var staticTime = new ManualTimeProvider();
        var staticCounter = new ConnectCounter();
        await using var staticClient = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseTimeProvider(staticTime)
            .UseEndpoints(
                [Endpoint("static-a", 5151), Endpoint("static-b", 5152)],
                _ => new CountingTransportFactory(staticCounter))
            .Build();
        staticClient.UpdateReconnectPolicy(policy);

        var dynamicTime = new ManualTimeProvider();
        var dynamicCounter = new ConnectCounter();
        await using var dynamicClient = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseTimeProvider(dynamicTime)
            .UseEndpointResolver(
                new FixedResolver(new SharpLinkEndpointSnapshot(1, [Endpoint("dynamic", 5153)])),
                _ => new CountingTransportFactory(dynamicCounter))
            .Build();
        dynamicClient.UpdateReconnectPolicy(policy);

        for (var index = 0; index < 8; index++)
            await Task.Yield();
        fixedTime.Advance(TimeSpan.FromDays(1));
        staticTime.Advance(TimeSpan.FromDays(1));
        dynamicTime.Advance(TimeSpan.FromDays(1));
        for (var index = 0; index < 8; index++)
            await Task.Yield();

        Ensure(fixedCounter.Count == 0, "fixed policy update before ConnectAsync must not dial");
        Ensure(staticCounter.Count == 0, "static policy update before ConnectAsync must not dial");
        Ensure(dynamicCounter.Count == 0, "dynamic policy update before ConnectAsync must not dial");
        Ensure(fixedTime.CreatedTimerCount == 0, "fixed policy update must not create a reconnect wait before initial connect");
        Ensure(staticTime.CreatedTimerCount == 0, "static policy update must not create a reconnect wait before initial connect");
        Ensure(dynamicTime.CreatedTimerCount == 0, "dynamic policy update must not create a reconnect wait before initial connect");
    }

    [Test]
    public async Task ExplicitPolicyShouldHaveIdenticalSnapshotAcrossEndpointModes()
    {
        var policy = Policy(
            initialMilliseconds: 37,
            maxMilliseconds: 901,
            multiplier: 1.7d,
            jitterMinimum: 0.9d,
            jitterMaximum: 1.1d,
            stableResetMilliseconds: 412);

        await using var fixedClient = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseReconnectPolicy(policy)
            .UseTransport(new NeverUsedTransportFactory())
            .Build();
        await using var staticClient = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseReconnectPolicy(policy)
            .UseEndpoints(
                [Endpoint("static-a", 5201), Endpoint("static-b", 5202)],
                _ => new NeverUsedTransportFactory())
            .Build();
        await using var dynamicClient = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseReconnectPolicy(policy)
            .UseEndpointResolver(
                new FixedResolver(new SharpLinkEndpointSnapshot(1, [Endpoint("dynamic", 5203)])),
                _ => new NeverUsedTransportFactory())
            .Build();

        Ensure(fixedClient.GetReconnectPolicy() == policy, "fixed endpoint must publish the explicit policy");
        Ensure(staticClient.GetReconnectPolicy() == policy, "static cluster must publish the explicit policy");
        Ensure(dynamicClient.GetReconnectPolicy() == policy, "dynamic cluster must publish the explicit policy");
    }

    [Test]
    public async Task RuntimeReconnectUpdateShouldRemainIndependentFromRetryGeneration()
    {
        await using var client = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseRetry(options =>
            {
                options.MaxAttempts = 4;
                options.InitialBackoff = TimeSpan.FromMilliseconds(13);
                options.MaxBackoff = TimeSpan.FromMilliseconds(89);
                options.JitterRatio = 0.15d;
            })
            .UseTransport(new NeverUsedTransportFactory())
            .Build();

        var retryBefore = client.GetRetryPolicySnapshot();
        var reconnect = Policy(17, 221, 1.6d, 0.95d, 1.05d, 333);
        client.UpdateReconnectPolicy(reconnect);
        var retryAfterReconnectUpdate = client.GetRetryPolicySnapshot();

        Ensure(retryAfterReconnectUpdate == retryBefore,
            "reconnect publication must not mutate the logical-RPC retry generation");
        Ensure(client.GetReconnectPolicy() == reconnect,
            "reconnect publication must expose the complete immutable policy");

        client.UpdateRetryPolicy(new SharpLinkRetryOptions
        {
            MaxAttempts = 2,
            InitialBackoff = TimeSpan.FromMilliseconds(5),
            MaxBackoff = TimeSpan.FromMilliseconds(20),
            JitterRatio = 0d
        });
        Ensure(client.GetReconnectPolicy() == reconnect,
            "retry publication must not mutate the reconnect generation");
    }

    [Test]
    public async Task ArmedDynamicReconnectWaitShouldRescheduleAgainstNewPolicyWithoutDuplicateDial()
    {
        var time = new ManualTimeProvider();
        var factory = new FailThenBlockTransportFactory();
        var resolver = new FixedResolver(
            new SharpLinkEndpointSnapshot(1, [Endpoint("dynamic", 5301)]));
        var original = Policy(10_000, 10_000, 1d, 1d, 1d, 0);
        var updated = Policy(1_000, 1_000, 1d, 1d, 1d, 0);
        var client = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseTimeProvider(time)
            .UseReconnectPolicy(original)
            .UseEndpointResolver(resolver, _ => factory)
            .Build();

        try
        {
            await EnsureInitialConnectFailureAsync(client);
            await time.WaitForCreatedTimerCountAsync(1);
            Ensure(factory.ConnectCount == 1,
                "the first reconnect wait must not dial before its timer expires");

            client.UpdateReconnectPolicy(updated);
            await time.WaitForCreatedTimerCountAsync(2);
            Ensure(factory.ConnectCount == 1,
                "publishing a new policy must wake/reschedule the wait without starting an eager duplicate dial");

            time.Advance(TimeSpan.FromMilliseconds(999));
            await Task.Yield();
            Ensure(factory.ConnectCount == 1,
                "new reconnect delay must remain armed until the published delay expires");

            time.Advance(TimeSpan.FromMilliseconds(1));
            await factory.ReconnectEntered.WaitAsync(TimeSpan.FromSeconds(3));
            Ensure(factory.ConnectCount == 2,
                "exactly one reconnect dial must start when the rescheduled wait expires");
        }
        finally
        {
            await client.StopAsync();
            await client.DisposeAsync();
        }

        Ensure(factory.ConnectCount == 2,
            "stop after a blocked reconnect must not manufacture an extra reconnect owner");
    }

    [Test]
    public async Task PolicyUpdateMustNotCancelReconnectDialThatAlreadyStarted()
    {
        var time = new ManualTimeProvider();
        var factory = new FailThenBlockTransportFactory();
        var client = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseTimeProvider(time)
            .UseReconnectPolicy(Policy(1_000, 4_000, 2d, 1d, 1d, 0))
            .UseEndpointResolver(
                new FixedResolver(new SharpLinkEndpointSnapshot(1, [Endpoint("dynamic", 5401)])),
                _ => factory)
            .Build();

        try
        {
            await EnsureInitialConnectFailureAsync(client);
            await time.WaitForCreatedTimerCountAsync(1);
            time.Advance(TimeSpan.FromSeconds(1));
            await factory.ReconnectEntered.WaitAsync(TimeSpan.FromSeconds(3));
            Ensure(factory.ConnectCount == 2, "second dial must be the topology-owned reconnect attempt");

            client.UpdateReconnectPolicy(Policy(25, 250, 2d, 1d, 1d, 0));
            await Task.Yield();
            await Task.Yield();

            Ensure(!factory.ActiveReconnectCancellation.IsCompleted,
                "policy publication must not cancel a ConnectAsync attempt that already crossed the dial boundary");
            Ensure(factory.ConnectCount == 2,
                "policy publication during an active dial must not start a parallel reconnect attempt");

            await client.StopAsync();
            await factory.ActiveReconnectCancellation.WaitAsync(TimeSpan.FromSeconds(3));
            Ensure(factory.ConnectCount == 2,
                "stop cancellation must terminate the existing dial without scheduling another owner");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public void PolicyValidationShouldRejectInvalidBounds()
    {
        EnsureThrows<ArgumentOutOfRangeException>(() =>
            new SharpLinkReconnectPolicy(TimeSpan.Zero, TimeSpan.FromSeconds(1), 2d, 1d, 1d, TimeSpan.Zero));
        EnsureThrows<ArgumentOutOfRangeException>(() =>
            new SharpLinkReconnectPolicy(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), 2d, 1d, 1d, TimeSpan.Zero));
        EnsureThrows<ArgumentOutOfRangeException>(() =>
            new SharpLinkReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 0.9d, 1d, 1d, TimeSpan.Zero));
        EnsureThrows<ArgumentOutOfRangeException>(() =>
            new SharpLinkReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 2d, 0d, 1d, TimeSpan.Zero));
        EnsureThrows<ArgumentOutOfRangeException>(() =>
            new SharpLinkReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 2d, 1.1d, 1d, TimeSpan.Zero));
        EnsureThrows<ArgumentOutOfRangeException>(() =>
            new SharpLinkReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 2d, 1d, 1d, TimeSpan.FromTicks(-1)));
    }

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

    private static void EnsurePolicy(
        SharpLinkReconnectPolicy policy,
        int initialMilliseconds,
        int maxMilliseconds,
        double multiplier,
        double jitterMinimum,
        double jitterMaximum,
        int stableResetMilliseconds,
        string name)
    {
        Ensure(policy.InitialDelay == TimeSpan.FromMilliseconds(initialMilliseconds), $"{name}: initial delay");
        Ensure(policy.MaxBackoff == TimeSpan.FromMilliseconds(maxMilliseconds), $"{name}: max backoff");
        Ensure(policy.BackoffMultiplier == multiplier, $"{name}: multiplier");
        Ensure(policy.JitterMinimumFactor == jitterMinimum, $"{name}: jitter minimum");
        Ensure(policy.JitterMaximumFactor == jitterMaximum, $"{name}: jitter maximum");
        Ensure(policy.StableResetWindow == TimeSpan.FromMilliseconds(stableResetMilliseconds), $"{name}: stable reset");
    }

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

    private sealed class ConnectCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class CountingTransportFactory(ConnectCounter counter) : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            counter.Increment();
            return ValueTask.FromException<ITransportConnection>(new IOException("unexpected initial-connect dial"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NeverUsedTransportFactory : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(
                new InvalidOperationException("transport must not be used by snapshot-only test"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    private sealed class FailThenBlockTransportFactory : IClientTransportFactory
    {
        private readonly TaskCompletionSource _reconnectEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _activeReconnectCancellation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;

        public Task ReconnectEntered => _reconnectEntered.Task;
        public Task ActiveReconnectCancellation => _activeReconnectCancellation.Task;
        public int ConnectCount => Volatile.Read(ref _connectCount);

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _connectCount);
            if (attempt == 1)
            {
                return ValueTask.FromException<ITransportConnection>(
                    new IOException("test initial dynamic dial failure"));
            }
            return new ValueTask<ITransportConnection>(BlockReconnectAsync(cancellationToken));
        }

        private async Task<ITransportConnection> BlockReconnectAsync(CancellationToken cancellationToken)
        {
            _reconnectEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _activeReconnectCancellation.TrySetResult();
                throw;
            }
            throw new InvalidOperationException("unreachable reconnect test state");
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

            internal bool TryClaimLocked(
                long now,
                out TimerCallback callback,
                out object? state)
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
