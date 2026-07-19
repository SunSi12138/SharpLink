namespace SharpLink.Runtime;

/// <summary>Configures one local shared-memory transport endpoint.</summary>
public sealed class SharedMemoryTransportOptions
{
    internal const int MinCapacityPerDirectionBytes = 64 * 1024;
    internal const int MaxCapacityPerDirectionBytes = 256 * 1024 * 1024;
    internal const int MaxSpinCount = 4096;

    /// <summary>
    /// Gets or sets the requested capacity of each unidirectional shared-memory ring.
    /// A null value uses the selected <see cref="SharpLinkPerformanceProfile"/> default.
    /// </summary>
    public int? CapacityPerDirectionBytes { get; set; }

    /// <summary>
    /// Gets or sets the number of local spin iterations attempted before awaiting a control-channel signal.
    /// A null value uses the selected <see cref="SharpLinkPerformanceProfile"/> default.
    /// </summary>
    public int? SpinCount { get; set; }

    /// <summary>Gets or sets the independent shared-memory transport handshake timeout.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Validates explicitly configured values.</summary>
    public void Validate()
    {
        if (CapacityPerDirectionBytes is { } capacity)
            ValidateCapacity(capacity);
        if (SpinCount is < 0 or > MaxSpinCount)
            throw new ArgumentOutOfRangeException(nameof(SpinCount));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(HandshakeTimeout, TimeSpan.Zero);
    }

    internal SharedMemoryTransportOptions CloneValidated()
    {
        Validate();
        return new SharedMemoryTransportOptions
        {
            CapacityPerDirectionBytes = CapacityPerDirectionBytes,
            SpinCount = SpinCount,
            HandshakeTimeout = HandshakeTimeout
        };
    }

    internal SharedMemoryResolvedOptions Resolve(SharpLinkPerformanceProfile profile)
    {
        if (!Enum.IsDefined(profile))
            throw new ArgumentOutOfRangeException(nameof(profile));

        var capacity = CapacityPerDirectionBytes ?? profile switch
        {
            SharpLinkPerformanceProfile.LowLatency => 1024 * 1024,
            SharpLinkPerformanceProfile.Throughput => 32 * 1024 * 1024,
            _ => 8 * 1024 * 1024
        };
        var spinCount = SpinCount ?? profile switch
        {
            SharpLinkPerformanceProfile.LowLatency => 64,
            SharpLinkPerformanceProfile.Throughput => 0,
            _ => 8
        };

        ValidateCapacity(capacity);
        if (spinCount is < 0 or > MaxSpinCount)
            throw new ArgumentOutOfRangeException(nameof(SpinCount));
        return new SharedMemoryResolvedOptions(capacity, spinCount, HandshakeTimeout);
    }

    private static void ValidateCapacity(int capacity)
    {
        if (capacity is < MinCapacityPerDirectionBytes or > MaxCapacityPerDirectionBytes ||
            !BitOperations.IsPow2(capacity))
        {
            throw new ArgumentOutOfRangeException(
                nameof(CapacityPerDirectionBytes),
                $"Shared-memory capacity must be a power of two between {MinCapacityPerDirectionBytes} and {MaxCapacityPerDirectionBytes} bytes.");
        }
    }
}

internal readonly record struct SharedMemoryResolvedOptions(
    int CapacityPerDirectionBytes,
    int SpinCount,
    TimeSpan HandshakeTimeout);

internal interface IPerformanceProfileAwareTransport
{
    void BindPerformanceProfile(SharpLinkPerformanceProfile profile);
}
