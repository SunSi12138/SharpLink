using System.Buffers;
using System.Runtime.InteropServices;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.Sdk;

[assembly: SharpLinkClusterContractAssembly(
    "package-trim",
    typeof(SharpLink.PackageTrimSmoke.IPackageTrimContract))]

namespace SharpLink.PackageTrimSmoke;

[StructLayout(LayoutKind.Sequential)]
public struct PackageTrimPayload
{
    public int Count;
    public long Stamp;
}

[RpcContract]
public interface IPackageTrimContract : IService
{
    [NonCancellable]
    ValueTask<PackageTrimPayload> EchoAsync(PackageTrimPayload value);
}

public static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct UnregisteredPackageTrimPayload
    {
        public int Value;
    }

    public static void Main()
    {
        // The generated owner-local factory carries and validates the ABI requirement.
        // No process-global Type catalog is needed by generated consumers.
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var codec = context.Codecs.GetCodec<PackageTrimPayload>();

        var expected = new PackageTrimPayload
        {
            Count = 42,
            Stamp = 0x0102030405060708
        };

        var writer = new ArrayBufferWriter<byte>();
        codec.Serialize(in expected, writer);
        var encoded = new ReadOnlySequence<byte>(writer.WrittenMemory);
        var actual = codec.Deserialize(in encoded);
        if (actual.Count != expected.Count || actual.Stamp != expected.Stamp)
            throw new InvalidOperationException("Packaged trim-only UnsafeBlit round-trip failed.");

        try
        {
            _ = context.Codecs.GetCodec<UnregisteredPackageTrimPayload>();
            throw new InvalidOperationException(
                "Packaged trim-only consumer accepted UnsafeBlit without generated ABI metadata.");
        }
        catch (PlatformNotSupportedException exception)
            when (exception.Message.Contains("source-generated ABI metadata", StringComparison.Ordinal))
        {
        }

        Console.WriteLine("PACKAGE_TRIM_SMOKE_PASS");
    }
}
