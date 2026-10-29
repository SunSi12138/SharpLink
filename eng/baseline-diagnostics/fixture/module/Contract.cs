using SharpLink.Abstractions;
using SharpLink.Sdk;
using System.Runtime.CompilerServices;

namespace PhysicalTcp;

[RpcContract]
public interface IScalarStream : IService
{
    IAsyncEnumerable<int> DownloadAsync(int count, CancellationToken cancellationToken);
    IAsyncEnumerable<int> NormalCompletionAsync(int count, CancellationToken cancellationToken);
}

[RpcService]
public sealed class ScalarStreamService : IScalarStream
{
    // Read by the cold controller only; the timed business method has no instrumentation.
    public static int NormalStarted;
    public static int NormalCompleted;
    public static int NormalActive;

    public async IAsyncEnumerable<int> DownloadAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return i;
            await Task.CompletedTask;
        }
    }

    // Cold-only observer. Never called by the measured Operation method.
    public async IAsyncEnumerable<int> NormalCompletionAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref NormalStarted);
        Interlocked.Increment(ref NormalActive);
        try
        {
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return i;
                await Task.CompletedTask;
            }
        }
        finally
        {
            Interlocked.Decrement(ref NormalActive);
            Interlocked.Increment(ref NormalCompleted);
        }
    }

}
