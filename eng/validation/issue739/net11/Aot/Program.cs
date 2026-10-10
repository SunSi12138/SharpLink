using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Issue739Aot;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        NativeIdentity.RequireNative();
        if (args.Length == 0) throw new ArgumentException("A bounded reader case or tcp is required");
        if (args[0].StartsWith("reader-", StringComparison.Ordinal)) ReaderCases.Run(args);
        else await TinyCases.Run(args);
    }
}

internal static class NativeIdentity
{
    internal static void RequireNative()
    {
        if (RuntimeFeature.IsDynamicCodeSupported || RuntimeFeature.IsDynamicCodeCompiled)
            throw new InvalidOperationException("This executable must be NativeAOT; JIT fallback is forbidden");
        VerifyElf();
    }

    private static string VerifyElf()
    {
        string path = Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath unavailable");
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[20];
        stream.ReadExactly(header);
        if (header[0] != 0x7f || header[1] != 'E' || header[2] != 'L' || header[3] != 'F'
            || header[4] != 2 || header[5] != 1 || header[18] != 62 || header[19] != 0)
            throw new InvalidOperationException("Expected little-endian ELF64 x86-64 native executable");
        return path;
    }

    internal static void Write(Utf8JsonWriter json)
    {
        string path = VerifyElf();
        using var stream = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        json.WriteBoolean("dynamicCodeSupported", RuntimeFeature.IsDynamicCodeSupported);
        json.WriteBoolean("dynamicCodeCompiled", RuntimeFeature.IsDynamicCodeCompiled);
        json.WriteString("processPath", path);
        json.WriteString("nativeImageFormat", "ELF64-x86-64");
        json.WriteString("executableSha256", hash);
        json.WriteString("nativeImageSha256", hash);
        json.WriteString("executionMode", RuntimeFeature.IsDynamicCodeSupported ? "local-correctness-only" : "nativeaot");
    }
}
