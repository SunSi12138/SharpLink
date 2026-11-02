using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace SharpLink.PackageSmoke;

// Test-only exception collection. It neither owns resources nor imposes a release deadline.
internal sealed class SmokeFailureCollector
{
    private readonly List<Exception> _failures = [];

    internal void Capture(Exception error) => _failures.Add(error);

    internal void Attempt(Action cleanup)
    {
        try { cleanup(); }
        catch (Exception error) { Capture(error); }
    }

    internal async Task ObserveAsync(Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch (Exception error)
        {
            // Await exposes only one failure. Keep every original direct sibling and its
            // complete nested graph; never Flatten or reference-deduplicate occurrences.
            if (operation.Exception is { } aggregate)
                _failures.AddRange(aggregate.InnerExceptions);
            else
                Capture(error);
        }
    }

    internal async Task ObserveAllAsync(params Task[] operations)
    {
        // All original branches must already be started by the caller. WhenAll's fault
        // aggregate omits canceled branches, and all-canceled exposes only one outcome.
        // Observe each original task instead; ObserveAsync records failure and completes.
        foreach (var operation in operations)
            await ObserveAsync(operation).ConfigureAwait(false);
    }

    internal async Task AttemptAsync(Func<ValueTask> cleanup)
    {
        Task operation;
        try { operation = cleanup().AsTask(); }
        catch (Exception error) { Capture(error); return; }
        await ObserveAsync(operation).ConfigureAwait(false);
    }

    internal Task ObserveUnlessAcceptedAsync(Task operation, Task? acceptedOperation)
        // The caller must already have asserted the expected result of this exact task.
        // A receipt cannot excuse another task, make an incomplete operation joined,
        // or consume additional fault siblings that await did not expose to the assertion.
        => ReferenceEquals(operation, acceptedOperation) && operation.IsCompleted &&
           (operation.Exception is null || operation.Exception.InnerExceptions.Count == 1)
            ? Task.CompletedTask
            : ObserveAsync(operation);

    internal void ThrowIfAny()
    {
        if (_failures.Count == 1)
            ExceptionDispatchInfo.Capture(_failures[0]).Throw();
        if (_failures.Count > 1)
            throw new AggregateException("Package smoke body/cleanup failures.", _failures);
    }

    internal static async Task<T> AwaitAsync<T>(Task<T> operation)
    {
        try { return await operation.ConfigureAwait(false); }
        catch (Exception error)
        {
            var failures = new SmokeFailureCollector();
            if (operation.Exception is { } aggregate)
                failures._failures.AddRange(aggregate.InnerExceptions);
            else
                failures.Capture(error);
            failures.ThrowIfAny();
            throw;
        }
    }

    internal static async Task JoinWithDiagnosticAsync(Task operation, Task diagnosticDeadline, string name)
    {
        var failures = new SmokeFailureCollector();
        var winner = await Task.WhenAny(operation, diagnosticDeadline).ConfigureAwait(false);
        if (!ReferenceEquals(winner, operation) && !operation.IsCompleted)
            failures.Capture(new TimeoutException($"{name} exceeded its two-second diagnostic wait; actual join is still required."));
        // The diagnostic deadline never releases resources or converts pending into success.
        await failures.ObserveAsync(operation).ConfigureAwait(false);
        failures.ThrowIfAny();
    }
}
