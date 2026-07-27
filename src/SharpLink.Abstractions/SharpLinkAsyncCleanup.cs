namespace SharpLink.Abstractions;

internal static class SharpLinkAsyncCleanup
{
    internal static void DisposeSynchronously(IAsyncDisposable resource)
    {
        Task.Run(async () => await resource.DisposeAsync().ConfigureAwait(false))
            .GetAwaiter()
            .GetResult();
    }
}
