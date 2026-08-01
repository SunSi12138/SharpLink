namespace SharpLink.Sdk;

/// <summary>Controls one RPC invocation without becoming part of its business payload.</summary>
/// <example>
/// <code>
/// var options = new SharpLinkCallOptions
/// {
///     Timeout = TimeSpan.FromSeconds(2),
///     WaitForReady = true
/// };
/// </code>
/// </example>
public readonly record struct SharpLinkCallOptions
{
    /// <summary>Gets the relative call timeout.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Gets the absolute UTC deadline.</summary>
    public DateTimeOffset? Deadline { get; init; }

    /// <summary>Gets immutable metadata sent with the request.</summary>
    public SharpLinkMetadata? Metadata { get; init; }

    /// <summary>Gets whether the call waits asynchronously for a ready connection.</summary>
    public bool WaitForReady { get; init; }
}
