using System.Runtime.CompilerServices;
using SharpLink.Abstractions;
using SharpLink.Sdk;

namespace SharpLink.WriterOwnedTest.Contracts;

// Kept in a generator-enabled assembly. UnitTests intentionally does not run the RPC
// generator because it also contains malformed-contract/metadata regression fixtures.
[RpcContract]
public interface IWriterOwnedTransportContract : IService
{
    ValueTask<int> Ping(int value, CancellationToken cancellationToken);
    IAsyncEnumerable<int> Download(int count, CancellationToken cancellationToken);
    ValueTask<int> Upload(IAsyncEnumerable<int> values, CancellationToken cancellationToken);
    IAsyncEnumerable<int> Duplex(IAsyncEnumerable<int> values, CancellationToken cancellationToken);
    ValueTask<int> Merge(IAsyncEnumerable<int> left, IAsyncEnumerable<int> right, CancellationToken cancellationToken);
    IAsyncEnumerable<byte[]> ByteDuplex(IAsyncEnumerable<byte[]> values, CancellationToken cancellationToken);
}

[RpcService]
public sealed class WriterOwnedTransportService : IWriterOwnedTransportContract
{
    public ValueTask<int> Ping(int value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(value);
    }

    public async IAsyncEnumerable<int> Download(int count, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return index;
            if ((index & 15) == 15) await Task.Yield();
        }
    }

    public async ValueTask<int> Upload(IAsyncEnumerable<int> values, CancellationToken cancellationToken)
    {
        var sum = 0;
        await foreach (var value in values.WithCancellation(cancellationToken)) sum = checked(sum + value);
        return sum;
    }

    public async IAsyncEnumerable<int> Duplex(IAsyncEnumerable<int> values,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var value in values.WithCancellation(cancellationToken)) yield return value + 1;
    }

    public async ValueTask<int> Merge(IAsyncEnumerable<int> left, IAsyncEnumerable<int> right,
        CancellationToken cancellationToken)
        => await Upload(left, cancellationToken) + await Upload(right, cancellationToken);

    public async IAsyncEnumerable<byte[]> ByteDuplex(IAsyncEnumerable<byte[]> values,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var value in values.WithCancellation(cancellationToken)) yield return value;
    }
}
