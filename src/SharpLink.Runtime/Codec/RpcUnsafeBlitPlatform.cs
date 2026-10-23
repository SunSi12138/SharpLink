using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SharpLink.Runtime;

internal static class RpcUnsafeBlitPlatform
{
    private static readonly bool DateTimeOffsetRawAbiSupported = ProbeDateTimeOffsetRawAbi();

    internal static void EnsureGeneratedSupported(Type targetType, SharpLinkGeneratedUnsafeBlitRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        if (!IsSupported(requirement, IntPtr.Size, DateTimeOffsetRawAbiSupported))
            throw new PlatformNotSupportedException(
                $"UnsafeBlit Codec for '{targetType.FullName}' does not satisfy its source-generated runtime ABI requirement.");
    }

    internal static bool IsSupported(
        SharpLinkGeneratedUnsafeBlitRequirement requirement,
        int nativePointerSize,
        bool dateTimeOffsetRawAbiSupported)
        => nativePointerSize == 8 && requirement.NativePointerWidth == 8 &&
           (!requirement.RequiresDateTimeOffsetRawAbi || dateTimeOffsetRawAbiSupported);

    private static bool ProbeDateTimeOffsetRawAbi()
    {
        var value = new DateTimeOffset(2026, 8, 31, 13, 45, 12, TimeSpan.FromMinutes(330));
        var raw = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1));
        if (raw.Length != 16)
            return false;

        Span<byte> expected = stackalloc byte[16];
        BinaryPrimitives.WriteInt16LittleEndian(expected, 330);
        BinaryPrimitives.WriteInt64LittleEndian(expected.Slice(8), value.UtcTicks);
        return raw.SequenceEqual(expected);
    }
}
