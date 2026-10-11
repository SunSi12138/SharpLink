using System.Buffers;
using System.Runtime.InteropServices;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.Sdk;

[assembly: SharpLinkClusterContractAssembly(
    "package-aot",
    typeof(SharpLink.PackageAotSmoke.IPackageAotContract))]

namespace SharpLink.PackageAotSmoke;

[StructLayout(LayoutKind.Sequential)]
public struct PackageAotPayload
{
    public int Count;
    public long Stamp;
}

[RpcContract]
public interface IPackageAotContract : IService
{
    [NonCancellable]
    ValueTask<PackageAotPayload> EchoAsync(PackageAotPayload value);
}

public static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct UnregisteredPackageAotPayload
    {
        public int Value;
    }

    public static void Main()
    {
        // The generated owner-local factory carries and validates the ABI requirement.
        // No process-global Type catalog is needed by generated consumers.
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var codec = context.Codecs.GetCodec<PackageAotPayload>();

        var expected = new PackageAotPayload
        {
            Count = 42,
            Stamp = 0x0102030405060708
        };

        var writer = new ArrayBufferWriter<byte>();
        codec.Serialize(in expected, writer);

        var encoded = new ReadOnlySequence<byte>(writer.WrittenMemory);
        var actual = codec.Deserialize(in encoded);
        if (actual.Count != expected.Count || actual.Stamp != expected.Stamp)
        {
            throw new InvalidOperationException(
                "Packaged NativeAOT UnsafeBlit round-trip failed.");
        }

        try
        {
            _ = context.Codecs.GetCodec<UnregisteredPackageAotPayload>();
            throw new InvalidOperationException(
                "Packaged NativeAOT consumer accepted UnsafeBlit without generated ABI metadata.");
        }
        catch (PlatformNotSupportedException exception)
            when (exception.Message.Contains("source-generated ABI metadata", StringComparison.Ordinal))
        {
            // Expected: packaged trimmed/NativeAOT consumers remain fail-closed without generated metadata.
        }
    }
}
