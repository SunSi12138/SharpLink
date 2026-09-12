namespace SharpLink.Client;

internal interface ISharpLinkReconnectJitter
{
    TimeSpan AddQuarterWindow(int baseDelayMilliseconds);

    TimeSpan ScaleTwentyPercent(int baseDelayMilliseconds);

    TimeSpan Apply(TimeSpan baseDelay, double minimumFactor, double maximumFactor)
    {
        if (minimumFactor == 1d && maximumFactor == 1.25d && baseDelay.TotalMilliseconds <= int.MaxValue)
            return AddQuarterWindow(checked((int)Math.Ceiling(baseDelay.TotalMilliseconds)));
        if (minimumFactor == 0.8d && maximumFactor == 1.2d && baseDelay.TotalMilliseconds <= int.MaxValue)
            return ScaleTwentyPercent(checked((int)Math.Ceiling(baseDelay.TotalMilliseconds)));
        if (minimumFactor == maximumFactor)
            return Scale(baseDelay, minimumFactor);
        throw new NotSupportedException(
            "This reconnect jitter test seam does not implement arbitrary jitter bounds.");
    }

    private static TimeSpan Scale(TimeSpan delay, double factor)
    {
        var ticks = delay.Ticks * factor;
        if (!double.IsFinite(ticks) || ticks >= TimeSpan.MaxValue.Ticks)
            return TimeSpan.MaxValue;
        return TimeSpan.FromTicks(Math.Max(1L, (long)Math.Round(ticks, MidpointRounding.AwayFromZero)));
    }
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

    public TimeSpan Apply(TimeSpan baseDelay, double minimumFactor, double maximumFactor)
    {
        var factor = minimumFactor == maximumFactor
            ? minimumFactor
            : minimumFactor + Random.Shared.NextDouble() * (maximumFactor - minimumFactor);
        var ticks = baseDelay.Ticks * factor;
        if (!double.IsFinite(ticks) || ticks >= TimeSpan.MaxValue.Ticks)
            return TimeSpan.MaxValue;
        return TimeSpan.FromTicks(Math.Max(1L, (long)Math.Round(ticks, MidpointRounding.AwayFromZero)));
    }
}
