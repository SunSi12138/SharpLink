using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace SharpLink.Client;

/// <summary>Adapts application-supplied resolve and watch delegates to an endpoint resolver.</summary>
/// <remarks>
/// When no watch delegate is supplied, the resolver polls the resolve delegate with one bounded delay.
/// This allows applications to adapt an existing registry client without SharpLink taking a dependency on it.
/// </remarks>
public sealed class DelegateSharpLinkEndpointResolver : ISharpLinkEndpointResolver
{
    private static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromSeconds(30);
    private readonly Func<CancellationToken, ValueTask<SharpLinkEndpointSnapshot>> _resolve;
    private readonly Func<CancellationToken, IAsyncEnumerable<SharpLinkEndpointSnapshot>>? _watch;
    private readonly TimeSpan _pollingInterval;
    private readonly CancellationTokenSource _disposeCts = new();
    private int _disposed;

    /// <summary>Initializes a polling delegate resolver.</summary>
    /// <param name="resolve">Returns the latest complete endpoint snapshot.</param>
    /// <param name="pollingInterval">The positive delay between resolve calls after the initial resolution.</param>
    public DelegateSharpLinkEndpointResolver(
        Func<CancellationToken, ValueTask<SharpLinkEndpointSnapshot>> resolve,
        TimeSpan? pollingInterval = null)
        : this(resolve, watch: null, pollingInterval)
    {
    }

    /// <summary>Initializes a delegate resolver with an optional continuous watch.</summary>
    /// <param name="resolve">Returns the latest complete endpoint snapshot.</param>
    /// <param name="watch">Optionally returns subsequent snapshots. When null, <paramref name="resolve"/> is polled.</param>
    /// <param name="pollingInterval">The positive polling delay used when <paramref name="watch"/> is null.</param>
    public DelegateSharpLinkEndpointResolver(
        Func<CancellationToken, ValueTask<SharpLinkEndpointSnapshot>> resolve,
        Func<CancellationToken, IAsyncEnumerable<SharpLinkEndpointSnapshot>>? watch,
        TimeSpan? pollingInterval = null)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _watch = watch;
        _pollingInterval = pollingInterval ?? DefaultPollingInterval;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_pollingInterval, TimeSpan.Zero);
    }

    /// <inheritdoc />
    public async ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        return await _resolve(linked.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        if (_watch is not null)
        {
            await foreach (var snapshot in _watch(linked.Token).WithCancellation(linked.Token).ConfigureAwait(false))
                yield return snapshot;
            yield break;
        }

        while (true)
        {
            await Task.Delay(_pollingInterval, linked.Token).ConfigureAwait(false);
            yield return await _resolve(linked.Token).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _disposeCts.Cancel();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(DelegateSharpLinkEndpointResolver));
    }
}

/// <summary>Configures DNS endpoint discovery refresh behavior.</summary>
public sealed class SharpLinkDnsResolverOptions
{
    /// <summary>Gets or sets the default refresh interval when DNS TTL is not available from the platform.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the smallest permitted refresh interval.</summary>
    public TimeSpan MinimumRefreshInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the largest permitted refresh interval.</summary>
    public TimeSpan MaximumRefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets the symmetric random jitter ratio from zero through one.</summary>
    public double JitterRatio { get; set; } = 0.2;

    /// <summary>Gets or sets an optional IP address-family filter. Null accepts A and AAAA records.</summary>
    public AddressFamily? AddressFamily { get; set; }

    internal SharpLinkDnsResolverOptions CloneValidated()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RefreshInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MinimumRefreshInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaximumRefreshInterval, TimeSpan.Zero);
        if (MinimumRefreshInterval > MaximumRefreshInterval)
            throw new ArgumentException("MinimumRefreshInterval cannot exceed MaximumRefreshInterval.");
        if (JitterRatio is < 0 or > 1 || double.IsNaN(JitterRatio))
            throw new ArgumentOutOfRangeException(nameof(JitterRatio));
        if (AddressFamily is { } addressFamily &&
            addressFamily is not (System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6))
        {
            throw new ArgumentOutOfRangeException(nameof(AddressFamily));
        }

        return new SharpLinkDnsResolverOptions
        {
            RefreshInterval = RefreshInterval,
            MinimumRefreshInterval = MinimumRefreshInterval,
            MaximumRefreshInterval = MaximumRefreshInterval,
            JitterRatio = JitterRatio,
            AddressFamily = AddressFamily
        };
    }
}

internal interface ISharpLinkDnsQuery
{
    ValueTask<IPAddress[]> QueryAsync(string host, CancellationToken cancellationToken);
}

internal sealed class BclSharpLinkDnsQuery : ISharpLinkDnsQuery
{
    public static readonly BclSharpLinkDnsQuery Instance = new();

    private BclSharpLinkDnsQuery()
    {
    }

    public async ValueTask<IPAddress[]> QueryAsync(string host, CancellationToken cancellationToken)
        => await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
}

