namespace SharpLink.Client;

/// <summary>Provides the two bounded jitter shapes used by reconnect workers.</summary>
internal interface ISharpLinkReconnectJitter
{
    TimeSpan AddQuarterWindow(int baseDelayMilliseconds);

    TimeSpan ScaleTwentyPercent(int baseDelayMilliseconds);
}

internal sealed class RandomSharpLinkReconnectJitter : ISharpLinkReconnectJitter
{
    internal static RandomSharpLinkReconnectJitter Instance { get; } = new();

    private RandomSharpLinkReconnectJitter()
    {
    }

    public TimeSpan AddQuarterWindow(int baseDelayMilliseconds)
        => TimeSpan.FromMilliseconds(
            baseDelayMilliseconds + Random.Shared.Next(baseDelayMilliseconds / 4 + 1));

    public TimeSpan ScaleTwentyPercent(int baseDelayMilliseconds)
        => TimeSpan.FromMilliseconds(
            baseDelayMilliseconds * (0.8 + Random.Shared.NextDouble() * 0.4));
}
