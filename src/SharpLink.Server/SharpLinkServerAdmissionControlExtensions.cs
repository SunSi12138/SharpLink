namespace SharpLink.Server;

/// <summary>Runtime admission-control operations for SharpLink servers.</summary>
public static class SharpLinkServerAdmissionControlExtensions
{
    /// <summary>
    /// Atomically enables admission control for requests that capture admission after this call returns.
    /// </summary>
    /// <param name="server">The server whose admission policy is enabled.</param>
    /// <param name="configure">Builds the complete admission policy before publication.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> or <paramref name="configure"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Admission is already enabled, or the server is stopping.</exception>
    /// <exception cref="NotSupportedException">The server implementation does not support runtime admission control.</exception>
    public static void EnableAdmissionControl(
        this ISharpLinkServer server,
        Action<SharpLinkAdmissionControlOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(configure);
        if (server is not ISharpLinkAdmissionRuntimeControl runtimeControl)
        {
            throw new NotSupportedException(
                "This ISharpLinkServer implementation does not support runtime admission control.");
        }

        runtimeControl.EnableAdmissionControl(configure);
    }

    /// <summary>
    /// Atomically replaces the complete enabled admission configuration for requests that capture
    /// admission after this call returns. This slice supports Global/Contract/Method concurrency
    /// add/remove/resize and queue-policy updates while preserving unchanged rate and partition state.
    /// </summary>
    /// <param name="server">The server whose enabled admission policy is updated.</param>
    /// <param name="configure">Builds the complete desired admission policy before publication.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> or <paramref name="configure"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Admission is disabled, the source generation changed during candidate construction, an
    /// unsupported rate/partition transition was requested, or the server is stopping.
    /// </exception>
    /// <exception cref="NotSupportedException">The server implementation does not support runtime admission control.</exception>
    public static void UpdateAdmissionControl(
        this ISharpLinkServer server,
        Action<SharpLinkAdmissionControlOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(configure);
        if (server is not ISharpLinkAdmissionRuntimeControl runtimeControl)
        {
            throw new NotSupportedException(
                "This ISharpLinkServer implementation does not support runtime admission control.");
        }

        runtimeControl.UpdateAdmissionControl(configure);
    }

    /// <summary>
    /// Atomically disables admission control for requests that capture admission after this call returns.
    /// Requests that already captured an enabled generation retain it until terminal completion.
    /// </summary>
    /// <param name="server">The server whose admission policy is disabled.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The server is stopping.</exception>
    /// <exception cref="NotSupportedException">The server implementation does not support runtime admission control.</exception>
    public static void DisableAdmissionControl(this ISharpLinkServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server is not ISharpLinkAdmissionRuntimeControl runtimeControl)
        {
            throw new NotSupportedException(
                "This ISharpLinkServer implementation does not support runtime admission control.");
        }

        runtimeControl.DisableAdmissionControl();
    }
}

internal interface ISharpLinkAdmissionRuntimeControl
{
    void EnableAdmissionControl(Action<SharpLinkAdmissionControlOptions> configure);

    void UpdateAdmissionControl(Action<SharpLinkAdmissionControlOptions> configure);

    void DisableAdmissionControl();
}
