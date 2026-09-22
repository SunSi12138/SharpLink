#if GENERATED_BINDING_PAIRED
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SharpLink.Abstractions;

namespace SharpLink.StreamCodecStructCoreEvidence;

internal static partial class GeneratedCodecBindingEvidenceRunner
{
    private enum AotPairedMode
    {
        Interface,
        WrapperCore,
        PreboundCore
    }

    private sealed record AotPairedMeasurement(
        string Mode,
        string Payload,
        string Operation,
        int StreamLength,
        double NanosecondsPerItem,
        double CpuNanosecondsPerItem,
        double AllocatedBytesPerItem,
        long Checksum);

    internal static void RunAotPaired(string[] args)
    {
        if (args.Length != 2)
            throw new ArgumentException("Usage: <iwp|ipw|wip|wpi|piw|pwi> <output-json>");

        var order = ParseAotPairedOrder(args[0]);
        var output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var rows = new List<AotPairedMeasurement>();
        AddFlatAotPaired(order, rows);
        AddPost727ConcreteNestedAotPaired(order, rows);
        AddPost727StateRefNestedAotPaired(order, rows);
        AddNestedAotPaired(order, rows);
        WriteAotPairedJson(output, args[0], rows);
    }

    private static AotPairedMode[] ParseAotPairedOrder(string order)
    {
        if (order.Length != 3)
            throw new ArgumentOutOfRangeException(nameof(order));

        var result = new AotPairedMode[3];
        var seen = 0;
        for (var i = 0; i < order.Length; i++)
        {
            result[i] = order[i] switch
            {
                'i' => AotPairedMode.Interface,
                'w' => AotPairedMode.WrapperCore,
                'p' => AotPairedMode.PreboundCore,
                _ => throw new ArgumentOutOfRangeException(nameof(order))
            };
            seen |= 1 << (int)result[i];
        }

        if (seen != 0b111)
            throw new ArgumentException("Order must contain i, w, and p exactly once.", nameof(order));
        return result;
    }

    private static void AddFlatAotPaired(
        IReadOnlyList<AotPairedMode> order,
        List<AotPairedMeasurement> rows)
    {
        var value = CreateValue<int>();
        var payload = CreatePayload(in value);
        var wrapper = new GeneratedIntCodec();
        IRpcCodec<int> resolved = wrapper;
        var sized = (IRpcSizedCodec<int>)resolved;
        var writer = new ScratchBufferWriter();

        foreach (var length in StreamLengths)
        {
            foreach (var mode in order)
            {
                var measured = Measure(
                    "int",
                    "stream-sized-serialize",
                    length,
                    count => AotPairedFlatSized(
                        mode, resolved, sized, wrapper, value, writer, length, count));
                rows.Add(ToAotPaired(mode, measured));
            }

            foreach (var mode in order)
            {
                var measured = Measure(
                    "int",
                    "stream-unsized-serialize",
                    length,
                    count => AotPairedFlatUnsized(
                        mode, resolved, wrapper, value, writer, length, count));
                rows.Add(ToAotPaired(mode, measured));
            }

            foreach (var mode in order)
            {
                var measured = Measure(
                    "int",
                    "stream-deserialize",
                    length,
                    count => AotPairedFlatDeserialize(
                        mode, resolved, wrapper, payload, length, count));
                rows.Add(ToAotPaired(mode, measured));
            }
        }
    }

    private static void AddPost727ConcreteNestedAotPaired(
        IReadOnlyList<AotPairedMode> order,
        List<AotPairedMeasurement> rows)
    {
        var child = new Payload16Codec();
        var wrapper = new Post727Nested64Codec(child, child, child, child);
        IRpcCodec<Nested64> resolved = wrapper;
        var sized = (IRpcSizedCodec<Nested64>)resolved;
        var value = CreateValue<Nested64>();
        var payload = CreatePayload(in value);
        var writer = new ScratchBufferWriter();

        foreach (var length in StreamLengths)
        {
            foreach (var mode in order)
            {
                var measured = Measure(
                    "generated-concrete64",
                    "stream-sized-serialize",
                    length,
                    count => AotPairedPost727NestedSized(
                        mode, resolved, sized, wrapper, value, writer, length, count));
                rows.Add(ToAotPaired(mode, measured));
            }

            foreach (var mode in order)
            {
                var measured = Measure(
                    "generated-concrete64",
                    "stream-unsized-serialize",
                    length,
                    count => AotPairedPost727NestedUnsized(
                        mode, resolved, wrapper, value, writer, length, count));
                rows.Add(ToAotPaired(mode, measured));
            }

            foreach (var mode in order)
            {
                var measured = Measure(
                    "generated-concrete64",
                    "stream-deserialize",
                    length,
                    count => AotPairedPost727NestedDeserialize(
                        mode, resolved, wrapper, payload, length, count));
                rows.Add(ToAotPaired(mode, measured));
            }
        }
    }

