namespace SharpLink.Client;

// Cold factory transactions own local protection, route publication validation, and demand rollback.
internal sealed partial class SharpLinkClient : ISharpLinkContractFactoryTransaction
{
    public RpcLocalContract GetLocalContract<TContract>() where TContract : IService
        => _assemblyRegistry.GetLocalContract(typeof(TContract));

    public RpcLocalContract GetLocalContract(System.Reflection.Assembly registeredAssembly, long contractId)
        => _assemblyRegistry.GetLocalContract(registeredAssembly, contractId);

    public RpcProxy<TContract> CreateProxy<TContract>(RpcProxyOptions? options = null) where TContract : IService
        => CreateProxyCore<TContract>(options, null);

    RpcProxy<TContract> ISharpLinkContractFactoryTransaction.CreateProxyTransactional<TContract>(
        RpcProxyOptions? options, Action validatePublication)
        => CreateProxyCore<TContract>(options, validatePublication);

    private RpcProxy<TContract> CreateProxyCore<TContract>(RpcProxyOptions? options, Action? validatePublication)
        where TContract : IService
    {
        options ??= new RpcProxyOptions();
        options.Validate();
        var local = GetLocalContract<TContract>();
        options.ValidateFor(local);
        var registration = _assemblyRegistry.AcquireLocalContract(local, out var moduleLease);
        using (moduleLease)
        {
            var construction = AcquireContractSite(registration, options);
            var published = false;
            try
            {
                var channel = CreateLocalChannel(registration, construction.Site, options.Metadata);
                // Generated/user factories and codecs never run under the registry publication lock.
                var service = (TContract)registration.Descriptor.ProxyFactory(channel, registration.Codecs);
                var proxy = new RpcProxy<TContract>(service, channel);
                _assemblyRegistry.ValidateLocalFactoryPublication(registration);
                validatePublication?.Invoke();
                published = true;
                return proxy;
            }
            finally
            {
                CompleteContractSiteConstruction(construction, published);
            }
        }
    }

    public IRpcContractChannel CreateContractChannel(RpcLocalContract local, RpcProxyOptions? options = null)
        => CreateContractChannelCore(local, options, null);

    IRpcContractChannel ISharpLinkContractFactoryTransaction.CreateContractChannelTransactional(
        RpcLocalContract local, RpcProxyOptions? options, Action validatePublication)
        => CreateContractChannelCore(local, options, validatePublication);

    private IRpcContractChannel CreateContractChannelCore(
        RpcLocalContract local, RpcProxyOptions? options, Action? validatePublication)
    {
        ArgumentNullException.ThrowIfNull(local);
        options ??= new RpcProxyOptions();
        options.ValidateFor(local);
        var registration = _assemblyRegistry.AcquireLocalContract(local, out var moduleLease);
        using (moduleLease)
        {
            var construction = AcquireContractSite(registration, options);
            var published = false;
            try
            {
                var channel = CreateLocalChannel(registration, construction.Site, options.Metadata);
                _assemblyRegistry.ValidateLocalFactoryPublication(registration);
                validatePublication?.Invoke();
                published = true;
                return channel;
            }
            finally
            {
                CompleteContractSiteConstruction(construction, published);
            }
        }
    }

}
