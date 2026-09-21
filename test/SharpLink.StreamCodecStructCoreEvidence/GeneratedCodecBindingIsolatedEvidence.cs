using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SharpLink.Abstractions;

namespace SharpLink.StreamCodecStructCoreEvidence;

internal static partial class GeneratedCodecBindingEvidenceRunner
{
    internal static void Run(string[] args)
    {
#if GENERATED_BINDING_INTERFACE
        RunInterfaceIsolated(args);
#elif GENERATED_BINDING_GUARDED_VALUE
        RunGuardedValueIsolated(args);
#elif GENERATED_BINDING_GUARDED_REF
        RunGuardedRefIsolated(args);
#elif GENERATED_BINDING_DIRECT
        RunDirectIsolated(args);
#else
        if (args.Length == 2 && string.Equals(args[0], "--jit-probe", StringComparison.Ordinal))
        {
            RunIsolatedJitProbe(args[1]);
            return;
        }

        if (args.Length != 2)
            throw new ArgumentException("Usage: <interface|guarded-value|guarded-ref|direct> <output-json>");

        switch (args[0])
        {
            case "interface":
                RunInterfaceIsolated([args[1]]);
                break;
            case "guarded-value":
                RunGuardedValueIsolated([args[1]]);
                break;
            case "guarded-ref":
                RunGuardedRefIsolated([args[1]]);
                break;
            case "direct":
                RunDirectIsolated([args[1]]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(args), args[0], "Unknown generated binding shape.");
        }
#endif
    }

    private static void RunInterfaceIsolated(string[] args)
    {
        var output = RequireOutput(args);
        VerifyFallback();
        var rows = new List<Measurement>();
        AddFlatInterface(rows);
        AddNestedInterface(rows);
        WriteJson(output, "interface", rows);
    }

    private static void RunGuardedValueIsolated(string[] args)
    {
        var output = RequireOutput(args);
        VerifyFallback();
        var rows = new List<Measurement>();
        AddFlatGuardedValue(rows);
        AddNestedGuardedValue(rows);
        WriteJson(output, "guarded-value", rows);
    }

    private static void RunGuardedRefIsolated(string[] args)
    {
        var output = RequireOutput(args);
        VerifyFallback();
        var rows = new List<Measurement>();
        AddFlatGuardedRef(rows);
        AddNestedGuardedRef(rows);
        WriteJson(output, "guarded-ref", rows);
    }

    private static void RunDirectIsolated(string[] args)
    {
        var output = RequireOutput(args);
        VerifyFallback();
        var rows = new List<Measurement>();
        AddFlatDirect(rows);
        AddNestedDirect(rows);
        WriteJson(output, "direct", rows);
    }

    private static string RequireOutput(string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("Usage: <output-json>");
        return System.IO.Path.GetFullPath(args[0]);
    }

    private static void AddFlatInterface(List<Measurement> rows)
    {
        var value = CreateValue<int>();
        var payload = CreatePayload(in value);
        IRpcCodec<int> resolved = new GeneratedIntCodec();
        var sized = (IRpcSizedCodec<int>)resolved;
        var writer = new ScratchBufferWriter();

        foreach (var length in StreamLengths)
        {
            rows.Add(Measure("int", "stream-sized-serialize", length, count =>
                RunInterfaceSizedStreams(resolved, sized, value, writer, length, count)));
            rows.Add(Measure("int", "stream-deserialize", length, count =>
                RunInterfaceDeserializeStreams(resolved, payload, length, count)));
        }

        rows.Add(MeasureCalls("int", "unary-serialize", count =>
            RunInterfaceUnarySerialize(resolved, value, writer, count)));
        rows.Add(MeasureCalls("int", "unary-deserialize", count =>
            RunInterfaceUnaryDeserialize(resolved, payload, count)));
    }

