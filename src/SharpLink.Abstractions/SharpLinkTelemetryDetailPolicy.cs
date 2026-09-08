namespace SharpLink.Abstractions;

/// <summary>Controls SharpLink-owned optional telemetry detail without replacing OpenTelemetry infrastructure.</summary>
public enum SharpLinkTelemetryDetailMode : byte
{
    /// <summary>Emits stable baseline RPC telemetry only.</summary>
    Basic = 0,

    /// <summary>Adds SharpLink diagnostic trace detail such as request identity and lifetime-source enrichment.</summary>
    Detailed = 1
}

/// <summary>Describes one atomically published SharpLink telemetry-detail policy generation.</summary>
public readonly record struct SharpLinkTelemetryDetailPolicySnapshot(
    ulong Generation,
    SharpLinkTelemetryDetailMode Mode)
{
    /// <summary>Gets whether optional SharpLink diagnostic trace detail is enabled.</summary>
    public bool Detailed => Mode == SharpLinkTelemetryDetailMode.Detailed;
}

internal interface ISharpLinkTelemetryDetailRuntime
{
    SharpLinkTelemetryDetailPolicySnapshot GetTelemetryDetailPolicySnapshot();
    void UpdateTelemetryDetailPolicy(SharpLinkTelemetryDetailMode mode);
}

/// <summary>Runtime helpers for replacing SharpLink-owned optional telemetry detail.</summary>
public static class SharpLinkTelemetryDetailExtensions
{
    /// <summary>Gets the currently published Client telemetry-detail generation.</summary>
    public static SharpLinkTelemetryDetailPolicySnapshot GetTelemetryDetailPolicySnapshot(this ISharpLinkClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client is ISharpLinkTelemetryDetailRuntime runtime
            ? runtime.GetTelemetryDetailPolicySnapshot()
            : throw new NotSupportedException("This ISharpLinkClient implementation does not support runtime telemetry-detail policy updates.");
    }

    /// <summary>Publishes the telemetry-detail mode captured by future Client logical calls.</summary>
    public static void UpdateTelemetryDetailPolicy(this ISharpLinkClient client, SharpLinkTelemetryDetailMode mode)
    {
        ArgumentNullException.ThrowIfNull(client);
        Validate(mode);
        if (client is not ISharpLinkTelemetryDetailRuntime runtime)
            throw new NotSupportedException("This ISharpLinkClient implementation does not support runtime telemetry-detail policy updates.");
        runtime.UpdateTelemetryDetailPolicy(mode);
    }

    /// <summary>Gets the currently published Server telemetry-detail generation.</summary>
    public static SharpLinkTelemetryDetailPolicySnapshot GetTelemetryDetailPolicySnapshot(this ISharpLinkServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server is ISharpLinkTelemetryDetailRuntime runtime
            ? runtime.GetTelemetryDetailPolicySnapshot()
            : throw new NotSupportedException("This ISharpLinkServer implementation does not support runtime telemetry-detail policy updates.");
    }

    /// <summary>Publishes the telemetry-detail mode captured by future Server call dispatches.</summary>
    public static void UpdateTelemetryDetailPolicy(this ISharpLinkServer server, SharpLinkTelemetryDetailMode mode)
    {
        ArgumentNullException.ThrowIfNull(server);
        Validate(mode);
        if (server is not ISharpLinkTelemetryDetailRuntime runtime)
            throw new NotSupportedException("This ISharpLinkServer implementation does not support runtime telemetry-detail policy updates.");
        runtime.UpdateTelemetryDetailPolicy(mode);
    }

    internal static void Validate(SharpLinkTelemetryDetailMode mode)
    {
        if (mode is not SharpLinkTelemetryDetailMode.Basic and not SharpLinkTelemetryDetailMode.Detailed)
            throw new ArgumentOutOfRangeException(nameof(mode));
    }
}

internal sealed class SharpLinkTelemetryDetailGeneration(
    ulong generation,
    SharpLinkTelemetryDetailMode mode)
{
    internal ulong Generation { get; } = generation;
    internal SharpLinkTelemetryDetailMode Mode { get; } = mode;
}
