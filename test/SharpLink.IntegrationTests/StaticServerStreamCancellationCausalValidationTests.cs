using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace SharpLink.IntegrationTests;

// Validation-only companion. The published product and both regression fixtures stay byte-identical.
public sealed class StaticServerStreamCancellationCausalValidationTests
{
    private const string NegativeMarker = "STATIC_NONE_CANCEL_CAUSAL_V2: static request used connection cancellation instead of an independent call cancellation token; consumer completed but producer stayed pending for 10000ms; explicit release joined physical cleanup without overlap.";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EarlyTerminationHasCompletedCancellationCausalWitness(bool earlyBreak)
    {
        var guard = TimeSpan.FromSeconds(10);
        var implementation = new StaticCancellationService();
        var observation = new TokenObservationService(implementation);
        var builder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback)
            .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10))
            .ReplaceService<IStaticCancellationService>(observation);
        var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
        var server = (SharpLinkServer)builder.Build();
        var client = (SharpLinkClient)SharpClientBuilder.Create().UseTcp(IPAddress.Loopback.ToString(), port)
            .UseConnectionPool(options => { options.MinConnections = 1; options.MaxConnections = 1; })
            .DisableRequestTimeout().UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)).Build();
        var terminate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? consumption = null;
        Exception? failure = null;
        var cleanup = new List<Exception>();
        object? record = null;
        var negative = false;
        try
        {
            await server.StartAsync().AsTask().WaitAsync(guard);
            await client.ConnectAsync().AsTask().WaitAsync(guard);
            var service = client.Get<IStaticCancellationService>();
            var session = await service.GetSessionIdAsync().AsTask().WaitAsync(guard);
            Ensure(session != "missing-session", "missing real TCP session");
            async Task ConsumeAsync()
            {
                if (earlyBreak)
                {
                    await foreach (var value in service.ReadAsync(CancellationToken.None))
                    {
                        Ensure(value == 42, "wrong first item");
                        await implementation.Waiting.Task.WaitAsync(guard);
                        await terminate.Task.WaitAsync(guard);
                        break;
                    }
                }
                else
                {
                    await using var reader = service.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
                    Ensure(await reader.MoveNextAsync().AsTask().WaitAsync(guard) && reader.Current == 42,
                        "missing first item");
                    await implementation.Waiting.Task.WaitAsync(guard);
                    await terminate.Task.WaitAsync(guard);
                }
            }
            consumption = ConsumeAsync();
            await implementation.Waiting.Task.WaitAsync(guard);
            var registry = (ServerConnectionRegistry)(typeof(SharpLinkServer).GetField("_connectionRegistry",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!);
            var connection = registry.Values.Single();
            Ensure(connection.DefaultCallContext?.SessionId == session,
                "token comparison must use the actual producer connection and original session");
            Ensure(observation.MethodCalls == 1 && observation.EnumeratorCalls == 1,
                "the actual producer tokens must have been observed exactly once");
            var inputIsConnectionToken = observation.InputToken == connection.ConnectionToken;
            var inputCanBeCanceled = observation.InputToken.CanBeCanceled;
            var enumeratorCanBeCanceled = observation.EnumeratorToken.CanBeCanceled;
            Ensure(!observation.InputToken.IsCancellationRequested &&
                !observation.EnumeratorToken.IsCancellationRequested,
                "neither actual producer token may be pre-cancelled");
            Ensure(implementation.CancellationObserved == 0 && implementation.MoveNextCompleted == 0 &&
                implementation.DisposeCount == 0 && !implementation.Release.Task.IsCompleted,
                "producer must be genuinely pending before consumer termination");
            terminate.TrySetResult();
            await consumption.WaitAsync(guard);
            var watch = Stopwatch.StartNew();
            var completed = await Task.WhenAny(implementation.Disposed.Task, Task.Delay(guard)) == implementation.Disposed.Task;
            var elapsedMilliseconds = watch.Elapsed.TotalMilliseconds;
            Ensure(await service.GetSessionIdAsync().AsTask().WaitAsync(guard) == session,
                "the original session must stay healthy before rescue");
            var sameConnection = registry.Values.Count == 1 && ReferenceEquals(registry.Values.Single(), connection);
            Ensure(sameConnection, "connection replacement cannot count as consumer cancellation");
            Ensure(!implementation.Release.Task.IsCompleted, "rescue must not precede the observation");
            var before = new { cancellation = implementation.CancellationObserved,
                moveCompleted = implementation.MoveNextCompleted, disposed = implementation.DisposeCount,
                overlap = implementation.DisposeOverlap };
            if (completed)
            {
                Ensure(!inputIsConnectionToken && observation.InputToken.IsCancellationRequested &&
                    observation.EnumeratorToken.IsCancellationRequested && before.cancellation == 1 && before.moveCompleted == 1 &&
                    before.disposed == 1 && before.overlap == 0, "completed producer lacks exact cancellation witness");
            }
            else
            {
                // A completed observation window is only one prerequisite, never the negative oracle.
                Ensure(elapsedMilliseconds >= 10000 && inputIsConnectionToken &&
                    !observation.InputToken.IsCancellationRequested && !observation.EnumeratorToken.IsCancellationRequested && before.cancellation == 0 &&
                    before.moveCompleted == 0 && before.disposed == 0 && before.overlap == 0,
                    "incomplete producer is not the precise uncancellable baseline condition");
                negative = true;
            }
            var inputCancelledBeforeRelease = observation.InputToken.IsCancellationRequested;
            var enumeratorCancelledBeforeRelease = observation.EnumeratorToken.IsCancellationRequested;
            implementation.Release.TrySetResult();
            await implementation.Disposed.Task.WaitAsync(guard);
            Ensure(implementation.MoveNextCompleted == 1 && implementation.DisposeCount == 1 &&
                implementation.DisposeOverlap == 0 && implementation.CancellationObserved == (negative ? 0 : 1),
                "explicit rescue must finish exactly the held physical operation without overlap");
            Ensure(await service.GetSessionIdAsync().AsTask().WaitAsync(guard) == session,
                "physical completion must preserve the same session");
            record = new { schema = 1, earlyBreak, runtime = Environment.Version.ToString(),
                producerAwaitRegistered = true, consumerCompleted = consumption.IsCompletedSuccessfully,
                inputIsConnectionToken, inputCanBeCanceled, enumeratorCanBeCanceled,
                methodCalls = observation.MethodCalls, enumeratorCalls = observation.EnumeratorCalls,
                inputCancelledBeforeRelease, enumeratorCancelledBeforeRelease,
                observationMilliseconds = 10000, observationCompleted = completed,
                elapsedMilliseconds, sameConnection, releaseWasWithheld = true, before,
                after = new { cancellation = implementation.CancellationObserved,
                    moveCompleted = implementation.MoveNextCompleted, disposed = implementation.DisposeCount,
                    overlap = implementation.DisposeOverlap },
                verdict = negative ? "connection-token-baseline" : "cancelled-candidate" };
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            terminate.TrySetResult();
            implementation.Release.TrySetResult();
            async Task Join(Func<Task> action)
            {
                try { await action().WaitAsync(guard); }
                catch (Exception exception) { cleanup.Add(exception); }
            }
            if (consumption is not null)
            {
                await Join(() => consumption);
                _ = consumption.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            await Join(() => client.StopAsync().AsTask());
            await Join(() => server.StopAsync(TimeSpan.Zero).AsTask());
            await Join(() => client.DisposeAsync().AsTask());
            await Join(() => server.DisposeAsync().AsTask());
        }
        if (failure is not null)
        {
            if (cleanup.Count > 0) failure.Data["CausalProbeCleanup"] = new AggregateException(cleanup);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        if (cleanup.Count > 0) throw new AggregateException("Causal probe cleanup did not complete", cleanup);
        Ensure(record is not null, "missing completed causal record");
        // Endpoint Stop/Dispose must not silently perform a second producer disposal.
        Ensure(implementation.MoveNextCompleted == 1 && implementation.DisposeCount == 1 &&
            implementation.DisposeOverlap == 0 && implementation.CancellationObserved == (negative ? 0 : 1),
            "final endpoint teardown changed the exact physical completion census");
        var finalRecord = JsonSerializer.SerializeToElement(record).EnumerateObject()
            .ToDictionary(static property => property.Name, static property => property.Value.Clone());
        finalRecord["after"] = JsonSerializer.SerializeToElement(new {
            cancellation = implementation.CancellationObserved, moveCompleted = implementation.MoveNextCompleted,
            disposed = implementation.DisposeCount, overlap = implementation.DisposeOverlap });
        finalRecord["teardownComplete"] = JsonSerializer.SerializeToElement(true);
        Console.WriteLine("STATIC_CANCEL_CAUSAL_RECORD|" + JsonSerializer.Serialize(finalRecord));
        if (negative) throw new InvalidOperationException(NegativeMarker);
    }

    // Observation only: capture actual tokens then delegate without substituting tokens,
    // wrapping MoveNext/Dispose, changing flags/deadlines, or catching exceptions.
    private sealed class TokenObservationService(StaticCancellationService inner) : IStaticCancellationService
    {
        internal CancellationToken InputToken, EnumeratorToken;
        internal int MethodCalls, EnumeratorCalls;

        public IAsyncEnumerable<int> ReadAsync(CancellationToken cancellationToken)
        {
            InputToken = cancellationToken;
            Interlocked.Increment(ref MethodCalls);
            return new ObservedStream(this, inner.ReadAsync(cancellationToken));
        }

        public ValueTask<string> GetSessionIdAsync() => inner.GetSessionIdAsync();

        private sealed class ObservedStream(TokenObservationService owner, IAsyncEnumerable<int> source)
            : IAsyncEnumerable<int>
        {
            public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                owner.EnumeratorToken = cancellationToken;
                Interlocked.Increment(ref owner.EnumeratorCalls);
                return source.GetAsyncEnumerator(cancellationToken);
            }
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
