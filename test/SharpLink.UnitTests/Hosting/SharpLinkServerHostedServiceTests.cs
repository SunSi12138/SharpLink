using Microsoft.Extensions.Logging.Abstractions;
using System.Threading;
using SharpLink.Hosting;
using SharpLink.Server;

namespace SharpLink.UnitTests.Hosting;

public class SharpLinkServerHostedServiceTests
{
    [Test]
    public async Task StopAsyncShouldCancelRunLoopDisposeServerAndBeIdempotent()
    {
        var transport = new BlockingTransport();
        var builder = SharpLinkServerBuilder.Create()
            .UseTransport(transport);
        var hosted = new SharpLinkServerHostedService(builder, NullLoggerFactory.Instance);

        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);

        Ensure(transport.DisposeCalled, "transport should be disposed when hosted service stops");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class BlockingTransport : ITransport
    {
        private int _disposed;
        public bool DisposeCalled => Volatile.Read(ref _disposed) == 1;

        public async Task<IRpcSession> ConnectAsync(CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
        }
    }
}
