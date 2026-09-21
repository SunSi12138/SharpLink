using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpLink.Abstractions;

namespace SharpLink.StreamCodecStructCoreEvidence;

internal static class GeneratedCodecBindingEvidenceRunner
{
    private const int Rounds = 3;
    private const int TargetCallsPerRound = 1_000_000;
    private const int TargetItemsPerRound = 1_000_000;
    private static readonly int[] StreamLengths = [1_000, 10_000, 100_000];

    internal static void Run(string[] args)
    {
#if GENERATED_BINDING_INTERFACE
        RunFixed("interface", args);
#elif GENERATED_BINDING_GUARDED_VALUE
        RunFixed("guarded-value", args);
#elif GENERATED_BINDING_GUARDED_REF
        RunFixed("guarded-ref", args);
#elif GENERATED_BINDING_DIRECT
        RunFixed("direct", args);
#else
        if (args.Length == 2 && string.Equals(args[0], "--jit-probe", StringComparison.Ordinal))
        {
            RunJitProbe(args[1]);
            return;
        }

        if (args.Length != 2)
            throw new ArgumentException("Usage: <interface|guarded-value|guarded-ref|direct> <output-json>");

        RunShape(args[0], args[1]);
#endif
    }

#if GENERATED_BINDING_INTERFACE || GENERATED_BINDING_GUARDED_VALUE || GENERATED_BINDING_GUARDED_REF || GENERATED_BINDING_DIRECT
    private static void RunFixed(string shape, string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("Usage: <output-json>");
        RunShape(shape, args[0]);
    }
#endif

    private static void RunShape(string shape, string outputPath)
    {
        if (shape is not ("interface" or "guarded-value" or "guarded-ref" or "direct"))
            throw new ArgumentOutOfRangeException(nameof(shape));

        outputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        VerifyFallback();

        var rows = new List<Measurement>();
        AddFlat(shape, rows);
        AddGeneratedLike(shape, rows);
        WriteJson(outputPath, shape, rows);
    }

    private static void AddFlat(string shape, List<Measurement> rows)
    {
        var value = CreateValue<int>();
        var payload = CreatePayload(in value);
        IRpcCodec<int> resolved = new GeneratedIntCodec();
        var binding = new FlatBinding(resolved);
        var writer = new ScratchBufferWriter();

        foreach (var length in StreamLengths)
        {
            rows.Add(Measure(
                "int",
                "stream-sized-serialize",
                length,
                count => binding.RunSizedStreams(shape, value, writer, length, count)));
            rows.Add(Measure(
                "int",
                "stream-deserialize",
                length,
                count => binding.RunDeserializeStreams(shape, payload, length, count)));
        }

        rows.Add(MeasureCalls(
            "int",
            "unary-serialize",
            count => binding.RunUnarySerialize(shape, value, writer, count)));
        rows.Add(MeasureCalls(
            "int",
            "unary-deserialize",
            count => binding.RunUnaryDeserialize(shape, payload, count)));
    }

    private static void AddGeneratedLike(string shape, List<Measurement> rows)
    {
        var child = new Payload16Codec();
        var wrapper = new GeneratedNested64Codec(child, child, child, child);
        IRpcCodec<Nested64> resolved = wrapper;
        var binding = new NestedBinding(resolved);
        var value = CreateValue<Nested64>();
        var payload = CreatePayload(in value);
        var writer = new ScratchBufferWriter();

        foreach (var length in StreamLengths)
        {
            rows.Add(Measure(
                "generated-like64",
                "stream-sized-serialize",
                length,
                count => binding.RunSizedStreams(shape, value, writer, length, count)));
            rows.Add(Measure(
                "generated-like64",
                "stream-deserialize",
                length,
                count => binding.RunDeserializeStreams(shape, payload, length, count)));
        }

        rows.Add(MeasureCalls(
            "generated-like64",
            "unary-serialize",
            count => binding.RunUnarySerialize(shape, value, writer, count)));
        rows.Add(MeasureCalls(
            "generated-like64",
            "unary-deserialize",
            count => binding.RunUnaryDeserialize(shape, payload, count)));
    }

