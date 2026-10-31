namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private const int MaximumLocalContractSites = 4_096;
    private readonly Lock _contractSiteGate = new();
    private readonly Dictionary<LocalContractSiteKey, LocalContractSiteEntry> _contractSites = [];

    private ContractSiteConstruction AcquireContractSite(
        ClientProxyRegistration registration,
        RpcProxyOptions options)
    {
        var local = registration.LocalControl.Local;
        var key = new LocalContractSiteKey(local.IssuerId, local.RegistrationGeneration,
            local.ContractId, local.RpcAssemblyHash, options.Requirement, options.Receipt, options.TargetScope);
        lock (_contractSiteGate)
        {
            registration.LocalControl.ThrowIfRetired();
            if (_contractSites.TryGetValue(key, out var existing) && existing.Site.TryGetTarget(out var site))
            {
                existing.Constructions++;
                return new ContractSiteConstruction(key, existing, site);
            }

            // Cold, bounded sweep. The cache never roots a site or a collectible registration.
            foreach (var pair in _contractSites.ToArray())
            {
                if (!pair.Value.Site.TryGetTarget(out var cached) || cached.LocalControl.IsRetired)
                    _contractSites.Remove(pair.Key);
            }
            if (_contractSites.Count >= MaximumLocalContractSites)
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted,
                    SharpLinkErrorDetails.ResourceExhausted.BindingDemandSites,
                    "The bounded local contract site capacity was exhausted.");

            // Metadata belongs to each returned wrapper, not the shared route/site key.
            var routeOptions = new RpcProxyOptions
            {
                Requirement = options.Requirement,
                Receipt = options.Receipt,
                TargetScope = options.TargetScope
            };
            site = new ContractCallSite(this, local, routeOptions, registration.LocalControl);
            var entry = new LocalContractSiteEntry(site);
            try
            {
                RegisterContractDemand(site);
                _contractSites.Add(key, entry);
            }
            catch
            {
                UnregisterContractDemand(site);
                throw;
            }
            return new ContractSiteConstruction(key, entry, site);
        }
    }

    private void CompleteContractSiteConstruction(ContractSiteConstruction construction, bool published)
    {
        var release = false;
        lock (_contractSiteGate)
        {
            var entry = construction.Entry;
            entry.Published |= published;
            entry.Constructions--;
            if (!entry.Published && entry.Constructions == 0)
            {
                if (_contractSites.TryGetValue(construction.Key, out var current) && ReferenceEquals(current, entry))
                    _contractSites.Remove(construction.Key);
                release = true;
            }
        }
        // A failed factory never cancels the demand belonging to another successful/factory-held site.
        if (release)
            UnregisterContractDemand(construction.Site);
    }

    private static IRpcContractChannel CreateLocalChannel(
        ClientProxyRegistration registration, ContractCallSite site, SharpLinkMetadata? metadata)
    {
        IRpcContractChannel channel = registration.Module is null
            ? site
            : new SharpLinkModuleRpcChannel(site, registration.Module);
        if (metadata is { Count: > 0 })
            channel = new SharpLinkMetadataRpcChannel(channel, metadata);
        return channel;
    }

    private readonly record struct LocalContractSiteKey(
        Guid IssuerId,
        long RegistrationGeneration,
        long ContractId,
        RpcHash128 RpcAssemblyHash,
        RpcBindingRequirement Requirement,
        RpcRegistrationReceipt? Receipt,
        SharpLinkClusterKey? Scope);

    private sealed class LocalContractSiteEntry(ContractCallSite site)
    {
        internal readonly WeakReference<ContractCallSite> Site = new(site);
        internal int Constructions = 1;
        internal bool Published;
    }

    private readonly record struct ContractSiteConstruction(
        LocalContractSiteKey Key,
        LocalContractSiteEntry Entry,
        ContractCallSite Site);

    internal sealed partial class ContractCallSite : IRpcContractChannel
    {
        private readonly SharpLinkClient _owner;

        internal ContractCallSite(
            SharpLinkClient owner,
            RpcLocalContract local,
            RpcProxyOptions options,
            ClientLocalContractControl localControl)
        {
            _owner = owner;
            Local = local;
            Options = options;
            LocalControl = localControl;
        }

        internal RpcLocalContract Local { get; }
        internal RpcProxyOptions Options { get; }
        internal ClientLocalContractControl LocalControl { get; }
        internal long ContractId => Local.ContractId;
        internal bool IsDemandRetiredForDiagnostics
        {
            get { lock (_bindingGate) return _demandRetired; }
        }
        public IRpcRuntimeContext RuntimeContext => _owner.RuntimeContext;

        public ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
            => _owner.PrepareContractAsync(this, timeout, cancellationToken);

        internal ClientInterceptorGeneration CaptureInterceptorGenerationForInvocation()
            => _owner.CaptureInterceptorGenerationForInvocation();

        internal ResolvedCallControl ResolveCallControlForInvocation(
            RpcMethodDescriptor method,
            SharpLinkMetadata? metadata,
            bool includeClientDefault,
            ClientInterceptorGeneration interceptors)
            => _owner.ResolveCallControlForInvocation(method, metadata, includeClientDefault, interceptors);
    }
}
