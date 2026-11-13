#if SHARPLINK_775_STATIC_SLICE
using System.Reflection;

namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private readonly Guid _sliceIssuer = Guid.NewGuid();
    private readonly Lock _staticBindingGate = new();
    private readonly Dictionary<long, WeakReference<StaticBindingCallSite>> _staticBindingSites = [];

    internal void EnsureStaticSliceAvailable()
    {
        if (Volatile.Read(ref _stopStarted) != 0 || _shutdownCts.IsCancellationRequested)
            throw CreateConnectionClosedException("Client is stopping.");
    }

    public RpcLocalContract GetLocalContract<T>() where T : IService
    {
        EnsureStaticSliceAvailable();
        if (!Volatile.Read(ref _proxies).TryGetValue(typeof(T), out var registration))
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Local contract is not registered.");
        return CaptureStaticLocalContract(registration);
    }

    public RpcLocalContract GetLocalContract(Assembly registeredAssembly, long contractId)
    {
        ArgumentNullException.ThrowIfNull(registeredAssembly);
        EnsureStaticSliceAvailable();
        foreach (var registration in _assemblyRegistry.CaptureProxySnapshot().Values)
        {
            if (registration.Descriptor.ContractId == contractId &&
                ReferenceEquals(FindOwningManifest(registration).OwnerAssembly, registeredAssembly))
                return CaptureStaticLocalContract(registration);
        }
        throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Exact local assembly/contract is not registered.");
    }

    private RpcLocalContract CaptureStaticLocalContract(ClientProxyRegistration registration)
    {
        if (registration.Module is not null)
            throw StaticBindingWire.Error(StaticBindingFailure.DynamicUnsupported);
        var hash = FindOwningManifest(registration).RpcAssemblyHash;
        if (hash.IsEmpty)
            throw StaticBindingWire.Error(StaticBindingFailure.HashMismatch);
        return new RpcLocalContract(_sliceIssuer, registration.SliceGeneration, registration.Descriptor.ContractId, hash);
    }

    private ClientProxyRegistration ResolveStaticLocalContract(RpcLocalContract local)
    {
        ArgumentNullException.ThrowIfNull(local);
        EnsureStaticSliceAvailable();
        if (local.Issuer != _sliceIssuer)
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Foreign local contract selector.");
        foreach (var registration in _assemblyRegistry.CaptureProxySnapshot().Values)
        {
            if (registration.SliceGeneration == local.Generation && registration.Descriptor.ContractId == local.ContractId)
            {
                var exact = CaptureStaticLocalContract(registration);
                if (exact.Hash == local.Hash) return registration;
                break;
            }
        }
        throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Exact local contract is unavailable.");
    }

    private StaticBindingCallSite AcquireStaticSite(RpcLocalContract local)
    {
        if (_cluster is not null)
            throw new NotSupportedException("The nonshipping static slice supports a fixed endpoint only.");
        lock (_staticBindingGate)
        {
            EnsureStaticSliceAvailable();
            if (!_staticBindingSites.TryGetValue(local.Generation, out var reference) || !reference.TryGetTarget(out var site))
            {
                foreach (var pair in _staticBindingSites.ToArray())
                    if (!pair.Value.TryGetTarget(out _)) _staticBindingSites.Remove(pair.Key);
                if (_staticBindingSites.Count == StaticBindingWire.Capacity)
                    throw StaticBindingWire.Error(StaticBindingFailure.Capacity);
                site = new StaticBindingCallSite(this, local);
                _staticBindingSites[local.Generation] = new WeakReference<StaticBindingCallSite>(site);
            }
            site.Constructions++;
            return site;
        }
    }

    private void CompleteStaticSiteConstruction(StaticBindingCallSite site, bool published)
    {
        lock (_staticBindingGate)
        {
            site.DemandCommitted |= published;
            site.Constructions--;
            if (!site.DemandCommitted && site.Constructions == 0 &&
                _staticBindingSites.TryGetValue(site.Local.Generation, out var reference) &&
                reference.TryGetTarget(out var current) && ReferenceEquals(current, site))
                _staticBindingSites.Remove(site.Local.Generation);
        }
        if (published) ScheduleStaticBindingRefresh(site);
    }

    private static IRpcContractChannel WrapStaticChannel(StaticBindingCallSite site, RpcProxyOptions? options)
        => options?.Metadata is { Count: > 0 } metadata ? new SharpLinkMetadataRpcChannel(site, metadata) : site;

    public IRpcContractChannel CreateContractChannel(RpcLocalContract local, RpcProxyOptions? options = null)
    {
        _ = ResolveStaticLocalContract(local);
        var site = AcquireStaticSite(local);
        var published = false;
        try
        {
            var channel = WrapStaticChannel(site, options);
            published = true;
            return channel;
        }
        finally { CompleteStaticSiteConstruction(site, published); }
    }

    public RpcProxy<T> CreateProxy<T>(RpcProxyOptions? options = null) where T : IService
    {
        var local = GetLocalContract<T>();
        var registration = ResolveStaticLocalContract(local);
        var site = AcquireStaticSite(local);
        var published = false;
        try
        {
            var channel = WrapStaticChannel(site, options);
            var service = (T)registration.Descriptor.ProxyFactory(channel, registration.Codecs);
            var result = new RpcProxy<T>(service, channel);
            published = true;
            return result;
        }
        finally { CompleteStaticSiteConstruction(site, published); }
    }
}
#endif
