using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace SharpLink.OneWayLayoutEvidence;

internal static class ManagedLayout
{
    internal static int SizeOf(Type type)
    {
        var method = new DynamicMethod("ManagedSize", typeof(int), Type.EmptyTypes, typeof(ManagedLayout).Module, true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Sizeof, type);
        il.Emit(OpCodes.Ret);
        return ((Func<int>)method.CreateDelegate(typeof(Func<int>)))();
    }

    internal static object Describe(Type type)
    {
        if (type.ContainsGenericParameters)
            throw new ArgumentException("A layout measurement must use an exact closed type.", nameof(type));
        return new
        {
            type = Program.TypeName(type),
            isValueType = type.IsValueType,
            managedSizeBytes = SizeOf(type),
            sizeMeaning = type.IsValueType ? "managed inline sizeof; excludes enclosing async box/object headers" : "reference slot only; not object size",
            fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .OrderBy(static field => field.MetadataToken)
                .Select(field => new
                {
                    field.Name,
                    type = Program.TypeName(field.FieldType),
                    managedStorageBytes = SizeOf(field.FieldType),
                    offset = type.IsValueType ? FieldOffset(type, field) : (long?)null,
                    containsManagedReferences = ContainsManagedReferences(field.FieldType)
                }).ToArray()
        };
    }

    internal static object? AsyncState(MethodInfo method)
    {
        var state = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        if (state is null)
            return null;
        if (state.IsGenericTypeDefinition)
        {
            var typeArguments = method.DeclaringType?.GetGenericArguments() ?? [];
            state = state.MakeGenericType(typeArguments.Concat(method.GetGenericArguments()).ToArray());
        }
        var moveNext = state.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        return new { layout = Describe(state), moveNext = IlReader.Describe(moveNext) };
    }

    private static bool ContainsManagedReferences(Type type)
    {
        if (!type.IsValueType)
            return true;
        return (bool)typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences))!
            .MakeGenericMethod(type).Invoke(null, null)!;
    }

    internal static long FieldOffset(Type type, FieldInfo field)
    {
        var method = new DynamicMethod("ManagedOffset", typeof(long), [typeof(object)], typeof(ManagedLayout).Module, true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Unbox, type);
        il.Emit(OpCodes.Ldflda, field);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Unbox, type);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Ret);
        return ((Func<object, long>)method.CreateDelegate(typeof(Func<object, long>)))(RuntimeHelpers.GetUninitializedObject(type));
    }
}
