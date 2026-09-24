using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.Benchmarks;

internal static class PhaseBTransportFailureChecks
{
    internal static async Task<int> RunAsync()
    {
        await ParticipantFaultPrecedesCancellationAsync();
        await TerminalCausePrecedesCanceledReadersAsync();
        await CancellationCallbackCannotReplaceCauseAsync();
        await WaiterJoinsCancellationCallbackPublicationAsync();
        await TimeoutWithoutParticipantFaultStaysCanceledAsync();
        await ConcurrentFaultsHaveOneStableWinnerAsync();
        await SuccessDoesNotCancelAsync();
        await CaseCleanupKeepsPrimaryAsync();
        await CleanupFailureFailsSuccessfulCaseAsync();
        await CleanupRunsAfterSynchronousFailureAsync();
        await DiagnosticFailureIsSecondaryAsync();
        await CaseSuccessIsUnchangedAsync();
        Console.WriteLine("12/12 transport failure arbitration checks passed.");
        return 12;
    }

    private static async Task ParticipantFaultPrecedesCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var failures = new PhaseBTransportFailure(cancellation);
        var source = new TaskCompletionSource();
        var expected = new IOException("controlled receive failure");
        var participants = new[] { failures.ObserveAsync(source.Task, "receive"),
            failures.ObserveAsync(Task.Delay(Timeout.Infinite, cancellation.Token), "producer") };
        var wait = failures.WaitAsync(participants);
        source.SetException(expected);
        Require(ReferenceEquals(await ErrorAsync(wait), expected), "cancellation masked the actual receive failure");
        Require(failures.Origin == "receive", "wrong first-failure owner");
        await ObserveAllAsync(participants);
    }

    private static async Task TerminalCausePrecedesCanceledReadersAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var failures = new PhaseBTransportFailure(cancellation);
        var expected = new IOException("controlled session output failure");
        var participants = new[] { failures.ObserveAsync(Task.Delay(Timeout.Infinite, cancellation.Token), "reader") };
        var wait = failures.WaitAsync(participants);
        failures.RecordAndCancel("sender-session", expected);
        Require(ReferenceEquals(await ErrorAsync(wait), expected), "session cause lost behind reader cancellation");
        Require(failures.Origin == "sender-session", "canceled reader replaced session origin");
        await ObserveAllAsync(participants);
    }

    private static async Task CancellationCallbackCannotReplaceCauseAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var failures = new PhaseBTransportFailure(cancellation);
        var expected = new IOException("first fault");
        var callback = new InvalidOperationException("controlled cleanup failure");
        using var registration = cancellation.Token.Register(() => throw callback);
        failures.RecordAndCancel("producer", expected); // Must not throw out of a session callback.
        var error = await ErrorAsync(failures.WaitAsync([Task.FromException(expected)]));
        Require(error is AggregateException aggregate && ReferenceEquals(aggregate.InnerExceptions[0], expected) &&
            aggregate.Flatten().InnerExceptions.Contains(callback), "cleanup failure replaced or hid primary fault");
    }

    private static async Task WaiterJoinsCancellationCallbackPublicationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var failures = new PhaseBTransportFailure(cancellation);
        var primary = new IOException("controlled concurrent primary");
        var secondary = new IOException("controlled concurrent cleanup");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var registration = cancellation.Token.Register(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test callback was not released.");
            throw secondary;
        });
        var wait = failures.WaitAsync([Task.Delay(Timeout.Infinite, cancellation.Token)]);
        var producer = Task.Run(() => failures.RecordAndCancel("sender-session", primary));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!wait.IsCompleted, "the result escaped before cancellation callback settlement");
        }
        finally { release.Set(); }
        await producer.WaitAsync(TimeSpan.FromSeconds(5));
        var error = await ErrorAsync(wait);
        Require(error is AggregateException aggregate && ReferenceEquals(aggregate.InnerExceptions[0], primary) &&
            aggregate.Flatten().InnerExceptions.Contains(secondary), "callback publication race lost secondary error");
    }

    private static async Task TimeoutWithoutParticipantFaultStaysCanceledAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var failures = new PhaseBTransportFailure(cancellation);
        var participants = new[] { failures.ObserveAsync(Task.Delay(Timeout.Infinite, cancellation.Token), "reader") };
        var wait = failures.WaitAsync(participants);
        cancellation.Cancel();
        Require(await ErrorAsync(wait) is OperationCanceledException, "external cancellation was relabeled as success");
        Require(failures.Origin == "case-timeout-or-external-cancellation", "invented a participant fault");
        await ObserveAllAsync(participants);
    }

    private static async Task ConcurrentFaultsHaveOneStableWinnerAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var failures = new PhaseBTransportFailure(cancellation);
        var errors = Enumerable.Range(0, 16).Select(i => new IOException($"fault {i}")).ToArray();
        await Task.WhenAll(errors.Select((error, i) => Task.Run(() => failures.RecordAndCancel($"worker-{i}", error))));
        var first = await ErrorAsync(failures.WaitAsync([Task.FromException(errors[0])]));
        var second = await ErrorAsync(failures.WaitAsync([Task.FromException(errors[1])]));
        Require(errors.Contains(first) && ReferenceEquals(first, second), "failure ownership changed after publication");
    }

    private static async Task SuccessDoesNotCancelAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var failures = new PhaseBTransportFailure(cancellation);
        await failures.WaitAsync([failures.ObserveAsync(Task.CompletedTask, "completed")]);
        Require(!cancellation.IsCancellationRequested, "successful case canceled itself");
    }

    private static async Task CaseCleanupKeepsPrimaryAsync()
    {
        var primary = new IOException("original measurement failure");
        var cleanup = new TimeoutException("controlled final-cleanup timeout");
        var described = false;
        var observed = await ErrorAsync(PhaseBTransportFailure.RunCaseAsync<int>(
            () => Task.FromException<int>(primary), () => Task.FromException(cleanup),
            error => described = ReferenceEquals(error, primary)));
        Require(described && observed is AggregateException aggregate &&
            ReferenceEquals(aggregate.InnerExceptions[0], primary) &&
            ReferenceEquals(aggregate.InnerExceptions[1], cleanup),
            "a failing finally replaced the primary measurement error");
    }

    private static async Task CleanupFailureFailsSuccessfulCaseAsync()
    {
        var failure = new IOException("cleanup after a successful measurement");
        var described = false;
        var observed = await ErrorAsync(PhaseBTransportFailure.RunCaseAsync(
            () => Task.FromResult(42), () => Task.FromException(failure), _ => described = true));
        Require(!described && ReferenceEquals(observed, failure), "cleanup failure was hidden behind a successful sample");
    }

    private static async Task CleanupRunsAfterSynchronousFailureAsync()
    {
        var primary = new OperationCanceledException("controlled synchronous measurement cancellation");
        var cleanupCalls = 0;
        var observed = await ErrorAsync(PhaseBTransportFailure.RunCaseAsync<int>(
            () => throw primary, () => { cleanupCalls++; return Task.CompletedTask; }, _ => { }));
        Require(cleanupCalls == 1 && ReferenceEquals(observed, primary),
            "synchronous cancellation did not retain its cause and exactly one cleanup");
    }

    private static async Task DiagnosticFailureIsSecondaryAsync()
    {
        var primary = new IOException("measurement");
        var diagnostic = new InvalidOperationException("diagnostic sink");
        var cleanup = new IOException("cleanup");
        var observed = await ErrorAsync(PhaseBTransportFailure.RunCaseAsync<int>(
            () => Task.FromException<int>(primary), () => Task.FromException(cleanup), _ => throw diagnostic));
        Require(observed is AggregateException aggregate &&
            aggregate.Flatten().InnerExceptions.Count == 3 &&
            aggregate.Flatten().InnerExceptions.Contains(primary) &&
            aggregate.Flatten().InnerExceptions.Contains(diagnostic) &&
            aggregate.Flatten().InnerExceptions.Contains(cleanup), "a secondary diagnostic failure obscured original evidence");
    }

    private static async Task CaseSuccessIsUnchangedAsync()
    {
        var cleanupCalls = 0;
        var diagnosticCalls = 0;
        var result = await PhaseBTransportFailure.RunCaseAsync(() => Task.FromResult(42),
            () => { cleanupCalls++; return Task.CompletedTask; }, _ => diagnosticCalls++);
        Require(result == 42 && cleanupCalls == 1 && diagnosticCalls == 0,
            "a successful case acquired diagnostic work or changed its result");
    }

    private static async Task<Exception> ErrorAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected the case to fail.");
    }

    private static async Task ObserveAllAsync(Task[] tasks)
    {
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { throw; }
        catch { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
