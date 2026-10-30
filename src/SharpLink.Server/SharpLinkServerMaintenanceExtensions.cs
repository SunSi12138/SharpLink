namespace SharpLink.Server;

/// <summary>Configures host-only, fixed-cohort server implementation maintenance.</summary>
public static class SharpLinkServerMaintenanceExtensions
{
    /// <summary>
    /// Seals configuration and enables compatible implementation replacement before starting the
    /// server. All clients must use TLS, authenticate to a listed subject, and enroll the exact
    /// listed runtime incarnation. Register initial implementation assemblies before this call.
    /// Queued admission is unsupported: current and physically retained policy generations must
    /// have waiting disabled. Incompatible configuration is rejected, never silently changed.
    /// The returned controller is host authority and must not be exposed as an RPC service.
    /// </summary>
    public static ISharpLinkMaintenanceController EnableCooperativeMaintenance(
        this ISharpLinkServer server,
        Guid domainId,
        IReadOnlyList<SharpLinkMaintenanceParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(participants);
        return server is SharpLinkServer implementation
            ? implementation.ConfigureMaintenance(domainId, participants)
            : throw new NotSupportedException("This server does not support cooperative maintenance.");
    }
}
