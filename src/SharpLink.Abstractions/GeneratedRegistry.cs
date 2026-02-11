namespace SharpLink.Abstractions;

public static class GeneratedProxyRegistry
{
    private static readonly ConcurrentDictionary<Type, Func<IRpcChannel, ISerializer, object>> Factories = new();

    public static void Register(Type serviceInterfaceType, Func<IRpcChannel, ISerializer, object> factory)
    {
        ArgumentNullException.ThrowIfNull(serviceInterfaceType);
        ArgumentNullException.ThrowIfNull(factory);
        Factories[serviceInterfaceType] = factory;
    }

    public static bool TryCreate(Type serviceInterfaceType, IRpcChannel channel, ISerializer serializer, out object? proxy)
    {
        if (Factories.TryGetValue(serviceInterfaceType, out var factory))
        {
            proxy = factory(channel, serializer);
            return true;
        }

        proxy = null;
        return false;
    }
}

public static class GeneratedStubRegistry
{
    private static readonly ConcurrentDictionary<Type, Func<IRpcStub>> Factories = new();

    public static void Register(Type serviceType, Func<IRpcStub> factory)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentNullException.ThrowIfNull(factory);
        Factories[serviceType] = factory;
    }

    public static bool TryCreate(Type serviceType, out IRpcStub? stub)
    {
        if (Factories.TryGetValue(serviceType, out var factory))
        {
            stub = factory();
            return true;
        }

        stub = null;
        return false;
    }
}
