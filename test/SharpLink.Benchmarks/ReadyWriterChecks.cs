#if SHARPLINK_READY_WRITER_EXPERIMENT
using System;
using System.Threading.Tasks;
namespace SharpLink.Benchmarks;
internal static class ReadyWriterChecks
{
    internal static async Task RunAsync()
    {
        var checks = await ReadyWriterCoordinator.RunStopChecksAsync();
        checks += await ReadyWriterCoordinator.RunDeterministicChecksAsync();
        checks += await ReadyWriterCoordinator.RunPreparedByteChecksAsync();
        foreach (var budget in new[] { 0, 8192 })
        foreach (var mode in new[] { "A-ready", "B3-ready" })
            foreach (var setup in new[] { (1, 16, 1, 1), (8, 16, 16, 1), (128, 16, 16, 16384), (8, 4096, 4, 16384), (1, 16384, 1, 1) })
            {
                var (streams, bytes, slots, flush) = setup;
                var sample = await PhaseBTransportCase.RunAsync(mode, "pipe", streams, 64, bytes, 0, 8192, slots, flush, preparedByteBudget: budget);
                var metrics = sample.ReadyWriterMetrics ?? throw new Exception("Missing attribution.");
                if (sample.ItemsReceived != 64L * streams || metrics["FramesReleased"] != sample.ItemsReceived || metrics["NormalQueueRejections"] != 0)
                    throw new Exception("Unexpected settlement.");
                if (metrics["RemainingQueuedBytes"] != 0 || metrics["MaximumRingDepth"] > slots ||
                    (budget != 0 && metrics["MaximumQueuedBytesPerStream"] > Math.Max(budget, metrics["MaximumObservedPacketBytes"])))
                    throw new Exception("Prepared-byte bound exceeded or bytes not released.");
                Console.WriteLine($"PASS {mode} c{streams} bytes{bytes} ring{slots} budget{budget} flush{flush}"); checks++;
            }
        Console.WriteLine($"{checks}/{checks} ready writer transport checks passed.");
    }
}
#endif
