using System.Threading;

namespace SharpLink.UnitTests;

/// <summary>
/// Runs deterministic test seams that synchronously wait on a peer-owned gate without consuming
/// the shared ThreadPool needed by that peer's continuation.
/// </summary>
internal static class LongRunningTestWorker
{
    private const TaskCreationOptions Options =
        TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning;

    internal static Task Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var started = new ManualResetEventSlim();
        var task = Task.Factory.StartNew(
            () =>
            {
                started.Set();
                action();
            },
            CancellationToken.None,
            Options,
            TaskScheduler.Default);
        // Do not charge a test's semantic phase timeout with scheduler delay before the dedicated
        // LongRunning worker has actually begun executing. Tests that coordinate blocking
        // cancellation/lifecycle callbacks can start their phase budget after this returns.
        started.Wait();
        return task;
    }

    internal static Task<TResult> Run<TResult>(Func<TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Task.Factory.StartNew(
            action,
            CancellationToken.None,
            Options,
            TaskScheduler.Default);
    }

    internal static Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Task.Factory.StartNew(
            action,
            CancellationToken.None,
            Options,
            TaskScheduler.Default).Unwrap();
    }

    internal static Task<TResult> RunAsync<TResult>(Func<Task<TResult>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Task.Factory.StartNew(
            action,
            CancellationToken.None,
            Options,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>Joins a cleanup owner while preserving any primary test failure.</summary>
    internal static async Task JoinAsync(Task task, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
            await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch when (task.IsCompleted)
        {
            // The normal test path asserts the worker result. Cleanup only guarantees that a
            // failed/cancelled owner has terminated before the next parallel test starts.
        }
    }

    /// <summary>Synchronously joins a cleanup owner for a synchronous race harness.</summary>
    internal static void Join(Task task, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
            if (!task.Wait(timeout))
                throw new TimeoutException("A long-running test owner did not stop within the cleanup bound.");
        }
        catch (AggregateException) when (task.IsCompleted)
        {
            // The normal test path observes worker failures. Cleanup only prevents a failed
            // owner from surviving into a later parallel test.
        }
    }
}
