namespace SharpLink.StreamLoadTest.Tests;

public class StreamFlowWindowOptionsTests
{
    [Test]
    public async Task DefaultFlowWindowsRemainProfileOwned()
    {
        var options = StreamLoadOptions.Parse([]);

        await Assert.That(options.StreamReceiveWindowBytes).IsNull();
        await Assert.That(options.ConnectionReceiveWindowBytes).IsNull();
    }

    [Test]
    public async Task ExplicitWindowsPreserveLongStreamAndConcurrency()
    {
        var options = StreamLoadOptions.Parse([
            "--stream-size", "10000",
            "--concurrency", "8",
            "--stream-receive-window-bytes", "8192",
            "--connection-receive-window-bytes", "65536"
        ]);

        await Assert.That(options.StreamReceiveWindowBytes).IsEqualTo(8192);
        await Assert.That(options.ConnectionReceiveWindowBytes).IsEqualTo(65536);
        await Assert.That(options.StreamSize).IsEqualTo(10000);
        await Assert.That(options.ConcurrencyConfig).IsEquivalentTo([8]);
    }

    [Test]
    public async Task StreamWindowOverrideDoesNotChangeUnspecifiedConnectionWindow()
    {
        var options = StreamLoadOptions.Parse(["--stream-receive-window-bytes", "8192"]);

        await Assert.That(options.StreamReceiveWindowBytes).IsEqualTo(8192);
        await Assert.That(options.ConnectionReceiveWindowBytes).IsNull();
    }

    [Test]
    public async Task ConnectionWindowOverrideDoesNotChangeUnspecifiedStreamWindow()
    {
        var options = StreamLoadOptions.Parse(["--connection-receive-window-bytes", "33554432"]);

        await Assert.That(options.StreamReceiveWindowBytes).IsNull();
        await Assert.That(options.ConnectionReceiveWindowBytes).IsEqualTo(33554432);
    }

    [Test]
    [Arguments("--stream-receive-window-bytes", "0")]
    [Arguments("--stream-receive-window-bytes", "-1")]
    [Arguments("--connection-receive-window-bytes", "0")]
    [Arguments("--connection-receive-window-bytes", "-1")]
    public async Task NonPositiveWindowOverridesAreRejected(string option, string value)
    {
        await Assert.That(() => StreamLoadOptions.Parse([option, value]))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ConnectionWindowCannotBeSmallerThanStreamWindow()
    {
        await Assert.That(() => StreamLoadOptions.Parse([
                "--stream-receive-window-bytes", "8192",
                "--connection-receive-window-bytes", "4096"
            ]))
            .Throws<ArgumentException>();
    }
}
