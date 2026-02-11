using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace DemoBase;

public static class DemoStream
{
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(
        IEnumerable<T> values,
        int delayMs = 5,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
            if (delayMs > 0)
                await Task.Delay(delayMs, cancellationToken);
        }
    }

    public static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> stream, CancellationToken cancellationToken = default)
    {
        var list = new List<T>();
        await foreach (var item in stream.WithCancellation(cancellationToken))
        {
            list.Add(item);
        }

        return list;
    }

    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
