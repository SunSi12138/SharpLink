using SharpLink.Abstractions;
using SharpLink.Sdk;
using System.Runtime.CompilerServices;

namespace PhysicalTcp;

[RpcContract]
public interface IScalarStream : IService
{
    IAsyncEnumerable<int> DownloadAsync(int count, CancellationToken cancellationToken);
    IAsyncEnumerable<int> CorrectnessAsync(int scenario, CancellationToken cancellationToken);
}

[RpcService]
public sealed class ScalarStreamService : IScalarStream
{
    // Read by the cold controller only; the timed business method has no instrumentation.
    public static int Started;
    public static int Completed;
    public static int Active;

    public async IAsyncEnumerable<int> DownloadAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return i;
            await Task.CompletedTask;
        }
    }

    // Diagnostic-only observer. No product type, transport, interceptor, or request is changed.
    public static Action<int, string, CancellationToken, CancellationToken>? Observe;

    public IAsyncEnumerable<int> CorrectnessAsync(int scenario, CancellationToken cancellationToken)
    {
        // Preserve the original method argument before EnumeratorCancellation can link tokens.
        Observe?.Invoke(scenario, "service-created", cancellationToken, default);
        return CorrectnessCore(scenario, cancellationToken, cancellationToken);
    }

    private static async IAsyncEnumerable<int> CorrectnessCore(int scenario,
        CancellationToken argumentToken, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Started);
        Interlocked.Increment(ref Active);
        Observe?.Invoke(scenario, "service-started", argumentToken, cancellationToken);
        using var registration = cancellationToken.Register(() =>
            Observe?.Invoke(scenario, "service-token-cancelled", argumentToken, cancellationToken));
        try
        {
            if (scenario == 1)
                throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "fixture-before-first-item");
            yield return 0;
            if (scenario == 2)
                throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "fixture-after-first-item");
            if (scenario == 7)
                throw new InvalidOperationException("fixture-unhandled-service-exception");
            Observe?.Invoke(scenario, "service-before-infinite-wait", argumentToken, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref Active);
            Interlocked.Increment(ref Completed);
            Observe?.Invoke(scenario, "service-finally", argumentToken, cancellationToken);
        }
    }
}
