using SharpLink.Abstractions;
using SharpLink.Sdk;
using System.Runtime.CompilerServices;

namespace PhysicalTcp;

[RpcContract]
public interface IScalarStream : IService
{
    IAsyncEnumerable<int> DownloadAsync(int count, CancellationToken cancellationToken);
}

[RpcService]
public sealed class ScalarStreamService : IScalarStream
{
    // Same business loop as the original static-intercepted fixture. No service
    // counters, timestamps, extra control RPCs, artificial yields or drain barriers.
    public async IAsyncEnumerable<int> DownloadAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return i;
            await Task.CompletedTask;
        }
    }
}
