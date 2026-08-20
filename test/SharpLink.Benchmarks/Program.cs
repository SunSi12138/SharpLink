using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Running;

namespace SharpLink.Benchmarks;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(
            args[0], "--feature-evidence", StringComparison.Ordinal))
        {
            await FeatureEvidenceRunner.RunAsync(args[1..]);
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--summarize-feature-evidence", StringComparison.Ordinal))
        {
            await FeatureEvidenceRunner.SummarizeAsync(args[1..]);
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--generated-abi-streaming-evidence", StringComparison.Ordinal))
        {
            await GeneratedAbiStreamingEvidenceRunner.RunAsync(args[1..]);
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--summarize-generated-abi-streaming-evidence", StringComparison.Ordinal))
        {
            await GeneratedAbiStreamingEvidenceRunner.SummarizeAsync(args[1..]);
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--layout-evidence", StringComparison.Ordinal))
        {
            await LayoutEvidenceRunner.RunAsync(args[1..]);
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--jit-evidence", StringComparison.Ordinal))
        {
            await JitEvidenceRunner.RunAsync(args[1..]);
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--compression-evidence", StringComparison.Ordinal))
        {
            await CompressionEvidenceRunner.RunAsync(args[1..]);
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--buffer-writer-growth-evidence", StringComparison.Ordinal))
        {
            BufferWriterGrowthEvidenceRunner.Run();
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--flow-control-evidence", StringComparison.Ordinal))
        {
            await FlowControlEvidenceRunner.RunAsync(args[1..]);
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--interceptor-attribution-evidence", StringComparison.Ordinal))
        {
            InterceptorAttributionEvidenceRunner.Run();
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--generated-string-growth-evidence", StringComparison.Ordinal))
        {
            GeneratedStringDtoGrowthEvidenceRunner.Run();
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--generated-mixed-growth-evidence", StringComparison.Ordinal))
        {
            GeneratedMixedDtoGrowthEvidenceRunner.Run();
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--generated-nested-growth-evidence", StringComparison.Ordinal))
        {
            GeneratedNestedDtoGrowthEvidenceRunner.Run();
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--generated-string-collection-growth-evidence", StringComparison.Ordinal))
        {
            GeneratedStringCollectionGrowthEvidenceRunner.Run();
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--generated-wrapper-growth-evidence", StringComparison.Ordinal))
        {
            GeneratedWrapperDtoGrowthEvidenceRunner.Run();
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--generated-recursive-allocation-evidence", StringComparison.Ordinal))
        {
            GeneratedRecursiveAllocationEvidenceRunner.Run();
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--send-credit-buffer-hold-evidence", StringComparison.Ordinal))
        {
            SendCreditBufferHoldEvidenceRunner.Run();
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--send-credit-fast-path-evidence", StringComparison.Ordinal))
        {
            SendCreditFastPathEvidenceRunner.Run();
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--pre-credit-starvation-evidence", StringComparison.Ordinal))
        {
            await PreCreditStarvationEvidenceRunner.RunAsync();
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--pre-credit-starved-memory-evidence", StringComparison.Ordinal))
        {
            await PreCreditStarvedMemoryEvidenceRunner.RunAsync(args[1..]);
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--pre-credit-concurrent-fast-evidence", StringComparison.Ordinal))
        {
            await PreCreditConcurrentFastConsumerEvidenceRunner.RunAsync(args[1..]);
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--pre-credit-8way-attribution-evidence", StringComparison.Ordinal))
        {
            await PreCredit8WayAttributionEvidenceRunner.RunAsync(args[1..]);
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--latency-recorder-evidence", StringComparison.Ordinal))
        {
            LatencyRecorderEvidenceRunner.Run(args[1..]);
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--analyze-latency-recorder-baseline", StringComparison.Ordinal))
        {
            LatencyRecorderBaselineAnalyzer.Run(args[1..]);
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--validate-performance-reports", StringComparison.Ordinal))
        {
            PerformanceReportValidationRunner.Run(args[1..]);
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--send-pump-isolation-evidence", StringComparison.Ordinal))
        {
            await SendPumpIsolationEvidenceRunner.RunAsync(args[1..]);
            return;
        }
        if (args.Length > 0 && string.Equals(
            args[0], "--connection-admission-evidence", StringComparison.Ordinal))
        {
            await ConnectionAdmissionEvidenceRunner.RunAsync(args[1..]);
            return;
        }
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
