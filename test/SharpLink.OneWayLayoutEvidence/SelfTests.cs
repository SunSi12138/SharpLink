using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace SharpLink.OneWayLayoutEvidence;

internal static class SelfTests
{
    internal static int Run()
    {
        Assert(ManagedLayout.SizeOf(typeof(long)) == sizeof(long), "long size");
        Assert(ManagedLayout.SizeOf(typeof(object)) == IntPtr.Size, "reference slot size");
        Assert(ManagedLayout.SizeOf(typeof(Mixed)) == Unsafe.SizeOf<Mixed>(), "mixed managed layout size");
        _ = ManagedLayout.Describe(typeof(Mixed));
        foreach (var field in typeof(Mixed).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            var offset = ManagedLayout.FieldOffset(typeof(Mixed), field);
            Assert(offset >= 0 && offset + ManagedLayout.SizeOf(field.FieldType) <= Unsafe.SizeOf<Mixed>(), "field offset within managed value");
        }
        var branch = typeof(SelfTests).GetMethod(nameof(Branch), BindingFlags.Static | BindingFlags.NonPublic)!;
        var il = IlReader.Read(branch);
        Assert(il.Count(static instruction => instruction.Opcode.FlowControl == FlowControl.Cond_Branch) >= 2, "branch and switch decoder");
        Assert(il.Any(static instruction => instruction.Opcode == OpCodes.Switch && instruction.SwitchTargets >= 4), "switch operand decoder");
        var asyncMethod = typeof(SelfTests).GetMethod(nameof(Async), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(Mixed));
        Assert(ManagedLayout.AsyncState(asyncMethod) is not null, "exact generic async state closure");
        Console.WriteLine("OneWay layout self-tests passed: managed sizeof, mixed-reference offsets, IL switch/branch decoding, closed generic async state.");
        return 0;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static int Branch(int value)
    {
        if (value < 0)
            return -1;
        return value switch { 0 => 11, 1 => 31, 2 => 22, 3 => 81, 4 => 17, _ => 45 };
    }

    private static async System.Threading.Tasks.Task<T> Async<T>(T value)
    {
        await System.Threading.Tasks.Task.Yield();
        return value;
    }

    private readonly record struct Mixed(object Reference, long Integer, byte Flag);
}
