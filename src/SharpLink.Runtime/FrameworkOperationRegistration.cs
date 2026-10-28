namespace SharpLink.Runtime;

// A physical operation can own its supervision node directly, without manufacturing a
// Task solely so that a second object and ContinueWith can observe its completion.
internal abstract class FrameworkOperationRegistration
{
    protected FrameworkOperationRegistration(
        FrameworkTaskSupervisor supervisor,
        string operation,
        TaskObservationMode observationMode,
        Func<Exception, bool> shutdownExpectedExceptionClassifier)
    {
        Supervisor = supervisor;
        Operation = operation;
        ObservationMode = observationMode;
        ShutdownExpectedExceptionClassifier = shutdownExpectedExceptionClassifier;
    }

    internal FrameworkTaskSupervisor Supervisor { get; }
    internal string Operation { get; }
    internal TaskObservationMode ObservationMode { get; }
    internal Func<Exception, bool> ShutdownExpectedExceptionClassifier { get; }
    internal long Sequence { get; set; }
    internal virtual object Identity => this;
    internal virtual TaskStatus Status => TaskStatus.WaitingForActivation;
}