    private static Measurement Measure(
        string payload,
        string operation,
        int streamLength,
        Func<int, long> run)
    {
        var streamsPerRound = Math.Max(1, DivideRoundUp(TargetItemsPerRound, streamLength));
        _ = run(Math.Max(4, DivideRoundUp(200_000, streamLength)));
        return MeasureCore(payload, operation, streamLength, streamsPerRound, run);
    }

    private static Measurement MeasureCalls(
        string payload,
        string operation,
        Func<int, long> run)
    {
        _ = run(100_000);
        return MeasureCore(payload, operation, 1, TargetCallsPerRound, run);
    }

    private static Measurement MeasureCore(
        string payload,
        string operation,
        int streamLength,
        int invocationsPerRound,
        Func<int, long> run)
    {
        var wall = new double[Rounds];
        var cpu = new double[Rounds];
        var alloc = new double[Rounds];
        long checksum = 0;
        using var process = Process.GetCurrentProcess();

        for (var round = 0; round < Rounds; round++)
        {
            var itemCount = checked((long)streamLength * invocationsPerRound);
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();

            checksum ^= run(invocationsPerRound);

            var elapsed = Stopwatch.GetTimestamp() - start;
            var allocAfter = GC.GetAllocatedBytesForCurrentThread();
            process.Refresh();
            var cpuAfter = process.TotalProcessorTime;

            wall[round] = elapsed * 1_000_000_000d / Stopwatch.Frequency / itemCount;
            cpu[round] = (cpuAfter - cpuBefore).TotalSeconds * 1_000_000_000d / itemCount;
            alloc[round] = (allocAfter - allocBefore) / (double)itemCount;
        }

        return new Measurement(
            payload,
            operation,
            streamLength,
            Median(wall),
            Median(cpu),
            Median(alloc),
            checksum);
    }

    private sealed class FlatBinding
    {
        private readonly IRpcCodec<int> _resolved;
        private readonly GeneratedIntCodec? _generated;

        internal FlatBinding(IRpcCodec<int> resolved)
        {
            _resolved = resolved;
            _generated = resolved as GeneratedIntCodec;
        }

        internal long RunSizedStreams(
            string shape,
            int value,
            ScratchBufferWriter writer,
            int length,
            int count)
        {
            long checksum = 0;
            for (var stream = 0; stream < count; stream++)
            {
                checksum += shape switch
                {
                    "interface" => PumpInterfaceSized(
                        _resolved,
                        (IRpcSizedCodec<int>)_resolved,
                        in value,
                        writer,
                        length),
                    "guarded-value" => _generated is { } generated
                        ? PumpSized<int, GeneratedIntCore>(generated.Core, in value, writer, length)
                        : PumpInterfaceSized(
                            _resolved,
                            (IRpcSizedCodec<int>)_resolved,
                            in value,
                            writer,
                            length),
                    "guarded-ref" => _generated is { } generated
                        ? PumpSizedIn<int, GeneratedIntCore>(in generated.CoreRef, in value, writer, length)
                        : PumpInterfaceSized(
                            _resolved,
                            (IRpcSizedCodec<int>)_resolved,
                            in value,
                            writer,
                            length),
                    "direct" => PumpSized<int, GeneratedIntCore>(
                        default,
                        in value,
                        writer,
                        length),
                    _ => throw new ArgumentOutOfRangeException(nameof(shape))
                };
            }
            return checksum;
        }

        internal long RunDeserializeStreams(
            string shape,
            ReadOnlySequence<byte> payload,
            int length,
            int count)
        {
            long checksum = 0;
            for (var stream = 0; stream < count; stream++)
            {
                checksum += shape switch
                {
                    "interface" => PumpInterfaceDeserialize(_resolved, in payload, length),
                    "guarded-value" => _generated is { } generated
                        ? PumpDeserialize<int, GeneratedIntCore>(generated.Core, in payload, length)
                        : PumpInterfaceDeserialize(_resolved, in payload, length),
                    "guarded-ref" => _generated is { } generated
                        ? PumpDeserializeIn<int, GeneratedIntCore>(in generated.CoreRef, in payload, length)
                        : PumpInterfaceDeserialize(_resolved, in payload, length),
                    "direct" => PumpDeserialize<int, GeneratedIntCore>(default, in payload, length),
                    _ => throw new ArgumentOutOfRangeException(nameof(shape))
                };
            }
            return checksum;
        }

