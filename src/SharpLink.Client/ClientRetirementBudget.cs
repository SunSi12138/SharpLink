namespace SharpLink.Client;

// A cold value capability: sharing a TimeProvider does not make two registries
// interchangeable. This adds no owner object, timer, lease, or RPC-path lookup.
internal readonly struct ClientRetirementBudget(object owner, RetirementGraceBudget budget)
{
    private readonly object _owner = owner;
    private readonly RetirementGraceBudget _budget = budget;

    internal RetirementGraceBudget GetFor(object owner)
    {
        if (!ReferenceEquals(_owner, owner))
            throw new InvalidOperationException("The retirement budget belongs to another exact child registry.");
        return _budget;
    }
}
