namespace SharpLink.Server;

internal sealed partial class SharpLinkServer : ISharpLinkConnectionAdmissionRuntimeControl
{
    void ISharpLinkConnectionAdmissionRuntimeControl.UpdateConnectionAdmission(
        Action<SharpLinkConnectionAdmissionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SharpLinkConnectionAdmissionOptions();
        configure(options);
        var candidate = options.CloneValidated();

        lock (_registryGate)
        {
            if (CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
            {
                throw new InvalidOperationException(
                    "Connection admission publication is sealed because the server is stopping.");
            }

            _connectionAdmission.UpdateTargets(
                candidate.MaxConcurrentConnections,
                candidate.MaxConcurrentHandshakes);
        }
    }
}
