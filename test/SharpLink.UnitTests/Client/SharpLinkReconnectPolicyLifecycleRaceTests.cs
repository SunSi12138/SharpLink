using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SharpLink.Client;
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleSharedSupport;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkReconnectPolicyLifecycleRaceTests
{
    [Test]
    public async Task FixedReconnectSuccessShouldResetTheNextFailureSequenceAfterPolicyUpdate()
    {
        var time = new ManualTimeProvider();
        var transport = new SequenceClientTransportFactory(failedConnectsAfterInitial: 1);
        var client = ClientBuilderTestHelper.Build(transport, builder =>
        {
            builder.UseTimeProvider(time);
            builder.UseHeartbeat(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60));
            builder.UseReconnectPolicy(Policy(1_000, 8_000, 2d, 0));
        });

        try
        {
            await client.ConnectAsync();
            var initial = await transport.WaitForConnectionAsync(0);
            var beforeFirstDisconnect = time.CreatedTimerCount;
            await InjectGoAwayAsync(initial);
            await WaitUntilAsync(() => client.ReadyConnectionCount == 0);
            await time.WaitForCreatedTimerCountAsync(beforeFirstDisconnect + 1);

            var beforeFirstFailure = time.CreatedTimerCount;
            time.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(
                () => transport.ConnectCount == 2,
                () => $"first fixed reconnect attempt did not run; connects={transport.ConnectCount}");
            await time.WaitForCreatedTimerCountAsync(beforeFirstFailure + 1);

            // Publish a genuinely different generation while the two-second live backoff is armed.
            // The update must preserve that active position, then a successful reconnect under the
            // replacement generation must reset the next failure sequence to its one-second initial.
            var beforePolicyWake = time.CreatedTimerCount;
            client.UpdateReconnectPolicy(Policy(1_000, 10_000, 3d, 0));
            await time.WaitForCreatedTimerCountAsync(beforePolicyWake + 1);

            time.Advance(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => client.ReadyConnectionCount == 1 && transport.ConnectCount == 3,
                () => $"fixed reconnect did not recover; ready={client.ReadyConnectionCount}, connects={transport.ConnectCount}");

            var recovered = await transport.WaitForConnectionAsync(1);
            var beforeSecondDisconnect = time.CreatedTimerCount;
            await InjectGoAwayAsync(recovered);
            await WaitUntilAsync(() => client.ReadyConnectionCount == 0);
            await time.WaitForCreatedTimerCountAsync(beforeSecondDisconnect + 1);

            time.Advance(TimeSpan.FromMilliseconds(999));
            await YieldAsync();
            Ensure(transport.ConnectCount == 3,
                "a successful reconnect with zero StableResetWindow must reset the next sequence to InitialDelay");

            time.Advance(TimeSpan.FromMilliseconds(1));
            await WaitUntilAsync(
                () => client.ReadyConnectionCount == 1 && transport.ConnectCount == 4,
                () => $"next fixed reconnect did not use the reset one-second delay; ready={client.ReadyConnectionCount}, connects={transport.ConnectCount}");
        }
        finally
        {
            await client.StopAsync();
        }
    }

    [Test]
    public async Task PolicyWakeShouldComposeWithFixedPoolShrinkAndLaterGrow()
    {
        var time = new ManualTimeProvider();
        var transport = new SequenceClientTransportFactory();
        var client = ClientBuilderTestHelper.Build(transport, builder =>
        {
            builder.UseTimeProvider(time);
            builder.UseHeartbeat(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60));
            builder.UseConnectionPool(options =>
            {
                options.MinConnections = 2;
                options.MaxConnections = 2;
            });
            builder.UseReconnectPolicy(Policy(10_000, 30_000, 2d, 0));
        });
        var runtime = (ISharpLinkClient)client;

        try
        {
            await client.ConnectAsync();
            Ensure(client.ReadyConnectionCount == 2 && transport.ConnectCount == 2,
                "fixed pool race setup requires two ready connections");
            var first = await transport.WaitForConnectionAsync(0);
            var beforeDisconnect = time.CreatedTimerCount;
            await InjectGoAwayAsync(first);
            await WaitUntilAsync(() => client.ReadyConnectionCount == 1);
            await time.WaitForCreatedTimerCountAsync(beforeDisconnect + 1);

            runtime.UpdateFixedConnectionPoolSizing(1, 1);
            runtime.UpdateReconnectPolicy(Policy(1_000, 1_000, 1d, 0));
            await YieldAsync();
            time.Advance(TimeSpan.FromSeconds(2));
            await YieldAsync();

            Ensure(client.ReadyConnectionCount == 1 && transport.ConnectCount == 2,
                "shrinking the authoritative pool target before the policy wake must prevent stale reconnect capacity");

            var beforeGrow = time.CreatedTimerCount;
            runtime.UpdateFixedConnectionPoolSizing(2, 2);
            // Growth starts the existing reconnect owner and the pool-sizing reconciliation timer.
            await time.WaitForCreatedTimerCountAsync(beforeGrow + 2);
            time.Advance(TimeSpan.FromMilliseconds(999));
            await YieldAsync();
            Ensure(transport.ConnectCount == 2,
                "pool growth must still honor the latest reconnect delay instead of dialing eagerly");

            time.Advance(TimeSpan.FromMilliseconds(1));
            await WaitUntilAsync(
                () => client.ReadyConnectionCount == 2 && transport.ConnectCount == 3,
                () => $"pool growth did not converge through the existing reconnect owner; ready={client.ReadyConnectionCount}, connects={transport.ConnectCount}");
        }
        finally
        {
            await client.StopAsync();
        }
    }

    [Test]
    public async Task DynamicGenerationReplacementShouldRetireTheOldReconnectOwnerAcrossPolicyWake()
    {
        var time = new ManualTimeProvider();
        var resolver = new ControllableResolver(
            new SharpLinkEndpointSnapshot(1, [CreateEndpoint("old", 5701)]));
        var oldFactory = new CountingFailFactory();
        var newFactory = new FailOnceThenBlockFactory();
        var replacementFactoryMaterialized = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = ClientBuilderTestHelper.BuildDynamic(
            resolver,
            endpoint =>
            {
                if (endpoint.Id == "old")
                    return oldFactory;
                replacementFactoryMaterialized.TrySetResult();
                return newFactory;
            },
            builder =>
            {
                builder.UseTimeProvider(time);
                builder.UseHeartbeat(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60));
                builder.UseReconnectPolicy(Policy(10_000, 10_000, 1d, 0));
            });

        try
        {
            var initial = await CaptureSharpLinkExceptionAsync(client.ConnectAsync().AsTask());
            Ensure(initial.Code == SharpLinkErrorCode.Unavailable,
                "old dynamic generation must fail the initial connection attempt");
            await time.WaitForCreatedTimerCountAsync(1);
            Ensure(oldFactory.ConnectCount == 1, "old generation initial dial count");
            var beforeReplacement = time.CreatedTimerCount;

            resolver.Publish(new SharpLinkEndpointSnapshot(2, [CreateEndpoint("new", 5702)]));
            client.UpdateReconnectPolicy(Policy(1_000, 1_000, 1d, 0));
            await replacementFactoryMaterialized.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await resolver.PublishedSnapshotProcessed.WaitAsync(TimeSpan.FromSeconds(3));

            // Snapshot processing includes topology commit plus reconnect reconciliation. Depending on
            // race order, the retired owner's cancelled wait may exit before ever re-arming, so only
            // require that the current generation has armed at least one replacement wait.
            var beforeFirstReplacementDial = time.CreatedTimerCount;
            Ensure(beforeFirstReplacementDial > beforeReplacement,
                "the accepted replacement generation must arm a reconnect wait");

            time.Advance(TimeSpan.FromMilliseconds(999));
            await YieldAsync();
            Ensure(oldFactory.ConnectCount == 1 && newFactory.ConnectCount == 0,
                "replacement generation reconnect must not dial before the latest one-second delay");

            time.Advance(TimeSpan.FromMilliseconds(1));
            await WaitUntilAsync(
                () => newFactory.ConnectCount == 1,
                () => $"replacement endpoint first reconnect did not run; old={oldFactory.ConnectCount}, new={newFactory.ConnectCount}");
            Ensure(oldFactory.ConnectCount == 1,
                "policy wake must not revive a reconnect owner from a retired dynamic endpoint generation");

            await time.WaitForCreatedTimerCountAsync(beforeFirstReplacementDial + 1);
            time.Advance(TimeSpan.FromSeconds(1));
            await newFactory.BlockedAttemptEntered.WaitAsync(TimeSpan.FromSeconds(3));
            Ensure(oldFactory.ConnectCount == 1,
                "only the current dynamic generation may keep reconnect ownership after replacement");
            Ensure(newFactory.ConnectCount == 2,
                "the current dynamic generation must own the subsequent reconnect attempt");

            time.Advance(TimeSpan.FromSeconds(20));
            await YieldAsync();
            Ensure(oldFactory.ConnectCount == 1,
                "advancing beyond the retired generation's original delay must not produce a stale dial");
        }
        finally
        {
            await client.StopAsync();
        }
    }

    [Test]
    public async Task FailedReadyPublicationShouldNotStartDynamicStableResetWindow()
    {
        var time = new ManualTimeProvider();
        var transport = new SequenceClientTransportFactory();
        var resolver = new ControllableResolver(
            new SharpLinkEndpointSnapshot(1, [CreateEndpoint("rollback", 5703)]));
        var client = ClientBuilderTestHelper.BuildDynamic(
            resolver,
            _ => transport,
            builder =>
            {
                builder.UseTimeProvider(time);
                builder.UseHeartbeat(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60));
                builder.UseReconnectPolicy(Policy(1_000, 8_000, 2d, 5_000));
            });
        var implementation = (SharpLinkClient)client;
        var reconciliationAttempt = 0;
        var timerCountAtLastRollback = 0;
        implementation.BeforeResponseCompressionReadyReconciliationTestHook = () =>
        {
            var attempt = Interlocked.Increment(ref reconciliationAttempt);
            if (attempt <= 2)
            {
                Volatile.Write(ref timerCountAtLastRollback, time.CreatedTimerCount);
                throw new IOException($"forced Ready reconciliation rollback {attempt}");
            }
        };

        try
        {
            var initial = await CaptureSharpLinkExceptionAsync(client.ConnectAsync().AsTask());
            Ensure(initial.Code == SharpLinkErrorCode.Unavailable,
                "the first Ready reconciliation failure must surface as an unavailable initial connection");
            Ensure(transport.ConnectCount == 1 && client.ReadyConnectionCount == 0,
                "failed initial Ready publication must leave the dynamic endpoint with no Ready connection");
            var firstRollbackTimerBaseline = Volatile.Read(ref timerCountAtLastRollback);
            await time.WaitForCreatedTimerCountAsync(firstRollbackTimerBaseline + 1);

            time.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(
                () => Volatile.Read(ref reconciliationAttempt) >= 2,
                () => $"second Ready reconciliation did not run; attempts={Volatile.Read(ref reconciliationAttempt)}, connects={transport.ConnectCount}");
            await WaitUntilAsync(
                () => client.ReadyConnectionCount == 0,
                () => $"second Ready reconciliation did not roll back its candidate; ready={client.ReadyConnectionCount}, attempts={Volatile.Read(ref reconciliationAttempt)}");
            Ensure(transport.ConnectCount == 2,
                "the second Ready reconciliation failure must come from exactly one reconnect attempt");
            var secondRollbackTimerBaseline = Volatile.Read(ref timerCountAtLastRollback);
            await time.WaitForCreatedTimerCountAsync(secondRollbackTimerBaseline + 1);

            time.Advance(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => transport.ConnectCount == 3 && client.ReadyConnectionCount == 1,
                () => $"third Ready publication did not succeed; ready={client.ReadyConnectionCount}, connects={transport.ConnectCount}, attempts={Volatile.Read(ref reconciliationAttempt)}");
            Ensure(reconciliationAttempt == 3,
                "exactly two Ready reconciliation attempts must roll back before the successful publication");

            // At t=5 the pre-fix stale anchor from the failed t=0 candidate would satisfy the
            // five-second stable window. The real continuously-Ready period only began at t=3.
            time.Advance(TimeSpan.FromSeconds(2));
            var recovered = await transport.WaitForConnectionAsync(2);
            var beforeDisconnect = time.CreatedTimerCount;
            await InjectGoAwayAsync(recovered);
            await WaitUntilAsync(
                () => client.ReadyConnectionCount == 0,
                () => $"GoAway did not remove the successfully published dynamic connection; ready={client.ReadyConnectionCount}");
            await time.WaitForCreatedTimerCountAsync(beforeDisconnect + 1);

            time.Advance(TimeSpan.FromMilliseconds(1_999));
            await YieldAsync();
            Ensure(transport.ConnectCount == 3,
                "failed Ready publications must not fabricate stability and reset the two-second live backoff");

            time.Advance(TimeSpan.FromMilliseconds(1));
            await WaitUntilAsync(
                () => transport.ConnectCount == 4,
                () => $"preserved two-second reconnect delay did not fire; connects={transport.ConnectCount}");
        }
        finally
        {
            implementation.BeforeResponseCompressionReadyReconciliationTestHook = null;
            await client.StopAsync();
        }
    }

    private static SharpLinkReconnectPolicy Policy(
        int initialMilliseconds,
        int maxMilliseconds,
        double multiplier,
        int stableResetMilliseconds)
        => new(
            TimeSpan.FromMilliseconds(initialMilliseconds),
            TimeSpan.FromMilliseconds(maxMilliseconds),
            multiplier,
            1d,
            1d,
            TimeSpan.FromMilliseconds(stableResetMilliseconds));

    private static async Task InjectGoAwayAsync(TestTransportConnection connection)
    {
        using var payload = new PooledByteBufferWriter();
        var lastAccepted = payload.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(lastAccepted, 0);
        payload.Advance(sizeof(ulong));
        ProtocolV2PayloadCodec.WriteError(
            payload,
            SharpLinkErrorCode.Unavailable,
            "reconnect-policy lifecycle race",
            1024,
            out _);
        await connection.InjectFrameAsync(
            ProtocolV2FrameType.GoAway,
            ProtocolV2FrameFlags.Error,
            0,
            payload.WrittenMemory);
    }

    private static async Task YieldAsync()
    {
        for (var index = 0; index < 8; index++)
            await Task.Yield();
    }

    private sealed class CountingFailFactory : IClientTransportFactory
    {
        private int _connectCount;
        public int ConnectCount => Volatile.Read(ref _connectCount);

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            return ValueTask.FromException<ITransportConnection>(new IOException("test endpoint failure"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailOnceThenBlockFactory : IClientTransportFactory
    {
        private readonly TaskCompletionSource _blockedAttemptEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;

        public int ConnectCount => Volatile.Read(ref _connectCount);
        public Task BlockedAttemptEntered => _blockedAttemptEntered.Task;

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _connectCount);
            if (attempt == 1)
            {
                return ValueTask.FromException<ITransportConnection>(
                    new IOException("replacement endpoint initial failure"));
            }
            return new ValueTask<ITransportConnection>(BlockAsync(cancellationToken));
        }

        private async Task<ITransportConnection> BlockAsync(CancellationToken cancellationToken)
        {
            _blockedAttemptEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable replacement reconnect state");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControllableResolver(SharpLinkEndpointSnapshot initial) : ISharpLinkEndpointResolver
    {
        private readonly Channel<SharpLinkEndpointSnapshot> _snapshots =
            Channel.CreateUnbounded<SharpLinkEndpointSnapshot>();
        private readonly TaskCompletionSource _publishedSnapshotProcessed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PublishedSnapshotProcessed => _publishedSnapshotProcessed.Task;

        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(initial);

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (await _snapshots.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_snapshots.Reader.TryRead(out var snapshot))
                {
                    yield return snapshot;
                    // The consumer only asks the iterator for the next value after its await-foreach
                    // body has completed, so this is an acknowledgement that ApplySnapshotAsync and
                    // the subsequent reconnect reconciliation have both returned.
                    _publishedSnapshotProcessed.TrySetResult();
                }
            }
        }

        public void Publish(SharpLinkEndpointSnapshot snapshot)
            => _snapshots.Writer.TryWrite(snapshot);

        public ValueTask DisposeAsync()
        {
            _snapshots.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
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
                    foreach (var timer in _timers)
                    {
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
