using SharpLink.Abstractions;
using SharpLink.Sdk;
using System.Runtime.CompilerServices;

namespace PhysicalTcp;

[RpcContract]
public interface IScalarStream : IService
{
    IAsyncEnumerable<int> DownloadAsync(int count, CancellationToken cancellationToken);
    IAsyncEnumerable<int> NormalCompletionAsync(int count, CancellationToken cancellationToken);
    IAsyncEnumerable<int> CorrectnessAsync(int scenario, CancellationToken cancellationToken);
}

[RpcService]
public sealed class ScalarStreamService : IScalarStream
{
    // Read by the cold controller only; the timed business method has no instrumentation.
    public static int Started;
    public static int Completed;
    public static int Active;
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

    public async IAsyncEnumerable<int> CorrectnessAsync(int scenario, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Started);
        Interlocked.Increment(ref Active);
        try
        {
            if (scenario == 1)
                throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "fixture-before-first-item");
            yield return 0;
            if (scenario == 2)
                throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "fixture-after-first-item");
            if (scenario == 7)
                throw new InvalidOperationException("fixture-unhandled-service-exception");
            // Cancellation/early-break/consumer-exception/explicit-dispose must cancel the
            // actual server producer, not merely dispose a stream that already completed.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref Active);
            Interlocked.Increment(ref Completed);
        }
    }
}
