using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
    public async Task DnsResolverShouldFilterMappedIpv4AddressesByTheirNormalizedFamily()
    {
        var query = new TestDnsQuery { Addresses = [IPAddress.Parse("::ffff:127.0.0.1")] };
        await using var ipv4 = new SharpLinkDnsEndpointResolver(
            "service.example",
            5001,
            new SharpLinkDnsResolverOptions { AddressFamily = AddressFamily.InterNetwork },
            query);
        await using var ipv6 = new SharpLinkDnsEndpointResolver(
            "service.example",
            5001,
            new SharpLinkDnsResolverOptions { AddressFamily = AddressFamily.InterNetworkV6 },
            query);

        var ipv4Snapshot = await ipv4.ResolveAsync(CancellationToken.None);
        var ipv6Snapshot = await ipv6.ResolveAsync(CancellationToken.None);

        Ensure(ipv4Snapshot.Endpoints.Count == 1, "mapped IPv4 must satisfy an IPv4 family filter");
        Ensure(ipv4Snapshot.Endpoints[0].Address is SharpLinkTcpAddress { Host: "127.0.0.1" },
            "mapped IPv4 endpoint must publish its normalized address");
        Ensure(ipv6Snapshot.Endpoints.Count == 0, "mapped IPv4 must not satisfy an IPv6 family filter");
    }

    [Test]
    public async Task DnsResolverShouldNotHideUnexpectedQueryFailuresBehindLastGood()
    {
        var query = new TestDnsQuery { Addresses = [IPAddress.Loopback] };
        await using var resolver = new SharpLinkDnsEndpointResolver(
            "service.example",
            5001,
            new SharpLinkDnsResolverOptions
            {
                RefreshInterval = TimeSpan.FromMilliseconds(1),
                MinimumRefreshInterval = TimeSpan.FromMilliseconds(1),
                MaximumRefreshInterval = TimeSpan.FromMilliseconds(1),
                JitterRatio = 0
            },
            query);
        _ = await resolver.ResolveAsync(CancellationToken.None);
        query.Exception = new InvalidOperationException("query implementation failed");

        await EnsureThrows<InvalidOperationException>(async () =>
            _ = await resolver.ResolveAsync(CancellationToken.None));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using var watch = resolver.WatchAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        await EnsureThrows<InvalidOperationException>(async () =>
            _ = await watch.MoveNextAsync());
    }

    [Test]
    public void EndpointSnapshotShouldNotExposeItsMutableBackingArray()
    {
        var original = new SharpLinkEndpoint
        {
            Id = "original",
            Address = new SharpLinkTcpAddress("127.0.0.1", 5001)
        };
        var snapshot = new SharpLinkEndpointSnapshot(1, [original]);
        var mutated = false;
        if (snapshot.Endpoints is IList<SharpLinkEndpoint> mutable)
        {
            var replacement = new SharpLinkEndpoint
            {
                Id = "injected",
                Address = new SharpLinkTcpAddress("127.0.0.1", 5002)
            };
            try
            {
                mutable[0] = replacement;
                mutated = ReferenceEquals(snapshot.Endpoints[0], replacement);
                mutable[0] = original;
            }
            catch (NotSupportedException)
            {
            }
        }

        Ensure(!mutated, "a published endpoint topology must remain immutable");
    }

    [Test]
    public void EndpointSnapshotShouldFreezeNestedEndpointAttributes()
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["zone"] = "east"
        };
        var snapshot = new SharpLinkEndpointSnapshot(1,
        [
            new SharpLinkEndpoint
            {
                Id = "endpoint",
                Address = new SharpLinkTcpAddress("127.0.0.1", 5001),
                Attributes = attributes
            }
        ]);

        attributes["zone"] = "west";
        var injected = false;
        if (snapshot.Endpoints[0].Attributes is IDictionary<string, string> mutable)
        {
            try
            {
                mutable["role"] = "admin";
                injected = snapshot.Endpoints[0].Attributes.ContainsKey("role");
            }
            catch (NotSupportedException)
            {
            }
        }

        Ensure(snapshot.Endpoints[0].Attributes["zone"] == "east" && !injected,
            "snapshot endpoints must own frozen attribute dictionaries");
    }

    [Test]
    public async Task BuiltInResolversShouldDisposeTheirCancellationSources()
    {
        var @delegate = new DelegateSharpLinkEndpointResolver(
            static _ => ValueTask.FromResult(new SharpLinkEndpointSnapshot(0, [])));
        var dns = new SharpLinkDnsEndpointResolver(
            "service.example",
            5001,
            new SharpLinkDnsResolverOptions(),
            new TestDnsQuery { Addresses = [IPAddress.Loopback] });

        await @delegate.DisposeAsync();
        await dns.DisposeAsync();

        EnsureCancellationSourceDisposed(@delegate);
        EnsureCancellationSourceDisposed(dns);
    }

    [Test]
    public async Task EndpointResolverPollingShouldSupportTimerRangeExceedingIntervals()
    {
        await using var @delegate = new DelegateSharpLinkEndpointResolver(
            static _ => ValueTask.FromResult(new SharpLinkEndpointSnapshot(0, [])),
            TimeSpan.MaxValue);
        await using var dns = new SharpLinkDnsEndpointResolver(
            "service.example",
            5001,
            new SharpLinkDnsResolverOptions
            {
                RefreshInterval = TimeSpan.MaxValue,
                MinimumRefreshInterval = TimeSpan.FromMilliseconds(1),
                MaximumRefreshInterval = TimeSpan.MaxValue,
                JitterRatio = 0
            },
            new TestDnsQuery { Addresses = [IPAddress.Loopback] });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await using var delegateWatch = @delegate.WatchAsync(cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        await using var dnsWatch = dns.WatchAsync(cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        var delegateMove = delegateWatch.MoveNextAsync().AsTask();
        var dnsMove = dnsWatch.MoveNextAsync().AsTask();
        var delegateFailure = await CaptureFailureAsync(delegateMove);
        var dnsFailure = await CaptureFailureAsync(dnsMove);

        Ensure(delegateFailure is OperationCanceledException,
            $"long delegate polling should remain cancellable, not fail as {delegateFailure?.GetType().Name}");
        Ensure(dnsFailure is OperationCanceledException,
            $"long DNS polling should remain cancellable, not fail as {dnsFailure?.GetType().Name}");
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

    [Test]
    public async Task DynamicBuilderShouldCapMinReadyByMaxEndpoints()
    {
        var resolver = new TrackingResolver();
        await using var client = SharpClientBuilder.Create()
            .UseEndpointResolver(resolver, _ => new TrackingFactory())
            .UseCluster(options =>
            {
                options.MaxEndpoints = 1;
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 1;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();
    }

    [Test]
    public async Task DynamicClusterShouldRejectAnonymousPipeFactories()
    {
        var resolver = new SingleSnapshotResolver(new SharpLinkEndpointSnapshot(0,
        [
            new SharpLinkEndpoint
            {
                Id = "pipe",
                Address = new SharpLinkAnonymousPipeAddress("in-handle", "out-handle")
            }
        ]));
        await using var client = SharpClientBuilder.Create()
            .UseEndpointResolver(resolver, _ => new AnonymousPipeClientTransportFactory("in-handle", "out-handle"))
            .Build();

        try
        {
            await client.ConnectAsync();
            throw new Exception("expected anonymous-pipe dynamic cluster rejection");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable, "dynamic cluster rejection code");
            Ensure(exception.InnerException is InvalidOperationException {
                Message: "The endpoint resolver returned an invalid initial topology."
            }, "dynamic cluster must reject the anonymous-pipe factory before attempting a connection");
        }
    }

    [Test]
    public async Task RetriedResolverFailureShouldNotBeAnUnhandledBackgroundError()
    {
        var loggerFactory = new CaptureLoggerFactory();
        await using var client = SharpClientBuilder.Create()
            .UseLoggerFactory(loggerFactory)
            .UseEndpointResolver(new FailingWatchResolver(), _ => new TrackingFactory())
            .Build();

        await client.ConnectAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!loggerFactory.HasEntry(static entry =>
                   entry.Level == LogLevel.Error || entry.EventId.Id == 6102))
        {
            await Task.Delay(10, timeout.Token);
        }

        Ensure(!loggerFactory.HasEntry(static entry => entry.Level == LogLevel.Error),
            "a resolver failure owned by the retry worker must not be reported as unhandled");
        Ensure(loggerFactory.HasEntry(static entry =>
                entry is { Level: LogLevel.Warning, EventId.Id: 6102,
                    Exception: InvalidOperationException { Message: "watch failed" } }),
            "the retried resolver failure should remain observable through its warning event");
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

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void EnsureCancellationSourceDisposed(object resolver)
    {
        var field = resolver.GetType().GetField(
            "_disposeCts",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("resolver cancellation source field was not found");
        var source = (CancellationTokenSource)field.GetValue(resolver)!;
        try
        {
            _ = source.Token;
            throw new Exception("resolver cancellation source was cancelled but not disposed");
        }
        catch (ObjectDisposedException)
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
        public Exception? Exception { get; set; }

        public ValueTask<IPAddress[]> QueryAsync(string host, CancellationToken cancellationToken)
        {
            if (Exception is { } exception)
                return ValueTask.FromException<IPAddress[]>(exception);
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

    private sealed class SingleSnapshotResolver(SharpLinkEndpointSnapshot snapshot) : ISharpLinkEndpointResolver
    {
        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(snapshot);

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingWatchResolver : ISharpLinkEndpointResolver
    {
        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new SharpLinkEndpointSnapshot(0, []));

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return await ValueTask.FromException<SharpLinkEndpointSnapshot>(
                new InvalidOperationException("watch failed"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CaptureLoggerFactory : ILoggerFactory
    {
        private readonly Lock _gate = new();
        private readonly List<LogEntry> _entries = [];

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        internal bool HasEntry(Func<LogEntry, bool> predicate)
        {
            lock (_gate)
                return _entries.Exists(entry => predicate(entry));
        }

        private sealed class CaptureLogger(CaptureLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._gate)
                    owner._entries.Add(new LogEntry(logLevel, eventId, exception));
            }
        }
    }

    private readonly record struct LogEntry(LogLevel Level, EventId EventId, Exception? Exception);

    private sealed class TrackingFactory : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}
