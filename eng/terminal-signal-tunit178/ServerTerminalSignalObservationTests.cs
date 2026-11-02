using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

// New source reconstruction after loss of the 0646 artifact; not its byte-exact recovery.
[NotInParallel]
public sealed class ServerTerminalSignalObservationTests
{
    [Test]
    public void FailedStartAndDisposeWithoutTerminalWaitShouldLeaveNoUnobservedSignal()
        => RequireNoUnobservedSignal(failDuringStart: true);

    [Test]
    public void FailedStopAndDisposeWithoutTerminalWaitShouldLeaveNoUnobservedSignal()
        => RequireNoUnobservedSignal(failDuringStart: false);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task FailedStartShouldPreserveFailureForLateWaiters(bool disposeFirst)
        => VerifyLateWaiters(failDuringStart: true, disposeFirst);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task FailedStopShouldPreserveEverySiblingForLateWaiters(bool disposeFirst)
        => VerifyLateWaiters(failDuringStart: false, disposeFirst);

    [Test]
    public async Task SuccessfulStopShouldStillCompleteTerminalSignalSuccessfully()
    {
        var server = CreateServer(new FailureListener(null, null));
        Exception? bodyFailure = null;
        try
        {
            RequireSuccess(await CaptureFailureAsync(server.StartAsync().AsTask()));
            RequireSuccess(await CaptureFailureAsync(server.StopAsync(TimeSpan.Zero).AsTask()));
            RequireSuccess(await CaptureFailureAsync(server.WaitForShutdownAsync()));
            Require(server.LifecycleState == SharpLinkServerLifecycleState.Stopped,
                "Successful terminal publication must leave the server Stopped.");
        }
        catch (Exception exception) { bodyFailure = exception; }

        FailureObservation disposal;
        try { disposal = await CaptureFailureAsync(server.DisposeAsync().AsTask()); }
        catch (Exception exception) { disposal = new(exception, null); }
        Exception? cleanupFailure = null;
        try { RequireSuccess(disposal); }
        catch (Exception exception) { cleanupFailure = exception; }
        ThrowBodyAndCleanupFailures(bodyFailure, cleanupFailure);
    }

    private static async Task VerifyLateWaiters(bool failDuringStart, bool disposeFirst)
    {
        var first = new IOException("first-" + Guid.NewGuid().ToString("N"));
        var second = new InvalidOperationException("second");
        var third = new IOException("third");
        var nested = new AggregateException(second, third);
        var injected = new AggregateException(first, nested);
        var server = CreateServer(new FailureListener(
            failDuringStart ? injected : null, failDuringStart ? null : injected));
        Exception? bodyFailure = null;
        Exception? cleanupFailure = null;
        Exception? terminal = null;
        var disposalAttempted = false;
        try
        {
            if (failDuringStart)
            {
                var start = await CaptureFailureAsync(server.StartAsync().AsTask());
                RequireObserved(start, injected);
                terminal = injected;
                RequireOrdered(injected, first, nested);
                RequireOrdered(nested, second, third);
            }
            else
            {
                RequireSuccess(await CaptureFailureAsync(server.StartAsync().AsTask()));
                var stop = await CaptureFailureAsync(server.StopAsync(TimeSpan.Zero).AsTask());
                terminal = stop.Thrown ?? throw new Exception("Stop must fail.");
                RequireObserved(stop, terminal);
                // Main AddTaskFailures flattens the Task.WhenAll failure, then
                // CreateTerminalFailure constructs one aggregate in this exact order.
                RequireOrdered(terminal, first, second, third);
            }

            if (disposeFirst)
            {
                disposalAttempted = true;
                FailureObservation disposal;
                try { disposal = await CaptureFailureAsync(server.DisposeAsync().AsTask()); }
                catch (Exception exception) { disposal = new(exception, null); }
                try { RequireObserved(disposal, terminal); }
                catch (Exception exception) { cleanupFailure = exception; }
            }

            RequireObserved(await CaptureFailureAsync(server.WaitForShutdownAsync()), terminal);
            RequireObserved(await CaptureFailureAsync(server.StopAsync(TimeSpan.Zero).AsTask()), terminal);
            RequireObserved(await CaptureFailureAsync(server.WaitForShutdownAsync()), terminal);
            Require(server.LifecycleState == SharpLinkServerLifecycleState.Faulted,
                "Observing a signal must not convert Faulted into success.");
        }
        catch (Exception exception) { bodyFailure = exception; }

        if (!disposalAttempted)
        {
            disposalAttempted = true;
            FailureObservation disposal;
            try { disposal = await CaptureFailureAsync(server.DisposeAsync().AsTask()); }
            catch (Exception exception) { disposal = new(exception, null); }
            try
            {
                if (terminal is null)
                    RequireSuccess(disposal); // Preserve any independent cleanup failure below.
                else
                    RequireObserved(disposal, terminal);
            }
            catch (Exception exception) { cleanupFailure = exception; }
        }
        ThrowBodyAndCleanupFailures(bodyFailure, cleanupFailure);
    }

