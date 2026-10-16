using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using SharpLink.Abstractions;

namespace SharpLink.Benchmarks;

internal static class StaticBindingDialectGuard
{
    // Covers BDN child processes as well as the direct evidence runner.
    [ModuleInitializer]
    internal static void Verify()
    {
        var expected = Environment.GetEnvironmentVariable("SHARPLINK_EXPECTED_MAGIC");
        if (expected is not null && expected != ProtocolV2Constants.Magic.ToString(CultureInfo.InvariantCulture))
            throw new InvalidOperationException("Benchmark child used the wrong protocol dialect.");
    }
}
