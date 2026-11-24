namespace SharpLink.Client;

/// <summary>Controls coordinated version changes on a SharpLink client.</summary>
public static class SharpLinkClientVersionCoordinationExtensions
{
    /// <summary>Sets policy for next attempts and handshakes. The initial mode is Disabled.</summary>
    /// <remarks>Changing mode does not add a capability to an existing session or alter an admitted attempt.</remarks>
    public static void SetVersionCoordinationMode(this ISharpLinkClient client, SharpLinkVersionCoordinationMode mode)
        => GetBuiltInClient(client).NStudyVersionCoordinationMode = NStudyVersionProjection.ToMode(mode);

    /// <summary>Registers one endpoint-lifetime, best-effort observer outside RPC and protocol processing.</summary>
    /// <remarks>Notifications can be dropped; read inventory to reconcile. A callback must honor cancellation.</remarks>
    public static void ObserveVersionChanges(this ISharpLinkClient client,
        Func<SharpLinkVersionChangeNotice, CancellationToken, ValueTask> observer)
        => GetBuiltInClient(client).ConfigureNStudyVersionObserver(NStudyVersionProjection.ToObserver(observer));

    /// <summary>Declares an installed dynamic contract on the current Ready connection, without changing either registration.</summary>
    public static ValueTask<SharpLinkVersionDeclarationResult> DeclareVersionTargetAsync(
        this ISharpLinkClient client, RpcLocalContract localContract, CancellationToken cancellationToken = default)
        => GetBuiltInClient(client).DeclareVersionTargetAsync(localContract, cancellationToken);

    /// <summary>Joins one server-staged change for this exact local contract and physical session.</summary>
    /// <remarks>
    /// Requires the single Ready session, a prepared source route, and counterpart server participation.
    /// In Manual mode supply a decision callback; both peers must support deferral.
    /// Defer settles this attempt without approval; retry explicitly for a fresh decision.
    /// Automatic mode still requires this call.
    /// Only a result with Resumed set confirms the client has prepared the new route.
    /// </remarks>
    public static ValueTask<SharpLinkClientVersionChangeResult> JoinVersionChangeAsync(
        this ISharpLinkClient client, RpcLocalContract localContract, SharpLinkVersionParticipant participant,
        Func<SharpLinkVersionChange, CancellationToken, ValueTask<SharpLinkVersionDecision>>? approveLocally = null,
        CancellationToken cancellationToken = default)
        => GetBuiltInClient(client).JoinVersionChangeAsync(localContract, participant, approveLocally, cancellationToken);

    private static SharpLinkClient GetBuiltInClient(ISharpLinkClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client as SharpLinkClient
            ?? throw new NotSupportedException("This client implementation does not provide version coordination.");
    }
}
