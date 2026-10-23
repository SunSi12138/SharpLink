namespace SharpLink.Client;

/// <summary>Explicit pre-connect enrollment in authenticated compatible server maintenance.</summary>
public static class SharpLinkClientMaintenanceExtensions
{
    /// <summary>
    /// Enables participation for a fixed endpoint before connecting. Client contract assemblies and
    /// pool/session topology configuration become immutable. TLS server authentication is required.
    /// Calls made while the host freezes the domain fail existing readiness checks; held compatible
    /// proxies remain usable after successful same-session resume.
    /// </summary>
    public static ISharpLinkClient EnableCooperativeMaintenance(
        this ISharpLinkClient client,
        SharpLinkClientMaintenanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        if (client is not SharpLinkClient implementation)
            throw new NotSupportedException("This client does not support cooperative maintenance.");
        implementation.ConfigureMaintenance(options);
        return client;
    }
}
