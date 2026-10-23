namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private IDisposable? TryEnterColdPreparation()
    {
        lock (_registryGate)
        {
            if (MaintenanceConfigured || _serviceModuleRegistry.IsColdWriterHeld ||
                CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
                return null;
            return _serviceModuleRegistry.EnterPreparation();
        }
    }

    private IDisposable EnterColdPreparation()
        => TryEnterColdPreparation() ?? throw new InvalidOperationException(
            "Maintenance or shutdown has sealed cold configuration preparation.");
}
