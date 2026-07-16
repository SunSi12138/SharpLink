using SharpLink.Abstractions;
using System;
using System.Threading.Tasks;

namespace SharpLink.LoadTestBase;

public sealed class LocalHarness(ISharpLinkServer server, ISharpLinkClient client, Action cleanup) : IAsyncDisposable
{
    private bool _disposed;

    public ISharpLinkServer Server { get; } = server;
    public ISharpLinkClient Client { get; } = client;

    public ValueTask DisposeServerAsync()
    {
        return Server.StopAsync(TimeSpan.Zero);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await Client.StopAsync();
        await Server.StopAsync(TimeSpan.Zero);
        cleanup();
    }
}
