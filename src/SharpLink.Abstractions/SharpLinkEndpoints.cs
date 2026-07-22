namespace SharpLink.Abstractions;

/// <summary>Represents an immutable transport address used by a SharpLink endpoint.</summary>
/// <remarks>
/// Custom address types must be immutable and provide stable value equality. Transport factories
/// interpret custom address types; the SharpLink core does not switch on every possible subtype.
/// </remarks>
public abstract record SharpLinkTransportAddress;

/// <summary>Represents a TCP host and port.</summary>
/// <param name="host">A non-empty host name, IPv4 address, or IPv6 address.</param>
/// <param name="port">A TCP port from 1 through 65535.</param>
public sealed record SharpLinkTcpAddress : SharpLinkTransportAddress
{
    /// <summary>Initializes a TCP address.</summary>
    /// <exception cref="ArgumentException"><paramref name="host"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is outside the TCP port range.</exception>
    public SharpLinkTcpAddress(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        Host = host;
        Port = port;
    }

    /// <summary>Gets the host name or IP address.</summary>
    public string Host { get; }

    /// <summary>Gets the TCP port.</summary>
    public int Port { get; }
}

/// <summary>Represents a Unix-domain socket path.</summary>
/// <param name="path">A non-empty socket path.</param>
public sealed record SharpLinkUnixDomainSocketAddress : SharpLinkTransportAddress
{
    /// <summary>Initializes a Unix-domain socket address.</summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or whitespace.</exception>
    public SharpLinkUnixDomainSocketAddress(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    /// <summary>Gets the socket path.</summary>
    public string Path { get; }
}

/// <summary>Represents a named-pipe server and pipe name.</summary>
/// <param name="pipeName">A non-empty pipe name.</param>
/// <param name="serverName">The pipe server name. The default is the local server.</param>
public sealed record SharpLinkNamedPipeAddress : SharpLinkTransportAddress
{
    /// <summary>Initializes a named-pipe address.</summary>
    /// <exception cref="ArgumentException"><paramref name="pipeName"/> or <paramref name="serverName"/> is null, empty, or whitespace.</exception>
    public SharpLinkNamedPipeAddress(string pipeName, string serverName = ".")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        PipeName = pipeName;
        ServerName = serverName;
    }

    /// <summary>Gets the pipe name.</summary>
    public string PipeName { get; }

    /// <summary>Gets the pipe server name.</summary>
    public string ServerName { get; }
}

/// <summary>Represents a same-user, same-machine shared-memory transport name.</summary>
/// <param name="name">A non-empty logical shared-memory name.</param>
public sealed record SharpLinkSharedMemoryAddress : SharpLinkTransportAddress
{
    /// <summary>Initializes a shared-memory address.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty, or whitespace.</exception>
    public SharpLinkSharedMemoryAddress(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Gets the logical shared-memory name.</summary>
    public string Name { get; }
}

/// <summary>Represents a one-time anonymous-pipe handle offer.</summary>
/// <remarks>
/// The handle values are intentionally never included in the string representation, diagnostics, or exceptions.
/// A single offer cannot be reused to reconnect or create an automatic multi-endpoint pool.
/// </remarks>
/// <param name="inHandle">A non-empty inbound anonymous-pipe handle.</param>
/// <param name="outHandle">A non-empty outbound anonymous-pipe handle.</param>
public sealed record SharpLinkAnonymousPipeAddress : SharpLinkTransportAddress
{
    /// <summary>Initializes an anonymous-pipe address.</summary>
    /// <exception cref="ArgumentException"><paramref name="inHandle"/> or <paramref name="outHandle"/> is null, empty, or whitespace.</exception>
    public SharpLinkAnonymousPipeAddress(string inHandle, string outHandle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(outHandle);
        InHandle = inHandle;
        OutHandle = outHandle;
    }

    /// <summary>Gets the inbound handle. Do not log this value.</summary>
    public string InHandle { get; }

    /// <summary>Gets the outbound handle. Do not log this value.</summary>
    public string OutHandle { get; }

    /// <inheritdoc />
    public override string ToString() => "SharpLinkAnonymousPipeAddress { Handles = [redacted] }";
}

/// <summary>Describes one logical endpoint in a static or dynamic SharpLink topology.</summary>
/// <remarks>
/// The client copies and freezes endpoints during <c>Build()</c> or snapshot acceptance. Attributes
/// are intended for selection and diagnostics only and must not determine connection-critical factory settings.
/// </remarks>
public sealed class SharpLinkEndpoint
{
    /// <summary>Gets or initializes the unique logical endpoint identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Gets or initializes the immutable transport address.</summary>
    public required SharpLinkTransportAddress Address { get; init; }

    /// <summary>Gets or initializes the optional logical authority used by the transport, such as TLS SNI.</summary>
    public string? Authority { get; init; }

