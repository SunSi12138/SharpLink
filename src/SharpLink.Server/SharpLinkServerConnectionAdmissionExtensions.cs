namespace SharpLink.Server;

/// <summary>Runtime pre-session connection-admission operations for SharpLink servers.</summary>
public static class SharpLinkServerConnectionAdmissionExtensions
{
    /// <summary>
    /// Atomically replaces the complete connection/handshake admission target pair used by
    /// future acquisition attempts. Existing admitted connections and in-flight handshakes
    /// keep their current lifecycle and are never closed or cancelled solely because a target
    /// was reduced.
    /// </summary>
    /// <param name="server">The server whose pre-session admission targets are updated.</param>
    /// <param name="configure">
    /// Builds the complete desired connection-admission configuration before publication.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> or <paramref name="configure"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The candidate admission limits are invalid.</exception>
    /// <exception cref="InvalidOperationException">The server is stopping or has stopped/faulted.</exception>
    /// <exception cref="NotSupportedException">The server implementation does not support runtime connection admission.</exception>
    public static void UpdateConnectionAdmission(
        this ISharpLinkServer server,
        Action<SharpLinkConnectionAdmissionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(configure);
        if (server is not ISharpLinkConnectionAdmissionRuntimeControl runtimeControl)
        {
            throw new NotSupportedException(
                "This ISharpLinkServer implementation does not support runtime connection admission.");
        }

        runtimeControl.UpdateConnectionAdmission(configure);
    }
}

internal interface ISharpLinkConnectionAdmissionRuntimeControl
{
    void UpdateConnectionAdmission(Action<SharpLinkConnectionAdmissionOptions> configure);
}
