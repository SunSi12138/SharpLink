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
        try
        {
            await server.StartAsync().AsTask().WaitAsync(guard);
            await client.ConnectAsync().AsTask().WaitAsync(guard);
            var service = client.Get<IStaticCancellationService>();
            var session = await service.GetSessionIdAsync().AsTask().WaitAsync(guard);
            Ensure(session != "missing-session", "the probe must run on a real server session");

            // No caller token, method deadline, dynamic registration, or interceptor is involved.
            // Wait for the second MoveNext before terminating, so EOF cannot masquerade as cancellation.
            if (earlyBreak)
            {
                async Task ConsumeOneAsync()
                {
                    await foreach (var value in service.ReadAsync(CancellationToken.None))
                    {
                        Ensure(value == 42, "first stream item must arrive before consumer abandonment");
                        await implementation.Waiting.Task.WaitAsync(guard);
                        break;
                    }
                }
                await ConsumeOneAsync().WaitAsync(guard);
            }
            else
            {
                await using var reader = service.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
                Ensure(await reader.MoveNextAsync().AsTask().WaitAsync(guard) && reader.Current == 42,
                    "first stream item must arrive before consumer abandonment");
                await implementation.Waiting.Task.WaitAsync(guard);
            }

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
        finally
        {
            // This release is teardown only, never evidence of consumer cancellation. It also
            // lets the unfixed baseline finish cleanup after the regression watchdog fails.
            implementation.Release.TrySetResult();
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

    private sealed class CooperativeEnumerator(StaticCancellationService owner, CancellationToken token)
        : IAsyncEnumerator<int>
    {
        private int _moves;
        private int _moving;
        public int Current => 42;

        public async ValueTask<bool> MoveNextAsync()
        {
            if (Interlocked.Increment(ref _moves) == 1)
                return true;
            Interlocked.Increment(ref _moving);
            owner.Waiting.TrySetResult();
            try
            {
                await owner.Release.Task.WaitAsync(token).ConfigureAwait(false);
                return false;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Interlocked.Increment(ref owner.CancellationObserved);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _moving);
                Interlocked.Increment(ref owner.MoveNextCompleted);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _moving) != 0)
                Interlocked.Increment(ref owner.DisposeOverlap);
            Interlocked.Increment(ref owner.DisposeCount);
            owner.Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
