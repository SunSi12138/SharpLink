namespace SharpLink.Abstractions;

/// <summary>
/// Carries a construction-time-bound Codec alongside a generated client stream without
/// changing the public stream element type.
/// </summary>
/// <typeparam name="T">The stream item type.</typeparam>
public sealed class RpcCodecBoundAsyncEnumerable<T> : IAsyncEnumerable<T>
{
    private readonly IAsyncEnumerable<T> _source;

    /// <summary>Creates a transparent stream wrapper with its bound item Codec.</summary>
    public RpcCodecBoundAsyncEnumerable(IAsyncEnumerable<T> source, IRpcCodec<T> codec)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        Codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    /// <summary>Gets the item Codec selected for the owning generated Contract.</summary>
    public IRpcCodec<T> Codec { get; }

    /// <inheritdoc />
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => _source.GetAsyncEnumerator(cancellationToken);
}
