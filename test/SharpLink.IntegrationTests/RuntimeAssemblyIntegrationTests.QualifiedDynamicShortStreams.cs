using System.Diagnostics;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    private static async Task VerifyQualifiedDynamicStreamsAsync(QualifiedDynamicStreams streams, Type service,
        string transport, string interceptor, CancellationToken ct)
    {
        foreach (var size in new[] { 32, 256 })
        foreach (var family in new[] { "c2s", "s2c", "duplex" })
        {
            await streams.ExecuteAsync(family, size, timed: false, ct);
            Console.WriteLine($"DYNAMIC_STREAM_VERIFIED transport={transport} family={family} items={size} interceptor={interceptor}");
        }
        Ensure(GenerationCounter(service, "InputItemsConsumed") == 2 * (32 + 256) &&
               GenerationCounter(service, "InputStreamExits") == 4 && GenerationCounter(service, "Created") == 1,
            "the original runtime implementation consumes both input shapes and exits each exactly once");
    }

    private readonly record struct DynamicStreamObservation(long FirstProgress, long BusinessComplete, long OwnersExited);

    private sealed class QualifiedDynamicStreams(object proxy, Type contract)
    {
        // Capture the actual generated proxy once, not reflection invocation on the call path.
        private readonly Func<IAsyncEnumerable<int>, CancellationToken, ValueTask<int>> _upload =
            contract.GetMethod("ClientStreamAsync")!
                .CreateDelegate<Func<IAsyncEnumerable<int>, CancellationToken, ValueTask<int>>>(proxy);
        private readonly Func<int, CancellationToken, IAsyncEnumerable<int>> _download =
            contract.GetMethod("ServerStreamAsync")!
                .CreateDelegate<Func<int, CancellationToken, IAsyncEnumerable<int>>>(proxy);
        private readonly Func<IAsyncEnumerable<int>, CancellationToken, IAsyncEnumerable<int>> _mirror =
            contract.GetMethod("DuplexAsync")!
                .CreateDelegate<Func<IAsyncEnumerable<int>, CancellationToken, IAsyncEnumerable<int>>>(proxy);

        internal async ValueTask<DynamicStreamObservation> ExecuteAsync(string family, int size, bool timed, CancellationToken ct)
        {
            var start = Stamp(timed);
            if (family == "c2s")
            {
                var source = new QualifiedDynamicInput(size, timed, ct);
                var sum = await _upload(source, ct).ConfigureAwait(false);
                var complete = Stamp(timed);
                Ensure(sum == size * (size + 1) / 2, "dynamic upload must return the exact typed sum");
                var exit = await source.JoinAsync(ct).ConfigureAwait(false);
                return Sample(start, source.FirstPull, complete, exit);
            }

            var input = family == "duplex" ? new QualifiedDynamicInput(size, timed, ct) : null;
            var output = family switch
            {
                "s2c" => _download(size, ct),
                "duplex" => _mirror(input!, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(family))
            };
            var received = 0;
            var first = 0L;
            long business;
            await using (var reader = output.GetAsyncEnumerator(ct))
            {
                while (await reader.MoveNextAsync().ConfigureAwait(false))
                {
                    if (received == 0)
                        first = Stamp(timed);
                    var expected = input is null ? received : (received + 1) * 2;
                    Ensure(received < size && reader.Current == expected, "dynamic output count and order are exact");
                    received++;
                }
                business = Stamp(timed);
            }
            var consumerExit = Stamp(timed);
            Ensure(received == size, "dynamic response reaches EOF after the exact number of items");
            var producerExit = input is null ? 0 : await input.JoinAsync(ct).ConfigureAwait(false);
            return Sample(start, first, business, Math.Max(consumerExit, producerExit));
        }

        private static DynamicStreamObservation Sample(long start, long first, long business, long owners)
        {
            Ensure(start == 0 || (start < first && first <= business), "first progress must lie inside the stream");
            return start == 0 ? default : new(first - start, business - start, Math.Max(business, owners) - start);
        }
    }

    private static long Stamp(bool timed) => timed ? Stopwatch.GetTimestamp() : 0;

    private sealed class QualifiedDynamicInput(int length, bool timed, CancellationToken caller) : IAsyncEnumerable<int>
    {
        private TaskCompletionSource<long>? _waiter;
        private long _first;
        private long _exit;
        private int _pulled;
        private int _opened;

        internal long FirstPull => Volatile.Read(ref _first);

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken producer = default) => ReadAsync(producer);

        internal async ValueTask<long> JoinAsync(CancellationToken ct)
        {
            var exit = Volatile.Read(ref _exit);
            if (exit == 0)
            {
                var waiter = LazyInitializer.EnsureInitialized(ref _waiter,
                    static () => new(TaskCreationOptions.RunContinuationsAsynchronously));
                exit = Volatile.Read(ref _exit);
                if (exit == 0)
                    exit = await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            Ensure(_opened == 1 && _pulled == length, "the input owner exits after emitting the entire stream once");
            return timed ? exit : 0;
        }

        private async IAsyncEnumerator<int> ReadAsync(CancellationToken producer)
        {
            Interlocked.Increment(ref _opened);
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, producer);
                for (var index = 1; index <= length; index++)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (index == 1 && timed)
                        Volatile.Write(ref _first, Stopwatch.GetTimestamp());
                    _pulled = index;
                    yield return index;
                    await Task.CompletedTask;
                }
            }
            finally
            {
                // The linked source has already been disposed when the app producer publishes exit.
                var exit = timed ? Stopwatch.GetTimestamp() : 1;
                Volatile.Write(ref _exit, exit);
                Volatile.Read(ref _waiter)?.TrySetResult(exit);
            }
        }
    }
}