    private static void AddFlatGuardedValue(List<Measurement> rows)
    {
        var value = CreateValue<int>();
        var payload = CreatePayload(in value);
        IRpcCodec<int> resolved = new GeneratedIntCodec();
        var generated = resolved as GeneratedIntCodec;
        var writer = new ScratchBufferWriter();

        foreach (var length in StreamLengths)
        {
            rows.Add(Measure("int", "stream-sized-serialize", length, count =>
                RunGuardedSizedStreams<int, GeneratedIntCodec, GeneratedIntCore>(
                    resolved,
                    generated,
                    static wrapper => wrapper.Core,
                    value,
                    writer,
                    length,
                    count)));
            rows.Add(Measure("int", "stream-deserialize", length, count =>
                RunGuardedDeserializeStreams<int, GeneratedIntCodec, GeneratedIntCore>(
                    resolved,
                    generated,
                    static wrapper => wrapper.Core,
                    payload,
                    length,
                    count)));
        }

        rows.Add(MeasureCalls("int", "unary-serialize", count =>
            RunGuardedUnarySerialize<int, GeneratedIntCodec, GeneratedIntCore>(
                resolved,
                generated,
                static wrapper => wrapper.Core,
                value,
                writer,
                count)));
        rows.Add(MeasureCalls("int", "unary-deserialize", count =>
            RunGuardedUnaryDeserialize<int, GeneratedIntCodec, GeneratedIntCore>(
                resolved,
                generated,
                static wrapper => wrapper.Core,
                payload,
                count)));
    }

    private static void AddFlatGuardedRef(List<Measurement> rows)
    {
        var value = CreateValue<int>();
        var payload = CreatePayload(in value);
        IRpcCodec<int> resolved = new GeneratedIntCodec();
        var generated = resolved as GeneratedIntCodec;
        var writer = new ScratchBufferWriter();

        foreach (var length in StreamLengths)
        {
            rows.Add(Measure("int", "stream-sized-serialize", length, count =>
                RunFlatGuardedRefSizedStreams(resolved, generated, value, writer, length, count)));
            rows.Add(Measure("int", "stream-deserialize", length, count =>
                RunFlatGuardedRefDeserializeStreams(resolved, generated, payload, length, count)));
        }

        rows.Add(MeasureCalls("int", "unary-serialize", count =>
            RunFlatGuardedRefUnarySerialize(resolved, generated, value, writer, count)));
        rows.Add(MeasureCalls("int", "unary-deserialize", count =>
            RunFlatGuardedRefUnaryDeserialize(resolved, generated, payload, count)));
    }

    private static void AddFlatDirect(List<Measurement> rows)
    {
        var value = CreateValue<int>();
        var payload = CreatePayload(in value);
        var core = default(GeneratedIntCore);
        var writer = new ScratchBufferWriter();

        foreach (var length in StreamLengths)
        {
            rows.Add(Measure("int", "stream-sized-serialize", length, count =>
                RunDirectSizedStreams(core, value, writer, length, count)));
            rows.Add(Measure("int", "stream-deserialize", length, count =>
                RunDirectDeserializeStreams<int, GeneratedIntCore>(core, payload, length, count)));
        }

        rows.Add(MeasureCalls("int", "unary-serialize", count =>
            RunDirectUnarySerialize<int, GeneratedIntCore>(core, value, writer, count)));
        rows.Add(MeasureCalls("int", "unary-deserialize", count =>
            RunDirectUnaryDeserialize<int, GeneratedIntCore>(core, payload, count)));
    }

    private static void AddNestedInterface(List<Measurement> rows)
    {
        var child = new Payload16Codec();
        IRpcCodec<Nested64> resolved = new GeneratedNested64Codec(child, child, child, child);
        var sized = (IRpcSizedCodec<Nested64>)resolved;
        var value = CreateValue<Nested64>();
        var payload = CreatePayload(in value);
        var writer = new ScratchBufferWriter();

        foreach (var length in StreamLengths)
        {
            rows.Add(Measure("generated-like64", "stream-sized-serialize", length, count =>
                RunInterfaceSizedStreams(resolved, sized, value, writer, length, count)));
            rows.Add(Measure("generated-like64", "stream-deserialize", length, count =>
                RunInterfaceDeserializeStreams(resolved, payload, length, count)));
        }

        rows.Add(MeasureCalls("generated-like64", "unary-serialize", count =>
            RunInterfaceUnarySerialize(resolved, value, writer, count)));
        rows.Add(MeasureCalls("generated-like64", "unary-deserialize", count =>
            RunInterfaceUnaryDeserialize(resolved, payload, count)));
    }

