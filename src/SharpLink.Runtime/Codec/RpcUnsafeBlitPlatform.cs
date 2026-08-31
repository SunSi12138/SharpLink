using System.Reflection;

namespace SharpLink.Runtime;

internal static class RpcUnsafeBlitPlatform
{
    private const int SupportedNativePointerSize = 8;

    internal static void EnsureSupported(Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        if (ContainsRuntimeSizedMember(targetType, new HashSet<Type>()))
        {
            throw new PlatformNotSupportedException(
                $"UnsafeBlit Codec for '{targetType.FullName}' contains runtime-sized members and does not have a stable wire layout.");
        }
        if (IntPtr.Size == SupportedNativePointerSize)
            return;

        throw new PlatformNotSupportedException(
            $"UnsafeBlit Codec for '{targetType.FullName}' requires the SharpLink 64-bit wire ABI.");
    }

    internal static bool IsSupported(Type targetType, int nativePointerSize)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        return nativePointerSize == SupportedNativePointerSize &&
               !ContainsRuntimeSizedMember(targetType, new HashSet<Type>());
    }

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

    private static bool IsRuntimeSizedIntrinsic(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Numerics.Vector<>);
}
