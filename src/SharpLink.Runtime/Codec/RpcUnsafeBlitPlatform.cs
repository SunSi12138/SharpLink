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
        if (IsSupported(targetType, IntPtr.Size))
            return;

        throw new PlatformNotSupportedException(
            $"UnsafeBlit Codec for '{targetType.FullName}' contains native-sized members and requires a 64-bit process.");
    }

    internal static bool IsSupported(Type targetType, int nativePointerSize)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        return !ContainsRuntimeSizedMember(targetType, new HashSet<Type>()) &&
               (nativePointerSize == SupportedNativePointerSize ||
                !ContainsNativeSizedMember(targetType, new HashSet<Type>()));
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

    private static bool ContainsNativeSizedMember(Type type, HashSet<Type> seen)
    {
        if (type == typeof(IntPtr) || type == typeof(UIntPtr) || type.IsPointer || type.IsFunctionPointer)
            return true;
        if (!type.IsValueType || type.IsPrimitive || type.IsEnum)
            return false;
        if (!seen.Add(type))
            return false;

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (ContainsNativeSizedMember(field.FieldType, seen))
                return true;
        }

        return false;
    }
}
