using Microsoft.Extensions.Logging.Abstractions;
using System.Threading;
using SharpLink.Hosting;
using SharpLink.Server;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.UnitTests.Hosting;

public class SharpLinkServerHostedServiceTests
{
    [Test]
    public async Task StopAsyncShouldCancelRunLoopDisposeServerAndBeIdempotent()
    {
        var transport = new BlockingTransport();
        var builder = SharpLinkServerBuilder.Create()
            .UseTransport(transport);
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var readiness = new SharpLinkServerReadiness();
        var hosted = new SharpLinkServerHostedService(
            builder,
            NullLoggerFactory.Instance,
            provider,
            readiness);

        await hosted.StartAsync(CancellationToken.None);
        Ensure(readiness.Status == SharpLinkHealthStatus.Ready,
            "readiness should be ready after hosted service starts");
        await hosted.StopAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);

        Ensure(transport.DisposeCalled, "transport should be disposed when hosted service stops");
        Ensure(readiness.Status == SharpLinkHealthStatus.Unhealthy,
            "readiness should be unhealthy after hosted service stops");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class BlockingTransport : IServerTransportListener
    {
        private int _disposed;
        public bool DisposeCalled => Volatile.Read(ref _disposed) == 1;
        public System.Net.EndPoint? LocalEndPoint => null;

        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }
}
