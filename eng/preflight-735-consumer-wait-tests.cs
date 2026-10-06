using System.Runtime.CompilerServices;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel("dispatcher-pool")]
public sealed class PooledDispatcherSlowWaitTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly AsyncLocal<object?> ContextMarker = new();

    [Test]
    public async Task RepeatedSuspendedReadsMustNotAllocateOneWrapperPerItem()
        => await MeasureSuspendedReadsAsync().WaitAsync(Bound);

    private static async Task MeasureSuspendedReadsAsync()
    {
        const int warmups = 128;
        const int count = 512;
        PooledAsyncStreamDispatcher<string?>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<string?>.Rent(default, StringCodec.Instance);
        var enumerator = dispatcher.GetAsyncEnumerator();
        var bytes = new ArrayBufferWriter<byte>();
        string? value = "wait-value";
        StringCodec.Instance.Serialize(in value, bytes);
        var payload = new ReadOnlySequence<byte>(bytes.WrittenMemory);
        long returned = 0;
        dispatcher.SetBytesConsumedCallback((_, _, size) => returned += size, 9910, 1);
        long allocated = 0;
        try
        {
            for (var iteration = 0; iteration < warmups + count; iteration++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var read = enumerator.MoveNextAsync();
                var perRead = GC.GetAllocatedBytesForCurrentThread() - before;
                Require(!read.IsCompleted, "every measured read must genuinely wait for DATA");
                await dispatcher.DispatchAsync(payload, bytes.WrittenCount);
                Require(await read && enumerator.Current == value,
                    "each suspended read must deliver exactly its own payload");
                if (iteration >= warmups) allocated += perRead;
            }
            dispatcher.Complete(exception: null);
            Require(!await enumerator.MoveNextAsync(), "terminal read must complete without invented DATA");
            await enumerator.DisposeAsync();
            Require(returned == (warmups + count) * bytes.WrittenCount,
                "suspended reads must return exactly the consumed byte count");
            Require(!dispatcher.HasRetainedReferencesForTests,
                "dispatcher pool return must still clear callbacks, codec and tokens");
            var directory = Environment.GetEnvironmentVariable("SHARPLINK_SLOW_WAIT_EVIDENCE");
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "pending-read.csv"),
                    $"count,allocated_bytes\n{count},{allocated}\n");
            }
            Console.WriteLine($"PENDING_READ_ALLOCATION count={count} bytes={allocated}");
            Require(allocated <= count * 16L,
                "suspended MoveNext still allocates one fresh wrapper for every item");
        }
        finally
        {
            dispatcher.Complete(exception: null);
            await enumerator.DisposeAsync();
            PooledAsyncStreamDispatcher<string?>.ClearPoolForTests();
        }
    }

    [Test]
    public async Task SuspendedReadPreservesCancellationIdentityAndTaskStatus()
    {
        PooledAsyncStreamDispatcher<string?>.ClearPoolForTests();
        using var stop = new CancellationTokenSource();
        var dispatcher = PooledAsyncStreamDispatcher<string?>.Rent(stop.Token, StringCodec.Instance);
        var enumerator = dispatcher.GetAsyncEnumerator();
        try
        {
            var read = enumerator.MoveNextAsync();
            Require(!read.IsCompleted, "cancellation control needs a pending consumer");
            var task = read.AsTask(); // Convert exactly once; the ValueTask is not reused.
            stop.Cancel();
            Exception? failure = null;
            try { _ = await task.WaitAsync(Bound); }
            catch (Exception error) { failure = error; }
            Require(failure is OperationCanceledException canceled && canceled.CancellationToken == stop.Token,
                "the pending read must retain its original cancellation token");
            Require(task.IsCanceled && !task.IsFaulted,
                "canceled reads must not change to faulted Task status");
        }
        finally
        {
            await enumerator.DisposeAsync();
            Require(!dispatcher.HasRetainedReferencesForTests, "canceled wait retained dispatcher references");
            PooledAsyncStreamDispatcher<string?>.ClearPoolForTests();
        }
    }

    [Test]
    public async Task SuspendedReadPreservesOriginalTerminalException()
    {
        PooledAsyncStreamDispatcher<string?>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<string?>.Rent(default, StringCodec.Instance);
        var enumerator = dispatcher.GetAsyncEnumerator();
        var expected = new InvalidOperationException("pending terminal control");
        try
        {
            var read = enumerator.MoveNextAsync();
            Require(!read.IsCompleted, "fault control needs a pending consumer");
            var task = read.AsTask();
            dispatcher.Complete(expected);
            Exception? failure = null;
            try { _ = await task.WaitAsync(Bound); }
            catch (Exception error) { failure = error; }
            Require(ReferenceEquals(failure, expected) && task.IsFaulted && !task.IsCanceled,
                "pooled completion must preserve original exception identity and fault status");
        }
        finally
        {
            await enumerator.DisposeAsync();
            Require(!dispatcher.HasRetainedReferencesForTests, "failed wait retained dispatcher references");
            PooledAsyncStreamDispatcher<string?>.ClearPoolForTests();
        }
    }

    [Test]
    public async Task DelayedOldResultCannotReadAReusedDispatcherGeneration()
    {
        PooledAsyncStreamDispatcher<string?>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<string?>.Rent(default, StringCodec.Instance);
        var enumerator = dispatcher.GetAsyncEnumerator();
        var expected = new InvalidOperationException("old generation terminal");
        var read = enumerator.MoveNextAsync();
        Require(!read.IsCompleted, "old generation must start with a pending consumer");
        dispatcher.Complete(expected);
        await WaitUntilCompleteAsync(read).WaitAsync(Bound);
        await enumerator.DisposeAsync();
        var replacement = PooledAsyncStreamDispatcher<string?>.Rent(default, StringCodec.Instance);
        try
        {
            Require(ReferenceEquals(dispatcher, replacement), "control must reuse the actual pooled dispatcher");
            Exception? failure = null;
            try { _ = await read; }
            catch (Exception error) { failure = error; }
            Require(ReferenceEquals(failure, expected),
                "an old unconsumed result must not observe the replacement generation");
            replacement.Complete(exception: null);
            Require(!await replacement.MoveNextAsync(), "replacement terminal must stay independent");
        }
        finally
        {
            replacement.Complete(exception: null);
            await replacement.DisposeAsync();
            PooledAsyncStreamDispatcher<string?>.ClearPoolForTests();
        }
    }

    private static async Task WaitUntilCompleteAsync(ValueTask<bool> operation)
    {
        while (!operation.IsCompleted) await Task.Yield();
    }

    [Test]
    public async Task ConsumedReadCacheMustNotRetainExecutionContextPayload()
    {
        var weak = await ExerciseContextAsync().WaitAsync(Bound);
        for (var pass = 0; pass < 3; pass++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Require(!weak.IsAlive, "a completed and consumed wait cached its old ExecutionContext payload");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> ExerciseContextAsync()
    {
        PooledAsyncStreamDispatcher<string?>.ClearPoolForTests();
        var marker = new byte[1024 * 1024];
        var weak = new WeakReference(marker);
        ContextMarker.Value = marker;
        var dispatcher = PooledAsyncStreamDispatcher<string?>.Rent(default, StringCodec.Instance);
        var enumerator = dispatcher.GetAsyncEnumerator();
        try
        {
            var read = enumerator.MoveNextAsync();
            Require(!read.IsCompleted, "context control must capture a suspended operation");
            dispatcher.Complete(exception: null);
            Require(!await read, "normal empty completion must remain false");
            await enumerator.DisposeAsync();
            Require(!dispatcher.HasRetainedReferencesForTests, "dispatcher itself retained user state");
        }
        finally
        {
            ContextMarker.Value = null;
            dispatcher.Complete(exception: null);
            await enumerator.DisposeAsync();
            PooledAsyncStreamDispatcher<string?>.ClearPoolForTests();
        }
        return weak;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
