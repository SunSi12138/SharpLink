namespace SharpLink.Client;

/// <summary>Configures the bounded connection pool owned by one SharpLink client endpoint.</summary>
/// <example>
/// <code>
/// var client = SharpClientBuilder.Create()
///     .UseConnectionPool(options =&gt;
///     {
///         options.MinConnections = 1;
///         options.MaxConnections = 4;
///     });
/// </code>
/// </example>
public sealed class SharpLinkConnectionPoolOptions
{
    /// <summary>The largest supported pool size for one endpoint.</summary>
    public const int MaximumConnections = 64;

    private long _sizing = Pack(MinConnectionsDefault, MaxConnectionsDefault);
    private const int MinConnectionsDefault = 1;
    private const int MaxConnectionsDefault = 1;

    /// <summary>Gets or sets the number of connections established by <c>ConnectAsync</c>.</summary>
    public int MinConnections
    {
        get => UnpackMin(Volatile.Read(ref _sizing));
        set => UpdateBuilderSizing(minConnections: value, maxConnections: null);
    }

    /// <summary>Gets or sets the maximum number of connections created under pressure.</summary>
    public int MaxConnections
    {
        get => UnpackMax(Volatile.Read(ref _sizing));
        set => UpdateBuilderSizing(minConnections: null, maxConnections: value);
    }

    /// <summary>Validates the configured pool bounds.</summary>
    public void Validate()
    {
        var sizing = Volatile.Read(ref _sizing);
        var minConnections = UnpackMin(sizing);
        var maxConnections = UnpackMax(sizing);
        if (minConnections is < 1 or > MaximumConnections)
            throw new ArgumentOutOfRangeException(nameof(MinConnections));
        if (maxConnections is < 1 or > MaximumConnections)
            throw new ArgumentOutOfRangeException(nameof(MaxConnections));
        if (maxConnections < minConnections)
        {
            throw new ArgumentException(
                "MaxConnections cannot be smaller than MinConnections.",
                nameof(MaxConnections));
        }
    }

    internal SharpLinkConnectionPoolOptions CloneValidated()
    {
        Validate();
        var sizing = Volatile.Read(ref _sizing);
        var clone = new SharpLinkConnectionPoolOptions();
        clone.PublishRuntimeSizing(UnpackMin(sizing), UnpackMax(sizing));
        return clone;
    }

    internal void PublishRuntimeSizing(int minConnections, int maxConnections)
        => Interlocked.Exchange(ref _sizing, Pack(minConnections, maxConnections));

    private void UpdateBuilderSizing(int? minConnections, int? maxConnections)
    {
        while (true)
        {
            var current = Volatile.Read(ref _sizing);
            var next = Pack(
                minConnections ?? UnpackMin(current),
                maxConnections ?? UnpackMax(current));
            if (Interlocked.CompareExchange(ref _sizing, next, current) == current)
                return;
        }
    }

    private static long Pack(int minConnections, int maxConnections)
        => unchecked((long)((ulong)(uint)maxConnections << 32 | (uint)minConnections));

    private static int UnpackMin(long sizing) => unchecked((int)(uint)sizing);
    private static int UnpackMax(long sizing) => unchecked((int)(uint)((ulong)sizing >> 32));
}
