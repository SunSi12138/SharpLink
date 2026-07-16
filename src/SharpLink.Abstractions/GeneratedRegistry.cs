namespace SharpLink.Abstractions;

public static class GeneratedProxyRegistry
{
    private static readonly ConcurrentDictionary<Type, Func<IRpcChannel, object>> Factories = new();

    public static void Register(Type serviceInterfaceType, Func<IRpcChannel, object> factory)
    {
        ArgumentNullException.ThrowIfNull(serviceInterfaceType);
        ArgumentNullException.ThrowIfNull(factory);
        if (Factories.TryAdd(serviceInterfaceType, factory))
            return;
        if (Factories.TryGetValue(serviceInterfaceType, out var existing) && existing == factory)
            return;
        throw new InvalidOperationException(
            $"A different generated proxy factory is already registered for '{serviceInterfaceType.FullName}'.");
    }

    public static bool TryCreate(Type serviceInterfaceType, IRpcChannel channel, out object? proxy)
    {
        if (Factories.TryGetValue(serviceInterfaceType, out var factory))
        {
            proxy = factory(channel);
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
        if (Factories.TryAdd(serviceType, factory))
            return;
        if (Factories.TryGetValue(serviceType, out var existing) && existing == factory)
            return;
        throw new InvalidOperationException(
            $"A different generated stub factory is already registered for '{serviceType.FullName}'.");
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
