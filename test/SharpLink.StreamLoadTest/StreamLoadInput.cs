using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.StreamLoadTest;

internal sealed class StreamLoadInput<T>(T[] values, CancellationToken caller) : IAsyncEnumerable<T>
{
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken producer = default)
        => ReadAsync(producer);

    private async IAsyncEnumerator<T> ReadAsync(CancellationToken producer)
    {
        // The returned enumerator owns the link for this production run.
        using var link = CancellationTokenSource.CreateLinkedTokenSource(caller, producer);
        var token = link.Token;
        foreach (var value in values)
        {
            token.ThrowIfCancellationRequested();
            yield return value;
            await Task.CompletedTask;
        }
    }
}