    private static void RequireNoUnobservedSignal(bool failDuringStart)
    {
        var marker = "terminal-owned-" + Guid.NewGuid().ToString("N");
        using var observation = new TerminalSignalObservation(marker);
        var references = CompleteFailureAndRelease(marker, failDuringStart);
        observation.RequireCollection(references);
        observation.RequireCount(0);
    }

    // This scope never waits for terminal or reads its Exception. Only a weak task
    // identity is obtained after public cleanup. No completed async helper frame
    // survives into the collection phase holding the server.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CompleteFailureAndRelease(string marker, bool failDuringStart)
    {
        var failure = new IOException(marker);
        var server = CreateServer(new FailureListener(
            failDuringStart ? failure : null, failDuringStart ? null : failure));
        Exception? bodyFailure = null;
        try
        {
            if (failDuringStart)
                RequireObserved(CaptureFailure(server.StartAsync().AsTask()), failure);
            else
            {
                RequireSuccess(CaptureFailure(server.StartAsync().AsTask()));
                RequireObserved(CaptureFailure(server.StopAsync(TimeSpan.Zero).AsTask()), failure);
            }
        }
        catch (Exception exception)
        {
            bodyFailure = exception;
        }

        // Independent cleanup always runs once; its failure cannot mask the body.
        FailureObservation disposal;
        try { disposal = CaptureFailure(server.DisposeAsync().AsTask()); }
        catch (Exception exception) { disposal = new(exception, null); }
        Exception? cleanupFailure = null;
        try { RequireObserved(disposal, failure); }
        catch (Exception exception) { cleanupFailure = exception; }
        ThrowBodyAndCleanupFailures(bodyFailure, cleanupFailure);

        var coordinator = server.LifecycleForDiagnostics;
        var field = coordinator.GetType().GetField("_terminalCompletion",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("Cannot identify the framework-owned terminal signal.");
        var completion = (TaskCompletionSource<bool>)(field.GetValue(coordinator)
            ?? throw new Exception("Terminal signal is missing."));
        Require(completion.Task.IsFaulted, "Terminal signal must remain faulted.");
        return [new WeakReference(server), new WeakReference(completion.Task)];
    }

    private static SharpLinkServer CreateServer(FailureListener listener)
        => (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(listener)
            .Build();

    private readonly record struct FailureObservation(Exception? Thrown, AggregateException? TaskFailure);

    private static FailureObservation CaptureFailure(Task task)
    {
        try { task.GetAwaiter().GetResult(); return new(null, task.Exception); }
        catch (Exception exception) { return new(exception, task.Exception); }
    }

    private static async Task<FailureObservation> CaptureFailureAsync(Task task)
    {
        try { await task; return new(null, task.Exception); }
        catch (Exception exception) { return new(exception, task.Exception); }
    }
    private static void RequireObserved(FailureObservation observed, Exception expected)
    {
        if (!ReferenceEquals(observed.Thrown, expected) ||
            observed.TaskFailure is not { InnerExceptions.Count: 1 } wrapper ||
            !ReferenceEquals(wrapper.InnerExceptions[0], expected))
        {
            throw new Exception("Expected the original failure and its complete single Task wrapper.",
                observed.TaskFailure ?? observed.Thrown);
        }
    }

    private static void RequireOrdered(Exception exception, params Exception[] expected)
    {
        if (exception is not AggregateException aggregate || aggregate.InnerExceptions.Count != expected.Length)
            throw new Exception("Unexpected terminal aggregate structure.", exception);
        for (var index = 0; index < expected.Length; index++)
            if (!ReferenceEquals(aggregate.InnerExceptions[index], expected[index]))
                throw new Exception($"Unexpected terminal sibling at index {index}.", exception);
    }

    private static void RequireSuccess(FailureObservation observed)
    {
        if (observed.Thrown is not null || observed.TaskFailure is not null)
            throw new Exception("Expected successful completion.", observed.TaskFailure ?? observed.Thrown);
    }

    private static void ThrowBodyAndCleanupFailures(Exception? bodyFailure, Exception? cleanupFailure)
    {
        if (bodyFailure is not null && cleanupFailure is not null)
            throw new AggregateException("Body failed; independent cleanup also failed.", bodyFailure, cleanupFailure);
        if (bodyFailure is not null)
            ExceptionDispatchInfo.Capture(bodyFailure).Throw();
        if (cleanupFailure is not null)
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class FailureListener(Exception? acceptFailure, Exception? disposeFailure) : IServerTransportListener
    {
        public System.Net.EndPoint? LocalEndPoint => null;

        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            if (acceptFailure is not null)
                ExceptionDispatchInfo.Capture(acceptFailure).Throw();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable accept continuation.");
        }

        public ValueTask DisposeAsync() => disposeFailure is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(disposeFailure);
    }
}

[NotInParallel]
public sealed class ServerTerminalSignalObservationPositiveControl
{
    [Test]
    [Explicit]
    public void UnobservedFaultMustBeCollectedAndReported()
    {
        var marker = "terminal-positive-" + Guid.NewGuid().ToString("N");
        using var observation = new TerminalSignalObservation(marker);
        var reference = CreateUnobservedFault(marker);
        observation.RequireCollection([reference]);
        observation.RequireCount(1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateUnobservedFault(string marker)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetException(new IOException(marker));
        return new WeakReference(completion.Task);
    }
}

internal sealed class TerminalSignalObservation : IDisposable
{
    private readonly string _marker;
    private int _matchingFailures;

    internal TerminalSignalObservation(string marker)
    {
        _marker = marker;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
    }

    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs eventArgs)
    {
        foreach (var failure in eventArgs.Exception.Flatten().InnerExceptions)
            if (failure.Message == _marker)
                Interlocked.Increment(ref _matchingFailures);
        // Event-only marker matching is not a structural assertion. Never SetObserved,
        // retain event args, or read any Task.Exception before its finalizer event.
    }

    internal void RequireCollection(WeakReference[] references)
    {
        for (var round = 0; round < 10; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            // The second collection can enqueue a newly unreachable exception holder.
            // No return or count check may precede this finalizer barrier.
            GC.WaitForPendingFinalizers();
            var retained = false;
            foreach (var reference in references)
                retained |= reference.IsAlive;
            if (!retained)
                return;
            Thread.Sleep(10);
        }
        throw new Exception("INCONCLUSIVE: target server/task retained after bounded GC/finalizer sweeps.");
    }

    internal void RequireCount(int expected)
    {
        var actual = Volatile.Read(ref _matchingFailures);
        if (actual != expected)
            throw new Exception($"Expected {expected} matching unobserved failure(s), received {actual}.");
    }

    public void Dispose() => TaskScheduler.UnobservedTaskException -= OnUnobserved;
}
