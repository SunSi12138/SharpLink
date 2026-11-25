using System;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.LoadTest;

internal static class Qualified32Workload
{
    internal const string Unary = "unary32";
    internal const string OneWay = "oneway32";
    internal const int PayloadBytes = 32;
    internal const int PayloadChars = PayloadBytes / sizeof(char);

    internal static bool Includes(string operation) => operation is Unary or OneWay;

    internal static async ValueTask<long?> ObserveRemoteAsync(ILoadTestService service, long? expectedTotal, int drainSeconds)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(drainSeconds));
        long? observed = null;
        try
        {
            do
            {
                observed = await service.ReadOneWay32CompletedAsync(deadline.Token).AsTask()
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
                if (expectedTotal is null || observed >= expectedTotal)
                    return observed;
                await Task.Delay(10, deadline.Token).ConfigureAwait(false);
            }
            while (true);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return observed;
        }
    }
}