    private static void AddPost727StateRefNestedAotPaired(
        IReadOnlyList<AotPairedMode> order,
        List<AotPairedMeasurement> rows)
    {
        var child = new Payload16Codec();
        var wrapper = new Post727Nested64Codec(child, child, child, child);
        IRpcCodec<Nested64> resolved = wrapper;
        var sized = (IRpcSizedCodec<Nested64>)resolved;
        var value = CreateValue<Nested64>();
        var payload = CreatePayload(in value);
        var writer = new ScratchBufferWriter();

        foreach (var length in StreamLengths)
        {
            foreach (var mode in order)
            {
                var measured = Measure(
                    "generated-state-ref64",
                    "stream-sized-serialize",
                    length,
                    count => AotPairedStateRefNestedSized(
                        mode, resolved, sized, wrapper, value, writer, length, count));
                rows.Add(ToAotPaired(mode, measured));
            }

            foreach (var mode in order)
            {
                var measured = Measure(
                    "generated-state-ref64",
                    "stream-unsized-serialize",
                    length,
                    count => AotPairedStateRefNestedUnsized(
                        mode, resolved, wrapper, value, writer, length, count));
                rows.Add(ToAotPaired(mode, measured));
            }

            foreach (var mode in order)
            {
                var measured = Measure(
                    "generated-state-ref64",
                    "stream-deserialize",
                    length,
                    count => AotPairedStateRefNestedDeserialize(
                        mode, resolved, wrapper, payload, length, count));
                rows.Add(ToAotPaired(mode, measured));
            }
        }
    }

    private static void AddNestedAotPaired(
        IReadOnlyList<AotPairedMode> order,
        List<AotPairedMeasurement> rows)
    {
        var child = new Payload16Codec();
        var wrapper = new GeneratedNested64Codec(child, child, child, child);
        IRpcCodec<Nested64> resolved = wrapper;
        var sized = (IRpcSizedCodec<Nested64>)resolved;
        var value = CreateValue<Nested64>();
        var payload = CreatePayload(in value);
        var writer = new ScratchBufferWriter();

        foreach (var length in StreamLengths)
        {
            foreach (var mode in order)
            {
                var measured = Measure(
                    "generated-like64",
                    "stream-sized-serialize",
                    length,
                    count => AotPairedNestedSized(
                        mode, resolved, sized, wrapper, value, writer, length, count));
                rows.Add(ToAotPaired(mode, measured));
            }

            foreach (var mode in order)
            {
                var measured = Measure(
                    "generated-like64",
                    "stream-unsized-serialize",
                    length,
                    count => AotPairedNestedUnsized(
                        mode, resolved, wrapper, value, writer, length, count));
                rows.Add(ToAotPaired(mode, measured));
            }

            foreach (var mode in order)
            {
                var measured = Measure(
                    "generated-like64",
                    "stream-deserialize",
                    length,
                    count => AotPairedNestedDeserialize(
                        mode, resolved, wrapper, payload, length, count));
                rows.Add(ToAotPaired(mode, measured));
            }
        }
    }

