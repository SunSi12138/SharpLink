using System.Threading.Tasks.Sources;

namespace SharpLink.IntegrationTests;

public sealed class StaticServerStreamCancellationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EarlyTerminationWithoutCallerTokenOrDeadlineMustCancelCooperativeProducer(bool earlyBreak)
    {
        var guard = TimeSpan.FromSeconds(10);
        var implementation = new StaticCancellationService();
        var builder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback)
            .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10))
            .ReplaceService<IStaticCancellationService>(implementation);
        var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
        await using var server = builder.Build();
        await using var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseConnectionPool(options => { options.MinConnections = 1; options.MaxConnections = 1; })
            .DisableRequestTimeout()
            .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10))
            .Build();
        Task? consumption = null;
        Exception? primaryFailure = null;
        try
        {
            await server.StartAsync().AsTask().WaitAsync(guard);
            await client.ConnectAsync().AsTask().WaitAsync(guard);
            var service = client.Get<IStaticCancellationService>();
            var session = await service.GetSessionIdAsync().AsTask().WaitAsync(guard);
            Ensure(session != "missing-session", "the probe must run on a real server session");

            // No caller token, method deadline, dynamic registration, or interceptor is involved.
            // Wait for the second MoveNext before terminating, so EOF cannot masquerade as cancellation.
            async Task ConsumeOneAsync()
            {
                if (earlyBreak)
                {
                    await foreach (var value in service.ReadAsync(CancellationToken.None))
                    {
                        Ensure(value == 42, "first stream item must arrive before consumer abandonment");
                        await implementation.Waiting.Task.WaitAsync(guard);
                        break;
                    }
                }
                else
                {
                    await using var reader = service.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
                    Ensure(await reader.MoveNextAsync().AsTask().WaitAsync(guard) && reader.Current == 42,
                        "first stream item must arrive before consumer abandonment");
                    await implementation.Waiting.Task.WaitAsync(guard);
                }
            }
            consumption = ConsumeOneAsync();
            await consumption.WaitAsync(guard);

            await implementation.Disposed.Task.WaitAsync(guard);
            Ensure(implementation.CancellationObserved == 1 && implementation.MoveNextCompleted == 1,
                "consumer abandonment must cancel and unwind the cooperative producer before shutdown");
            Ensure(implementation.DisposeCount == 1 && implementation.DisposeOverlap == 0,
                "the producer must be disposed exactly once after MoveNext has unwound");
            Ensure(!implementation.Release.Task.IsCompleted,
                "test cleanup must not be the cause of producer completion");
            Ensure(await service.GetSessionIdAsync().AsTask().WaitAsync(guard) == session,
                "early termination must leave the original TCP session usable");
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            // This release is teardown only, never evidence of consumer cancellation. It also
            // lets the unfixed baseline finish cleanup after the regression watchdog fails.
            implementation.Release.TrySetResult();
            if (consumption is not null)
            {
                try { await consumption.WaitAsync(guard); }
                catch (Exception cleanupFailure)
                {
                    // Observe a late fault even if the bounded join itself timed out. Keep
                    // the original test failure authoritative without discarding cleanup detail.
                    _ = consumption.ContinueWith(static task => { _ = task.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    if (primaryFailure is null)
                        throw;
                    primaryFailure.Data["StaticStreamConsumerCleanupFailure"] = cleanupFailure;
                }
            }
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

[RpcContract]
public interface IStaticCancellationService : IService
{
    IAsyncEnumerable<int> ReadAsync(CancellationToken cancellationToken);

    [NonCancellable]
    ValueTask<string> GetSessionIdAsync();
}

[RpcService]
public sealed class StaticCancellationService : IStaticCancellationService
{
    internal readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int CancellationObserved, MoveNextCompleted, DisposeCount, DisposeOverlap;

    public IAsyncEnumerable<int> ReadAsync(CancellationToken cancellationToken)
        => new CooperativeStream(this);

    public ValueTask<string> GetSessionIdAsync()
        => ValueTask.FromResult(SharpLinkCallContext.Current?.SessionId ?? "missing-session");

    private sealed class CooperativeStream(StaticCancellationService owner) : IAsyncEnumerable<int>
    {
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new CooperativeEnumerator(owner, cancellationToken);
    }

    private sealed class CooperativeEnumerator : IAsyncEnumerator<int>, IValueTaskSource<bool>
    {
        private readonly StaticCancellationService _owner;
        private readonly CancellationToken _token;
        private ManualResetValueTaskSourceCore<bool> _pending;
        private int _moves;
        private int _moving;

        internal CooperativeEnumerator(StaticCancellationService owner, CancellationToken token)
        {
            _owner = owner;
            _token = token;
            _pending.RunContinuationsAsynchronously = true;
            _pending.Reset();
        }

        public int Current => 42;

        public ValueTask<bool> MoveNextAsync()
        {
            var ordinal = Interlocked.Increment(ref _moves);
            if (ordinal == 1)
                return ValueTask.FromResult(true);
            if (ordinal != 2)
                throw new InvalidOperationException("The cooperative producer must terminate after its pending second move.");
            var wait = _owner.Release.Task.WaitAsync(_token);
            if (wait.IsCompleted)
                throw new InvalidOperationException("The producer must be pending before consumer termination.");
            Interlocked.Increment(ref _moving);
            _ = CompleteAsync(wait);
            return new ValueTask<bool>(this, _pending.Version);
        }

        private async Task CompleteAsync(Task wait)
        {
            Exception? failure = null;
            try { await wait.ConfigureAwait(false); }
            catch (OperationCanceledException exception) when (_token.IsCancellationRequested)
            {
                Interlocked.Increment(ref _owner.CancellationObserved);
                failure = exception;
            }
            catch (Exception exception) { failure = exception; }
            Interlocked.Decrement(ref _moving);
            Interlocked.Increment(ref _owner.MoveNextCompleted);
            if (failure is null)
                _pending.SetResult(false);
            else
                _pending.SetException(failure);
        }

        public bool GetResult(short token) => _pending.GetResult(token);
        public ValueTaskSourceStatus GetStatus(short token) => _pending.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            // The actual generated pump has registered its continuation on a genuinely
            // pending producer operation before the consumer is allowed to terminate.
            _pending.OnCompleted(continuation, state, token, flags);
            _owner.Waiting.TrySetResult();
        }

        public ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _moving) != 0)
                Interlocked.Increment(ref _owner.DisposeOverlap);
            Interlocked.Increment(ref _owner.DisposeCount);
            _owner.Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
