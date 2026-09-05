namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    // Non-owning aliases retained for focused collaborators that participate in shutdown
    // without owning the lifecycle state machine themselves.
    private CancellationTokenSource _acceptCts => _lifecycle.AcceptSource;

    private Lock _stateGate => _lifecycle.StateGate;

    internal ServerLifecycleCoordinator LifecycleForDiagnostics => _lifecycle;

    // Compatibility forwarding seam for an existing shutdown-failure characterization probe.
    private Task DisposeAllSessionsAsync()
        => _lifecycle.DisposeAllSessionsForDiagnosticsAsync();
}
