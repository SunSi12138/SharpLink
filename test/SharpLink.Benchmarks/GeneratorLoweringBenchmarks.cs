using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace SharpLink.Benchmarks;

/// <summary>
/// Compares the two generated service-method call shapes tracked by issue #158:
/// <list type="bullet">
///   <item><b>Variant A (current)</b>: inspect-then-await —
///     <c>var pending = call(); if (pending.IsCompletedSuccessfully) { ... } else { return Await...(pending); }</c>.</item>
///   <item><b>Variant B</b>: direct-await — <c>var result = await call();</c>.</item>
/// </list>
/// <para>
/// The sink (<see cref="Sink"/>) stands in for the generated response serializer, so only the
/// call shape differs between the two variants. Every workload uses a non-inlinable service
/// call to model the interface dispatch of a generated stub.
/// </para>
/// <para>
/// Scope: this measures the <b>traditional async lowering</b> that ships in the .NET 10 SDK.
/// The runtime-async comparison axis of issue #158 cannot be populated until a runtime-async
/// feature actually ships, so it is intentionally absent here.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 5, iterationCount: 20)]
public class GeneratorLoweringBenchmarks
{
    private readonly SyncResultSource _syncSource = new();
    private readonly Consumer _consumer = new();

    // ---- Task<int>, synchronously completed ----------------------------------------

    [Benchmark]
    public ValueTask TaskSync_InspectThenAwait()
    {
        Task<int> pending = CallTaskSync();
        if (pending.IsCompletedSuccessfully)
        {
            Sink(pending.GetAwaiter().GetResult());
            return ValueTask.CompletedTask;
        }

        return AwaitTaskSinkAsync(pending);
    }

    [Benchmark]
    public async ValueTask TaskSync_DirectAwait()
    {
        Sink(await CallTaskSync().ConfigureAwait(false));
    }

    // ---- ValueTask<int>, synchronously completed ------------------------------------

    [Benchmark]
    public ValueTask ValueTaskSync_InspectThenAwait()
    {
        ValueTask<int> pending = CallValueTaskSync();
        if (pending.IsCompletedSuccessfully)
        {
            Sink(pending.Result);
            return ValueTask.CompletedTask;
        }

        return AwaitValueTaskSinkAsync(pending);
    }

    [Benchmark]
    public async ValueTask ValueTaskSync_DirectAwait()
    {
        Sink(await CallValueTaskSync().ConfigureAwait(false));
    }

    // ---- Task<int>, genuinely suspends ----------------------------------------------

    [Benchmark]
    public ValueTask TaskSuspend_InspectThenAwait()
    {
        Task<int> pending = CallTaskSuspend();
        if (pending.IsCompletedSuccessfully)
        {
            Sink(pending.GetAwaiter().GetResult());
            return ValueTask.CompletedTask;
        }

        return AwaitTaskSinkAsync(pending);
    }

    [Benchmark]
    public async ValueTask TaskSuspend_DirectAwait()
    {
        Sink(await CallTaskSuspend().ConfigureAwait(false));
    }

    // ---- ValueTask<int>, genuinely suspends ------------------------------------------

    [Benchmark]
    public ValueTask ValueTaskSuspend_InspectThenAwait()
    {
        ValueTask<int> pending = CallValueTaskSuspend();
        if (pending.IsCompletedSuccessfully)
        {
            Sink(pending.Result);
            return ValueTask.CompletedTask;
        }

        return AwaitValueTaskSinkAsync(pending);
    }

    [Benchmark]
    public async ValueTask ValueTaskSuspend_DirectAwait()
    {
        Sink(await CallValueTaskSuspend().ConfigureAwait(false));
    }

    // ---- IValueTaskSource<int>-backed ValueTask<int> (pooled operation) --------------

    [Benchmark]
    public ValueTask ValueTaskSourceSync_InspectThenAwait()
    {
        ValueTask<int> pending = CallValueTaskSourceSync();
        if (pending.IsCompletedSuccessfully)
        {
            Sink(pending.Result);
            return ValueTask.CompletedTask;
        }

        return AwaitValueTaskSinkAsync(pending);
    }

    [Benchmark]
    public async ValueTask ValueTaskSourceSync_DirectAwait()
    {
        Sink(await CallValueTaskSourceSync().ConfigureAwait(false));
    }

    // ---- service methods (non-inlinable to model interface dispatch) -----------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<int> CallTaskSync() => Task.FromResult(42);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<int> CallValueTaskSync() => new(42);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> CallTaskSuspend()
    {
        await Task.Yield();
        return 42;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async ValueTask<int> CallValueTaskSuspend()
    {
        await Task.Yield();
        return 42;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ValueTask<int> CallValueTaskSourceSync()
        => new(_syncSource, SyncResultSource.Version);

    // ---- slow-path helpers and the serializer stand-in -------------------------------

    private async ValueTask AwaitTaskSinkAsync(Task<int> pending)
    {
        Sink(await pending.ConfigureAwait(false));
    }

    private async ValueTask AwaitValueTaskSinkAsync(ValueTask<int> pending)
    {
        Sink(await pending.ConfigureAwait(false));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Sink(int value) => _consumer.Consume(value);

    /// <summary>
    /// A pooled <see cref="IValueTaskSource{TResult}"/> that completes synchronously.
    /// Represents the "real async operation representation" that must not be rewritten
    /// into a <see cref="Task{TResult}"/> by any lowering.
    /// </summary>
    private sealed class SyncResultSource : IValueTaskSource<int>
    {
        internal const short Version = 1;

        public int GetResult(short token) => 42;

        public ValueTaskSourceStatus GetStatus(short token) => ValueTaskSourceStatus.Succeeded;

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            continuation(state);
        }
    }
}
