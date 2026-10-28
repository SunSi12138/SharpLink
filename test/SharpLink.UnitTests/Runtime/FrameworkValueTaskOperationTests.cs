using System;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace SharpLink.UnitTests.Runtime;

public sealed class FrameworkValueTaskOperationTests
{
    private static readonly AsyncLocal<string?> Ambient = new();

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PendingSuccessFaultAndCancellationMustRetainOwnershipUntilCleanup(bool direct)
    {
        for (var outcome = 0; outcome < 3; outcome++)
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            Exception? expected = outcome switch
            {
                1 => new InvalidOperationException("send failed"),
                2 => new OperationCanceledException(canceled.Token),
                _ => null
            };
            var supervisor = new FrameworkTaskSupervisor();
            var source = new ControlledSource();
            var probe = new CompletionProbe();
            var pending = Create(direct, supervisor, source, probe);
            pending.Register();
            supervisor.Seal();
            var drain = supervisor.DrainAsync();
            Ensure(!drain.IsCompleted && supervisor.CaptureSnapshot().ActiveTasks == 1, "pending physical work must hold drain");
            source.Complete(expected);
            var failure = await CaptureFailure(drain);
            Ensure(source.Results == 1 && probe.Cleanups == 1 && probe.Completions == 1, "consume and cleanup must happen exactly once");
            Ensure(outcome == 0 ? failure is null : outcome == 1 ? ReferenceEquals(failure, expected) :
                failure is TaskCanceledException canceledFailure && canceledFailure.CancellationToken == canceled.Token,
                "success/fault/cancellation identity and token must match Task supervision");
            Ensure(supervisor.CaptureSnapshot().IsDrained, "cleanup must retire supervision");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InlineOnCompletedAndCompletionBeforeRegistrationMustNotLoseOwnership(bool direct)
    {
        foreach (var inline in new[] { false, true })
        {
            var supervisor = new FrameworkTaskSupervisor();
            var source = new ControlledSource { CompleteInsideOnCompleted = inline };
            var probe = new CompletionProbe();
            var pending = Create(direct, supervisor, source, probe);
            if (!inline) source.Complete();
            pending.Register();
            supervisor.Seal();
            await supervisor.DrainAsync();
            Ensure(source.Results == 1 && probe.Cleanups == 1, "reentrant or already-completed send must consume once");
            Ensure(supervisor.CaptureSnapshot().TotalTracked == 1, "one logical registration must be counted");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectedRegistrationMustStillConsumeAndReleasePhysicalWork(bool direct)
    {
        var supervisor = new FrameworkTaskSupervisor();
        var source = new ControlledSource();
        var probe = new CompletionProbe();
        var pending = Create(direct, supervisor, source, probe);
        supervisor.Seal();
        await supervisor.DrainAsync();
        try { pending.Register(); throw new Exception("expected rejection"); }
        catch (InvalidOperationException) { }
        source.Complete(new InvalidOperationException("rejected send"));
        await probe.Finished.Task;
        Ensure(source.Results == 1 && probe.Cleanups == 1, "rejected work still owns physical cleanup");
        var snapshot = supervisor.CaptureSnapshot();
        Ensure(snapshot.RejectedRegistrations == 1 && snapshot.TotalTracked == 0 && snapshot.RetainedFailures == 0,
            "rejection cannot reenter accepted ownership or failure accounting");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PhysicalAndSupervisionExecutionContextsMustRemainDistinct(bool direct)
    {
        string? observerContext = null;
        var supervisor = new FrameworkTaskSupervisor((_, _) => observerContext = Ambient.Value);
        var source = new ControlledSource();
        var probe = new CompletionProbe();
        var previousContext = SynchronizationContext.Current;
        var scheduler = new CountingSynchronizationContext();
        Pending pending;
        try
        {
            Ambient.Value = "physical";
            SynchronizationContext.SetSynchronizationContext(scheduler);
            pending = Create(direct, supervisor, source, probe);
            Ambient.Value = "supervision";
            pending.Register();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        supervisor.Seal();
        var drain = supervisor.DrainAsync();
        Ambient.Value = "producer";
        source.Complete(new InvalidOperationException("context marker"));
        await CaptureFailure(drain);
        Ensure(probe.Context == "physical" && observerContext == "supervision", "physical work and observer retain their original distinct contexts");
        Ensure(Ambient.Value == "producer", "callback changes cannot leak into completion producer");
        Ensure(scheduler.Posts == 0 && (source.Flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext) == 0,
            "ConfigureAwait(false) must not schedule onto caller SynchronizationContext");
        if (direct)
            Ensure((source.Flags & ValueTaskSourceOnCompletedFlags.FlowExecutionContext) != 0, "direct observer must request safe context flow");
        Ambient.Value = null;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RegistrationRacingSealAndDrainMustEitherRetainOrDiagnoseRejection(bool direct)
    {
        for (var round = 0; round < 100; round++)
        {
            var supervisor = new FrameworkTaskSupervisor();
            var source = new ControlledSource();
            var probe = new CompletionProbe();
            var pending = Create(direct, supervisor, source, probe);
            using var start = new ManualResetEventSlim();
            Task? drain = null;
            var register = Task.Run(() =>
            {
                start.Wait();
                try { pending.Register(); }
                catch (InvalidOperationException) { }
            });
            var stop = Task.Run(() =>
            {
                start.Wait();
                supervisor.Seal();
                drain = supervisor.DrainAsync();
            });
            start.Set();
            await Task.WhenAll(register, stop);
            var snapshot = supervisor.CaptureSnapshot();
            Ensure(snapshot.TotalTracked + snapshot.RejectedRegistrations == 1, "race must have exactly one accepted/rejected decision");
            if (snapshot.TotalTracked == 1)
                Ensure(!drain!.IsCompleted, "accepted pending work must hold drain");
            source.Complete();
            await probe.Finished.Task;
            await drain!;
            Ensure(probe.Cleanups == 1 && source.Results == 1 && supervisor.CaptureSnapshot().IsDrained,
                "every race must consume once and converge to drained");
        }
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, false)]
    [Arguments(false, true, true)]
    [Arguments(true, false, false)]
    [Arguments(true, false, true)]
    [Arguments(true, true, false)]
    [Arguments(true, true, true)]
    public async Task SuppressionMustPreservePhysicalAndSupervisionContextBoundaries(
        bool direct, bool physicalSuppressed, bool supervisionSuppressed)
    {
        string? observed = null;
        var supervisor = new FrameworkTaskSupervisor((_, _) => observed = Ambient.Value);
        var source = new ControlledSource();
        var probe = new CompletionProbe();
        Pending pending;
        Ambient.Value = "physical";
        if (physicalSuppressed)
        {
            using var suppression = ExecutionContext.SuppressFlow();
            pending = Create(direct, supervisor, source, probe);
        }
        else
            pending = Create(direct, supervisor, source, probe);
        Ambient.Value = "supervision";
        if (supervisionSuppressed)
        {
            using var suppression = ExecutionContext.SuppressFlow();
            pending.Register();
        }
        else
            pending.Register();
        supervisor.Seal();
        var drain = supervisor.DrainAsync();
        Ambient.Value = "producer";
        source.Complete(new InvalidOperationException("suppressed marker"));
        await CaptureFailure(drain);
        Ensure(probe.Context == (physicalSuppressed ? "producer" : "physical"),
            "physical suspension must preserve capture/suppression, not capture the restored caller");
        Ensure(observed == (supervisionSuppressed ? "cleanup mutation" : "supervision"),
            "observer suppression must match the former ContinueWith behavior");
        Ensure(Ambient.Value == (physicalSuppressed ? "cleanup mutation" : "producer"),
            "producer context isolation must match the former async state machine");
        Ambient.Value = null;
    }

    private static Pending Create(bool direct, FrameworkTaskSupervisor supervisor, ControlledSource source, CompletionProbe probe)
        => direct
            ? new Pending(supervisor, new ProbeOperation(supervisor, source.Value.ConfigureAwait(false).GetAwaiter(), probe), null)
            : new Pending(supervisor, null, CompleteLegacy(source.Value, probe));

    private static async Task CompleteLegacy(ValueTask value, CompletionProbe probe)
    {
        try
        {
            probe.Completions++;
            await value.ConfigureAwait(false);
        }
        finally
        {
            probe.Context = Ambient.Value;
            Ambient.Value = "cleanup mutation";
            probe.Cleanups++;
            probe.Finished.TrySetResult();
        }
    }

    private sealed record Pending(FrameworkTaskSupervisor Supervisor, ProbeOperation? Operation, Task? Task)
    {
        internal void Register()
        {
            if (Operation is null)
                Supervisor.Track(Task!, "probe", TaskObservationMode.FrameworkOwned, static _ => false);
            else
            {
                try { Supervisor.RegisterOperation(Operation); }
                finally { Operation.ObserveCompletion(); }
            }
        }
    }

    private sealed class ProbeOperation(FrameworkTaskSupervisor supervisor,
        ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter awaiter, CompletionProbe probe)
        : FrameworkValueTaskOperation(supervisor, "probe", static _ => false, awaiter)
    {
        protected override void CompletePhysicalOperation(ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter value)
        {
            try { probe.Completions++; value.GetResult(); }
            finally
            {
                probe.Context = Ambient.Value;
                Ambient.Value = "cleanup mutation";
                probe.Cleanups++;
                probe.Finished.TrySetResult();
            }
        }
    }

    private sealed class CompletionProbe
    {
        internal int Completions;
        internal int Cleanups;
        internal string? Context;
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ControlledSource : IValueTaskSource
    {
        private Action<object?>? _continuation;
        private object? _state;
        private ExecutionContext? _context;
        private Exception? _failure;
        private bool _completed;
        internal bool CompleteInsideOnCompleted;
        internal int Results;
        internal ValueTaskSourceOnCompletedFlags Flags;
        internal ValueTask Value => new(this, 0);
        public ValueTaskSourceStatus GetStatus(short token) => !_completed ? ValueTaskSourceStatus.Pending :
            _failure is OperationCanceledException ? ValueTaskSourceStatus.Canceled :
            _failure is not null ? ValueTaskSourceStatus.Faulted : ValueTaskSourceStatus.Succeeded;
        public void GetResult(short token)
        {
            Results++;
            Ensure(_completed, "cannot consume a pending result");
            if (_failure is not null) ExceptionDispatchInfo.Capture(_failure).Throw();
        }
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            Ensure(_continuation is null, "one subscription only");
            Flags = flags;
            _continuation = continuation;
            _state = state;
            _context = (flags & ValueTaskSourceOnCompletedFlags.FlowExecutionContext) != 0 ? ExecutionContext.Capture() : null;
            if (CompleteInsideOnCompleted) _completed = true;
            if (_completed) Invoke();
        }
        internal void Complete(Exception? failure = null)
        {
            _failure = failure;
            _completed = true;
            if (_continuation is not null) Invoke();
        }
        private void Invoke()
        {
            if (_context is { } context)
                ExecutionContext.Run(context, static state => ((ControlledSource)state!)._continuation!(((ControlledSource)state)._state), this);
            else
                _continuation!(_state);
        }
    }

    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        internal int Posts;
        public override void Post(SendOrPostCallback callback, object? state) => Posts++;
    }

    private static async Task<Exception?> CaptureFailure(Task task)
    {
        try { await task; return null; }
        catch (Exception exception) { return exception; }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
