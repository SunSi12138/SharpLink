using SharpLink.Abstractions;
using System;

namespace SharpLink.LoadTestBase;

public sealed class LocalHarness(ISharpLinkServer server, ISharpLinkClient client, Action cleanup) : IDisposable
{
    private bool _disposed;

    public ISharpLinkServer Server { get; } = server;
    public ISharpLinkClient Client { get; } = client;

    public void DisposeServer()
    {
        (Server as IDisposable)?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        (Client as IDisposable)?.Dispose();
        (Server as IDisposable)?.Dispose();
        cleanup();
    }
}


