namespace SharpLink.Client;

internal sealed partial class SharpLinkMultiClusterClient
{
    private readonly Action<SharpClientBuilder>? _configureChildTimeoutPolicy;

    internal SharpLinkMultiClusterClient(
        SharpLinkMultiClusterOptions options,
        FrozenDictionary<SharpLinkClusterKey, SharpLinkClusterSlot> clusters,
        FrozenDictionary<Type, SharpLinkClusterRouteRegistration> routes,
        IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest> routeManifestSnapshot,
        int configuredConnectionBudget,
        ILoggerFactory? loggerFactory,
        Action<SharpClientBuilder>? configureChildTimeoutPolicy)
        : this(
            options,
            clusters,
            routes,
            routeManifestSnapshot,
            configuredConnectionBudget,
            loggerFactory)
        => _configureChildTimeoutPolicy = configureChildTimeoutPolicy;

    void ISharpLinkMultiClusterLifecycleControl.ConfigureChildBuilder(SharpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        _configureChildTimeoutPolicy?.Invoke(builder);
    }
}
