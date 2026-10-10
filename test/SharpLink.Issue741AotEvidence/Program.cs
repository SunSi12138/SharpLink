using System;
using System.Threading.Tasks;

namespace SharpLink.Benchmarks;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 1 ||
            !string.Equals(args[0], "--issue741-rpc-evidence", StringComparison.Ordinal))
        {
            throw new ArgumentException("Expected --issue741-rpc-evidence plus six benchmark arguments.");
        }

        await Issue741RpcEvidenceRunner.RunAsync(args[1..]).ConfigureAwait(false);
        return 0;
    }
}
