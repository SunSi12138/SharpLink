using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Running;

namespace SharpLink.Benchmarks;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(
            args[0], "--compression-evidence", StringComparison.Ordinal))
        {
            await CompressionEvidenceRunner.RunAsync(args[1..]);
            return;
        }
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