    private static AotPairedMeasurement ToAotPaired(
        AotPairedMode mode,
        Measurement measurement)
        => new(
            mode switch
            {
                AotPairedMode.Interface => "interface",
                AotPairedMode.WrapperCore => "wrapper-core",
                AotPairedMode.PreboundCore => "prebound-core",
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            },
            measurement.Payload,
            measurement.Operation,
            measurement.StreamLength,
            measurement.NanosecondsPerItem,
            measurement.CpuNanosecondsPerItem,
            measurement.AllocatedBytesPerItem,
            measurement.Checksum);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long AotPairedFlatSized(
        AotPairedMode mode,
        IRpcCodec<int> fallback,
        IRpcSizedCodec<int> sized,
        GeneratedIntCodec wrapper,
        int value,
        ScratchBufferWriter writer,
        int length,
        int streamCount)
    {
        long checksum = 0;
        switch (mode)
        {
            case AotPairedMode.Interface:
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpInterfaceSized(fallback, sized, in value, writer, length);
                break;
            case AotPairedMode.WrapperCore:
                for (var stream = 0; stream < streamCount; stream++)
                {
                    var core = wrapper.Core;
                    checksum += PumpSized<int, GeneratedIntCore>(core, in value, writer, length);
                }
                break;
            case AotPairedMode.PreboundCore:
                var flatCore = wrapper.Core;
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpSized<int, GeneratedIntCore>(flatCore, in value, writer, length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long AotPairedFlatUnsized(
        AotPairedMode mode,
        IRpcCodec<int> fallback,
        GeneratedIntCodec wrapper,
        int value,
        ScratchBufferWriter writer,
        int length,
        int streamCount)
    {
        long checksum = 0;
        switch (mode)
        {
            case AotPairedMode.Interface:
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpInterfaceUnsizedPaired(fallback, in value, writer, length);
                break;
            case AotPairedMode.WrapperCore:
                for (var stream = 0; stream < streamCount; stream++)
                {
                    var core = wrapper.Core;
                    checksum += PumpUnsizedPaired<int, GeneratedIntCore>(
                        core, in value, writer, length);
                }
                break;
            case AotPairedMode.PreboundCore:
                var preboundCore = wrapper.Core;
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpUnsizedPaired<int, GeneratedIntCore>(
                        preboundCore, in value, writer, length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long AotPairedFlatDeserialize(
        AotPairedMode mode,
        IRpcCodec<int> fallback,
        GeneratedIntCodec wrapper,
        ReadOnlySequence<byte> payload,
        int length,
        int streamCount)
    {
        long checksum = 0;
        switch (mode)
        {
            case AotPairedMode.Interface:
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpInterfaceDeserialize(fallback, in payload, length);
                break;
            case AotPairedMode.WrapperCore:
                for (var stream = 0; stream < streamCount; stream++)
                {
                    var core = wrapper.Core;
                    checksum += PumpDeserialize<int, GeneratedIntCore>(core, in payload, length);
                }
                break;
            case AotPairedMode.PreboundCore:
                var flatCore = wrapper.Core;
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpDeserialize<int, GeneratedIntCore>(flatCore, in payload, length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long AotPairedPost727NestedSized(
        AotPairedMode mode,
        IRpcCodec<Nested64> fallback,
        IRpcSizedCodec<Nested64> sized,
        Post727Nested64Codec wrapper,
        Nested64 value,
        ScratchBufferWriter writer,
        int length,
        int streamCount)
    {
        long checksum = 0;
        switch (mode)
        {
            case AotPairedMode.Interface:
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpInterfaceSized(fallback, sized, in value, writer, length);
                break;
            case AotPairedMode.WrapperCore:
                for (var stream = 0; stream < streamCount; stream++)
                {
                    var core = wrapper.Core;
                    checksum += PumpSized<Nested64, Post727Nested64Core>(
                        core, in value, writer, length);
                }
                break;
            case AotPairedMode.PreboundCore:
                var nestedCore = wrapper.Core;
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpSized<Nested64, Post727Nested64Core>(
                        nestedCore, in value, writer, length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long AotPairedPost727NestedUnsized(
        AotPairedMode mode,
        IRpcCodec<Nested64> fallback,
        Post727Nested64Codec wrapper,
        Nested64 value,
        ScratchBufferWriter writer,
        int length,
        int streamCount)
    {
        long checksum = 0;
        switch (mode)
        {
            case AotPairedMode.Interface:
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpInterfaceUnsizedPaired(fallback, in value, writer, length);
                break;
            case AotPairedMode.WrapperCore:
                for (var stream = 0; stream < streamCount; stream++)
                {
                    var core = wrapper.Core;
                    checksum += PumpUnsizedPaired<Nested64, Post727Nested64Core>(
                        core, in value, writer, length);
                }
                break;
            case AotPairedMode.PreboundCore:
                var preboundCore = wrapper.Core;
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpUnsizedPaired<Nested64, Post727Nested64Core>(
                        preboundCore, in value, writer, length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long AotPairedPost727NestedDeserialize(
        AotPairedMode mode,
        IRpcCodec<Nested64> fallback,
        Post727Nested64Codec wrapper,
        ReadOnlySequence<byte> payload,
        int length,
        int streamCount)
    {
        long checksum = 0;
        switch (mode)
        {
            case AotPairedMode.Interface:
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpInterfaceDeserialize(fallback, in payload, length);
                break;
            case AotPairedMode.WrapperCore:
                for (var stream = 0; stream < streamCount; stream++)
                {
                    var core = wrapper.Core;
                    checksum += PumpDeserialize<Nested64, Post727Nested64Core>(
                        core, in payload, length);
                }
                break;
            case AotPairedMode.PreboundCore:
                var nestedCore = wrapper.Core;
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpDeserialize<Nested64, Post727Nested64Core>(
                        nestedCore, in payload, length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long AotPairedStateRefNestedSized(
        AotPairedMode mode,
        IRpcCodec<Nested64> fallback,
        IRpcSizedCodec<Nested64> sized,
        Post727Nested64Codec wrapper,
        Nested64 value,
        ScratchBufferWriter writer,
        int length,
        int streamCount)
    {
        long checksum = 0;
        switch (mode)
        {
            case AotPairedMode.Interface:
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpInterfaceSized(fallback, sized, in value, writer, length);
                break;
            case AotPairedMode.WrapperCore:
                for (var stream = 0; stream < streamCount; stream++)
                {
                    var core = wrapper.OwnerCore;
                    checksum += PumpSized<Nested64, Post727StateRefNested64Core>(
                        core, in value, writer, length);
                }
                break;
            case AotPairedMode.PreboundCore:
                var preboundCore = wrapper.OwnerCore;
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpSized<Nested64, Post727StateRefNested64Core>(
                        preboundCore, in value, writer, length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long AotPairedStateRefNestedUnsized(
        AotPairedMode mode,
        IRpcCodec<Nested64> fallback,
        Post727Nested64Codec wrapper,
        Nested64 value,
        ScratchBufferWriter writer,
        int length,
        int streamCount)
    {
        long checksum = 0;
        switch (mode)
        {
            case AotPairedMode.Interface:
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpInterfaceUnsizedPaired(fallback, in value, writer, length);
                break;
            case AotPairedMode.WrapperCore:
                for (var stream = 0; stream < streamCount; stream++)
                {
                    var core = wrapper.OwnerCore;
                    checksum += PumpUnsizedPaired<Nested64, Post727StateRefNested64Core>(
                        core, in value, writer, length);
                }
                break;
            case AotPairedMode.PreboundCore:
                var preboundCore = wrapper.OwnerCore;
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpUnsizedPaired<Nested64, Post727StateRefNested64Core>(
                        preboundCore, in value, writer, length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long AotPairedStateRefNestedDeserialize(
        AotPairedMode mode,
        IRpcCodec<Nested64> fallback,
        Post727Nested64Codec wrapper,
        ReadOnlySequence<byte> payload,
        int length,
        int streamCount)
    {
        long checksum = 0;
        switch (mode)
        {
            case AotPairedMode.Interface:
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpInterfaceDeserialize(fallback, in payload, length);
                break;
            case AotPairedMode.WrapperCore:
                for (var stream = 0; stream < streamCount; stream++)
                {
                    var core = wrapper.OwnerCore;
                    checksum += PumpDeserialize<Nested64, Post727StateRefNested64Core>(
                        core, in payload, length);
                }
                break;
            case AotPairedMode.PreboundCore:
                var preboundCore = wrapper.OwnerCore;
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpDeserialize<Nested64, Post727StateRefNested64Core>(
                        preboundCore, in payload, length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long AotPairedNestedSized(
        AotPairedMode mode,
        IRpcCodec<Nested64> fallback,
        IRpcSizedCodec<Nested64> sized,
        GeneratedNested64Codec wrapper,
        Nested64 value,
        ScratchBufferWriter writer,
        int length,
        int streamCount)
    {
        long checksum = 0;
        switch (mode)
        {
            case AotPairedMode.Interface:
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpInterfaceSized(fallback, sized, in value, writer, length);
                break;
            case AotPairedMode.WrapperCore:
                for (var stream = 0; stream < streamCount; stream++)
                {
                    var core = wrapper.Core;
                    checksum += PumpSized<Nested64, GeneratedNested64Core>(
                        core, in value, writer, length);
                }
                break;
            case AotPairedMode.PreboundCore:
                var nestedCore = wrapper.Core;
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpSized<Nested64, GeneratedNested64Core>(
                        nestedCore, in value, writer, length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long AotPairedNestedUnsized(
        AotPairedMode mode,
        IRpcCodec<Nested64> fallback,
        GeneratedNested64Codec wrapper,
        Nested64 value,
        ScratchBufferWriter writer,
        int length,
        int streamCount)
    {
        long checksum = 0;
        switch (mode)
        {
            case AotPairedMode.Interface:
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpInterfaceUnsizedPaired(fallback, in value, writer, length);
                break;
            case AotPairedMode.WrapperCore:
                for (var stream = 0; stream < streamCount; stream++)
                {
                    var core = wrapper.Core;
                    checksum += PumpUnsizedPaired<Nested64, GeneratedNested64Core>(
                        core, in value, writer, length);
                }
                break;
            case AotPairedMode.PreboundCore:
                var preboundCore = wrapper.Core;
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpUnsizedPaired<Nested64, GeneratedNested64Core>(
                        preboundCore, in value, writer, length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long AotPairedNestedDeserialize(
        AotPairedMode mode,
        IRpcCodec<Nested64> fallback,
        GeneratedNested64Codec wrapper,
        ReadOnlySequence<byte> payload,
        int length,
        int streamCount)
    {
        long checksum = 0;
        switch (mode)
        {
            case AotPairedMode.Interface:
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpInterfaceDeserialize(fallback, in payload, length);
                break;
            case AotPairedMode.WrapperCore:
                for (var stream = 0; stream < streamCount; stream++)
                {
                    var core = wrapper.Core;
                    checksum += PumpDeserialize<Nested64, GeneratedNested64Core>(
                        core, in payload, length);
                }
                break;
            case AotPairedMode.PreboundCore:
                var nestedCore = wrapper.Core;
                for (var stream = 0; stream < streamCount; stream++)
                    checksum += PumpDeserialize<Nested64, GeneratedNested64Core>(
                        nestedCore, in payload, length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long PumpInterfaceUnsizedPaired<T>(
        IRpcCodec<T> codec,
        in T value,
        ScratchBufferWriter writer,
        int count)
        where T : unmanaged
    {
        long checksum = 0;
        for (var i = 0; i < count; i++)
        {
            writer.Reset();
            codec.Serialize(in value, writer);
            checksum += writer.WrittenSpan[0];
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long PumpUnsizedPaired<T, TCore>(
        TCore core,
        in T value,
        ScratchBufferWriter writer,
        int count)
        where T : unmanaged
        where TCore : struct, IRpcCodec<T>
    {
        long checksum = 0;
        for (var i = 0; i < count; i++)
        {
            writer.Reset();
            core.Serialize(in value, writer);
            checksum += writer.WrittenSpan[0];
        }
        return checksum;
    }

    private static void WriteAotPairedJson(
        string output,
        string order,
        IReadOnlyList<AotPairedMeasurement> rows)
    {
        using var stream = File.Create(output);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("order", order);
        writer.WriteNumber("generatedConcreteCoreBytes", Unsafe.SizeOf<Post727Nested64Core>());
        writer.WriteNumber("generatedStateRefCoreBytes", Unsafe.SizeOf<Post727StateRefNested64Core>());
        writer.WriteNumber("fallbackInterfaceChildCoreBytes", Unsafe.SizeOf<GeneratedNested64Core>());
        writer.WriteStartArray("measurements");
        foreach (var row in rows)
        {
            writer.WriteStartObject();
            writer.WriteString("mode", row.Mode);
            writer.WriteString("payload", row.Payload);
            writer.WriteString("operation", row.Operation);
            writer.WriteNumber("streamLength", row.StreamLength);
            writer.WriteNumber("nanosecondsPerItem", row.NanosecondsPerItem);
            writer.WriteNumber("cpuNanosecondsPerItem", row.CpuNanosecondsPerItem);
            writer.WriteNumber("allocatedBytesPerItem", row.AllocatedBytesPerItem);
            writer.WriteNumber("checksum", row.Checksum);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }
}
#endif
