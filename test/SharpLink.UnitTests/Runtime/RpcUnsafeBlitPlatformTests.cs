using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

public sealed class RpcUnsafeBlitPlatformTests
{
    [Test]
    public void NativeSizedUnsafeBlitShouldBe64BitOnly()
    {
        Ensure(
            RpcUnsafeBlitPlatform.IsSupported(typeof(NativeSizedPayload), 8),
            "native-sized UnsafeBlit payloads must be accepted by the supported 64-bit runtime");
        Ensure(
            !RpcUnsafeBlitPlatform.IsSupported(typeof(NativeSizedPayload), 4),
            "native-sized UnsafeBlit payloads must be rejected by a 32-bit runtime");
        Ensure(
            RpcUnsafeBlitPlatform.IsSupported(typeof(PortablePayload), 4),
            "fixed-width UnsafeBlit payloads must remain valid on a 32-bit runtime");
    }

    private struct NativeSizedPayload
    {
        public int Prefix { get; set; }
        public nint Handle { get; set; }
    }

    private struct PortablePayload
    {
        public int Prefix { get; set; }
        public long Value { get; set; }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
