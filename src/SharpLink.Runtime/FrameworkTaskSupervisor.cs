using System.Runtime.ExceptionServices;
using System.Linq;

namespace SharpLink.Runtime;

internal enum TaskObservationMode : byte
{
    FrameworkOwned,
    ExternallyObserved
}

internal sealed class FrameworkTaskRegistration : FrameworkOperationRegistration
{
    internal FrameworkTaskRegistration(
        FrameworkTaskSupervisor supervisor,
        Task task,
        string operation,
        TaskObservationMode observationMode,
        Func<Exception, bool> shutdownExpectedExceptionClassifier,
        long sequence)
        : base(supervisor, operation, observationMode, shutdownExpectedExceptionClassifier)
    {
        Task = task;
        Sequence = sequence;
    }

    internal Task Task { get; }
    internal override object Identity => Task;
    internal override TaskStatus Status => Task.Status;
}

internal sealed record FrameworkTaskDiagnosticSnapshot(
    string Operation,
    TaskObservationMode ObservationMode,
    TaskStatus Status);

internal sealed record FrameworkTaskSupervisorSnapshot(
    bool IsSealed,
    bool IsDrained,
    long TotalTracked,
    int ActiveTasks,
    int FrameworkOwnedTasks,
    int ExternallyObservedTasks,
    int RetainedFailures,
    int DroppedFailures,
    int SuppressedShutdownFailures,
    int LateRegistrations,
    int RejectedRegistrations,
    int TruncatedOperations,
    IReadOnlyList<FrameworkTaskDiagnosticSnapshot> Operations);

internal sealed class FrameworkTaskSupervisor
{
    private const int MaximumRetainedFailures = 64;
    private const int MaximumSnapshotOperations = 32;

    private readonly Lock _gate = new();
    private readonly Dictionary<object, FrameworkOperationRegistration> _active =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<ExceptionDispatchInfo> _failures = [];
    private readonly Action<string, Exception>? _unexpectedFaultObserver;
    private TaskCompletionSource<bool>? _drainSignal;
    private long _nextSequence;
    private long _totalTracked;
    private int _droppedFailures;
    private int _pendingDroppedFailures;
    private int _suppressedShutdownFailures;
    private int _lateRegistrations;
    private int _rejectedRegistrations;
    private bool _sealed;
    private bool _drainStarted;

    internal FrameworkTaskSupervisor(Action<string, Exception>? unexpectedFaultObserver = null)
        => _unexpectedFaultObserver = unexpectedFaultObserver;

    internal FrameworkTaskRegistration Track(
        Task task,
        string operation,
        TaskObservationMode observationMode,
        Func<Exception, bool> shutdownExpectedExceptionClassifier)
        => TrackCore(task, operation, observationMode, shutdownExpectedExceptionClassifier, returnRegistration: true)!;

    // Production wrappers discard the registration. A terminal success needs only the
    // lifecycle accounting, not a registration object or a task continuation.
    internal void TrackDetached(
        Task task,
        string operation,
        TaskObservationMode observationMode,
        Func<Exception, bool> shutdownExpectedExceptionClassifier)
        => TrackCore(task, operation, observationMode, shutdownExpectedExceptionClassifier, returnRegistration: false);

    private FrameworkTaskRegistration? TrackCore(
        Task task,
        string operation,
        TaskObservationMode observationMode,
        Func<Exception, bool> shutdownExpectedExceptionClassifier,
        bool returnRegistration)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(shutdownExpectedExceptionClassifier);
        if (!Enum.IsDefined(observationMode))
            throw new ArgumentOutOfRangeException(nameof(observationMode));

        FrameworkTaskRegistration registration;
        lock (_gate)
        {
            if (_drainStarted && _active.Count == 0)
            {
                _rejectedRegistrations++;
                ObserveRejectedTask(task);
                throw new InvalidOperationException(
                    $"Framework task '{operation}' cannot be registered after supervisor drain has completed.");
            }
            if (task.IsCompletedSuccessfully)
            {
                // Async methods that complete synchronously can share Task.CompletedTask. Such
                // invocations are already terminal and have no fault to observe, so Task object
                // identity cannot be used to reject another logical registration.
                var sequence = ++_nextSequence;
                _totalTracked++;
                if (_sealed)
                    _lateRegistrations++;
                return returnRegistration
                    ? new FrameworkTaskRegistration(
                        this, task, operation, observationMode,
                        shutdownExpectedExceptionClassifier, sequence)
                    : null;
            }
            if (_active.ContainsKey(task))
            {
                throw new InvalidOperationException(
                    $"Framework task '{operation}' is already registered with this supervisor.");
            }

            registration = new FrameworkTaskRegistration(
                this,
                task,
                operation,
                observationMode,
                shutdownExpectedExceptionClassifier,
                ++_nextSequence);
            _active.Add(task, registration);
            _totalTracked++;
            if (_sealed)
                _lateRegistrations++;
        }

        _ = task.ContinueWith(
            static (completedTask, state) =>
            {
                var completedRegistration = (FrameworkTaskRegistration)state!;
                completedRegistration.Supervisor.Complete(completedRegistration, completedTask);
            },
            registration,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return registration;
    }

    internal void Seal()
    {
        TaskCompletionSource<bool>? completed = null;
        lock (_gate)
        {
            if (_sealed)
                return;
            _sealed = true;
            if (_active.Count == 0)
                completed = _drainSignal;
        }
        completed?.TrySetResult(true);
    }

