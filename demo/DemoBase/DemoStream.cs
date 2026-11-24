using System.Net;
using System.Net.Sockets;

namespace DemoBase;

public static class DemoStream
{
    public static IAsyncEnumerable<T> ToAsyncEnumerable<T>(
        IEnumerable<T> values,
        int delayMs = 5,
        CancellationToken cancellationToken = default)
        => new OwnedSequence<T>(values, delayMs, cancellationToken);

    private sealed class OwnedSequence<T>(IEnumerable<T> values, int delayMs, CancellationToken caller)
        : IAsyncEnumerable<T>
    {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken producer = default)
            => EnumerateAsync(producer);

        private async IAsyncEnumerator<T> EnumerateAsync(CancellationToken producer)
        {
            // The enumerator owns its link, even when a different thread starts production.
            using var lifetime = caller.CanBeCanceled && producer.CanBeCanceled && caller != producer
                ? CancellationTokenSource.CreateLinkedTokenSource(caller, producer)
                : null;
            var token = lifetime?.Token ?? (producer.CanBeCanceled ? producer : caller);
            foreach (var value in values)
            {
                token.ThrowIfCancellationRequested();
                yield return value;
                if (delayMs > 0)
                    await Task.Delay(delayMs, token);
            }
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
