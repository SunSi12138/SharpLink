namespace SharpLink.Abstractions;

internal static class SharpLinkTimer
{
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromMilliseconds(int.MaxValue);

    internal static async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        while (delay > MaximumDelay)
        {
            await Task.Delay(MaximumDelay, cancellationToken).ConfigureAwait(false);
            delay -= MaximumDelay;
        }
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }
}