    private static void AddNestedGuardedValue(List<Measurement> rows)
    {
        var child = new Payload16Codec();
        IRpcCodec<Nested64> resolved = new GeneratedNested64Codec(child, child, child, child);
        var generated = resolved as GeneratedNested64Codec;
        var value = CreateValue<Nested64>();
        var payload = CreatePayload(in value);
        var writer = new ScratchBufferWriter();

        foreach (var length in StreamLengths)
        {
            rows.Add(Measure("generated-like64", "stream-sized-serialize", length, count =>
                RunGuardedSizedStreams<Nested64, GeneratedNested64Codec, GeneratedNested64Core>(
                    resolved,
                    generated,
                    static wrapper => wrapper.Core,
                    value,
                    writer,
                    length,
                    count)));
            rows.Add(Measure("generated-like64", "stream-deserialize", length, count =>
                RunGuardedDeserializeStreams<Nested64, GeneratedNested64Codec, GeneratedNested64Core>(
                    resolved,
                    generated,
                    static wrapper => wrapper.Core,
                    payload,
                    length,
                    count)));
        }

        rows.Add(MeasureCalls("generated-like64", "unary-serialize", count =>
            RunGuardedUnarySerialize<Nested64, GeneratedNested64Codec, GeneratedNested64Core>(
                resolved,
                generated,
                static wrapper => wrapper.Core,
                value,
                writer,
                count)));
        rows.Add(MeasureCalls("generated-like64", "unary-deserialize", count =>
            RunGuardedUnaryDeserialize<Nested64, GeneratedNested64Codec, GeneratedNested64Core>(
                resolved,
                generated,
                static wrapper => wrapper.Core,
                payload,
                count)));
    }

    private static void AddNestedGuardedRef(List<Measurement> rows)
    {
        var child = new Payload16Codec();
        IRpcCodec<Nested64> resolved = new GeneratedNested64Codec(child, child, child, child);
        var generated = resolved as GeneratedNested64Codec;
        var value = CreateValue<Nested64>();
        var payload = CreatePayload(in value);
        var writer = new ScratchBufferWriter();

        foreach (var length in StreamLengths)
        {
            rows.Add(Measure("generated-like64", "stream-sized-serialize", length, count =>
                RunNestedGuardedRefSizedStreams(resolved, generated, value, writer, length, count)));
            rows.Add(Measure("generated-like64", "stream-deserialize", length, count =>
                RunNestedGuardedRefDeserializeStreams(resolved, generated, payload, length, count)));
        }

        rows.Add(MeasureCalls("generated-like64", "unary-serialize", count =>
            RunNestedGuardedRefUnarySerialize(resolved, generated, value, writer, count)));
        rows.Add(MeasureCalls("generated-like64", "unary-deserialize", count =>
            RunNestedGuardedRefUnaryDeserialize(resolved, generated, payload, count)));
    }

    private static void AddNestedDirect(List<Measurement> rows)
    {
        var child = new Payload16Codec();
        var wrapper = new GeneratedNested64Codec(child, child, child, child);
        var core = wrapper.Core;
        var value = CreateValue<Nested64>();
        var payload = CreatePayload(in value);
        var writer = new ScratchBufferWriter();

        foreach (var length in StreamLengths)
        {
            rows.Add(Measure("generated-like64", "stream-sized-serialize", length, count =>
                RunDirectSizedStreams(core, value, writer, length, count)));
            rows.Add(Measure("generated-like64", "stream-deserialize", length, count =>
                RunDirectDeserializeStreams<Nested64, GeneratedNested64Core>(core, payload, length, count)));
        }

        rows.Add(MeasureCalls("generated-like64", "unary-serialize", count =>
            RunDirectUnarySerialize<Nested64, GeneratedNested64Core>(core, value, writer, count)));
        rows.Add(MeasureCalls("generated-like64", "unary-deserialize", count =>
            RunDirectUnaryDeserialize<Nested64, GeneratedNested64Core>(core, payload, count)));
    }

