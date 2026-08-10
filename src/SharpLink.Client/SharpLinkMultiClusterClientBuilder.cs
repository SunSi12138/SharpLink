using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace SharpLink.Client;

/// <summary>Builds a coordinator that routes generated contracts to isolated child clients.</summary>
public sealed class SharpLinkMultiClusterClientBuilder
{
    private readonly SharpLinkMultiClusterOptions _options = new();
    private readonly Dictionary<SharpLinkClusterKey, ClusterConfiguration> _clusters = [];
    private ILoggerFactory? _loggerFactory;

    /// <summary>Creates a multi-cluster client builder.</summary>
    public static SharpLinkMultiClusterClientBuilder Create() => new();

    /// <summary>Configures global multi-cluster limits.</summary>
    public SharpLinkMultiClusterClientBuilder Configure(Action<SharpLinkMultiClusterOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_options);
        return this;
    }

    /// <summary>Adds a cluster slot that must have at least one static contract route.</summary>
    public SharpLinkMultiClusterClientBuilder AddCluster(
        SharpLinkClusterKey cluster,
        Action<SharpClientBuilder> configure)
        => AddCluster(cluster, configure, configureSlot: null);

    /// <summary>Adds a cluster slot and configures whether it can accept dynamic-only contracts.</summary>
    public SharpLinkMultiClusterClientBuilder AddCluster(
        SharpLinkClusterKey cluster,
        Action<SharpClientBuilder> configure,
        Action<SharpLinkMultiClusterSlotOptions>? configureSlot)
    {
        ValidateCluster(cluster);
        ArgumentNullException.ThrowIfNull(configure);
        if (_clusters.ContainsKey(cluster))
            throw new InvalidOperationException($"Cluster '{cluster}' has already been configured.");

        var slotOptions = new SharpLinkMultiClusterSlotOptions();
        configureSlot?.Invoke(slotOptions);
        var child = SharpClientBuilder.Create();
        configure(child);
        _clusters.Add(cluster, new ClusterConfiguration(cluster, child, slotOptions.AllowDynamicContracts));
        return this;
    }

    /// <summary>Builds the coordinator without opening network connections.</summary>
    public ISharpLinkMultiClusterClient Build()
    {
        var options = _options.CloneValidated();
        if (_clusters.Count == 0)
            throw new InvalidOperationException("At least one cluster slot must be configured.");
        if (_clusters.Count > options.MaxClusters)
            throw new InvalidOperationException($"Configured cluster count exceeds MaxClusters ({options.MaxClusters}).");

        var routeManifestSnapshot = SharpLinkGeneratedClusterRouteCatalog.CreateSnapshot();
        var configuredRoutes = routeManifestSnapshot
            .SelectMany(static manifest => manifest.Routes)
            .Where(route => _clusters.ContainsKey(route.Cluster))
            .ToArray();
        var routedAssemblies = new HashSet<Assembly>(ReferenceEqualityComparer.Instance);
        foreach (var route in configuredRoutes)
            routedAssemblies.Add(route.ContractAssembly);
        var manifestByAssembly = LoadRoutedManifestGraph(routedAssemblies);

        var manifestsByCluster = _clusters.Keys.ToDictionary(
            static key => key,
            static _ => new Dictionary<Assembly, ISharpLinkGeneratedAssemblyManifest>(ReferenceEqualityComparer.Instance));
        var assemblyOwners = new Dictionary<Assembly, SharpLinkClusterKey>(ReferenceEqualityComparer.Instance);
        foreach (var route in configuredRoutes)
        {
            if (!manifestByAssembly.TryGetValue(route.ContractAssembly, out var contractManifest))
            {
                throw new InvalidOperationException(
                    $"Static route '{route.ContractAssemblyIdentity}' does not reference a compatible generated contract manifest.");
            }
            if (contractManifest.Contracts.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Static route '{route.ContractAssemblyIdentity}' must reference an assembly that owns at least one generated contract.");
            }
            if (assemblyOwners.TryGetValue(route.ContractAssembly, out var existingCluster))
            {
                if (existingCluster != route.Cluster)
                {
                    throw new InvalidOperationException(
                        $"Contract assembly '{route.ContractAssemblyIdentity}' is routed to both '{existingCluster}' and '{route.Cluster}'.");
                }
                continue;
            }

            assemblyOwners.Add(route.ContractAssembly, route.Cluster);
            AddManifestClosure(contractManifest, route.Cluster, manifestsByCluster, manifestByAssembly);
        }

        foreach (var configuration in _clusters.Values)
        {
            if (manifestsByCluster[configuration.Key].Values.All(static manifest => manifest.Contracts.Count == 0) &&
                !configuration.AllowDynamicContracts)
            {
                throw new InvalidOperationException(
                    $"Cluster '{configuration.Key}' has no static contract route. Configure AllowDynamicContracts to create a dynamic-only slot.");
            }
        }

        var compiledPlans = new List<CompiledClusterPlan>(_clusters.Count);
        var configuredConnections = 0;
        foreach (var configuration in _clusters.Values)
        {
            var staticManifests = manifestsByCluster[configuration.Key].Values
                .OrderBy(static manifest => manifest.OwnerAssembly.FullName, StringComparer.Ordinal)
                .Select(manifest => IsRoutedToCluster(manifest, configuration.Key, assemblyOwners)
                    ? manifest
                    : new DependencyManifestView(manifest))
                .ToArray();
            try
            {
                var plan = configuration.Builder.CompileForMultiCluster(staticManifests);
                configuredConnections = checked(configuredConnections + plan.MaximumConnections);
                compiledPlans.Add(new CompiledClusterPlan(configuration, plan, staticManifests));
            }
            catch (Exception buildException)
            {
                RethrowAfterDiscardingCompiledPlans(buildException, compiledPlans);
            }
        }
        if (configuredConnections > options.MaxTotalConfiguredConnections)
        {
            var budgetFailure = new InvalidOperationException(
                $"Configured child connection budget ({configuredConnections}) exceeds MaxTotalConfiguredConnections ({options.MaxTotalConfiguredConnections}).");
            RethrowAfterDiscardingCompiledPlans(budgetFailure, compiledPlans);
        }

        var createdSlots = new List<SharpLinkClusterSlot>(_clusters.Count);
        using var transaction = new SynchronousBuildTransaction();
        try
        {
            foreach (var compiled in compiledPlans)
            {
                compiled.MaterializationStarted = true;
                var child = transaction.Own(
                    compiled.Configuration.Builder.MaterializeCompiledPlan(compiled.Plan),
                    static client => SharpLinkAsyncCleanup.DisposeSynchronously(client),
                    SynchronousBuildResourceMetadata.FrameworkOwned(
                        $"Multi-cluster child '{compiled.Configuration.Key}'"));
                createdSlots.Add(new SharpLinkClusterSlot(
                    compiled.Configuration.Key,
                    child,
                    compiled.Configuration.AllowDynamicContracts,
                    compiled.Plan.MaximumConnections,
                    compiled.StaticManifests));
            }

            var slots = createdSlots.ToFrozenDictionary(static slot => slot.Key);
            var routes = BuildStaticRoutes(slots, assemblyOwners, manifestByAssembly);
            var client = new SharpLinkMultiClusterClient(
                options,
                slots,
                routes,
                routeManifestSnapshot,
                configuredConnections,
                _loggerFactory);
            transaction.Commit();
            return client;
        }
        catch (Exception buildException)
        {
            RethrowAfterDiscardingCompiledPlans(buildException, compiledPlans, transaction);
            throw new UnreachableException();
        }
    }

    /// <summary>Applies a logger factory to child builders that do not already have one.</summary>
    public void UseLoggerFactoryIfUnset(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory ??= loggerFactory;
        foreach (var configuration in _clusters.Values)
            configuration.Builder.UseLoggerFactoryIfUnset(loggerFactory);
    }

    internal static SharpLinkPreparedCluster PrepareRuntimeCluster(
        SharpLinkClusterKey cluster,
        SharpClientBuilder builder,
        bool allowDynamicContracts)
    {
        ValidateCluster(cluster);
        ArgumentNullException.ThrowIfNull(builder);

        var configuredRoutes = SharpLinkGeneratedClusterRouteCatalog.CreateSnapshot()
            .SelectMany(static manifest => manifest.Routes)
            .Where(route => route.Cluster == cluster)
            .ToArray();
        var routedAssemblies = new HashSet<Assembly>(ReferenceEqualityComparer.Instance);
        foreach (var route in configuredRoutes)
            routedAssemblies.Add(route.ContractAssembly);
        var manifestsByAssembly = LoadRoutedManifestGraph(routedAssemblies);
        var manifestsByCluster = new Dictionary<SharpLinkClusterKey, Dictionary<Assembly, ISharpLinkGeneratedAssemblyManifest>>
        {
            [cluster] = new(ReferenceEqualityComparer.Instance)
        };
        var assemblyOwners = new Dictionary<Assembly, SharpLinkClusterKey>(ReferenceEqualityComparer.Instance);
        foreach (var route in configuredRoutes)
        {
            if (!manifestsByAssembly.TryGetValue(route.ContractAssembly, out var contractManifest))
            {
                throw new InvalidOperationException(
                    $"Static route '{route.ContractAssemblyIdentity}' does not reference a compatible generated contract manifest.");
            }
            if (contractManifest.Contracts.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Static route '{route.ContractAssemblyIdentity}' must reference an assembly that owns at least one generated contract.");
            }
            if (!assemblyOwners.TryAdd(route.ContractAssembly, cluster))
                continue;
            AddManifestClosure(contractManifest, cluster, manifestsByCluster, manifestsByAssembly);
        }

        if (manifestsByCluster[cluster].Values.All(static manifest => manifest.Contracts.Count == 0) &&
            !allowDynamicContracts)
        {
            throw new InvalidOperationException(
                $"Cluster '{cluster}' has no static contract route. Configure AllowDynamicContracts to create a dynamic-only slot.");
        }

        var staticManifests = manifestsByCluster[cluster].Values
            .OrderBy(static manifest => manifest.OwnerAssembly.FullName, StringComparer.Ordinal)
            .Select(manifest => IsRoutedToCluster(manifest, cluster, assemblyOwners)
                ? manifest
                : new DependencyManifestView(manifest))
            .ToArray();
        var plan = builder.CompileForMultiCluster(staticManifests);
        var connectionBudget = plan.MaximumConnections;
        using var transaction = new SynchronousBuildTransaction();
        try
        {
            var child = transaction.Own(
                builder.MaterializeCompiledPlan(plan),
                static client => SharpLinkAsyncCleanup.DisposeSynchronously(client),
                SynchronousBuildResourceMetadata.FrameworkOwned(
                    $"Runtime multi-cluster child '{cluster}'"));
            var slot = new SharpLinkClusterSlot(
                cluster,
                child,
                allowDynamicContracts,
                connectionBudget,
                staticManifests);
            var slots = new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot> { [cluster] = slot }
                .ToFrozenDictionary();
            var routes = BuildStaticRoutes(slots, assemblyOwners, manifestsByAssembly);
            var prepared = new SharpLinkPreparedCluster(slot, routes);
            transaction.Commit();
            return prepared;
        }
        catch (Exception buildException)
        {
            transaction.Rollback(buildException);
            throw new UnreachableException();
        }
    }

    internal static SharpLinkPreparedCluster PrepareReplacementCluster(
        SharpLinkClusterSlot existingSlot,
        SharpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(existingSlot);
        ArgumentNullException.ThrowIfNull(builder);
        var staticManifests = existingSlot.StaticManifests ?? [];
        var plan = builder.CompileForMultiCluster(staticManifests);
        var connectionBudget = plan.MaximumConnections;
        using var transaction = new SynchronousBuildTransaction();
        try
        {
            var child = transaction.Own(
                builder.MaterializeCompiledPlan(plan),
                static client => SharpLinkAsyncCleanup.DisposeSynchronously(client),
                SynchronousBuildResourceMetadata.FrameworkOwned(
                    $"Replacement multi-cluster child '{existingSlot.Key}'"));
            var slot = new SharpLinkClusterSlot(
                existingSlot.Key,
                child,
                existingSlot.AllowDynamicContracts,
                connectionBudget,
                staticManifests);
            var prepared = new SharpLinkPreparedCluster(
                slot,
                FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty);
            transaction.Commit();
            return prepared;
        }
        catch (Exception buildException)
        {
            transaction.Rollback(buildException);
            throw new UnreachableException();
        }
    }

    private static FrozenDictionary<Type, SharpLinkClusterRouteRegistration> BuildStaticRoutes(
        FrozenDictionary<SharpLinkClusterKey, SharpLinkClusterSlot> slots,
        IReadOnlyDictionary<Assembly, SharpLinkClusterKey> assemblyOwners,
        IReadOnlyDictionary<Assembly, ISharpLinkGeneratedAssemblyManifest> manifestsByAssembly)
    {
        var routes = new Dictionary<Type, SharpLinkClusterRouteRegistration>();
        var routesById = new Dictionary<long, SharpLinkClusterRouteRegistration>();
        foreach (var pair in assemblyOwners)
        {
            var slot = slots[pair.Value];
            var manifest = manifestsByAssembly[pair.Key];
            foreach (var contract in manifest.Contracts)
            {
                var registration = new SharpLinkClusterRouteRegistration(
                    contract.ContractType,
                    contract.ContractId,
                    contract.Fingerprint,
                    slot,
                    manifest.OwnerAssembly);
                if (!routes.TryAdd(contract.ContractType, registration) ||
                    !routesById.TryAdd(contract.ContractId, registration))
                {
                    throw new InvalidOperationException(
                        $"Contract '{contract.ContractName}' ({contract.ContractId}) is exposed by more than one multi-cluster slot.");
                }
            }
        }

        return routes.ToFrozenDictionary();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void RethrowAfterDiscardingCompiledPlans(
        Exception primaryException,
        IReadOnlyList<CompiledClusterPlan> compiledPlans,
        SynchronousBuildTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(primaryException);
        List<Exception>? cleanupFailures = null;
        for (var index = compiledPlans.Count - 1; index >= 0; index--)
        {
            var compiled = compiledPlans[index];
            if (compiled.MaterializationStarted)
                continue;

            try
            {
                compiled.Configuration.Builder.DiscardCompiledPlan(compiled.Plan);
            }
            catch (Exception cleanupException)
            {
                (cleanupFailures ??= []).Add(cleanupException);
            }
        }

        var failure = cleanupFailures is null
            ? primaryException
            : new AggregateException([primaryException, .. cleanupFailures]);
        if (transaction is not null)
        {
            transaction.Rollback(failure);
            throw new UnreachableException();
        }

        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        throw new UnreachableException();
    }

    private static Dictionary<Assembly, ISharpLinkGeneratedAssemblyManifest> LoadRoutedManifestGraph(
        IEnumerable<Assembly> routedAssemblies)
    {
        var manifestsByAssembly = new Dictionary<Assembly, ISharpLinkGeneratedAssemblyManifest>(ReferenceEqualityComparer.Instance);
        var pendingAssemblies = new Queue<Assembly>(routedAssemblies);
        while (pendingAssemblies.TryDequeue(out var assembly))
        {
            if (manifestsByAssembly.ContainsKey(assembly))
                continue;

            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
            if (!TryGetRegisteredManifest(assembly, out var manifest))
                continue;

            SharpLinkClient.ValidateStaticManifestCompatibility(manifest);
            manifestsByAssembly.Add(assembly, manifest);
            foreach (var dependencyIdentity in manifest.Dependencies)
            {
                var dependencyAssembly = ResolveDependencyAssembly(assembly, dependencyIdentity);
                if (dependencyAssembly is not null)
                    pendingAssemblies.Enqueue(dependencyAssembly);
            }
        }

        return manifestsByAssembly;
    }

    private static bool TryGetRegisteredManifest(
        Assembly assembly,
        out ISharpLinkGeneratedAssemblyManifest manifest)
    {
        foreach (var candidate in SharpLinkGeneratedAssemblyCatalog.CreateSnapshot())
        {
            if (ReferenceEquals(candidate.OwnerAssembly, assembly))
            {
                manifest = candidate;
                return true;
            }
        }

        manifest = null!;
        return false;
    }

    private static Assembly? ResolveDependencyAssembly(Assembly ownerAssembly, string dependencyIdentity)
    {
        var loadContext = AssemblyLoadContext.GetLoadContext(ownerAssembly) ?? AssemblyLoadContext.Default;
        var loaded = loadContext.Assemblies.FirstOrDefault(assembly =>
            string.Equals(assembly.FullName, dependencyIdentity, StringComparison.Ordinal));
        if (loaded is not null)
            return loaded;

        try
        {
            return loadContext.LoadFromAssemblyName(new AssemblyName(dependencyIdentity));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    private static void AddManifestClosure(
        ISharpLinkGeneratedAssemblyManifest manifest,
        SharpLinkClusterKey cluster,
        IReadOnlyDictionary<SharpLinkClusterKey, Dictionary<Assembly, ISharpLinkGeneratedAssemblyManifest>> manifestsByCluster,
        IReadOnlyDictionary<Assembly, ISharpLinkGeneratedAssemblyManifest> manifestsByAssembly)
    {
        var destination = manifestsByCluster[cluster];
        if (!destination.TryAdd(manifest.OwnerAssembly, manifest))
            return;

        foreach (var dependencyIdentity in manifest.Dependencies)
        {
            var dependencyAssembly = ResolveDependencyAssembly(manifest.OwnerAssembly, dependencyIdentity);
            if (dependencyAssembly is null || !manifestsByAssembly.TryGetValue(dependencyAssembly, out var dependency))
            {
                throw new InvalidOperationException(
                    $"Static route for '{manifest.OwnerAssembly.FullName}' is missing generated dependency '{dependencyIdentity}' in cluster '{cluster}'.");
            }
            AddManifestClosure(dependency, cluster, manifestsByCluster, manifestsByAssembly);
        }
    }

    private static void ValidateCluster(SharpLinkClusterKey cluster)
    {
        if (!SharpLinkClusterKey.IsValid(cluster.Value))
            throw new ArgumentException("A valid non-default SharpLinkClusterKey is required.", nameof(cluster));
    }

    private static bool IsRoutedToCluster(
        ISharpLinkGeneratedAssemblyManifest manifest,
        SharpLinkClusterKey cluster,
        IReadOnlyDictionary<Assembly, SharpLinkClusterKey> assemblyOwners)
        => assemblyOwners.TryGetValue(manifest.OwnerAssembly, out var owner) && owner == cluster;

    // A dependency can contribute codecs to multiple slots, but proxy descriptors become
    // visible only when its contract-owning assembly is explicitly routed to that slot.
    private sealed class DependencyManifestView(ISharpLinkGeneratedAssemblyManifest source)
        : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => source.ApiVersion;
        public int ProtocolVersion => source.ProtocolVersion;
        public string GeneratorVersion => source.GeneratorVersion;
        public Assembly OwnerAssembly => source.OwnerAssembly;
        public string CompileTimeDescriptor => source.CompileTimeDescriptor;
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => source.Codecs;
        public IReadOnlyList<string> Dependencies => source.Dependencies;
    }

    private sealed record ClusterConfiguration(
        SharpLinkClusterKey Key,
        SharpClientBuilder Builder,
        bool AllowDynamicContracts);

    private sealed class CompiledClusterPlan(
        ClusterConfiguration configuration,
        ClientBuildPlan plan,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> staticManifests)
    {
        internal ClusterConfiguration Configuration { get; } = configuration;
        internal ClientBuildPlan Plan { get; } = plan;
        internal IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> StaticManifests { get; } = staticManifests;
        internal bool MaterializationStarted { get; set; }
    }
}

internal sealed record SharpLinkPreparedCluster(
    SharpLinkClusterSlot Slot,
    FrozenDictionary<Type, SharpLinkClusterRouteRegistration> StaticRoutes);
