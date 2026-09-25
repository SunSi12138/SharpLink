using System;
using System.Threading.Tasks;

namespace SharpLink.Benchmarks;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 ||
            !string.Equals(args[0], "--client-call-shape-evidence", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Usage: --client-call-shape-evidence <scenario> " +
                "<warmup-operations> <measurement-seconds> <max-operations> <output-json>");
        }

        await ClientCallShapeEvidenceRunner.RunAsync(args[1..]).ConfigureAwait(false);
        return 0;
    }
}