        internal long RunUnarySerialize(
            string shape,
            int value,
            ScratchBufferWriter writer,
            int count)
        {
            long checksum = 0;
            for (var i = 0; i < count; i++)
            {
                writer.Reset();
                switch (shape)
                {
                    case "interface":
                        _resolved.Serialize(in value, writer);
                        break;
                    case "guarded-value":
                        if (_generated is { } generated)
                        {
                            var core = generated.Core;
                            core.Serialize(in value, writer);
                        }
                        else
                        {
                            _resolved.Serialize(in value, writer);
                        }
                        break;
                    case "guarded-ref":
                        if (_generated is { } generatedRef)
                            generatedRef.CoreRef.Serialize(in value, writer);
                        else
                            _resolved.Serialize(in value, writer);
                        break;
                    case "direct":
                        default(GeneratedIntCore).Serialize(in value, writer);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(shape));
                }
                checksum += writer.WrittenSpan[0];
            }
            return checksum;
        }

        internal long RunUnaryDeserialize(
            string shape,
            ReadOnlySequence<byte> payload,
            int count)
        {
            long checksum = 0;
            for (var i = 0; i < count; i++)
            {
                var value = shape switch
                {
                    "interface" => _resolved.Deserialize(in payload),
                    "guarded-value" => _generated is { } generated
                        ? generated.Core.Deserialize(in payload)
                        : _resolved.Deserialize(in payload),
                    "guarded-ref" => _generated is { } generatedRef
                        ? generatedRef.CoreRef.Deserialize(in payload)
                        : _resolved.Deserialize(in payload),
                    "direct" => default(GeneratedIntCore).Deserialize(in payload),
                    _ => throw new ArgumentOutOfRangeException(nameof(shape))
                };
                checksum += value;
            }
            return checksum;
        }
    }

    private sealed class NestedBinding
    {
        private readonly IRpcCodec<Nested64> _resolved;
        private readonly GeneratedNested64Codec? _generated;

        internal NestedBinding(IRpcCodec<Nested64> resolved)
        {
            _resolved = resolved;
            _generated = resolved as GeneratedNested64Codec;
        }

        internal long RunSizedStreams(
            string shape,
            Nested64 value,
            ScratchBufferWriter writer,
            int length,
            int count)
        {
            long checksum = 0;
            for (var stream = 0; stream < count; stream++)
            {
                checksum += shape switch
                {
                    "interface" => PumpInterfaceSized(
                        _resolved,
                        (IRpcSizedCodec<Nested64>)_resolved,
                        in value,
                        writer,
                        length),
                    "guarded-value" => _generated is { } generated
                        ? PumpSized<Nested64, GeneratedNested64Core>(
                            generated.Core,
                            in value,
                            writer,
                            length)
                        : PumpInterfaceSized(
                            _resolved,
                            (IRpcSizedCodec<Nested64>)_resolved,
                            in value,
                            writer,
                            length),
                    "guarded-ref" => _generated is { } generated
                        ? PumpSizedIn<Nested64, GeneratedNested64Core>(
                            in generated.CoreRef,
                            in value,
                            writer,
                            length)
                        : PumpInterfaceSized(
                            _resolved,
                            (IRpcSizedCodec<Nested64>)_resolved,
                            in value,
                            writer,
                            length),
                    "direct" => PumpSized<Nested64, GeneratedNested64Core>(
                        _generated!.Core,
                        in value,
                        writer,
                        length),
                    _ => throw new ArgumentOutOfRangeException(nameof(shape))
                };
            }
            return checksum;
        }

        internal long RunDeserializeStreams(
            string shape,
            ReadOnlySequence<byte> payload,
            int length,
            int count)
        {
            long checksum = 0;
            for (var stream = 0; stream < count; stream++)
            {
                checksum += shape switch
                {
                    "interface" => PumpInterfaceDeserialize(_resolved, in payload, length),
                    "guarded-value" => _generated is { } generated
                        ? PumpDeserialize<Nested64, GeneratedNested64Core>(
                            generated.Core,
                            in payload,
                            length)
                        : PumpInterfaceDeserialize(_resolved, in payload, length),
                    "guarded-ref" => _generated is { } generated
                        ? PumpDeserializeIn<Nested64, GeneratedNested64Core>(
                            in generated.CoreRef,
                            in payload,
                            length)
                        : PumpInterfaceDeserialize(_resolved, in payload, length),
                    "direct" => PumpDeserialize<Nested64, GeneratedNested64Core>(
                        _generated!.Core,
                        in payload,
                        length),
                    _ => throw new ArgumentOutOfRangeException(nameof(shape))
                };
            }
            return checksum;
        }

        internal long RunUnarySerialize(
            string shape,
            Nested64 value,
            ScratchBufferWriter writer,
            int count)
        {
            long checksum = 0;
            for (var i = 0; i < count; i++)
            {
                writer.Reset();
                switch (shape)
                {
                    case "interface":
                        _resolved.Serialize(in value, writer);
                        break;
                    case "guarded-value":
                        if (_generated is { } generated)
                        {
                            var core = generated.Core;
                            core.Serialize(in value, writer);
                        }
                        else
                        {
                            _resolved.Serialize(in value, writer);
                        }
                        break;
                    case "guarded-ref":
                        if (_generated is { } generatedRef)
                            generatedRef.CoreRef.Serialize(in value, writer);
                        else
                            _resolved.Serialize(in value, writer);
                        break;
                    case "direct":
                        _generated!.Core.Serialize(in value, writer);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(shape));
                }
                checksum += writer.WrittenSpan[0];
            }
            return checksum;
        }

        internal long RunUnaryDeserialize(
            string shape,
            ReadOnlySequence<byte> payload,
            int count)
        {
            long checksum = 0;
            for (var i = 0; i < count; i++)
            {
                var value = shape switch
                {
                    "interface" => _resolved.Deserialize(in payload),
                    "guarded-value" => _generated is { } generated
                        ? generated.Core.Deserialize(in payload)
                        : _resolved.Deserialize(in payload),
                    "guarded-ref" => _generated is { } generatedRef
                        ? generatedRef.CoreRef.Deserialize(in payload)
                        : _resolved.Deserialize(in payload),
                    "direct" => _generated!.Core.Deserialize(in payload),
                    _ => throw new ArgumentOutOfRangeException(nameof(shape))
                };
                checksum += Unsafe.As<Nested64, byte>(ref value);
            }
            return checksum;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long PumpInterfaceSized<T>(
        IRpcCodec<T> codec,
        IRpcSizedCodec<T> sized,
        in T value,
        ScratchBufferWriter writer,
        int count)
        where T : unmanaged
    {
        long checksum = 0;
        for (var i = 0; i < count; i++)
        {
            writer.Reset();
            if (sized.TryGetEncodedSize(in value, out var size, out var snapshot))
            {
                sized.SerializeSized(in value, writer, size, snapshot);
                if (snapshot is not null)
                    sized.ReleaseSnapshot(snapshot);
            }
            else
            {
                codec.Serialize(in value, writer);
            }
            checksum += writer.WrittenSpan[0];
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long PumpSized<T, TCore>(
        TCore core,
        in T value,
        ScratchBufferWriter writer,
        int count)
        where T : unmanaged
        where TCore : struct, IRpcCodec<T>, IRpcSizedCodec<T>
    {
        long checksum = 0;
        for (var i = 0; i < count; i++)
        {
            writer.Reset();
            if (core.TryGetEncodedSize(in value, out var size, out var snapshot))
            {
                core.SerializeSized(in value, writer, size, snapshot);
                if (snapshot is not null)
                    core.ReleaseSnapshot(snapshot);
            }
            else
            {
                core.Serialize(in value, writer);
            }
            checksum += writer.WrittenSpan[0];
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long PumpSizedIn<T, TCore>(
        in TCore core,
        in T value,
        ScratchBufferWriter writer,
        int count)
        where T : unmanaged
        where TCore : struct, IRpcCodec<T>, IRpcSizedCodec<T>
    {
        long checksum = 0;
        for (var i = 0; i < count; i++)
        {
            writer.Reset();
            if (core.TryGetEncodedSize(in value, out var size, out var snapshot))
            {
                core.SerializeSized(in value, writer, size, snapshot);
                if (snapshot is not null)
                    core.ReleaseSnapshot(snapshot);
            }
            else
            {
                core.Serialize(in value, writer);
            }
            checksum += writer.WrittenSpan[0];
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long PumpInterfaceDeserialize<T>(
        IRpcCodec<T> codec,
        in ReadOnlySequence<byte> payload,
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long PumpDeserialize<T, TCore>(
        TCore core,
        in ReadOnlySequence<byte> payload,
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long PumpDeserializeIn<T, TCore>(
        in TCore core,
        in ReadOnlySequence<byte> payload,
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

#if !GENERATED_BINDING_INTERFACE && !GENERATED_BINDING_GUARDED_VALUE && !GENERATED_BINDING_GUARDED_REF && !GENERATED_BINDING_DIRECT
    private static void RunJitProbe(string shape)
    {
        var value = CreateValue<int>();
        var payload = CreatePayload(in value);
        IRpcCodec<int> resolved = new GeneratedIntCodec();
        var binding = new FlatBinding(resolved);
        var writer = new ScratchBufferWriter();

        var checksum = binding.RunSizedStreams(shape, value, writer, 10_000, 64);
        checksum += binding.RunDeserializeStreams(shape, payload, 10_000, 64);
        checksum += binding.RunUnarySerialize(shape, value, writer, 100_000);
        checksum += binding.RunUnaryDeserialize(shape, payload, 100_000);
        Console.WriteLine("generated-binding-jit-probe checksum=" + checksum);
    }
#endif

    private static void VerifyFallback()
    {
        IRpcCodec<int> custom = new CustomIntCodec();
        var binding = new FlatBinding(custom);
        var value = 42;
        var payload = CreatePayload(in value);
        var writer = new ScratchBufferWriter();

        var expected = binding.RunUnarySerialize("interface", value, writer, 8);
        var actual = binding.RunUnarySerialize("guarded-value", value, writer, 8);
        if (expected != actual)
            throw new InvalidOperationException("Generated fast-path fallback changed serialize behavior.");

        expected = binding.RunUnaryDeserialize("interface", payload, 8);
        actual = binding.RunUnaryDeserialize("guarded-value", payload, 8);
        if (expected != actual)
            throw new InvalidOperationException("Generated fast-path fallback changed deserialize behavior.");
    }

    private sealed class GeneratedIntCodec : IRpcCodec<int>, IRpcSizedCodec<int>
    {
        private readonly GeneratedIntCore _core;

        internal GeneratedIntCore Core => _core;
        internal ref readonly GeneratedIntCore CoreRef => ref _core;

        public bool CanExactSize => true;
        public void Serialize(in int value, IBufferWriter<byte> buffer) => _core.Serialize(in value, buffer);
        public int Deserialize(in ReadOnlySequence<byte> buffer) => _core.Deserialize(in buffer);
        public bool TryGetEncodedSize(in int value, out int size) => _core.TryGetEncodedSize(in value, out size);
        public bool TryGetEncodedSize(in int value, out int size, out IRpcSizedCodecSnapshot? snapshot)
            => _core.TryGetEncodedSize(in value, out size, out snapshot);
        public void SerializeSized(in int value, IBufferWriter<byte> buffer, int size, IRpcSizedCodecSnapshot? snapshot)
            => _core.SerializeSized(in value, buffer, size, snapshot);
        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot) => _core.ReleaseSnapshot(snapshot);
    }

    private readonly struct GeneratedIntCore : IRpcCodec<int>, IRpcSizedCodec<int>
    {
        public bool CanExactSize => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(sizeof(int));
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(span), value);
            buffer.Advance(sizeof(int));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Deserialize(in ReadOnlySequence<byte> buffer)
            => Unsafe.ReadUnaligned<int>(ref MemoryMarshal.GetReference(buffer.FirstSpan));

        public bool TryGetEncodedSize(in int value, out int size)
        {
            size = sizeof(int);
            return true;
        }

        public bool TryGetEncodedSize(in int value, out int size, out IRpcSizedCodecSnapshot? snapshot)
        {
            size = sizeof(int);
            snapshot = null;
            return true;
        }

        public void SerializeSized(in int value, IBufferWriter<byte> buffer, int size, IRpcSizedCodecSnapshot? snapshot)
            => Serialize(in value, buffer);

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
        }
    }

    private sealed class Payload16Codec : IRpcCodec<Payload16>, IRpcSizedCodec<Payload16>
    {
        public bool CanExactSize => true;

        public void Serialize(in Payload16 value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(16);
            MemoryMarshal.Write(span, in value);
            buffer.Advance(16);
        }

        public Payload16 Deserialize(in ReadOnlySequence<byte> buffer)
            => MemoryMarshal.Read<Payload16>(buffer.FirstSpan);

        public bool TryGetEncodedSize(in Payload16 value, out int size)
        {
            size = 16;
            return true;
        }

        public bool TryGetEncodedSize(in Payload16 value, out int size, out IRpcSizedCodecSnapshot? snapshot)
        {
            size = 16;
            snapshot = null;
            return true;
        }

        public void SerializeSized(in Payload16 value, IBufferWriter<byte> buffer, int size, IRpcSizedCodecSnapshot? snapshot)
            => Serialize(in value, buffer);

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
        }
    }

    private sealed class GeneratedNested64Codec : IRpcCodec<Nested64>, IRpcSizedCodec<Nested64>
    {
        private readonly GeneratedNested64Core _core;

        internal GeneratedNested64Codec(
            IRpcCodec<Payload16> a,
            IRpcCodec<Payload16> b,
            IRpcCodec<Payload16> c,
            IRpcCodec<Payload16> d)
            => _core = new GeneratedNested64Core(a, b, c, d);

        internal GeneratedNested64Core Core => _core;
        internal ref readonly GeneratedNested64Core CoreRef => ref _core;

        public bool CanExactSize => true;
        public void Serialize(in Nested64 value, IBufferWriter<byte> buffer) => _core.Serialize(in value, buffer);
        public Nested64 Deserialize(in ReadOnlySequence<byte> buffer) => _core.Deserialize(in buffer);
        public bool TryGetEncodedSize(in Nested64 value, out int size) => _core.TryGetEncodedSize(in value, out size);
        public bool TryGetEncodedSize(in Nested64 value, out int size, out IRpcSizedCodecSnapshot? snapshot)
            => _core.TryGetEncodedSize(in value, out size, out snapshot);
        public void SerializeSized(in Nested64 value, IBufferWriter<byte> buffer, int size, IRpcSizedCodecSnapshot? snapshot)
            => _core.SerializeSized(in value, buffer, size, snapshot);
        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot) => _core.ReleaseSnapshot(snapshot);
    }

    private readonly struct GeneratedNested64Core : IRpcCodec<Nested64>, IRpcSizedCodec<Nested64>
    {
        private readonly IRpcCodec<Payload16> _a;
        private readonly IRpcCodec<Payload16> _b;
        private readonly IRpcCodec<Payload16> _c;
        private readonly IRpcCodec<Payload16> _d;

        internal GeneratedNested64Core(
            IRpcCodec<Payload16> a,
            IRpcCodec<Payload16> b,
            IRpcCodec<Payload16> c,
            IRpcCodec<Payload16> d)
        {
            _a = a;
            _b = b;
            _c = c;
            _d = d;
        }

        public bool CanExactSize => true;

        public void Serialize(in Nested64 value, IBufferWriter<byte> buffer)
        {
            _a.Serialize(in value.A, buffer);
            _b.Serialize(in value.B, buffer);
            _c.Serialize(in value.C, buffer);
            _d.Serialize(in value.D, buffer);
        }

        public Nested64 Deserialize(in ReadOnlySequence<byte> buffer)
            => new()
            {
                A = _a.Deserialize(buffer.Slice(0, 16)),
                B = _b.Deserialize(buffer.Slice(16, 16)),
                C = _c.Deserialize(buffer.Slice(32, 16)),
                D = _d.Deserialize(buffer.Slice(48, 16))
            };

        public bool TryGetEncodedSize(in Nested64 value, out int size)
        {
            size = 64;
            return true;
        }

        public bool TryGetEncodedSize(in Nested64 value, out int size, out IRpcSizedCodecSnapshot? snapshot)
        {
            size = 64;
            snapshot = null;
            return true;
        }

        public void SerializeSized(in Nested64 value, IBufferWriter<byte> buffer, int size, IRpcSizedCodecSnapshot? snapshot)
        {
            ((IRpcSizedCodec<Payload16>)_a).SerializeSized(in value.A, buffer, 16, null);
            ((IRpcSizedCodec<Payload16>)_b).SerializeSized(in value.B, buffer, 16, null);
            ((IRpcSizedCodec<Payload16>)_c).SerializeSized(in value.C, buffer, 16, null);
            ((IRpcSizedCodec<Payload16>)_d).SerializeSized(in value.D, buffer, 16, null);
        }

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
        }
    }

    private sealed class CustomIntCodec : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(sizeof(int));
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(span), value);
            buffer.Advance(sizeof(int));
        }

        public int Deserialize(in ReadOnlySequence<byte> buffer)
            => Unsafe.ReadUnaligned<int>(ref MemoryMarshal.GetReference(buffer.FirstSpan));
    }

    private sealed class ScratchBufferWriter : IBufferWriter<byte>
    {
        private readonly byte[] _buffer = new byte[512];
        private int _written;

        internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);
        internal void Reset() => _written = 0;
        public void Advance(int count) => _written += count;
        public Memory<byte> GetMemory(int sizeHint = 0) => _buffer.AsMemory(_written);
        public Span<byte> GetSpan(int sizeHint = 0) => _buffer.AsSpan(_written);
    }

    private static T CreateValue<T>()
        where T : unmanaged
    {
        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<T>()];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = unchecked((byte)(42 + i));
        return MemoryMarshal.Read<T>(bytes);
    }

    private static ReadOnlySequence<byte> CreatePayload<T>(in T value)
        where T : unmanaged
    {
        var bytes = new byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(bytes.AsSpan(), in value);
        return new ReadOnlySequence<byte>(bytes);
    }

    private static int DivideRoundUp(int value, int divisor)
        => checked((value + divisor - 1) / divisor);

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(static x => x).ToArray();
        return ordered[ordered.Length / 2];
    }

    private static void WriteJson(
        string outputPath,
        string shape,
        IReadOnlyList<Measurement> measurements)
    {
        using var stream = File.Create(outputPath);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("shape", shape);
        writer.WriteString("framework", RuntimeInformation.FrameworkDescription);
        writer.WriteString("architecture", RuntimeInformation.ProcessArchitecture.ToString());
        writer.WriteString("tieredPgo", Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "default");
        writer.WriteStartArray("measurements");
        foreach (var row in measurements)
        {
            writer.WriteStartObject();
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
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Payload16
    {
        public long A;
        public long B;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Nested64
    {
        public Payload16 A;
        public Payload16 B;
        public Payload16 C;
        public Payload16 D;
    }

    private sealed record Measurement(
        string Payload,
        string Operation,
        int StreamLength,
        double NanosecondsPerItem,
        double CpuNanosecondsPerItem,
        double AllocatedBytesPerItem,
        long Checksum);
}
