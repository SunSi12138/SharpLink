using System.Reflection;

namespace SharpLink.Server;

/// <summary>Controls coordinated version changes on a SharpLink server.</summary>
public static class SharpLinkServerVersionCoordinationExtensions
{
    /// <summary>Sets policy for next attempts and handshakes. The initial mode is Disabled.</summary>
    /// <remarks>Changing mode does not add a capability to an existing session or alter an admitted attempt.</remarks>
    public static void SetVersionCoordinationMode(this ISharpLinkServer server, SharpLinkVersionCoordinationMode mode)
        => GetBuiltInServer(server).NStudyVersionCoordinationMode = NStudyVersionProjection.ToMode(mode);

    /// <summary>Registers one endpoint-lifetime, best-effort observer outside RPC and protocol processing.</summary>
    /// <remarks>Notifications can be dropped; read inventory to reconcile. A callback must honor cancellation.</remarks>
    public static void ObserveVersionChanges(this ISharpLinkServer server,
        Func<SharpLinkVersionChangeNotice, CancellationToken, ValueTask> observer)
        => GetBuiltInServer(server).ConfigureNStudyVersionObserver(NStudyVersionProjection.ToObserver(observer));

    /// <summary>Stages and negotiates replacement of an installed assembly group with an application-provided assembly.</summary>
    /// <remarks>
    /// Include every coordination-capable peer with an open binding to any service in the old assembly,
    /// even when that peer has no binding for <paramref name="contractId"/>. Omitting one prevents publication.
    /// The first participant authorizes the server publication; other participants govern only their own resume.
    /// In Manual mode each session must support deferral, and the callback decides each participant separately.
    /// Defer ends that peer's attempt without approval. The primary can defer publication; other participants
    /// cannot veto publication approved by the primary. Retry with a fresh attempt, not a saved vote.
    /// Automatic mode still requires explicit participation by each client.
    /// An activation sent by the server is not proof of client resume.
    /// Cancellation after publication does not roll it back; query local inventory to reconcile.
    /// </remarks>
    public static ValueTask<SharpLinkServerVersionChangeResult> ReplaceAssemblyCoordinatedAsync(
        this ISharpLinkServer server, Assembly oldAssembly, Assembly newAssembly, long contractId,
        IReadOnlyList<SharpLinkVersionParticipant> participants, TimeSpan gracefulTimeout,
        Func<SharpLinkVersionChange, CancellationToken, ValueTask<SharpLinkVersionDecision>>? approveLocally = null,
        CancellationToken cancellationToken = default)
        => GetBuiltInServer(server).ReplaceAssemblyCoordinatedAsync(oldAssembly, newAssembly, contractId,
            participants, gracefulTimeout, approveLocally, cancellationToken);

    private static SharpLinkServer GetBuiltInServer(ISharpLinkServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server as SharpLinkServer
            ?? throw new NotSupportedException("This server implementation does not provide version coordination.");
    }
}
