using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.LoadTestBase;

namespace SharpLink.StreamLoadTest;

internal readonly record struct QualifiedShortStreamSample(
    int Items, long FirstProgressTicks, long BusinessCompleteTicks, long ApplicationOwnersExitedTicks);

public sealed record ShortStreamPhaseStatistics(
    string FirstProgressMeaning,
    LatencyStatistics FirstProgress,
    LatencyStatistics BusinessComplete,
    LatencyStatistics ApplicationOwnersExited);

internal sealed class ShortStreamPhaseRecorder(StageLatencyRecorder owners)
{
    private readonly StageLatencyRecorder _first = new(owners.WorkerCount, owners.MaximumTotalSamples);
    private readonly StageLatencyRecorder _business = new(owners.WorkerCount, owners.MaximumTotalSamples);

    internal void Record(int worker, QualifiedShortStreamSample sample)
    {
        _first.GetWorker(worker).RecordTicks(worker, sample.FirstProgressTicks);
        _business.GetWorker(worker).RecordTicks(worker, sample.BusinessCompleteTicks);
        owners.GetWorker(worker).RecordTicks(worker, sample.ApplicationOwnersExitedTicks);
    }

    internal ShortStreamPhaseStatistics Complete(string operation, LatencyStatistics ownerStatistics) => new(
        operation == "c2s" ? "first input pull" : "first response item",
        _first.Complete(), _business.Complete(), ownerStatistics);
}

internal static class QualifiedShortStream
{
    internal static bool Supports(string operation, int items)
        => operation is ("c2s" or "s2c" or "duplex") && items is (32 or 256);

    internal static async ValueTask<QualifiedShortStreamSample> ExecuteAsync(IStreamLoadService rpc,
        string operation, int[] values, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        if (operation == "c2s")
        {
            var upload = new ObservedInput(values, ct);
            var actual = await rpc.UploadAsync(upload).ConfigureAwait(false);
            var end = Stopwatch.GetTimestamp();
            var expected = (long)values.Length * (values.Length + 1) / 2;
            if (actual != expected)
                throw new InvalidOperationException($"Expected upload sum {expected}, received {actual}.");
            var producerExit = await upload.JoinAsync(ct).ConfigureAwait(false);
            return Sample(values.Length, start, upload.FirstPull, end, producerExit);
        }

        ObservedInput? input = operation == "duplex" ? new(values, ct) : null;
        var response = operation switch
        {
            "s2c" => rpc.DownloadAsync(values.Length),
            "duplex" => rpc.DuplexAsync(input!),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        var count = 0;
        var first = 0L;
        long business;
        await using (var items = response.GetAsyncEnumerator(ct))
        {
            while (await items.MoveNextAsync().ConfigureAwait(false))
            {
                if (count == 0)
                    first = Stopwatch.GetTimestamp();
                if (count >= values.Length || items.Current != (input is null ? count : values[count] + 1))
                    throw new InvalidOperationException($"Unexpected {operation} output at index {count}.");
                count++;
            }
            business = Stopwatch.GetTimestamp();
        }
        var consumerExit = Stopwatch.GetTimestamp();
        if (count != values.Length)
            throw new InvalidOperationException($"Expected {values.Length} {operation} responses, received {count}.");
        var owners = input is null ? consumerExit : Math.Max(consumerExit, await input.JoinAsync(ct).ConfigureAwait(false));
        return Sample(count, start, first, business, owners);
    }

    private static QualifiedShortStreamSample Sample(int count, long start, long first, long business, long owners)
    {
        if (first < start || first > business)
            throw new InvalidOperationException("The first application progress was not observed within this call.");
        return new(count, first - start, business - start, Math.Max(business, owners) - start);
    }

    private sealed class ObservedInput(int[] values, CancellationToken caller) : IAsyncEnumerable<int>
    {
        private long _firstPull;
        private long _end;
        private TaskCompletionSource<long>? _waiter;

        internal long FirstPull => Volatile.Read(ref _firstPull);

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken producer = default) => ReadAsync(producer);

        private async IAsyncEnumerator<int> ReadAsync(CancellationToken producer)
        {
            try
            {
                // Both arms own the same explicit link; it is disposed before publishing
                // the application producer exit. No timing observer is added to the RPC runtime.
                using var link = CancellationTokenSource.CreateLinkedTokenSource(caller, producer);
                for (var index = 0; index < values.Length; index++)
                {
                    link.Token.ThrowIfCancellationRequested();
                    if (index == 0)
                        Volatile.Write(ref _firstPull, Stopwatch.GetTimestamp());
                    yield return values[index];
                    await Task.CompletedTask;
                }
            }
            finally
            {
                var stamp = Stopwatch.GetTimestamp();
                Volatile.Write(ref _end, stamp);
                Volatile.Read(ref _waiter)?.TrySetResult(stamp);
            }
        }

        internal ValueTask<long> JoinAsync(CancellationToken ct)
        {
            var completed = Volatile.Read(ref _end);
            if (completed != 0)
                return ValueTask.FromResult(completed);

            // Most streams are already done. Allocate a waiter only if there is a
            // real producer tail, and close the race with its final timestamp.
            var waiter = LazyInitializer.EnsureInitialized(ref _waiter,
                static () => new(TaskCreationOptions.RunContinuationsAsynchronously));
            completed = Volatile.Read(ref _end);
            if (completed != 0)
                waiter.TrySetResult(completed);
            return new(waiter.Task.WaitAsync(ct));
        }
    }
}
