using System.Runtime.CompilerServices;

namespace SharpLink.Runtime;

// Nonpooled physical completion and supervision share this one node. This is not a
// public awaitable and must only be used with exactly one outstanding ValueTask.
internal abstract class FrameworkValueTaskOperation : FrameworkOperationRegistration
{
    private ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter _awaiter;
    private ExecutionContext? _operationContext;
    private ExecutionContext? _supervisionContext;
    private Exception? _failure;

    protected FrameworkValueTaskOperation(
        FrameworkTaskSupervisor supervisor,
        string operation,
        Func<Exception, bool> shutdownExpectedExceptionClassifier,
        ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter awaiter)
        : base(supervisor, operation, TaskObservationMode.FrameworkOwned, shutdownExpectedExceptionClassifier)
    {
        _awaiter = awaiter;
        _operationContext = ExecutionContext.Capture();
    }

    internal void ObserveCompletion()
    {
        // The former async bootstrap and its ContinueWith observer captured different
        // contexts. Preserve both: physical cleanup uses the context at suspension;
        // supervision diagnostics use the caller's context at registration.
        _supervisionContext = ExecutionContext.Capture();
        var context = _operationContext;
        _operationContext = null;
        if (context is null)
        {
            // Null means the physical suspension suppressed flow. Start has restored
            // the caller by now, so safe OnCompleted must not capture that caller instead.
            if (ExecutionContext.IsFlowSuppressed())
                AttachContinuation();
            else
            {
                using var suppression = ExecutionContext.SuppressFlow();
                AttachContinuation();
            }
        }
        else
            ExecutionContext.Run(context, static state => ((FrameworkValueTaskOperation)state!).AttachContinuation(), this);
    }

    private void AttachContinuation()
        // Safe OnCompleted flows ExecutionContext; the configured awaiter does not
        // capture SynchronizationContext or a non-default TaskScheduler.
        => _awaiter.OnCompleted(Complete);

    private void Complete()
    {
        var awaiter = _awaiter;
        _awaiter = default;
        try
        {
            CompletePhysicalOperation(awaiter);
        }
        catch (Exception exception)
        {
            _failure = exception is OperationCanceledException canceled
                ? new TaskCanceledException("A task was canceled.", canceled, canceled.CancellationToken)
                : exception;
        }
        finally
        {
            var context = _supervisionContext;
            _supervisionContext = null;
            if (context is null)
                CompleteSupervision();
            else
                ExecutionContext.Run(context, static state => ((FrameworkValueTaskOperation)state!).CompleteSupervision(), this);
        }
    }

    private void CompleteSupervision()
    {
        var failure = _failure;
        _failure = null;
        Supervisor.CompleteOperation(this, failure);
    }

    protected abstract void CompletePhysicalOperation(ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter awaiter);
}
