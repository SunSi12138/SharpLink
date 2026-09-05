using System.Buffers.Binary;
#if !SHARPLINK_NATIVEAOT
using System.Reflection;
#endif
using System.Runtime.InteropServices;

namespace SharpLink.Runtime;

internal static class RpcUnsafeBlitPlatform
{
    private const int SupportedNativePointerSize = 8;
    private static readonly bool DateTimeOffsetRawAbiSupported = ProbeDateTimeOffsetRawAbi();

    internal static void EnsureSupported(Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        if (SharpLinkGeneratedUnsafeBlitCatalog.TryGet(targetType, out var generatedRequirement))
        {
            if (!IsSupported(generatedRequirement, IntPtr.Size, DateTimeOffsetRawAbiSupported))
            {
                throw new PlatformNotSupportedException(
                    $"UnsafeBlit Codec for '{targetType.FullName}' does not satisfy its source-generated runtime ABI requirement.");
            }
            return;
        }

#if SHARPLINK_NATIVEAOT
        throw new PlatformNotSupportedException(
            $"UnsafeBlit Codec for '{targetType.FullName}' requires source-generated ABI metadata under NativeAOT. " +
            "Use the type in a generated RPC contract or bind an explicit Codec/Adapter.");
#else
        if (ContainsRuntimeSizedMember(targetType, new HashSet<Type>()))
        {
            throw new PlatformNotSupportedException(
                $"UnsafeBlit Codec for '{targetType.FullName}' contains runtime-sized members and does not have a stable wire layout.");
        }
        if (IntPtr.Size != SupportedNativePointerSize)
        {
            throw new PlatformNotSupportedException(
                $"UnsafeBlit Codec for '{targetType.FullName}' requires the SharpLink 64-bit wire ABI.");
        }
        if (!DateTimeOffsetRawAbiSupported && ContainsDateTimeOffset(targetType, new HashSet<Type>()))
        {
            throw new PlatformNotSupportedException(
                $"UnsafeBlit Codec for '{targetType.FullName}' contains DateTimeOffset, whose raw representation does not match the SharpLink declared framework ABI on this runtime.");
        }
#endif
    }

    internal static bool IsSupported(Type targetType, int nativePointerSize)
        => IsSupported(targetType, nativePointerSize, DateTimeOffsetRawAbiSupported);

    internal static bool IsSupported(
        Type targetType,
        int nativePointerSize,
        bool dateTimeOffsetRawAbiSupported)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        if (SharpLinkGeneratedUnsafeBlitCatalog.TryGet(targetType, out var generatedRequirement))
            return IsSupported(generatedRequirement, nativePointerSize, dateTimeOffsetRawAbiSupported);

#if SHARPLINK_NATIVEAOT
        return false;
#else
        return nativePointerSize == SupportedNativePointerSize &&
               !ContainsRuntimeSizedMember(targetType, new HashSet<Type>()) &&
               (dateTimeOffsetRawAbiSupported || !ContainsDateTimeOffset(targetType, new HashSet<Type>()));
#endif
    }

    private static bool IsSupported(
        SharpLinkGeneratedUnsafeBlitRequirement requirement,
        int nativePointerSize,
        bool dateTimeOffsetRawAbiSupported)
        => nativePointerSize == requirement.NativePointerWidth &&
           (!requirement.RequiresDateTimeOffsetRawAbi || dateTimeOffsetRawAbiSupported);

#if !SHARPLINK_NATIVEAOT
    private static bool ContainsRuntimeSizedMember(Type type, HashSet<Type> seen)
    {
        if (IsRuntimeSizedIntrinsic(type))
            return true;
        if (!type.IsValueType || type.IsPrimitive || type.IsEnum)
            return false;
        if (!seen.Add(type))
            return false;

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (ContainsRuntimeSizedMember(field.FieldType, seen))
                return true;
        }

        return false;
    }

    private static bool ContainsDateTimeOffset(Type type, HashSet<Type> seen)
    {
        if (type == typeof(DateTimeOffset))
            return true;
        if (!type.IsValueType || type.IsPrimitive || type.IsEnum || !seen.Add(type))
            return false;

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (ContainsDateTimeOffset(field.FieldType, seen))
                return true;
        }
        return false;
    }

    private static bool IsRuntimeSizedIntrinsic(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Numerics.Vector<>);
#endif

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