    /// <summary>Gets or initializes immutable selection and diagnostics attributes.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();
}

/// <summary>Creates a transport factory owned by a SharpLink client for one endpoint generation.</summary>
/// <param name="endpoint">The frozen endpoint for which to create a factory.</param>
/// <returns>An independently disposable factory for the endpoint generation.</returns>
public delegate IClientTransportFactory SharpLinkEndpointTransportFactory(SharpLinkEndpoint endpoint);

/// <summary>Selects an endpoint from one immutable Ready candidate snapshot.</summary>
/// <remarks>
/// Implementations must be synchronous, non-blocking, allocation-free on their normal path, and must not
/// modify topology. Return an index in <paramref name="context"/>; invalid, excluded, or unavailable results
/// fail only the current call with <see cref="SharpLinkErrorCode.FailedPrecondition"/>.
/// </remarks>
public interface ISharpLinkEndpointSelector
{
    /// <summary>Selects an index from the current Ready candidate snapshot.</summary>
    /// <param name="context">The current candidate snapshot and exclusion mask.</param>
    /// <returns>An index from zero through <c>context.Count - 1</c>.</returns>
    int Select(in SharpLinkEndpointSelectionContext context);
}

/// <summary>Provides a zero-allocation read-only view of one endpoint selection snapshot.</summary>
public readonly ref struct SharpLinkEndpointSelectionContext
{
    private readonly ReadOnlySpan<SharpLinkEndpointCandidate> _candidates;

    /// <summary>Initializes a selection context over one candidate snapshot.</summary>
    /// <param name="candidates">The immutable candidates available to the current call.</param>
    /// <param name="excludedMask">Bits identifying candidates already excluded by this call.</param>
    public SharpLinkEndpointSelectionContext(
        ReadOnlySpan<SharpLinkEndpointCandidate> candidates,
        ulong excludedMask)
    {
        _candidates = candidates;
        ExcludedMask = excludedMask;
    }

    /// <summary>Gets the candidate count.</summary>
    public int Count => _candidates.Length;

    /// <summary>Gets bits for candidates excluded by the current call.</summary>
    public ulong ExcludedMask { get; }

    /// <summary>Gets a candidate by its snapshot index.</summary>
    /// <param name="index">An index from zero through <see cref="Count"/> minus one.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the snapshot.</exception>
    public SharpLinkEndpointCandidate this[int index] => _candidates[index];
}

/// <summary>Describes a Ready endpoint visible to one selector invocation.</summary>
/// <remarks>
/// The endpoint identity and generation belong to the immutable candidate snapshot. Connection and
/// active-call counts are read from endpoint-owned atomic state, so a selector never needs a rebuilt
/// candidate array merely because in-flight work changed.
/// </remarks>
public readonly record struct SharpLinkEndpointCandidate
{
    private readonly int _readyConnectionCount;
    private readonly int _activeCallCount;
    private readonly Func<int>? _readyConnectionCountProvider;
    private readonly Func<int>? _activeCallCountProvider;

    /// <summary>Initializes a candidate with fixed diagnostic counts.</summary>
    /// <param name="endpoint">The frozen endpoint.</param>
    /// <param name="readyConnectionCount">The Ready connection count.</param>
    /// <param name="activeCallCount">The active-call count.</param>
    /// <param name="generation">The client-assigned endpoint generation.</param>
    public SharpLinkEndpointCandidate(
        SharpLinkEndpoint endpoint,
        int readyConnectionCount,
        int activeCallCount,
        long generation)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _readyConnectionCount = readyConnectionCount;
        _activeCallCount = activeCallCount;
        _readyConnectionCountProvider = null;
        _activeCallCountProvider = null;
        Generation = generation;
    }

    internal SharpLinkEndpointCandidate(
        SharpLinkEndpoint endpoint,
        Func<int> readyConnectionCountProvider,
        Func<int> activeCallCountProvider,
        long generation)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _readyConnectionCount = 0;
        _activeCallCount = 0;
        _readyConnectionCountProvider = readyConnectionCountProvider ?? throw new ArgumentNullException(nameof(readyConnectionCountProvider));
        _activeCallCountProvider = activeCallCountProvider ?? throw new ArgumentNullException(nameof(activeCallCountProvider));
        Generation = generation;
    }

    /// <summary>Gets the frozen endpoint identity and attributes.</summary>
    public SharpLinkEndpoint Endpoint { get; }

    /// <summary>Gets the current number of Ready connections for this endpoint.</summary>
    public int ReadyConnectionCount => _readyConnectionCountProvider?.Invoke() ?? _readyConnectionCount;

    /// <summary>Gets the current active-call count for this endpoint.</summary>
    public int ActiveCallCount => _activeCallCountProvider?.Invoke() ?? _activeCallCount;

    /// <summary>Gets the client-assigned endpoint generation.</summary>
    public long Generation { get; }
}
