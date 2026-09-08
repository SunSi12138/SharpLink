namespace SharpLink.Client;

/// <summary>
/// Owns the independently published endpoint-selection policy. Writers are serialized by the
/// Client control/lifecycle gate; readers capture one immutable generation through a volatile read.
/// Cursor state remains topology-owned, so policy replacement does not allocate or migrate
/// RoundRobin/LeastPending history.
/// </summary>
internal sealed class EndpointSelectionPolicyState
{
    private EndpointSelectionPolicyGeneration _current;

    public EndpointSelectionPolicyState(
        SharpLinkLoadBalancingStrategy strategy,
        ISharpLinkEndpointSelector? selector)
    {
        if (!Enum.IsDefined(strategy))
            throw new ArgumentOutOfRangeException(nameof(strategy));
        _current = new EndpointSelectionPolicyGeneration(0, strategy, selector);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EndpointSelectionPolicyGeneration Capture()
        => Volatile.Read(ref _current);

    public SharpLinkEndpointSelectionPolicySnapshot GetSnapshot()
    {
        var current = Capture();
        return new SharpLinkEndpointSelectionPolicySnapshot(
            current.Generation,
            current.HasCustomSelector
                ? SharpLinkEndpointSelectionPolicyKind.Custom
                : SharpLinkEndpointSelectionPolicyKind.BuiltIn,
            current.HasCustomSelector ? null : current.Strategy);
    }

    public void PublishBuiltIn(SharpLinkLoadBalancingStrategy strategy)
    {
        if (!Enum.IsDefined(strategy))
            throw new ArgumentOutOfRangeException(nameof(strategy));
        var current = Capture();
        if (!current.HasCustomSelector && current.Strategy == strategy)
            return;
        if (current.Generation == ulong.MaxValue)
            throw new InvalidOperationException("Endpoint selection policy generation is exhausted.");
        Volatile.Write(
            ref _current,
            new EndpointSelectionPolicyGeneration(current.Generation + 1, strategy, selector: null));
    }

    public void PublishCustom(ISharpLinkEndpointSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var current = Capture();
        if (ReferenceEquals(current.Selector, selector))
            return;
        if (current.Generation == ulong.MaxValue)
            throw new InvalidOperationException("Endpoint selection policy generation is exhausted.");
        Volatile.Write(
            ref _current,
            new EndpointSelectionPolicyGeneration(current.Generation + 1, current.Strategy, selector));
    }
}

/// <summary>Immutable policy captured once at the physical-attempt boundary.</summary>
internal sealed class EndpointSelectionPolicyGeneration
{
    public EndpointSelectionPolicyGeneration(
        ulong generation,
        SharpLinkLoadBalancingStrategy strategy,
        ISharpLinkEndpointSelector? selector)
    {
        Generation = generation;
        Strategy = strategy;
        Selector = selector;
    }

    public ulong Generation { get; }
    public SharpLinkLoadBalancingStrategy Strategy { get; }
    public ISharpLinkEndpointSelector? Selector { get; }
    public bool HasCustomSelector => Selector is not null;
}
