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
    public static void Main()
    {
        if (!SharpLinkGeneratedUnsafeBlitCatalog.TryGet(
                typeof(PackageAotPayload),
                out var requirement))
        {
            throw new InvalidOperationException(
                "Package NativeAOT smoke did not receive generated UnsafeBlit ABI metadata.");
        }

        if (requirement.NativePointerWidth != IntPtr.Size)
        {
            throw new InvalidOperationException(
                $"Generated UnsafeBlit ABI pointer width {requirement.NativePointerWidth} " +
                $"does not match runtime width {IntPtr.Size}.");
        }

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
    }
}
