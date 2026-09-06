namespace SharpLink.Server;

/// <summary>Post-pointer publication for stable FixedWindow targets.</summary>
internal sealed partial class SharpLinkAdmissionController
{
    internal void PublishRateTargets()
    {
        foreach (var binding in _ruleStateBindings)
            binding.RateState?.OnPublished();
        _partitions?.PublishRateTargets();
    }
}

internal sealed partial class AdmissionPartitionPool
{
    private AdmissionPartitionPolicyGeneration? _publishedPolicy;

    internal void PublishRateTargets()
    {
        lock (_gate)
        {
            _publishedPolicy = _currentPolicy;
            foreach (var entry in _entries.Values)
                entry.Current.Rate?.OnPublished();
        }
    }

    private AdmissionPartitionEntry FinalizeNewEntryLocked(AdmissionPartitionEntry entry)
    {
        if (ReferenceEquals(entry.Current.Policy, _publishedPolicy))
            entry.Current.Rate?.OnPublished();
        return entry;
    }
}
