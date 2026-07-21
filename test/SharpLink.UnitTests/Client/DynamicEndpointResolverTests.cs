using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class DynamicEndpointResolverTests
{
    [Test]
    public async Task DnsResolverShouldNormalizeOrderAndRetainTheLastGoodSnapshot()
    {
        var query = new TestDnsQuery
        {
            Addresses =
            [
                IPAddress.Parse("::1"),
                IPAddress.Parse("127.0.0.1"),
                IPAddress.Parse("127.0.0.1")
            ]
        };
        await using var resolver = new SharpLinkDnsEndpointResolver(
            "service.example",
            5001,
            new SharpLinkDnsResolverOptions
            {
                RefreshInterval = TimeSpan.FromMilliseconds(1),
                MinimumRefreshInterval = TimeSpan.FromMilliseconds(1),
                MaximumRefreshInterval = TimeSpan.FromSeconds(1),
                JitterRatio = 0
            },
            query);

        var first = await resolver.ResolveAsync(CancellationToken.None);
        Ensure(first.Version == 1, "first DNS snapshot version");
        Ensure(first.Endpoints.Count == 2, "DNS endpoint de-duplication");
        Ensure(first.Endpoints[0].Authority == "service.example", "DNS authority");

        query.Addresses = [IPAddress.Parse("127.0.0.1"), IPAddress.Parse("::1")];
        var reordered = await resolver.ResolveAsync(CancellationToken.None);
        Ensure(reordered.Version == first.Version, "DNS order-only change must not publish a topology update");

        query.Throw = true;
        var retained = await resolver.ResolveAsync(CancellationToken.None);
        Ensure(retained.Version == first.Version, "DNS lookup failure must retain the last good topology");
    }

    [Test]
    public async Task DnsResolverShouldBoundGeneratedIdsForAValidLongHostname()
    {
        var label = new string('a', 63);
        var host = $"{label}.{label}.{label}.{new string('a', 61)}";
        var query = new TestDnsQuery { Addresses = [IPAddress.Loopback] };
        await using var resolver = new SharpLinkDnsEndpointResolver(
            host,
            5001,
            new SharpLinkDnsResolverOptions(),
            query);

        var snapshot = await resolver.ResolveAsync(CancellationToken.None);

        Ensure(snapshot.Endpoints.Count == 1, "long-host DNS endpoint count");
        Ensure(snapshot.Endpoints[0].Id.Length <= 256, "long-host DNS endpoint ID limit");
        Ensure(snapshot.Endpoints[0].Authority == host, "long-host DNS authority preservation");
    }

    [Test]
    public async Task DynamicBuilderShouldOwnResolverAndRejectFixedTransportConflict()
    {
        var resolver = new TrackingResolver();
        await using (var client = SharpClientBuilder.Create()
                         .UseEndpointResolver(resolver, _ => new TrackingFactory())
                         .Build())
        {
        }
        Ensure(resolver.DisposeCount == 1, "resolver should be disposed exactly once");

        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = SharpClientBuilder.Create()
                .UseTransport(new TrackingFactory())
                .UseEndpointResolver(new TrackingResolver(), _ => new TrackingFactory())
                .Build();
            return Task.CompletedTask;
        });
    }

    private static async Task EnsureThrows<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class TestDnsQuery : ISharpLinkDnsQuery
    {
        public IPAddress[] Addresses { get; set; } = [];
        public bool Throw { get; set; }

        public ValueTask<IPAddress[]> QueryAsync(string host, CancellationToken cancellationToken)
        {
            if (Throw)
                return ValueTask.FromException<IPAddress[]>(new SocketException());
            return ValueTask.FromResult(Addresses);
        }
    }

    private sealed class TrackingResolver : ISharpLinkEndpointResolver
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new SharpLinkEndpointSnapshot(0, []));

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingFactory : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
