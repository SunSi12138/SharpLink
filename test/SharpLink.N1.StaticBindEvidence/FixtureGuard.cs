using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.N1.StaticBindEvidence;

internal sealed class FixtureGuardException(string message, Exception inner) : TimeoutException(message, inner);
internal static class FixtureGuard
{
    internal static async Task GuardedWaitAsync(this Task source, TimeSpan guard,
        [CallerArgumentExpression(nameof(source))] string label = "")
    {
        var start = Stopwatch.GetTimestamp(); var promise = source.WaitAsync(guard);
        try { await promise.ConfigureAwait(false); }
        catch (TimeoutException exception) when (!OwnsException(source, exception))
        { throw Capture(label, guard, start, source, promise, exception); }
    }
    internal static async Task<T> GuardedWaitAsync<T>(this Task<T> source, TimeSpan guard,
        [CallerArgumentExpression(nameof(source))] string label = "")
    {
        var start = Stopwatch.GetTimestamp(); var promise = source.WaitAsync(guard);
        try { return await promise.ConfigureAwait(false); }
        catch (TimeoutException exception) when (!OwnsException(source, exception))
        { throw Capture(label, guard, start, source, promise, exception); }
    }
    private static bool OwnsException(Task source, Exception observed)
    {
        if (!source.IsFaulted) return false;
        foreach (var exception in source.Exception!.InnerExceptions) if (ReferenceEquals(exception, observed)) return true;
        return false;
    }
    internal static FixtureGuardException Capture(string label, TimeSpan guard, long start, Task? source,
        Task? waitPromise, Exception exception)
    {
        var record = JsonSerializer.Serialize(new
        {
            phase = label, guardMs = guard.TotalMilliseconds, elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            source = source is null ? null : new { status = source.Status.ToString(), source.IsCompleted, source.IsCompletedSuccessfully, source.IsCanceled,
                actualFault = source.IsFaulted ? source.Exception!.ToString() : null },
            waitPromise = waitPromise is null ? null : new { status = waitPromise.Status.ToString(), waitPromise.IsCompleted },
            outcome = "INCONCLUSIVE", classification = "guard"
        });
        Console.Error.WriteLine("N1_GUARD " + record);
        // These are observations at guard recovery, not timestamps proving source-signal order.
        return new FixtureGuardException("FIXTURE guard: " + record, exception);
    }
}
