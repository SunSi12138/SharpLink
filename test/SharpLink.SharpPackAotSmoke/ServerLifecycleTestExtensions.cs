namespace SharpLink.SharpPackAotSmoke;

internal static class ServerLifecycleTestExtensions
{
    internal static System.Threading.Tasks.ValueTask RunUntilStoppedAsync(
        this SharpLink.Abstractions.ISharpLinkServer server,
        System.Threading.CancellationToken cancellationToken = default)
        => new(RunUntilStoppedCoreAsync(server, cancellationToken));

    private static async System.Threading.Tasks.Task RunUntilStoppedCoreAsync(
        SharpLink.Abstractions.ISharpLinkServer server,
        System.Threading.CancellationToken cancellationToken)
    {
        await server.StartAsync(cancellationToken).ConfigureAwait(false);
        var terminal = server.WaitForShutdownAsync();
        if (!cancellationToken.CanBeCanceled)
        {
            await terminal.ConfigureAwait(false);
            return;
        }

        var cancellation = System.Threading.Tasks.Task.Delay(
            System.Threading.Timeout.InfiniteTimeSpan,
            cancellationToken);
        if (ReferenceEquals(
                await System.Threading.Tasks.Task.WhenAny(terminal, cancellation).ConfigureAwait(false),
                terminal))
        {
            await terminal.ConfigureAwait(false);
            return;
        }

        await server.StopAsync(System.TimeSpan.Zero).ConfigureAwait(false);
        await terminal.ConfigureAwait(false);
    }
}