    internal Task DrainAsync()
    {
        Task signal;
        lock (_gate)
        {
            if (!_sealed)
            {
                throw new InvalidOperationException(
                    "Framework task supervision must be sealed before it can be drained.");
            }

            _drainStarted = true;
            _drainSignal ??= new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (_active.Count == 0)
                _drainSignal.TrySetResult(true);
            signal = _drainSignal.Task;
        }
        return DrainCoreAsync(signal);
    }

    internal FrameworkTaskSupervisorSnapshot CaptureSnapshot()
    {
        lock (_gate)
        {
            var operationCount = Math.Min(_active.Count, MaximumSnapshotOperations);
            var operations = new FrameworkTaskDiagnosticSnapshot[operationCount];
            var ordered = _active.Values
                .OrderBy(static registration => registration.Sequence)
                .Take(operationCount);
            var index = 0;
            var frameworkOwned = 0;
            var externallyObserved = 0;
            foreach (var registration in _active.Values)
            {
                if (registration.ObservationMode == TaskObservationMode.FrameworkOwned)
                    frameworkOwned++;
                else
                    externallyObserved++;
            }
            foreach (var registration in ordered)
            {
                operations[index++] = new FrameworkTaskDiagnosticSnapshot(
                    registration.Operation,
                    registration.ObservationMode,
                    registration.Status);
            }

            return new FrameworkTaskSupervisorSnapshot(
                _sealed,
                _drainStarted && _active.Count == 0,
                _totalTracked,
                _active.Count,
                frameworkOwned,
                externallyObserved,
                _failures.Count,
                _droppedFailures,
                _suppressedShutdownFailures,
                _lateRegistrations,
                _rejectedRegistrations,
                _active.Count - operationCount,
                operations);
        }
    }

    internal void RegisterOperation(FrameworkOperationRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!ReferenceEquals(registration.Supervisor, this))
            throw new ArgumentException("The operation belongs to another supervisor.", nameof(registration));
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.Operation);
        ArgumentNullException.ThrowIfNull(registration.ShutdownExpectedExceptionClassifier);
        if (!Enum.IsDefined(registration.ObservationMode))
            throw new ArgumentOutOfRangeException(nameof(registration));
        lock (_gate)
        {
            if (_drainStarted && _active.Count == 0)
            {
                _rejectedRegistrations++;
                throw new InvalidOperationException(
                    $"Framework task '{registration.Operation}' cannot be registered after supervisor drain has completed.");
            }
            if (registration.Sequence != 0 || _active.ContainsKey(registration.Identity))
                throw new InvalidOperationException($"Framework task '{registration.Operation}' is already registered with this supervisor.");
            registration.Sequence = ++_nextSequence;
            _active.Add(registration.Identity, registration);
            _totalTracked++;
            if (_sealed)
                _lateRegistrations++;
        }
    }

    private void Complete(FrameworkTaskRegistration registration, Task completedTask)
        => CompleteOperation(registration, (Exception?)completedTask.Exception ??
            (registration.ObservationMode == TaskObservationMode.FrameworkOwned && completedTask.IsCanceled
                ? new TaskCanceledException(completedTask)
                : null));

    internal void CompleteOperation(FrameworkOperationRegistration registration, Exception? failure)
    {
        List<Exception>? unexpected = null;
        TaskCompletionSource<bool>? drained = null;
        lock (_gate)
        {
            if (!_active.Remove(registration.Identity))
                return;

            if (registration.ObservationMode == TaskObservationMode.FrameworkOwned && failure is not null)
            {
                if (failure is AggregateException aggregate)
                {
                    foreach (var exception in aggregate.Flatten().InnerExceptions)
                        RecordFailureLocked(registration, exception, ref unexpected);
                }
                else
                {
                    RecordFailureLocked(registration, failure, ref unexpected);
                }
            }

            if (_sealed && _active.Count == 0)
                drained = _drainSignal;
        }

        if (unexpected is not null && _unexpectedFaultObserver is not null)
        {
            for (var index = 0; index < unexpected.Count; index++)
            {
                try
                {
                    _unexpectedFaultObserver(registration.Operation, unexpected[index]);
                }
                catch
                {
                    // Diagnostics must never fault the continuation that observes the task.
                }
            }
        }
        drained?.TrySetResult(true);
    }

    private void RecordFailureLocked(
        FrameworkOperationRegistration registration,
        Exception exception,
        ref List<Exception>? unexpected)
    {
        if (_sealed && registration.ShutdownExpectedExceptionClassifier(exception))
        {
            _suppressedShutdownFailures++;
            return;
        }

        (unexpected ??= []).Add(exception);
        if (_failures.Count < MaximumRetainedFailures)
            _failures.Add(ExceptionDispatchInfo.Capture(exception));
        else
        {
            _droppedFailures++;
            _pendingDroppedFailures++;
        }
    }

    private async Task DrainCoreAsync(Task signal)
    {
        await signal.ConfigureAwait(false);
        ExceptionDispatchInfo[] failures;
        int dropped;
        lock (_gate)
        {
            failures = [.. _failures];
            _failures.Clear();
            dropped = _pendingDroppedFailures;
            _pendingDroppedFailures = 0;
        }

        if (failures.Length == 0 && dropped == 0)
            return;
        if (failures.Length == 1 && dropped == 0)
            failures[0].Throw();

        var exceptions = new List<Exception>(failures.Length + (dropped == 0 ? 0 : 1));
        for (var index = 0; index < failures.Length; index++)
            exceptions.Add(failures[index].SourceException);
        if (dropped != 0)
        {
            exceptions.Add(new InvalidOperationException(
                $"Framework task supervision dropped {dropped} additional failures after reaching its bounded retention limit."));
        }
        throw new AggregateException(exceptions);
    }

    private static void ObserveRejectedTask(Task task)
    {
        _ = task.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
