namespace SharpLink.Abstractions;

/// <summary>Creates one source-generated Codec for an immutable runtime-context snapshot.</summary>
public interface IRpcGeneratedCodecFactory
{
    /// <summary>Gets the closed DTO or collection type handled by the factory.</summary>
    Type TargetType { get; }

    /// <summary>Gets the deterministic schema identifier used for idempotent registration.</summary>
    string SchemaId { get; }

    /// <summary>Creates a Codec whose dependencies are resolved from the target Context.</summary>
    /// <param name="provider">The target Context Codec provider.</param>
    IRpcCodec Create(IRpcCodecProvider provider);
}

/// <summary>
/// Stores append-only source-generated Codec metadata. Registrations with the same
/// target type and schema are idempotent; conflicting schemas fail immediately.
/// </summary>
public static class RpcGeneratedCodecRegistry
{
    private static readonly ConcurrentDictionary<Type, IRpcGeneratedCodecFactory> Factories = new();

    /// <summary>Adds one generated Codec factory to the process metadata registry.</summary>
    /// <param name="factory">The immutable generated factory.</param>
    public static void Register(IRpcGeneratedCodecFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(factory.TargetType);
        ArgumentException.ThrowIfNullOrWhiteSpace(factory.SchemaId);

        while (true)
        {
            if (Factories.TryGetValue(factory.TargetType, out var existing))
            {
                if (string.Equals(existing.SchemaId, factory.SchemaId, StringComparison.Ordinal))
                    return;
                throw new InvalidOperationException(
                    $"Generated Codec schema conflict for '{factory.TargetType.FullName}': " +
                    $"'{existing.SchemaId}' and '{factory.SchemaId}'.");
            }

            if (Factories.TryAdd(factory.TargetType, factory))
                return;
        }
    }

    /// <summary>Creates an immutable-by-convention snapshot for a new runtime Context.</summary>
    public static IReadOnlyDictionary<Type, IRpcGeneratedCodecFactory> CreateSnapshot()
        => new Dictionary<Type, IRpcGeneratedCodecFactory>(Factories);
}
