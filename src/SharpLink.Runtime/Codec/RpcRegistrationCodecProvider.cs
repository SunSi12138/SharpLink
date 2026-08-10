namespace SharpLink.Runtime;

/// <summary>
/// Resolves codecs from one unpublished manifest candidate before falling back to the live Runtime.
/// </summary>
internal sealed class RpcRegistrationCodecProvider(
    IRpcCodecProvider fallback,
    IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> candidateRegistrations) :
    IRpcCodecProvider
{
    private readonly ConcurrentDictionary<Type, IRpcCodec> _resolved = new();

    public IRpcCodec<T> GetCodec<T>()
    {
        if (!candidateRegistrations.TryGetValue(typeof(T), out var registration))
            return fallback.GetCodec<T>();

        var codec = _resolved.GetOrAdd(
            typeof(T),
            static (_, state) => state.Registration.GetCodec(state.Provider),
            (Registration: registration, Provider: (IRpcCodecProvider)this));
        return codec as IRpcCodec<T> ?? throw new InvalidOperationException(
            $"The candidate Codec for '{typeof(T).FullName}' implements an incompatible Codec interface.");
    }
}
