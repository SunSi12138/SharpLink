using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpLink.LoadTestBase;

namespace SharpLink.LoadTest.Tests;

public class LatencyRecorderLayoutTests
{
    [Test]
    public void EmbeddedHotStateShouldStayInSeparateCacheLinesAtEveryStartingPhase()
    {
        const BindingFlags instanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var hotField = typeof(WorkerLatencyRecorder).GetField("_hot", instanceFields)
            ?? throw new Exception("the recorder must contain its actual hot state inline");
        var stateType = hotField.FieldType;
        var layout = stateType.StructLayoutAttribute
            ?? throw new Exception("the hot state must declare its layout");
        Ensure(stateType.IsValueType && stateType.IsNestedPrivate &&
               layout.Value == LayoutKind.Explicit && layout.Size == 256,
            "the private hot state must reserve an explicit 256-byte block");

        var managedSize = (int)(GenericHelper(nameof(ManagedSize), stateType).Invoke(null, null)
            ?? throw new Exception("managed size was unavailable"));
        var containsReferences = (bool)(GenericHelper(nameof(ContainsReferences), stateType).Invoke(null, null)
            ?? throw new Exception("managed reference information was unavailable"));
        Ensure(managedSize == 256 && Marshal.SizeOf(stateType) == managedSize && !containsReferences,
            "the managed value itself, rather than only its marshalling metadata, must occupy 256 reference-free bytes");

        var expectedOffsets = new Dictionary<string, int>
        {
            ["IsRecording"] = 128,
            ["Position"] = 132,
            ["Count"] = 136
        };
        var stateFields = stateType.GetFields(instanceFields);
        Ensure(stateFields.Length == expectedOffsets.Count,
            "the recording hotspot must contain exactly three primitive fields");
        var recorder = new StageLatencyRecorder(2, 2);
        var worker = recorder.GetWorker(0);
        var firstHotByte = managedSize;
        var lastHotByte = -1;
        foreach (var field in stateFields)
        {
            Ensure(field.FieldType == typeof(int) && field.FieldType.IsPrimitive &&
                   expectedOffsets.ContainsKey(field.Name),
                "each actual hot field must be one of the three reference-free Int32 values");
            var offset = EmbeddedOffset(hotField, field, nested: true)(worker);
            Ensure(offset == expectedOffsets[field.Name] &&
                   Marshal.OffsetOf(stateType, field.Name).ToInt64() == offset,
                $"{field.Name} must have its declared offset in the actual embedded managed state");
            firstHotByte = Math.Min(firstHotByte, checked((int)offset));
            lastHotByte = Math.Max(lastHotByte, checked((int)offset + sizeof(int) - 1));
        }
        Ensure(firstHotByte == 128 && lastHotByte == 139,
            "the actual recording hotspot must occupy bytes 128 through 139");

        // Check the real containing object too: Auto layout may reorder its
        // fields, but cannot put another field inside the state's reserved tail.
        foreach (var field in typeof(WorkerLatencyRecorder).GetFields(instanceFields))
        {
            if (field.Name == hotField.Name)
                continue;
            var offset = EmbeddedOffset(hotField, field, nested: false)(worker);
            var size = (int)(GenericHelper(nameof(ManagedSize), field.FieldType).Invoke(null, null)
                ?? throw new Exception("containing field size was unavailable"));
            Ensure(offset + size <= 0 || offset >= managedSize,
                $"the containing field {field.Name} cannot reuse any of the 256 embedded bytes");
        }

        foreach (var lineSize in new[] { 64, 128 })
        {
            // The first block is entirely unaligned; the second independently
            // takes every phase at its nearest non-overlapping location. Any
            // farther location with that phase only increases the separation.
            Ensure(managedSize + firstHotByte - lastHotByte >= lineSize,
                "even the nearest hotspot bytes must be farther apart than a cache line");
            for (var firstPhase = 0; firstPhase < lineSize; firstPhase++)
            {
                var minimumSecondStart = firstPhase + managedSize;
                for (var secondPhase = 0; secondPhase < lineSize; secondPhase++)
                {
                    var secondStart = minimumSecondStart +
                        (secondPhase - minimumSecondStart % lineSize + lineSize) % lineSize;
                    var firstLastLine = (firstPhase + lastHotByte) / lineSize;
                    var secondFirstLine = (secondStart + firstHotByte) / lineSize;
                    Ensure(firstLastLine < secondFirstLine,
                        $"{lineSize}-byte lines cannot be shared by hotspot phases {firstPhase} and {secondPhase}");
                }
            }
        }
    }

    private static Func<WorkerLatencyRecorder, long> EmbeddedOffset(
        FieldInfo hotField,
        FieldInfo targetField,
        bool nested)
    {
        // Keep both addresses as managed byrefs to the same actual object;
        // no pinning, native pointers, or assumption about object alignment.
        var method = new DynamicMethod(
            $"HotOffset_{targetField.Name}",
            typeof(long),
            [typeof(WorkerLatencyRecorder)],
            typeof(LatencyRecorderLayoutTests),
            skipVisibility: true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldflda, hotField);
        il.Emit(OpCodes.Ldarg_0);
        if (nested)
            il.Emit(OpCodes.Ldflda, hotField);
        il.Emit(OpCodes.Ldflda, targetField);
        il.Emit(OpCodes.Call, GenericHelper(nameof(ManagedOffset), hotField.FieldType, targetField.FieldType));
        il.Emit(OpCodes.Ret);
        return (Func<WorkerLatencyRecorder, long>)method.CreateDelegate(typeof(Func<WorkerLatencyRecorder, long>));
    }

    private static MethodInfo GenericHelper(string name, params Type[] arguments)
        => (typeof(LatencyRecorderLayoutTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception($"the layout helper {name} was unavailable"))
            .MakeGenericMethod(arguments);

    private static int ManagedSize<T>() => Unsafe.SizeOf<T>();

    private static bool ContainsReferences<T>() => RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    private static long ManagedOffset<TState, TField>(ref TState state, ref TField field)
        => Unsafe.ByteOffset(ref Unsafe.As<TState, byte>(ref state), ref Unsafe.As<TField, byte>(ref field)).ToInt64();

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
