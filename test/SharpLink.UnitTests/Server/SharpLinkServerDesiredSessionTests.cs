using System.Net;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class SharpLinkServerDesiredSessionTests
{
    [Test]
    public async Task FutureOnlyPublicationShouldAdvanceImmutableDesiredGeneration()
    {
        await using var server = CreateServer();
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
        await using var server = CreateServer();
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

    private static ISharpLinkServer CreateServer()
        => SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(new NoopListener())
            .Build();

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

    private sealed class NoopListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