    private static long RunInterfaceSizedStreams<T>(
        IRpcCodec<T> codec,
        IRpcSizedCodec<T> sized,
        T value,
        ScratchBufferWriter writer,
        int length,
        int streamCount)
        where T : unmanaged
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
            checksum += PumpInterfaceSized(codec, sized, in value, writer, length);
        return checksum;
    }

    private static long RunInterfaceDeserializeStreams<T>(
        IRpcCodec<T> codec,
        ReadOnlySequence<byte> payload,
        int length,
        int streamCount)
        where T : unmanaged
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
            checksum += PumpInterfaceDeserialize(codec, in payload, length);
        return checksum;
    }

    private static long RunGuardedSizedStreams<T, TWrapper, TCore>(
        IRpcCodec<T> fallback,
        TWrapper? generated,
        Func<TWrapper, TCore> getCore,
        T value,
        ScratchBufferWriter writer,
        int length,
        int streamCount)
        where T : unmanaged
        where TWrapper : class
        where TCore : struct, IRpcCodec<T>, IRpcSizedCodec<T>
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
        {
            if (generated is not null)
            {
                var core = getCore(generated);
                checksum += PumpSized<T, TCore>(core, in value, writer, length);
            }
            else
            {
                checksum += PumpInterfaceSized(
                    fallback,
                    (IRpcSizedCodec<T>)fallback,
                    in value,
                    writer,
                    length);
            }
        }
        return checksum;
    }

    private static long RunGuardedDeserializeStreams<T, TWrapper, TCore>(
        IRpcCodec<T> fallback,
        TWrapper? generated,
        Func<TWrapper, TCore> getCore,
        ReadOnlySequence<byte> payload,
        int length,
        int streamCount)
        where T : unmanaged
        where TWrapper : class
        where TCore : struct, IRpcCodec<T>
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
        {
            if (generated is not null)
            {
                var core = getCore(generated);
                checksum += PumpDeserialize<T, TCore>(core, in payload, length);
            }
            else
            {
                checksum += PumpInterfaceDeserialize(fallback, in payload, length);
            }
        }
        return checksum;
    }

    private static long RunDirectSizedStreams<T, TCore>(
        TCore core,
        T value,
        ScratchBufferWriter writer,
        int length,
        int streamCount)
        where T : unmanaged
        where TCore : struct, IRpcCodec<T>, IRpcSizedCodec<T>
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
            checksum += PumpSized<T, TCore>(core, in value, writer, length);
        return checksum;
    }

    private static long RunDirectDeserializeStreams<T, TCore>(
        TCore core,
        ReadOnlySequence<byte> payload,
        int length,
        int streamCount)
        where T : unmanaged
        where TCore : struct, IRpcCodec<T>
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
            checksum += PumpDeserialize<T, TCore>(core, in payload, length);
        return checksum;
    }

    private static long RunInterfaceUnarySerialize<T>(
        IRpcCodec<T> codec,
        T value,
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

    private static long RunInterfaceUnaryDeserialize<T>(
        IRpcCodec<T> codec,
        ReadOnlySequence<byte> payload,
        int count)
        where T : unmanaged
    {
        long checksum = 0;
        for (var i = 0; i < count; i++)
        {
            var value = codec.Deserialize(in payload);
            checksum += Unsafe.As<T, byte>(ref value);
        }
        return checksum;
    }

    private static long RunGuardedUnarySerialize<T, TWrapper, TCore>(
        IRpcCodec<T> fallback,
        TWrapper? generated,
        Func<TWrapper, TCore> getCore,
        T value,
        ScratchBufferWriter writer,
        int count)
        where T : unmanaged
        where TWrapper : class
        where TCore : struct, IRpcCodec<T>
    {
        long checksum = 0;
        for (var i = 0; i < count; i++)
        {
            writer.Reset();
            if (generated is not null)
            {
                var core = getCore(generated);
                core.Serialize(in value, writer);
            }
            else
            {
                fallback.Serialize(in value, writer);
            }
            checksum += writer.WrittenSpan[0];
        }
        return checksum;
    }

    private static long RunGuardedUnaryDeserialize<T, TWrapper, TCore>(
        IRpcCodec<T> fallback,
        TWrapper? generated,
        Func<TWrapper, TCore> getCore,
        ReadOnlySequence<byte> payload,
        int count)
        where T : unmanaged
        where TWrapper : class
        where TCore : struct, IRpcCodec<T>
    {
        long checksum = 0;
        for (var i = 0; i < count; i++)
        {
            T value;
            if (generated is not null)
            {
                var core = getCore(generated);
                value = core.Deserialize(in payload);
            }
            else
            {
                value = fallback.Deserialize(in payload);
            }
            checksum += Unsafe.As<T, byte>(ref value);
        }
        return checksum;
    }

    private static long RunDirectUnarySerialize<T, TCore>(
        TCore core,
        T value,
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

    private static long RunDirectUnaryDeserialize<T, TCore>(
        TCore core,
        ReadOnlySequence<byte> payload,
        int count)
        where T : unmanaged
        where TCore : struct, IRpcCodec<T>
    {
        long checksum = 0;
        for (var i = 0; i < count; i++)
        {
            var value = core.Deserialize(in payload);
            checksum += Unsafe.As<T, byte>(ref value);
        }
        return checksum;
    }

    private static long RunFlatGuardedRefSizedStreams(
        IRpcCodec<int> fallback,
        GeneratedIntCodec? generated,
        int value,
        ScratchBufferWriter writer,
        int length,
        int streamCount)
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
        {
            if (generated is not null)
                checksum += PumpSizedIn<int, GeneratedIntCore>(in generated.CoreRef, in value, writer, length);
            else
                checksum += PumpInterfaceSized(fallback, (IRpcSizedCodec<int>)fallback, in value, writer, length);
        }
        return checksum;
    }

    private static long RunFlatGuardedRefDeserializeStreams(
        IRpcCodec<int> fallback,
        GeneratedIntCodec? generated,
        ReadOnlySequence<byte> payload,
        int length,
        int streamCount)
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
        {
            if (generated is not null)
                checksum += PumpDeserializeIn<int, GeneratedIntCore>(in generated.CoreRef, in payload, length);
            else
                checksum += PumpInterfaceDeserialize(fallback, in payload, length);
        }
        return checksum;
    }

    private static long RunFlatGuardedRefUnarySerialize(
        IRpcCodec<int> fallback,
        GeneratedIntCodec? generated,
        int value,
        ScratchBufferWriter writer,
        int count)
    {
        long checksum = 0;
        for (var i = 0; i < count; i++)
        {
            writer.Reset();
            if (generated is not null)
                generated.CoreRef.Serialize(in value, writer);
            else
                fallback.Serialize(in value, writer);
            checksum += writer.WrittenSpan[0];
        }
        return checksum;
    }

    private static long RunFlatGuardedRefUnaryDeserialize(
        IRpcCodec<int> fallback,
        GeneratedIntCodec? generated,
        ReadOnlySequence<byte> payload,
        int count)
    {
        long checksum = 0;
        for (var i = 0; i < count; i++)
        {
            var value = generated is not null
                ? generated.CoreRef.Deserialize(in payload)
                : fallback.Deserialize(in payload);
            checksum += value;
        }
        return checksum;
    }

    private static long RunNestedGuardedRefSizedStreams(
        IRpcCodec<Nested64> fallback,
        GeneratedNested64Codec? generated,
        Nested64 value,
        ScratchBufferWriter writer,
        int length,
        int streamCount)
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
        {
            if (generated is not null)
                checksum += PumpSizedIn<Nested64, GeneratedNested64Core>(in generated.CoreRef, in value, writer, length);
            else
                checksum += PumpInterfaceSized(fallback, (IRpcSizedCodec<Nested64>)fallback, in value, writer, length);
        }
        return checksum;
    }

    private static long RunNestedGuardedRefDeserializeStreams(
        IRpcCodec<Nested64> fallback,
        GeneratedNested64Codec? generated,
        ReadOnlySequence<byte> payload,
        int length,
        int streamCount)
    {
        long checksum = 0;
        for (var stream = 0; stream < streamCount; stream++)
        {
            if (generated is not null)
                checksum += PumpDeserializeIn<Nested64, GeneratedNested64Core>(in generated.CoreRef, in payload, length);
            else
                checksum += PumpInterfaceDeserialize(fallback, in payload, length);
        }
        return checksum;
    }

    private static long RunNestedGuardedRefUnarySerialize(
        IRpcCodec<Nested64> fallback,
        GeneratedNested64Codec? generated,
        Nested64 value,
        ScratchBufferWriter writer,
        int count)
    {
        long checksum = 0;
        for (var i = 0; i < count; i++)
        {
            writer.Reset();
            if (generated is not null)
                generated.CoreRef.Serialize(in value, writer);
            else
                fallback.Serialize(in value, writer);
            checksum += writer.WrittenSpan[0];
        }
        return checksum;
    }

    private static long RunNestedGuardedRefUnaryDeserialize(
        IRpcCodec<Nested64> fallback,
        GeneratedNested64Codec? generated,
        ReadOnlySequence<byte> payload,
        int count)
    {
        long checksum = 0;
        for (var i = 0; i < count; i++)
        {
            var value = generated is not null
                ? generated.CoreRef.Deserialize(in payload)
                : fallback.Deserialize(in payload);
            checksum += Unsafe.As<Nested64, byte>(ref value);
        }
        return checksum;
    }

#if !GENERATED_BINDING_INTERFACE && !GENERATED_BINDING_GUARDED_VALUE && !GENERATED_BINDING_GUARDED_REF && !GENERATED_BINDING_DIRECT
    private static void RunIsolatedJitProbe(string shape)
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "sharplink-generated-binding-probe-" + shape + ".json");

        switch (shape)
        {
            case "interface":
                RunInterfaceIsolated([path]);
                break;
            case "guarded-value":
                RunGuardedValueIsolated([path]);
                break;
            case "guarded-ref":
                RunGuardedRefIsolated([path]);
                break;
            case "direct":
                RunDirectIsolated([path]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }

        System.IO.File.Delete(path);
    }
#endif
}
