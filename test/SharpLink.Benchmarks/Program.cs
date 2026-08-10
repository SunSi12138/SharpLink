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
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
