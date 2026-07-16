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
    private static readonly ConcurrentDictionary<Type, Func<IRpcStub>> ContractFactories = new();

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

    /// <summary>Registers the generated dispatcher shared by implementations of one contract.</summary>
    /// <param name="contractType">The generated RPC contract interface.</param>
    /// <param name="factory">Creates a stateless dispatcher for the contract.</param>
    public static void RegisterContract(Type contractType, Func<IRpcStub> factory)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        ArgumentNullException.ThrowIfNull(factory);
        if (ContractFactories.TryAdd(contractType, factory))
            return;
        if (!ContractFactories.TryGetValue(contractType, out var existing))
            throw new InvalidOperationException($"Contract stub registration failed for '{contractType.FullName}'.");

        var existingStub = existing();
        var newStub = factory();
        if (existingStub.InterfaceHash != newStub.InterfaceHash)
        {
            throw new InvalidOperationException(
                $"A conflicting generated stub is already registered for '{contractType.FullName}'.");
        }
    }

    /// <summary>Creates the generated dispatcher registered for a contract interface.</summary>
    /// <param name="contractType">The generated RPC contract interface.</param>
    /// <param name="stub">Receives a new stateless dispatcher when registered.</param>
    /// <returns><see langword="true"/> when the contract has a generated dispatcher.</returns>
    public static bool TryCreateContract(Type contractType, out IRpcStub? stub)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        if (ContractFactories.TryGetValue(contractType, out var factory))
        {
            stub = factory();
            return true;
        }

        stub = null;
        return false;
    }
}
