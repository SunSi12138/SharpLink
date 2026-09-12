namespace SharpLink.Client;

internal sealed partial class SharpLinkMultiClusterClient
{
    private readonly ClientRequestTimeoutPolicy _requestTimeoutPolicy;

    internal SharpLinkMultiClusterClient(
        SharpLinkMultiClusterOptions options,
        FrozenDictionary<SharpLinkClusterKey, SharpLinkClusterSlot> clusters,
        FrozenDictionary<Type, SharpLinkClusterRouteRegistration> routes,
        IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest> routeManifestSnapshot,
        int configuredConnectionBudget,
        ILoggerFactory? loggerFactory,
        ClientRequestTimeoutPolicy requestTimeoutPolicy)
        : this(
            options,
            clusters,
            routes,
            routeManifestSnapshot,
            configuredConnectionBudget,
            loggerFactory)
        => _requestTimeoutPolicy = requestTimeoutPolicy;

    void ISharpLinkMultiClusterLifecycleControl.ConfigureChildBuilder(SharpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ApplyRequestTimeoutPolicyIfUnspecified(_requestTimeoutPolicy);
    }
}
