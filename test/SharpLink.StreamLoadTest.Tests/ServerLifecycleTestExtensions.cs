using SharpLink.Abstractions;

namespace SharpLink.StreamLoadTest.Tests;

internal static class ServerLifecycleTestExtensions
{
    internal static ValueTask RunUntilStoppedAsync(
        this ISharpLinkServer server,
        CancellationToken cancellationToken = default)
        => new(RunUntilStoppedCoreAsync(server, cancellationToken));

    private static async Task RunUntilStoppedCoreAsync(
        ISharpLinkServer server,
        CancellationToken cancellationToken)
    {
        await server.StartAsync(cancellationToken).ConfigureAwait(false);
        var terminal = server.WaitForShutdownAsync();
        if (!cancellationToken.CanBeCanceled)
        {
            await terminal.ConfigureAwait(false);
            return;
        }

        var cancellation = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (ReferenceEquals(await Task.WhenAny(terminal, cancellation).ConfigureAwait(false), terminal))
        {
            await terminal.ConfigureAwait(false);
            return;
        }

        await server.StopAsync(TimeSpan.Zero).ConfigureAwait(false);
        await terminal.ConfigureAwait(false);
    }
}
