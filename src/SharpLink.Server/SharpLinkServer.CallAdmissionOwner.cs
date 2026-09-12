namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    internal bool IsRunningForCallAdmission => CurrentState == ServerState.Running;

    internal ServerResourceGovernor ResourceGovernorForCallAdmission => ResourceGovernor;

    internal void TrySignalCallsDrainedForCallAdmission(ServerConnectionState? releasingConnection)
        => TrySignalCallsDrained(releasingConnection);
}
