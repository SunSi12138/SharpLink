namespace SharpLink.Abstractions;

/// <summary>Helpers used by generated proxies to preserve Task/ValueTask contract shapes.</summary>
public static class RpcInvocationExtensions
{
    /// <summary>Awaits an internal acknowledgement value without exposing it to the contract.</summary>
    public static async ValueTask AsVoid<T>(this ValueTask<T> pending)
    {
        _ = await pending.ConfigureAwait(false);
    }
}
