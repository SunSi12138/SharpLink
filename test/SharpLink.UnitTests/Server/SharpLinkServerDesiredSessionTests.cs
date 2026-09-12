using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class SharpLinkServerDesiredSessionTests
{
    [Test]
    public async Task FutureOnlyPublicationShouldAdvanceImmutableDesiredGeneration()
    {
        await using var fixture = await SharpLinkServerTestFixture.CreateAsync();
        var server = fixture.Server;
        var initial = server.DesiredSession;

        var published = await server.PublishDesiredSessionAsync(
            new SharpLinkServerDesiredSessionConfiguration
            {
                MaxFramePayloadBytes = Math.Max(
                    SharpLinkProtocolOptions.MinMaxFramePayloadBytes,
                    initial.Configuration.MaxFramePayloadBytes / 2)
            });

        Ensure(published.ServerInstanceId == initial.ServerInstanceId,
            "one server instance should retain one desired-generation authority");
        Ensure(published.Generation == initial.Generation + 1,
            "a changed desired session should advance generation exactly once");
        Ensure(server.DesiredSession == published,
            "the published snapshot should become the server desired session");
    }

    [Test]
    public async Task InvalidDesiredCandidateShouldNotAdvanceGeneration()
    {
        await using var fixture = await SharpLinkServerTestFixture.CreateAsync();
        var server = fixture.Server;
        var initial = server.DesiredSession;

        await EnsureThrows<ArgumentOutOfRangeException>(async () =>
        {
            await server.PublishDesiredSessionAsync(
                new SharpLinkServerDesiredSessionConfiguration
                {
                    MaxFramePayloadBytes = SharpLinkProtocolOptions.MaxMaxFramePayloadBytes
                });
        });

        Ensure(server.DesiredSession == initial,
            "an invalid desired candidate must leave the current generation unchanged");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static async Task EnsureThrows<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
