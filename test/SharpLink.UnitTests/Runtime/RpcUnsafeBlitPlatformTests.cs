using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

public sealed class RpcUnsafeBlitPlatformTests
{
    [Test]
    public void UnsafeBlitShouldValidateGenerated64BitRequirement()
    {
        Ensure(RpcUnsafeBlitPlatform.IsSupported(new(8, false), 8, true), "supported generated ABI");
        Ensure(!RpcUnsafeBlitPlatform.IsSupported(new(8, false), 4, true), "32-bit runtime must fail");
        Ensure(!RpcUnsafeBlitPlatform.IsSupported(new(4, false), 4, true), "unsupported declared ABI must fail");
        Ensure(!RpcUnsafeBlitPlatform.IsSupported(default, 8, true), "missing generated metadata must fail");
    }

    [Test]
    public void DateTimeOffsetRawAbiShouldBeCapabilityGuarded()
    {
        Ensure(RpcUnsafeBlitPlatform.IsSupported(new(8, true), 8, true), "supported framework raw ABI");
        Ensure(!RpcUnsafeBlitPlatform.IsSupported(new(8, true), 8, false), "incompatible framework raw ABI");
        Ensure(RpcUnsafeBlitPlatform.IsSupported(new(8, false), 8, false), "unrelated graph stays supported");
    }

    [Test]
    public void RuntimeSizedVectorMustNotAcquireAnImplicitCodec()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        try
        {
            _ = context.Codecs.GetCodec<System.Numerics.Vector<int>>();
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }
        throw new InvalidOperationException("Runtime-sized Vector must not receive an implicit raw codec.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
