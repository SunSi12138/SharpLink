using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SharpLink.Benchmarks;

// Failed setup must release each owner even when another cleanup fails, without
// losing the preparation error. Cleanup failures are aggregated only after every action.
internal static class BenchmarkStartupCleanup
{
    internal static async Task RunAsync(Exception primary, params Func<ValueTask>[] actions)
    {
        var failures = new List<Exception>();
        foreach (var action in actions)
        {
            try
            {
                var cleanup = action().AsTask();
                try
                {
                    await cleanup.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // A timed-out disposal still owns its task; observe any later fault.
                    _ = cleanup.ContinueWith(static task => { _ = task.Exception; },
                        System.Threading.CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    throw;
                }
            }
            catch (Exception cleanupFailure)
            {
                failures.Add(cleanupFailure);
            }
        }

        if (failures.Count != 0)
        {
            failures.Insert(0, primary);
            throw new AggregateException("Benchmark startup failed and cleanup also reported failures.", failures);
        }
    }
}
