namespace SharpLink.Client;

internal enum ClientRequestTimeoutPolicyState : byte
{
    Unspecified,
    Enabled,
    Disabled
}

internal enum ClientRequestTimeoutSource : byte
{
    None,
    Recommended,
    Custom
}

internal readonly record struct ClientRequestTimeoutPolicy(
    ClientRequestTimeoutPolicyState State,
    TimeSpan Timeout,
    ClientRequestTimeoutSource Source)
{
    internal static ClientRequestTimeoutPolicy Unspecified => default;

    internal static ClientRequestTimeoutPolicy Recommended(TimeSpan timeout)
        => CreateEnabled(timeout, ClientRequestTimeoutSource.Recommended);

    internal static ClientRequestTimeoutPolicy Custom(TimeSpan timeout)
        => CreateEnabled(timeout, ClientRequestTimeoutSource.Custom);

    internal static ClientRequestTimeoutPolicy Disabled
        => new(ClientRequestTimeoutPolicyState.Disabled, default, ClientRequestTimeoutSource.None);

    internal bool IsSpecified => State != ClientRequestTimeoutPolicyState.Unspecified;

    internal bool HasTimeout => State == ClientRequestTimeoutPolicyState.Enabled;

    internal TimeSpan? TimeoutOrNull => HasTimeout ? Timeout : null;

    private static ClientRequestTimeoutPolicy CreateEnabled(
        TimeSpan timeout,
        ClientRequestTimeoutSource source)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        return new ClientRequestTimeoutPolicy(ClientRequestTimeoutPolicyState.Enabled, timeout, source);
    }
}

internal enum ClientCallLifetimeSource : byte
{
    None,
    MethodTimeout,
    ClientRecommendedTimeout,
    ClientCustomTimeout,
    InheritedTimeBudget
}

internal static class ClientCallLifetimeSourceExtensions
{
    internal static ClientCallLifetimeSource ToLifetimeSource(this ClientRequestTimeoutSource source)
        => source switch
        {
            ClientRequestTimeoutSource.Recommended => ClientCallLifetimeSource.ClientRecommendedTimeout,
            ClientRequestTimeoutSource.Custom => ClientCallLifetimeSource.ClientCustomTimeout,
            _ => ClientCallLifetimeSource.None
        };

    internal static string? ToTelemetryValue(this ClientCallLifetimeSource source)
        => source switch
        {
            ClientCallLifetimeSource.MethodTimeout => "method_timeout",
            ClientCallLifetimeSource.ClientRecommendedTimeout => "client_recommended_timeout",
            ClientCallLifetimeSource.ClientCustomTimeout => "client_custom_timeout",
            ClientCallLifetimeSource.InheritedTimeBudget => "inherited_time_budget",
            _ => null
        };
}
