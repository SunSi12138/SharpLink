using SharpLink.Client;
using SharpLink.UnitTests.Runtime;
using System.IO.Pipelines;
using System.Threading;

namespace SharpLink.UnitTests.Client;

public class LateResponseLogLimiterTests
{
    [Test]
    public void LimiterShouldLogOncePerConnectionWindowAndReportSuppressedCount()
    {
        const long timestampFrequency = 10;
        var firstConnection = new LateResponseLogLimiter(timestampFrequency);
        var secondConnection = new LateResponseLogLimiter(timestampFrequency);
        const long started = -10;

        Ensure(firstConnection.ShouldLog(started, out var firstSuppressed),
            "first response should log immediately");
        Ensure(firstSuppressed == 0, "first warning suppressed count");
        Ensure(!firstConnection.ShouldLog(started + 1, out _), "second response should be suppressed");
        Ensure(!firstConnection.ShouldLog(started + 2, out _), "third response should be suppressed");

        Ensure(secondConnection.ShouldLog(started + 2, out var secondSuppressed),
            "a different connection must have an independent window");
        Ensure(secondSuppressed == 0, "second connection suppressed count");

        Ensure(firstConnection.ShouldLog(
                started + firstConnection.IntervalTimestampTicks,
                out var suppressed),
            "response at the next window should log");
        Ensure(suppressed == 2, "warning should report responses suppressed in the prior window");
    }

    [Test]
    public async Task ClientConnectionLimiterShouldUseItsRuntimeProviderTimestamp()
    {
        var timeProvider = new ManualTimeProvider();
        var runtimeContext = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(timeProvider)
            .Build(includeGeneratedAssemblyCatalog: false);
        await using var client = new SharpLinkClient(
            new TestClientTransportFactory(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            runtimeContext);
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "late-response-limiter",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(runtimeContext));
        using var cancellation = new CancellationTokenSource();
        await using var connection = new ClientConnection(
            client,
            session,
            cancellation,
            maxPendingCalls: 8,
            runtimeContext);

        Ensure(connection.ShouldLogLateResponse(out var firstSuppressed) && firstSuppressed == 0,
            "the connection must log the first late response at its provider timestamp");
        Ensure(!connection.ShouldLogLateResponse(out _),
            "a second response in the same provider window must be suppressed");
        timeProvider.SetUtcNow(timeProvider.GetUtcNow().AddDays(1));
        Ensure(!connection.ShouldLogLateResponse(out _),
            "a UTC-only jump must not open a limiter window");
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        Ensure(connection.ShouldLogLateResponse(out var suppressed) && suppressed == 2,
            "the provider monotonic boundary must open the next window and report both suppressions");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
