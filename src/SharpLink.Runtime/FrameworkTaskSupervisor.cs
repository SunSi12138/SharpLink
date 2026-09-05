using System.Runtime.ExceptionServices;
using System.Linq;

namespace SharpLink.Runtime;

internal enum TaskObservationMode : byte
{
    FrameworkOwned,
    ExternallyObserved
}

internal sealed class FrameworkTaskRegistration
{
    internal FrameworkTaskRegistration(
        FrameworkTaskSupervisor supervisor,
        Task task,
        string operation,
        TaskObservationMode observationMode,
        Func<Exception, bool> shutdownExpectedExceptionClassifier,
        long sequence)
    {
        Supervisor = supervisor;
        Task = task;
        Operation = operation;
        ObservationMode = observationMode;
        ShutdownExpectedExceptionClassifier = shutdownExpectedExceptionClassifier;
        Sequence = sequence;
    }

    internal FrameworkTaskSupervisor Supervisor { get; }

    internal Task Task { get; }

    internal string Operation { get; }

    internal TaskObservationMode ObservationMode { get; }

    internal Func<Exception, bool> ShutdownExpectedExceptionClassifier { get; }

    internal long Sequence { get; }
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
    private readonly Dictionary<Task, FrameworkTaskRegistration> _active =
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
                registration = new FrameworkTaskRegistration(
                    this,
                    task,
                    operation,
                    observationMode,
                    shutdownExpectedExceptionClassifier,
                    ++_nextSequence);
                _totalTracked++;
                if (_sealed)
                    _lateRegistrations++;
                return registration;
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
                    registration.Task.Status);
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

    private void Complete(FrameworkTaskRegistration registration, Task completedTask)
    {
        List<Exception>? unexpected = null;
        TaskCompletionSource<bool>? drained = null;
        lock (_gate)
        {
            if (!_active.Remove(completedTask))
                return;

            if (registration.ObservationMode == TaskObservationMode.FrameworkOwned)
            {
                if (completedTask.Exception is { } aggregate)
                {
                    foreach (var exception in aggregate.Flatten().InnerExceptions)
                        RecordFailureLocked(registration, exception, ref unexpected);
                }
                else if (completedTask.IsCanceled)
                {
                    RecordFailureLocked(
                        registration,
                        new TaskCanceledException(completedTask),
                        ref unexpected);
                }
            }
            else
            {
                _ = completedTask.Exception;
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
        FrameworkTaskRegistration registration,
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
