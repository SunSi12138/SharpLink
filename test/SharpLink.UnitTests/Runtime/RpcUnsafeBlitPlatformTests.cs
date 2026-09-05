using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

public sealed class RpcUnsafeBlitPlatformTests
{
    [Test]
    public void UnsafeBlitShouldBe64BitOnly()
    {
        Ensure(
            RpcUnsafeBlitPlatform.IsSupported(typeof(NativeSizedPayload), 8),
            "native-sized UnsafeBlit payloads must be accepted by the supported 64-bit runtime");
        Ensure(
            !RpcUnsafeBlitPlatform.IsSupported(typeof(NativeSizedPayload), 4),
            "native-sized UnsafeBlit payloads must be rejected by a 32-bit runtime");
        Ensure(
            RpcUnsafeBlitPlatform.IsSupported(typeof(PortablePayload), 8),
            "fixed-width composite UnsafeBlit payloads must remain valid on the supported 64-bit ABI");
        Ensure(
            !RpcUnsafeBlitPlatform.IsSupported(typeof(PortablePayload), 4),
            "fixed-width composite UnsafeBlit payloads must also reject 32-bit runtimes because CLR padding/alignment is ABI-dependent");
    }

    [Test]
    public void DateTimeOffsetRawAbiShouldBeCapabilityGuarded()
    {
        Ensure(
            RpcUnsafeBlitPlatform.IsSupported(typeof(DateTimeOffsetPayload), 8),
            "the current supported runtime must satisfy the declared DateTimeOffset raw ABI");
        Ensure(
            !RpcUnsafeBlitPlatform.IsSupported(
                typeof(DateTimeOffsetPayload),
                8,
                dateTimeOffsetRawAbiSupported: false),
            "UnsafeBlit must reject a runtime whose DateTimeOffset raw representation does not satisfy the declared ABI");
        Ensure(
            RpcUnsafeBlitPlatform.IsSupported(
                typeof(PortablePayload),
                8,
                dateTimeOffsetRawAbiSupported: false),
            "an unrelated fixed-width UnsafeBlit graph must not be rejected by the DateTimeOffset-specific ABI guard");
    }

    [Test]
    public void RuntimeSizedVectorShouldNeverUseUnsafeBlit()
    {
        Ensure(
            !RpcUnsafeBlitPlatform.IsSupported(typeof(System.Numerics.Vector<int>), 8),
            "runtime-sized Vector<T> must not be accepted by UnsafeBlit even on 64-bit runtimes");
        Ensure(
            !RpcUnsafeBlitPlatform.IsSupported(typeof(VectorPayload), 8),
            "a value type containing Vector<T> must also be rejected by UnsafeBlit");

        try
        {
            RpcUnsafeBlitPlatform.EnsureSupported(typeof(System.Numerics.Vector<int>));
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }

        throw new InvalidOperationException("Vector<T> must fail the runtime UnsafeBlit guard.");
    }

    [Test]
    public void WirePlatformShouldRequireLittleEndian()
    {
        Ensure(RpcWirePlatform.IsSupported(isLittleEndian: true),
            "little-endian runtimes define the supported SharpLink primitive wire ABI");
        Ensure(!RpcWirePlatform.IsSupported(isLittleEndian: false),
            "big-endian runtimes must not advertise native-memory primitive Codec identities");

        try
        {
            RpcWirePlatform.EnsureSupported(isLittleEndian: false);
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }

        throw new InvalidOperationException("Big-endian runtime simulation must fail the SharpLink wire platform guard.");
    }

    private struct NativeSizedPayload
    {
        public int Prefix { get; set; }
        public nint Handle { get; set; }
    }

    private struct PortablePayload
    {
        public byte Prefix { get; set; }
        public long Value { get; set; }
    }

    private struct DateTimeOffsetPayload
    {
        public int Prefix { get; set; }
        public DateTimeOffset Value { get; set; }
    }

    private struct VectorPayload
    {
        public System.Numerics.Vector<int> Value { get; set; }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
