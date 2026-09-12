namespace SharpLink.Client;

/// <summary>Identifies the endpoint-selection policy currently published by a multi-endpoint Client.</summary>
public enum SharpLinkEndpointSelectionPolicyKind
{
    /// <summary>One of SharpLink's built-in load-balancing strategies.</summary>
    BuiltIn,

    /// <summary>An application-owned <see cref="ISharpLinkEndpointSelector"/>.</summary>
    Custom
}

/// <summary>Describes one immutable endpoint-selection policy generation.</summary>
/// <param name="Generation">The monotonically increasing publication generation.</param>
/// <param name="Kind">Whether the generation uses a built-in or custom selector.</param>
/// <param name="BuiltInStrategy">The built-in strategy, or null for a custom selector.</param>
public readonly record struct SharpLinkEndpointSelectionPolicySnapshot(
    ulong Generation,
    SharpLinkEndpointSelectionPolicyKind Kind,
    SharpLinkLoadBalancingStrategy? BuiltInStrategy);

/// <summary>Runtime endpoint-selection configuration for multi-endpoint SharpLink clients.</summary>
/// <remarks>
/// Publication changes only how a future physical attempt selects from its captured Ready endpoint
/// snapshot. Healthy sessions and resolver topology are not rebuilt. One physical attempt captures
/// one complete policy generation; a later retry attempt may observe a newer generation.
/// </remarks>
public static class SharpLinkEndpointSelectionRuntimeExtensions
{
    /// <summary>Gets the currently published endpoint-selection policy generation.</summary>
    public static SharpLinkEndpointSelectionPolicySnapshot GetEndpointSelectionPolicySnapshot(
        this ISharpLinkClient client)
        => GetRuntime(client).CaptureEndpointSelectionPolicySnapshot();

    /// <summary>
    /// Atomically publishes a built-in strategy for future physical attempts without reconnecting
    /// healthy sessions or changing endpoint topology.
    /// </summary>
    /// <param name="client">The running multi-endpoint Client.</param>
    /// <param name="strategy">The complete built-in selection strategy to publish.</param>
    public static void UpdateLoadBalancing(
        this ISharpLinkClient client,
        SharpLinkLoadBalancingStrategy strategy)
    {
        if (!Enum.IsDefined(strategy))
            throw new ArgumentOutOfRangeException(nameof(strategy));
        GetRuntime(client).PublishEndpointSelectionStrategy(strategy);
    }

    /// <summary>
    /// Atomically publishes an application-owned selector for future physical attempts without
    /// reconnecting healthy sessions or changing endpoint topology.
    /// </summary>
    /// <param name="client">The running multi-endpoint Client.</param>
    /// <param name="selector">The synchronous selector to publish.</param>
    public static void UpdateEndpointSelector(
        this ISharpLinkClient client,
        ISharpLinkEndpointSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        GetRuntime(client).PublishEndpointSelector(selector);
    }

    private static SharpLinkClient GetRuntime(ISharpLinkClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client as SharpLinkClient ?? throw new NotSupportedException(
            "This ISharpLinkClient implementation does not support runtime endpoint-selection updates.");
    }
}
