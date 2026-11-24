namespace SharpLink.IntegrationTests;

internal static class PreparedTestClientExtensions
{
    internal static async ValueTask<TContract> PrepareTestServiceAsync<TContract>(this ISharpLinkClient client)
        where TContract : IService
    {
        var proxy = client.CreateProxy<TContract>();
        await proxy.PrepareAsync(TimeSpan.FromSeconds(5));
        return proxy.Service;
    }
}
