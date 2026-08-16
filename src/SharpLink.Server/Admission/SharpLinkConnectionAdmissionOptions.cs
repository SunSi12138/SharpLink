namespace SharpLink.Server;

/// <summary>
/// Configures the connection-level resource envelope applied before RPC call admission:
/// one hard bound for simultaneously live accepted connections and one independent hard
/// bound for connections still inside TLS / Protocol v2 / authentication handshake.
/// </summary>
/// <remarks>
/// <para>
/// A connection slot is held from a successful listener <c>Accept</c> until the connection
/// reaches its single terminal cleanup; a handshake slot is released as soon as the
/// connection becomes Ready (or fails before Ready). Both bounds reject immediately:
/// an over-limit accepted connection is closed without entering the handshake or session
/// lifecycle and without spawning a framework task.
/// </para>
/// <para>
/// These bounds are distinct from <c>MaxConcurrentCallsPerConnection</c> /
/// <c>MaxConcurrentCallsPerServer</c>: call limits protect RPC dispatch after the
/// handshake completes, while these bounds protect the pre-auth accepted/live set.
/// </para>
/// </remarks>
public sealed class SharpLinkConnectionAdmissionOptions
{
    /// <summary>The default maximum simultaneously live accepted connections (1024).</summary>
    public const int DefaultMaxConcurrentConnections = 1024;

    /// <summary>
    /// Gets or sets the maximum simultaneously live accepted connections, including
    /// connections still handshaking and connections already Ready.
    /// </summary>
    public int MaxConcurrentConnections { get; set; } = DefaultMaxConcurrentConnections;

    /// <summary>
    /// Gets or sets the maximum connections simultaneously inside TLS / Protocol v2 /
    /// authentication handshake. Zero means no independent handshake bound: handshake
    /// concurrency is bounded by <see cref="MaxConcurrentConnections"/> instead.
    /// A positive value must not exceed <see cref="MaxConcurrentConnections"/>.
    /// </summary>
    public int MaxConcurrentHandshakes { get; set; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConcurrentConnections);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxConcurrentHandshakes);
        if (MaxConcurrentHandshakes > MaxConcurrentConnections)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentHandshakes),
                "MaxConcurrentHandshakes must not exceed MaxConcurrentConnections.");
        }
    }

    internal SharpLinkConnectionAdmissionOptions CloneValidated()
    {
        Validate();
        return new SharpLinkConnectionAdmissionOptions
        {
            MaxConcurrentConnections = MaxConcurrentConnections,
            MaxConcurrentHandshakes = MaxConcurrentHandshakes
        };
    }
}
