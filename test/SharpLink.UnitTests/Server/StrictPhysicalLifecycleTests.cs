using Microsoft.Extensions.Logging;
using System.Linq;
using SharpLink.Sdk;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

// Actual server lifecycle/supervisor tasks. These are not TCP producer fixtures and
// do not claim to cover generated producer/fence behavior. No teardown blanket catch.
public sealed class StrictPhysicalLifecycleTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private const string DeferredMessage =
        "Strict physical supervisor drain remains incomplete; its exact task and eventual outcome remain owned by the lifecycle observer.";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SolePhysicalCancellationRetainsExactRawIdentityBeforeAndAfterSeal(bool afterSeal)
    {
        var listener = new Listener();
        var server = CreateServer(listener);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var raw = new OperationCanceledException("independent physical cleanup", cancellation.Token);
        var release = Signal();
        var physical = FailAfterAsync(release.Task, raw);
        Exception? bodyFailure = null;
        try
        {
            await server.StartAsync();
            await listener.AcceptStarted.Task.WaitAsync(Bound);
            FrameworkTaskRegistration? receipt = null;
            server.TrackStrictPhysicalRuntimeTask(physical, "StrictLifecycleProbe", ref receipt);
            Ensure(receipt is { IsStrictPhysical: true } && ReferenceEquals(receipt.Task, physical), "exact strict receipt");
            Task stop;
            if (afterSeal)
            {
                stop = server.StopAsync(TimeSpan.Zero).AsTask();
                await UntilAsync(() => server.FrameworkTaskSnapshotForDiagnostics.IsSealed);
            }
            else stop = server.WaitForShutdownAsync();
            release.TrySetResult();
            var failure = await CaptureAsync(stop);
            RequireClosure(failure, [raw]);
            await ObserveLifecycleAsync(server);
            var retained = server.LifecycleForDiagnostics.StrictPhysicalDrainForDiagnostics;
            Ensure(retained.Requested && retained.Observed, "strict drain outcome observed");
            Ensure(ReferenceEquals(retained.Failure, raw), "sole physical OCE identity must not be synthesized");
            Ensure(server.LifecycleState == SharpLinkServerLifecycleState.Faulted, "independent failure escalates");
        }
        catch (Exception exception) { bodyFailure = exception; }
        finally { release.TrySetResult(); }
        await FinishAsync(server, bodyFailure, [raw]);
    }

    [Test]
    public async Task CallerOwnedSetupFailureAndListenerCleanupFailureBothSurviveSuccessfulDrain()
    {
        var setup = new InvalidOperationException("strict tracker setup residual");
        var cleanup = new IOException("listener cleanup residual");
        var listener = new Listener(cleanup);
        var server = CreateServer(listener);
        Exception? bodyFailure = null;
        try
        {
            await server.StartAsync();
            await listener.AcceptStarted.Task.WaitAsync(Bound);
            server.ReportStrictPhysicalRuntimeFailure(setup, acceptedReceipt: null);
            RequireClosure(await CaptureAsync(server.WaitForShutdownAsync()), [setup, cleanup]);
            await ObserveLifecycleAsync(server);
            var retained = server.LifecycleForDiagnostics.StrictPhysicalDrainForDiagnostics;
            Ensure(retained.Requested && retained.Observed && retained.Failure is null,
                "successful supervisor drain cannot erase caller-owned setup/cleanup errors");
            Ensure(listener.DisposeAttempts == 1, "actual listener cleanup attempted exactly once");
            RequireClosure(server.LifecycleForDiagnostics.StrictPhysicalTerminalFailureForDiagnostics, [setup, cleanup]);
            RequireOccurrence(server.LifecycleForDiagnostics.StrictPhysicalTerminalFailureForDiagnostics, setup);
            RequireOccurrence(server.LifecycleForDiagnostics.StrictPhysicalTerminalFailureForDiagnostics, cleanup);
        }
        catch (Exception exception) { bodyFailure = exception; }
        await FinishAsync(server, bodyFailure, [setup, cleanup]);
    }

    [Test]
    public async Task BudgetExpiryKeepsOriginalPrimaryAndActualDeferredDrainWithLatePhysicalFailure()
    {
        var clock = new ManualTimeProvider();
        var listener = new Listener();
        var server = CreateServer(listener, clock);
        var first = new InvalidOperationException("first physical failure");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var late = new OperationCanceledException("late physical cleanup", cancellation.Token);
        var releaseFirst = Signal();
        var releaseLate = Signal();
        Exception? bodyFailure = null;
        try
        {
            await server.StartAsync();
            await listener.AcceptStarted.Task.WaitAsync(Bound);
            FrameworkTaskRegistration? firstReceipt = null, lateReceipt = null;
            server.TrackStrictPhysicalRuntimeTask(FailAfterAsync(releaseFirst.Task, first), "FirstPhysical", ref firstReceipt);
            server.TrackStrictPhysicalRuntimeTask(FailAfterAsync(releaseLate.Task, late), "HeldPhysical", ref lateReceipt);
            releaseFirst.TrySetResult();
            await UntilAsync(() => server.LifecycleForDiagnostics.StrictPhysicalDrainForDiagnostics.Drain is not null &&
                clock.EarliestTimerTimestamp != long.MaxValue);
            var originalDrain = server.LifecycleForDiagnostics.StrictPhysicalDrainForDiagnostics.Drain!;
            Ensure(!originalDrain.IsCompleted, "held actual task retains supervisor drain");
            clock.Advance(TimeSpan.FromSeconds(6));
            var terminal = server.WaitForShutdownAsync();
            var failure = await CaptureAsync(terminal);
            RequireClosure(failure, [first], allowDeferred: true);
            RequireOccurrence(failure, first);
            var pending = server.LifecycleForDiagnostics.StrictPhysicalDrainForDiagnostics;
            Ensure(!pending.Observed && ReferenceEquals(originalDrain, pending.Drain), "budget does not replace/complete actual drain");
            Ensure(server.FrameworkTaskSnapshotForDiagnostics.ActiveTasks > 0, "held actual task remains owned");
            releaseLate.TrySetResult();
            await ObserveLifecycleAsync(server);
            var completed = server.LifecycleForDiagnostics.StrictPhysicalDrainForDiagnostics;
            Ensure(completed.Observed && ReferenceEquals(originalDrain, completed.Drain), "one actual drain through deferred completion");
            RequireClosure(completed.Failure, [first, late]);
            RequireOccurrence(completed.Failure, late);
            Ensure(ReferenceEquals(failure, await CaptureAsync(terminal)), "completed terminal task is not retroactively replaced");
        }
        catch (Exception exception) { bodyFailure = exception; }
        finally
        {
            releaseFirst.TrySetResult();
            releaseLate.TrySetResult();
            clock.Advance(TimeSpan.FromSeconds(30));
        }
        await FinishAsync(server, bodyFailure, [first, late], allowDeferred: true);
    }

    [Test]
    public async Task LateStrictReportAfterLegacyObserverClosedReusesCompletedDrainAndRetainsBothPrimaries()
    {
        var listener = new Listener();
        var server = CreateServer(listener);
        var original = new InvalidOperationException("earlier legacy runtime failure");
        var lateSetup = new IOException("later strict setup failure");
        Exception? bodyFailure = null;
        try
        {
            await server.StartAsync();
            await listener.AcceptStarted.Task.WaitAsync(Bound);
            server.LifecycleForDiagnostics.BeginAndObserveTerminalFailure(original);
            var terminal = server.WaitForShutdownAsync();
            var originalFailure = await CaptureAsync(terminal);
            RequireClosure(originalFailure, [original]);
            await ObserveLifecycleAsync(server);
            var drain = server.LifecycleForDiagnostics.StrictPhysicalDrainForDiagnostics.Drain;
            Ensure(drain is { IsCompleted: true }, "legacy cleanup published actual completed drain");
            server.ReportStrictPhysicalRuntimeFailure(lateSetup, acceptedReceipt: null);
            await ObserveLifecycleAsync(server);
            var retained = server.LifecycleForDiagnostics.StrictPhysicalDrainForDiagnostics;
            Ensure(retained.Requested && retained.Observed && ReferenceEquals(drain, retained.Drain),
                "late strict callback reuses completed drain even after old observer closed");
            RequireClosure(server.LifecycleForDiagnostics.StrictPhysicalTerminalFailureForDiagnostics, [original, lateSetup]);
            RequireOccurrence(server.LifecycleForDiagnostics.StrictPhysicalTerminalFailureForDiagnostics, original);
            RequireOccurrence(server.LifecycleForDiagnostics.StrictPhysicalTerminalFailureForDiagnostics, lateSetup);
            Ensure(ReferenceEquals(originalFailure, await CaptureAsync(terminal)), "late failure does not mutate completed Stop");
        }
        catch (Exception exception) { bodyFailure = exception; }
        await FinishAsync(server, bodyFailure, [original]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StrictFailureDuringOrdinaryStopCannotBeMaskedByThrowingCleanupLogger(bool exhaustBudget)
    {
        var clock = new ManualTimeProvider();
        var raw = new OperationCanceledException("strict physical during ordinary stop");
        var cleanup = new IOException("independent listener cleanup");
        var reporting = new InvalidOperationException("injected cleanup diagnostic failure");
        using var logger = new ThrowingLoggerFactory(reporting, exhaustBudget
            ? LogEvents.Server.FrameworkCleanupTimeout : LogEvents.Server.DeferredCleanupFailed);
        var listener = new Listener(cleanup);
        var server = CreateServer(listener, clock, logger);
        var releaseFailure = Signal();
        var releaseHeld = Signal();
        Exception? bodyFailure = null;
        try
        {
            await server.StartAsync();
            await listener.AcceptStarted.Task.WaitAsync(Bound);
            FrameworkTaskRegistration? faultReceipt = null, heldReceipt = null;
            server.TrackStrictPhysicalRuntimeTask(FailAfterAsync(releaseFailure.Task, raw), "StrictDuringStop", ref faultReceipt);
            server.TrackStrictPhysicalRuntimeTask(releaseHeld.Task, "HeldPhysicalSuccess", ref heldReceipt);
            var stop = server.StopAsync(TimeSpan.Zero).AsTask();
            await UntilAsync(() => server.FrameworkTaskSnapshotForDiagnostics.IsSealed);
            Ensure(server.LifecycleState == SharpLinkServerLifecycleState.Draining, "ordinary stop owns lifecycle first");
            releaseFailure.TrySetResult();
            await UntilAsync(() => server.LifecycleForDiagnostics.StrictPhysicalDrainForDiagnostics.Requested);
            if (exhaustBudget)
            {
                await UntilAsync(() => clock.EarliestTimerTimestamp != long.MaxValue);
                clock.Advance(TimeSpan.FromSeconds(6));
            }
            else releaseHeld.TrySetResult();
            var failure = await CaptureAsync(stop);
            RequireClosure(failure, [raw, cleanup, reporting], allowDeferred: exhaustBudget);
            RequireOccurrence(failure, raw);
            RequireOccurrence(failure, reporting);
            Ensure(logger.ThrowCount > 0, "real configured ILogger threw at the targeted cleanup event");
            Ensure(server.WaitForShutdownAsync().IsCompleted, "throwing diagnostics cannot skip terminal completion");
            releaseHeld.TrySetResult();
            await ObserveLifecycleAsync(server);
            var cleanupObserver = server.LifecycleForDiagnostics.StrictShutdownCleanupObserverForDiagnostics;
            if (cleanupObserver is not null) await cleanupObserver.WaitAsync(Bound);
            var retained = server.LifecycleForDiagnostics.StrictPhysicalDrainForDiagnostics;
            Ensure(retained.Observed && ReferenceEquals(retained.Failure, raw), "actual strict drain survives reporter failure");
            RequireClosure(server.LifecycleForDiagnostics.StrictPhysicalTerminalFailureForDiagnostics,
                [raw, cleanup, reporting], allowDeferred: exhaustBudget);
            RequireOccurrence(server.LifecycleForDiagnostics.StrictPhysicalTerminalFailureForDiagnostics, cleanup);
        }
        catch (Exception exception) { bodyFailure = exception; }
        finally
        {
            releaseFailure.TrySetResult();
            releaseHeld.TrySetResult();
            clock.Advance(TimeSpan.FromSeconds(30));
        }
        await FinishAsync(server, bodyFailure, [raw, cleanup, reporting], allowDeferred: exhaustBudget);
    }

    private static SharpLinkServer CreateServer(Listener listener, TimeProvider? clock = null,
        ILoggerFactory? loggerFactory = null)
    {
        var builder = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration().UseTransport(listener);
        if (clock is not null) builder.UseTimeProvider(clock);
        if (loggerFactory is not null) builder.UseLoggerFactory(loggerFactory);
        return (SharpLinkServer)builder.Build();
    }

    private static async Task FailAfterAsync(Task release, Exception raw)
    {
        await release.ConfigureAwait(false);
        throw PhysicalTaskFailureException.From(raw);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ObserveLifecycleAsync(SharpLinkServer server)
    {
        await UntilAsync(() => server.LifecycleForDiagnostics.TerminalFailureObserverTaskForDiagnostics is not null);
        await server.LifecycleForDiagnostics.TerminalFailureObserverTaskForDiagnostics!.WaitAsync(Bound);
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(Bound);
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }

    private static async Task<Exception?> CaptureAsync(Task task)
    {
        try { await task.WaitAsync(Bound); return null; }
        catch (Exception failure) { return failure; }
    }

    private static async Task FinishAsync(SharpLinkServer server, Exception? bodyFailure,
        Exception[] expected, bool allowDeferred = false)
    {
        Exception? cleanupAssertion = null;
        try
        {
            var cleanup = await CaptureAsync(server.DisposeAsync().AsTask());
            if (cleanup is not null) RequireClosure(cleanup, expected, allowDeferred);
            var observer = server.LifecycleForDiagnostics.TerminalFailureObserverTaskForDiagnostics;
            if (observer is not null) await observer.WaitAsync(Bound);
            var deferred = server.LifecycleForDiagnostics.StrictShutdownCleanupObserverForDiagnostics;
            if (deferred is not null) await deferred.WaitAsync(Bound);
        }
        catch (Exception failure) { cleanupAssertion = failure; }
        if (bodyFailure is not null && cleanupAssertion is not null) throw new AggregateException(bodyFailure, cleanupAssertion);
        if (bodyFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(bodyFailure).Throw();
        if (cleanupAssertion is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupAssertion).Throw();
    }

    // Exact allowed closure: every leaf must be an injected raw identity, or the one
    // specified lifecycle incomplete-drain diagnostic. No Any/contains acceptance.
    private static void RequireClosure(Exception? failure, Exception[] expected, bool allowDeferred = false)
    {
        Ensure(failure is not null, "expected terminal failure");
        foreach (var raw in expected) if (ReferenceEquals(failure, raw)) return;
        if (failure is AggregateException aggregate)
        {
            Ensure(aggregate.InnerExceptions.Count > 0, "empty wrapper is not a physical outcome");
            foreach (var child in aggregate.InnerExceptions) RequireClosure(child, expected, allowDeferred);
            return;
        }
        if (allowDeferred && failure!.GetType() == typeof(TimeoutException) &&
            failure.Message == DeferredMessage && failure.InnerException is null) return;
        throw new Exception("Unexpected independent lifecycle failure.", failure);
    }

    private static void RequireOccurrence(Exception? failure, Exception expected)
    {
        if (ReferenceEquals(failure, expected)) return;
        if (failure is AggregateException aggregate && aggregate.InnerExceptions.Any(child => HasIdentity(child, expected))) return;
        throw new Exception("Expected raw failure identity was not retained.", failure);
    }

    private static bool HasIdentity(Exception failure, Exception expected)
        => ReferenceEquals(failure, expected) || failure is AggregateException aggregate &&
            aggregate.InnerExceptions.Any(child => HasIdentity(child, expected));

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class ThrowingLoggerFactory(Exception failure, int targetEvent) : ILoggerFactory, ILogger
    {
        internal int ThrowCount;
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id != targetEvent) return;
            Interlocked.Increment(ref ThrowCount);
            throw failure;
        }
    }

    private sealed class Listener(Exception? disposeFailure = null) : IServerTransportListener
    {
        internal TaskCompletionSource AcceptStarted { get; } = Signal();
        internal int DisposeAttempts;
        public System.Net.EndPoint? LocalEndPoint => null;
        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            AcceptStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Canceled accept unexpectedly continued.");
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeAttempts);
            return disposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(disposeFailure);
        }
    }
}
