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

    /// <summary>Gets or sets the number of connections established by <c>ConnectAsync</c>.</summary>
    public int MinConnections { get; set; } = 1;

    /// <summary>Gets or sets the maximum number of connections created under pressure.</summary>
    public int MaxConnections { get; set; } = 1;

    /// <summary>Validates the configured pool bounds.</summary>
    public void Validate()
    {
        if (MinConnections is < 1 or > MaximumConnections)
            throw new ArgumentOutOfRangeException(nameof(MinConnections));
        if (MaxConnections is < 1 or > MaximumConnections)
            throw new ArgumentOutOfRangeException(nameof(MaxConnections));
        if (MaxConnections < MinConnections)
        {
            throw new ArgumentException(
                "MaxConnections cannot be smaller than MinConnections.",
                nameof(MaxConnections));
        }
    }

    internal SharpLinkConnectionPoolOptions CloneValidated()
    {
        Validate();
        return new SharpLinkConnectionPoolOptions
        {
            MinConnections = MinConnections,
            MaxConnections = MaxConnections
        };
    }
}
