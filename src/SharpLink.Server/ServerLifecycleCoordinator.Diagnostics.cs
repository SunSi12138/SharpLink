namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    internal sealed partial class ServerLifecycleCoordinator
    {
        internal Task? ShutdownCleanupObserverTaskForDiagnostics
            => Volatile.Read(ref _shutdownCleanupObserver);

        internal Task? DeferredServiceCleanupTaskForDiagnostics
            => Volatile.Read(ref _deferredServiceCleanupTask);

        internal TaskCompletionSource<bool> CallsDrainedSignalForTesting => _callsDrained;

        internal Task DisposeAllSessionsForDiagnosticsAsync() => DisposeAllSessionsAsync();

        internal static Task<bool> WaitUntilWithProviderForDiagnosticsAsync(
            Task task,
            long deadline,
            TimeProvider timeProvider)
            => WaitUntilWithProviderAsync(task, deadline, timeProvider);
    }
}