/// <summary>Resolves A and AAAA records into a dynamic TCP endpoint topology.</summary>
/// <remarks>
/// DNS record order is ignored. Stable endpoint IDs are derived from the original host, port, address
/// family, and normalized IP address, while the original host remains the default TLS authority.
/// </remarks>
public sealed class SharpLinkDnsEndpointResolver : ISharpLinkEndpointResolver
{
    private readonly string _host;
    private readonly int _port;
    private readonly SharpLinkDnsResolverOptions _options;
    private readonly ISharpLinkDnsQuery _query;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Lock _gate = new();
    private SharpLinkEndpointSnapshot? _lastSnapshot;
    private string[] _lastEndpointKeys = [];
    private long _version;
    private int _disposed;

    /// <summary>Initializes a DNS endpoint resolver.</summary>
    /// <param name="host">The non-empty DNS host name retained as endpoint authority.</param>
    /// <param name="port">The TCP port from 1 through 65535.</param>
    /// <param name="options">Optional options copied and frozen by the resolver.</param>
    public SharpLinkDnsEndpointResolver(
        string host,
        int port,
        SharpLinkDnsResolverOptions? options = null)
        : this(host, port, (options ?? new SharpLinkDnsResolverOptions()).CloneValidated(), BclSharpLinkDnsQuery.Instance)
    {
    }

    internal SharpLinkDnsEndpointResolver(
        string host,
        int port,
        SharpLinkDnsResolverOptions options,
        ISharpLinkDnsQuery query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        _host = host;
        _port = port;
        _options = (options ?? throw new ArgumentNullException(nameof(options))).CloneValidated();
        _query = query ?? throw new ArgumentNullException(nameof(query));
    }

    /// <inheritdoc />
    public async ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        try
        {
            var result = await QueryAndCreateSnapshotAsync(linked.Token).ConfigureAwait(false);
            return result.Snapshot;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            throw;
        }
        catch when (TryGetLastSnapshot(out var lastSnapshot))
        {
            return lastSnapshot;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        while (true)
        {
            await Task.Delay(GetRefreshDelay(), linked.Token).ConfigureAwait(false);
            SharpLinkEndpointSnapshot? published = null;
            try
            {
                var result = await QueryAndCreateSnapshotAsync(linked.Token).ConfigureAwait(false);
                if (result.Changed)
                    published = result.Snapshot;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                yield break;
            }
            catch
            {
                // DNS failure intentionally retains the last successful topology until the next refresh.
            }
            if (published is not null)
                yield return published;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _disposeCts.Cancel();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<(SharpLinkEndpointSnapshot Snapshot, bool Changed)> QueryAndCreateSnapshotAsync(
        CancellationToken cancellationToken)
    {
        var addresses = await _query.QueryAsync(_host, cancellationToken).ConfigureAwait(false);
        var values = new List<(string Key, IPAddress Address)>(addresses.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < addresses.Length; index++)
        {
            var address = addresses[index];
            var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            if (_options.AddressFamily is { } family && normalized.AddressFamily != family)
                continue;
            if (normalized.AddressFamily is not (System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6))
                continue;
            var key = $"{normalized.AddressFamily}:{normalized}";
            if (seen.Add(key))
                values.Add((key, normalized));
        }
        values.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        var keys = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
            keys[index] = values[index].Key;

        lock (_gate)
        {
            if (_lastSnapshot is not null && keys.AsSpan().SequenceEqual(_lastEndpointKeys, StringComparer.Ordinal))
                return (_lastSnapshot, false);

            var endpoints = new SharpLinkEndpoint[values.Count];
            for (var index = 0; index < values.Count; index++)
            {
                var value = values[index];
                endpoints[index] = new SharpLinkEndpoint
                {
                    Id = $"dns:{GetHostHash(_host)}:{_port}:{value.Key}",
                    Address = new SharpLinkTcpAddress(value.Address.ToString(), _port),
                    Authority = _host
                };
            }
            var snapshot = new SharpLinkEndpointSnapshot(++_version, endpoints);
            _lastEndpointKeys = keys;
            _lastSnapshot = snapshot;
            return (snapshot, true);
        }
    }

    private static string GetHostHash(string host)
        => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(host)).AsSpan(0, 16));

    private TimeSpan GetRefreshDelay()
    {
        var baseInterval = _options.RefreshInterval;
        if (baseInterval < _options.MinimumRefreshInterval)
            baseInterval = _options.MinimumRefreshInterval;
        if (baseInterval > _options.MaximumRefreshInterval)
            baseInterval = _options.MaximumRefreshInterval;
        if (_options.JitterRatio == 0)
            return baseInterval;

        var multiplier = 1 - _options.JitterRatio + Random.Shared.NextDouble() * _options.JitterRatio * 2;
        var jittered = TimeSpan.FromTicks((long)(baseInterval.Ticks * multiplier));
        return jittered < _options.MinimumRefreshInterval ? _options.MinimumRefreshInterval :
            jittered > _options.MaximumRefreshInterval ? _options.MaximumRefreshInterval : jittered;
    }

    private bool TryGetLastSnapshot(out SharpLinkEndpointSnapshot snapshot)
    {
        lock (_gate)
        {
            snapshot = _lastSnapshot!;
            return snapshot is not null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SharpLinkDnsEndpointResolver));
    }
}
