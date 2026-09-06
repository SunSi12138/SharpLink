namespace SharpLink.Runtime;

internal sealed class CompressionSendPolicySnapshot
{
    private CompressionSendPolicySnapshot(
        bool enabled,
        int minimumPayloadBytes,
        int minimumSavingsBytes,
        double minimumSavingsRatio)
    {
        Enabled = enabled;
        MinimumPayloadBytes = minimumPayloadBytes;
        MinimumSavingsBytes = minimumSavingsBytes;
        MinimumSavingsRatio = minimumSavingsRatio;
    }

    internal bool Enabled { get; }
    internal int MinimumPayloadBytes { get; }
    internal int MinimumSavingsBytes { get; }
    internal double MinimumSavingsRatio { get; }

    internal static CompressionSendPolicySnapshot CreateInitial(SharpLinkCompressionSendPolicy policy)
        => CreateValidated(policy);

    internal static CompressionSendPolicySnapshot CreateValidated(SharpLinkCompressionSendPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return CreateValidated(
            policy.Enabled,
            policy.MinimumPayloadBytes,
            policy.MinimumSavingsBytes,
            policy.MinimumSavingsRatio);
    }

    private static CompressionSendPolicySnapshot CreateValidated(
        bool enabled,
        int minimumPayloadBytes,
        int minimumSavingsBytes,
        double minimumSavingsRatio)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumSavingsBytes);
        if (double.IsNaN(minimumSavingsRatio) || minimumSavingsRatio is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumSavingsRatio));
        return new CompressionSendPolicySnapshot(
            enabled,
            minimumPayloadBytes,
            minimumSavingsBytes,
            minimumSavingsRatio);
    }

    internal bool IsBeneficial(int originalBytes, int compressedBytes)
    {
        if (originalBytes < MinimumPayloadBytes || compressedBytes >= originalBytes)
            return false;
        var savings = originalBytes - compressedBytes;
        return savings >= MinimumSavingsBytes && savings >= originalBytes * MinimumSavingsRatio;
    }
}

internal sealed class CompressionSendPolicyState
{
    private CompressionSendPolicySnapshot _current;

    private CompressionSendPolicyState(CompressionSendPolicySnapshot initial)
        => _current = initial;

    internal static CompressionSendPolicyState CreateInitial(SharpLinkCompressionSendPolicy policy)
        => new(CompressionSendPolicySnapshot.CreateInitial(policy));

    internal CompressionSendPolicySnapshot Current => Volatile.Read(ref _current);

    internal void Update(SharpLinkCompressionSendPolicy policy)
    {
        var candidate = CompressionSendPolicySnapshot.CreateValidated(policy);
        Volatile.Write(ref _current, candidate);
    }
}

internal sealed class ResponseCompressionPreferenceSnapshot
{
    internal static ResponseCompressionPreferenceSnapshot InitialAllowed { get; } = new(0, allowed: true);

    internal ResponseCompressionPreferenceSnapshot(ulong generation, bool allowed)
    {
        Generation = generation;
        Allowed = allowed;
    }

    internal ulong Generation { get; }
    internal bool Allowed { get; }
}
