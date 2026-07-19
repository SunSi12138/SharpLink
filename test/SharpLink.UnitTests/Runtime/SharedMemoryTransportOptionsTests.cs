namespace SharpLink.UnitTests;

public class SharedMemoryTransportOptionsTests
{
    [Test]
    public async Task ProfileDefaultsShouldResolveToDocumentedValues()
    {
        var options = new SharedMemoryTransportOptions();

        var lowLatency = options.Resolve(SharpLinkPerformanceProfile.LowLatency);
        var balanced = options.Resolve(SharpLinkPerformanceProfile.Balanced);
        var throughput = options.Resolve(SharpLinkPerformanceProfile.Throughput);

        await Assert.That(lowLatency.CapacityPerDirectionBytes).IsEqualTo(1024 * 1024);
        await Assert.That(lowLatency.SpinCount).IsEqualTo(64);
        await Assert.That(balanced.CapacityPerDirectionBytes).IsEqualTo(8 * 1024 * 1024);
        await Assert.That(balanced.SpinCount).IsEqualTo(8);
        await Assert.That(throughput.CapacityPerDirectionBytes).IsEqualTo(32 * 1024 * 1024);
        await Assert.That(throughput.SpinCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(65535)]
    [Arguments(65537)]
    [Arguments(268435457)]
    public async Task InvalidCapacityShouldBeRejected(int capacity)
    {
        var options = new SharedMemoryTransportOptions { CapacityPerDirectionBytes = capacity };
        await Assert.That(options.Validate).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(4097)]
    public async Task InvalidSpinCountShouldBeRejected(int spinCount)
    {
        var options = new SharedMemoryTransportOptions { SpinCount = spinCount };
        await Assert.That(options.Validate).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task NonPositiveHandshakeTimeoutShouldBeRejected(int milliseconds)
    {
        var options = new SharedMemoryTransportOptions
        {
            HandshakeTimeout = TimeSpan.FromMilliseconds(milliseconds)
        };
        await Assert.That(options.Validate).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ExplicitValuesShouldOverrideProfileDefaults()
    {
        var options = new SharedMemoryTransportOptions
        {
            CapacityPerDirectionBytes = 4 * 1024 * 1024,
            SpinCount = 12,
            HandshakeTimeout = TimeSpan.FromSeconds(3)
        };

        var resolved = options.Resolve(SharpLinkPerformanceProfile.Throughput);

        await Assert.That(resolved.CapacityPerDirectionBytes).IsEqualTo(4 * 1024 * 1024);
        await Assert.That(resolved.SpinCount).IsEqualTo(12);
        await Assert.That(resolved.HandshakeTimeout).IsEqualTo(TimeSpan.FromSeconds(3));
    }
}
