using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpLink.Abstractions;

namespace SharpLink.StreamCodecStructCoreEvidence;

internal static partial class StructCoreEvidenceRunner
{
    private interface ICoreVisitor<T>
        where T : unmanaged
    {
        long Visit<TCore>(TCore core)
            where TCore : struct, IRpcCodec<T>;
    }

    private interface ICoreInVisitor<T>
        where T : unmanaged
    {
        long Visit<TCore>(in TCore core)
            where TCore : struct, IRpcCodec<T>;
    }

    private interface ISizedCoreVisitor<T>
        where T : unmanaged
    {
        long Visit<TCore>(TCore core)
            where TCore : struct, IRpcCodec<T>, IRpcSizedCodec<T>;
    }

    private interface ISizedCoreInVisitor<T>
        where T : unmanaged
    {
        long Visit<TCore>(in TCore core)
            where TCore : struct, IRpcCodec<T>, IRpcSizedCodec<T>;
    }

    private interface ICoreOpener<T>
        where T : unmanaged
    {
        long Open<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreVisitor<T>;

        long OpenIn<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreInVisitor<T>;
    }

    private interface ISizedCoreOpener<T> : ICoreOpener<T>
        where T : unmanaged
    {
        long OpenSized<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ISizedCoreVisitor<T>;

        long OpenSizedIn<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ISizedCoreInVisitor<T>;
    }

    private class SerializeState<T>
        where T : unmanaged
    {
        public SerializeState(T value, ScratchBufferWriter writer, int itemCount)
        {
            Value = value;
            Writer = writer;
            ItemCount = itemCount;
        }

        public T Value;
        public ScratchBufferWriter Writer;
        public int ItemCount;
    }

    private sealed class SizedState<T> : SerializeState<T>
        where T : unmanaged
    {
        public SizedState(T value, ScratchBufferWriter writer, int itemCount)
            : base(value, writer, itemCount)
        {
        }
    }

    private sealed class DeserializeState<T>
        where T : unmanaged
    {
        public DeserializeState(ReadOnlySequence<byte> payload, int itemCount)
        {
            Payload = payload;
            ItemCount = itemCount;
        }

        public ReadOnlySequence<byte> Payload;
        public int ItemCount;
    }

    private readonly struct SerializeVisitor<T> : ICoreVisitor<T>
        where T : unmanaged
    {
        private readonly SerializeState<T> _state;

        public SerializeVisitor(SerializeState<T> state) => _state = state;

        public long Visit<TCore>(TCore core)
            where TCore : struct, IRpcCodec<T>
            => StreamCodecStructCore_PumpGenericUnsized(
                core, in _state.Value, _state.Writer, _state.ItemCount);
    }

    private readonly struct SerializeInVisitor<T> : ICoreInVisitor<T>
        where T : unmanaged
    {
        private readonly SerializeState<T> _state;

        public SerializeInVisitor(SerializeState<T> state) => _state = state;

        public long Visit<TCore>(in TCore core)
            where TCore : struct, IRpcCodec<T>
            => StreamCodecStructCore_PumpGenericUnsizedIn(
                in core, in _state.Value, _state.Writer, _state.ItemCount);
    }

    private readonly struct SizedVisitor<T> : ISizedCoreVisitor<T>
        where T : unmanaged
    {
        private readonly SizedState<T> _state;

        public SizedVisitor(SizedState<T> state) => _state = state;

        public long Visit<TCore>(TCore core)
            where TCore : struct, IRpcCodec<T>, IRpcSizedCodec<T>
            => StreamCodecStructCore_PumpGenericSized(
                core, in _state.Value, _state.Writer, _state.ItemCount);
    }

    private readonly struct SizedInVisitor<T> : ISizedCoreInVisitor<T>
        where T : unmanaged
    {
        private readonly SizedState<T> _state;

        public SizedInVisitor(SizedState<T> state) => _state = state;

        public long Visit<TCore>(in TCore core)
            where TCore : struct, IRpcCodec<T>, IRpcSizedCodec<T>
            => StreamCodecStructCore_PumpGenericSizedIn(
                in core, in _state.Value, _state.Writer, _state.ItemCount);
    }

    private readonly struct DeserializeVisitor<T> : ICoreVisitor<T>
        where T : unmanaged
    {
        private readonly DeserializeState<T> _state;

        public DeserializeVisitor(DeserializeState<T> state) => _state = state;

        public long Visit<TCore>(TCore core)
            where TCore : struct, IRpcCodec<T>
            => StreamCodecStructCore_PumpGenericDeserialize<T, TCore>(
                core, in _state.Payload, _state.ItemCount);
    }

    private readonly struct DeserializeInVisitor<T> : ICoreInVisitor<T>
        where T : unmanaged
    {
        private readonly DeserializeState<T> _state;

        public DeserializeInVisitor(DeserializeState<T> state) => _state = state;

        public long Visit<TCore>(in TCore core)
            where TCore : struct, IRpcCodec<T>
            => StreamCodecStructCore_PumpGenericDeserializeIn<T, TCore>(
                in core, in _state.Payload, _state.ItemCount);
    }

    private static class UnmanagedOperations<T>
        where T : unmanaged
    {
        public static int Size => Unsafe.SizeOf<T>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize(in T value, IBufferWriter<byte> writer)
        {
            var span = writer.GetSpan(Size);
            MemoryMarshal.Write(span, in value);
            writer.Advance(Size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize(in ReadOnlySequence<byte> payload)
            => MemoryMarshal.Read<T>(payload.FirstSpan);
    }

    private sealed class InlineClassCodec<T> : IRpcCodec<T>
        where T : unmanaged
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(in T value, IBufferWriter<byte> buffer)
            => UnmanagedOperations<T>.Serialize(in value, buffer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Deserialize(in ReadOnlySequence<byte> buffer)
            => UnmanagedOperations<T>.Deserialize(in buffer);
    }

    private sealed class InlineSizedClassCodec<T> : IRpcCodec<T>, IRpcSizedCodec<T>
        where T : unmanaged
    {
        public bool CanExactSize => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(in T value, IBufferWriter<byte> buffer)
            => UnmanagedOperations<T>.Serialize(in value, buffer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Deserialize(in ReadOnlySequence<byte> buffer)
            => UnmanagedOperations<T>.Deserialize(in buffer);

        public bool TryGetEncodedSize(in T value, out int size)
        {
            size = UnmanagedOperations<T>.Size;
            return true;
        }

        public bool TryGetEncodedSize(
            in T value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
        {
            size = UnmanagedOperations<T>.Size;
            snapshot = null;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SerializeSized(
            in T value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
            => UnmanagedOperations<T>.Serialize(in value, buffer);

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
        }
    }

    private readonly struct FlatUnsizedCore<T> : IRpcCodec<T>
        where T : unmanaged
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(in T value, IBufferWriter<byte> buffer)
            => UnmanagedOperations<T>.Serialize(in value, buffer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Deserialize(in ReadOnlySequence<byte> buffer)
            => UnmanagedOperations<T>.Deserialize(in buffer);
    }

    private readonly struct FlatSizedCore<T> : IRpcCodec<T>, IRpcSizedCodec<T>
        where T : unmanaged
    {
        public bool CanExactSize => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(in T value, IBufferWriter<byte> buffer)
            => UnmanagedOperations<T>.Serialize(in value, buffer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Deserialize(in ReadOnlySequence<byte> buffer)
            => UnmanagedOperations<T>.Deserialize(in buffer);

        public bool TryGetEncodedSize(in T value, out int size)
        {
            size = UnmanagedOperations<T>.Size;
            return true;
        }

        public bool TryGetEncodedSize(
            in T value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
        {
            size = UnmanagedOperations<T>.Size;
            snapshot = null;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SerializeSized(
            in T value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
            => UnmanagedOperations<T>.Serialize(in value, buffer);

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
        }
    }

    private sealed class FlatUnsizedCodecShell<T> : IRpcCodec<T>, ICoreOpener<T>
        where T : unmanaged
    {
        private readonly FlatUnsizedCore<T> _core = default;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(in T value, IBufferWriter<byte> buffer)
            => _core.Serialize(in value, buffer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Deserialize(in ReadOnlySequence<byte> buffer)
            => _core.Deserialize(in buffer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Open<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreVisitor<T>
            => visitor.Visit(_core);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long OpenIn<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreInVisitor<T>
            => visitor.Visit(in _core);
    }

    private sealed class FlatSizedCodecShell<T> :
        IRpcCodec<T>,
        IRpcSizedCodec<T>,
        ISizedCoreOpener<T>
        where T : unmanaged
    {
        private readonly FlatSizedCore<T> _core = default;

        public bool CanExactSize => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(in T value, IBufferWriter<byte> buffer)
            => _core.Serialize(in value, buffer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Deserialize(in ReadOnlySequence<byte> buffer)
            => _core.Deserialize(in buffer);

        public bool TryGetEncodedSize(in T value, out int size)
            => _core.TryGetEncodedSize(in value, out size);

        public bool TryGetEncodedSize(
            in T value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
            => _core.TryGetEncodedSize(in value, out size, out snapshot);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SerializeSized(
            in T value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
            => _core.SerializeSized(in value, buffer, size, snapshot);

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
            => _core.ReleaseSnapshot(snapshot);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Open<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreVisitor<T>
            => visitor.Visit(_core);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long OpenIn<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreInVisitor<T>
            => visitor.Visit(in _core);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long OpenSized<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ISizedCoreVisitor<T>
            => visitor.Visit(_core);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long OpenSizedIn<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ISizedCoreInVisitor<T>
            => visitor.Visit(in _core);
    }

    private sealed class Nested64InlineUnsizedClassCodec : IRpcCodec<Nested64>
    {
        private readonly IRpcCodec<Payload16> _a;
        private readonly IRpcCodec<Payload16> _b;
        private readonly IRpcCodec<Payload16> _c;
        private readonly IRpcCodec<Payload16> _d;

        public Nested64InlineUnsizedClassCodec(
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
    }

    private sealed class Nested64InlineSizedClassCodec :
        IRpcCodec<Nested64>,
        IRpcSizedCodec<Nested64>
    {
        private readonly IRpcCodec<Payload16> _a;
        private readonly IRpcCodec<Payload16> _b;
        private readonly IRpcCodec<Payload16> _c;
        private readonly IRpcCodec<Payload16> _d;

        public Nested64InlineSizedClassCodec(
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

        public bool TryGetEncodedSize(
            in Nested64 value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
        {
            size = 64;
            snapshot = null;
            return true;
        }

        public void SerializeSized(
            in Nested64 value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
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

    private readonly struct Nested64UnsizedCore : IRpcCodec<Nested64>
    {
        private readonly IRpcCodec<Payload16> _a;
        private readonly IRpcCodec<Payload16> _b;
        private readonly IRpcCodec<Payload16> _c;
        private readonly IRpcCodec<Payload16> _d;

        public Nested64UnsizedCore(
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
    }

    private readonly struct Nested64SizedCore :
        IRpcCodec<Nested64>,
        IRpcSizedCodec<Nested64>
    {
        private readonly IRpcCodec<Payload16> _a;
        private readonly IRpcCodec<Payload16> _b;
        private readonly IRpcCodec<Payload16> _c;
        private readonly IRpcCodec<Payload16> _d;

        public Nested64SizedCore(
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

        public bool TryGetEncodedSize(
            in Nested64 value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
        {
            size = 64;
            snapshot = null;
            return true;
        }

        public void SerializeSized(
            in Nested64 value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
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

    private sealed class Nested64UnsizedCodecShell : IRpcCodec<Nested64>, ICoreOpener<Nested64>
    {
        private readonly Nested64UnsizedCore _core;

        public Nested64UnsizedCodecShell(
            IRpcCodec<Payload16> a,
            IRpcCodec<Payload16> b,
            IRpcCodec<Payload16> c,
            IRpcCodec<Payload16> d)
            => _core = new Nested64UnsizedCore(a, b, c, d);

        public void Serialize(in Nested64 value, IBufferWriter<byte> buffer)
            => _core.Serialize(in value, buffer);

        public Nested64 Deserialize(in ReadOnlySequence<byte> buffer)
            => _core.Deserialize(in buffer);

        public long Open<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreVisitor<Nested64>
            => visitor.Visit(_core);

        public long OpenIn<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreInVisitor<Nested64>
            => visitor.Visit(in _core);
    }

    private sealed class Nested64SizedCodecShell :
        IRpcCodec<Nested64>,
        IRpcSizedCodec<Nested64>,
        ISizedCoreOpener<Nested64>
    {
        private readonly Nested64SizedCore _core;

        public Nested64SizedCodecShell(
            IRpcCodec<Payload16> a,
            IRpcCodec<Payload16> b,
            IRpcCodec<Payload16> c,
            IRpcCodec<Payload16> d)
            => _core = new Nested64SizedCore(a, b, c, d);

        public bool CanExactSize => true;

        public void Serialize(in Nested64 value, IBufferWriter<byte> buffer)
            => _core.Serialize(in value, buffer);

        public Nested64 Deserialize(in ReadOnlySequence<byte> buffer)
            => _core.Deserialize(in buffer);

        public bool TryGetEncodedSize(in Nested64 value, out int size)
            => _core.TryGetEncodedSize(in value, out size);

        public bool TryGetEncodedSize(
            in Nested64 value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
            => _core.TryGetEncodedSize(in value, out size, out snapshot);

        public void SerializeSized(
            in Nested64 value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
            => _core.SerializeSized(in value, buffer, size, snapshot);

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
            => _core.ReleaseSnapshot(snapshot);

        public long Open<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreVisitor<Nested64>
            => visitor.Visit(_core);

        public long OpenIn<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ICoreInVisitor<Nested64>
            => visitor.Visit(in _core);

        public long OpenSized<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ISizedCoreVisitor<Nested64>
            => visitor.Visit(_core);

        public long OpenSizedIn<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ISizedCoreInVisitor<Nested64>
            => visitor.Visit(in _core);
    }

    private sealed class ScratchBufferWriter : IBufferWriter<byte>
    {
        private readonly byte[] _buffer = new byte[2048];
        private int _written;

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public void Reset() => _written = 0;

        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (_written > _buffer.Length - count)
                throw new InvalidOperationException("Scratch writer capacity exceeded.");
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void Ensure(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            if (sizeHint > _buffer.Length - _written)
                throw new InvalidOperationException("Scratch writer capacity exceeded.");
        }
    }

    private static T CreateValue<T>()
        where T : unmanaged
    {
        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<T>()];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = unchecked((byte)(0x2a + index));
        return MemoryMarshal.Read<T>(bytes);
    }

    private static ReadOnlySequence<byte> CreatePayload<T>(in T value)
        where T : unmanaged
    {
        var bytes = new byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(bytes.AsSpan(), in value);
        return new ReadOnlySequence<byte>(bytes);
    }

    private static int PayloadSize(string payload)
        => payload switch
        {
            "int" or "int-open-cost" => 4,
            "long" => 8,
            "guid" or "payload16" => 16,
            "payload64" or "generated-like64" => 64,
            "payload256" => 256,
            _ => 0
        };

    private static int DivideRoundUp(int value, int divisor)
        => checked((value + divisor - 1) / divisor);

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(static value => value).ToArray();
        var middle = ordered.Length / 2;
        return (ordered.Length & 1) == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }

    private static void WriteJson(
        string outputPath,
        string shape,
        IReadOnlyList<Measurement> measurements)
    {
        using var stream = File.Create(outputPath);
        using var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteString(
            "commit",
            Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown");
        writer.WriteString("shape", shape);
        writer.WriteString("framework", RuntimeInformation.FrameworkDescription);
        writer.WriteString("os", RuntimeInformation.OSDescription);
        writer.WriteString("architecture", RuntimeInformation.ProcessArchitecture.ToString());
        writer.WriteNumber("processorCount", Environment.ProcessorCount);
        writer.WriteString(
            "tieredCompilation",
            Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default");
        writer.WriteString(
            "tieredPgo",
            Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "default");
        writer.WriteNumber("rounds", Rounds);
        writer.WriteNumber("targetItemsPerRound", TargetItemsPerRound);
        writer.WriteStartArray("notes");
        writer.WriteStringValue("production-interface models the post-#724 baseline: capability is already hoisted to stream scope while item calls stay interface-based.");
        writer.WriteStringValue("shell-interface keeps the same per-item interface calls but makes the class implementation forward into a readonly struct core.");
        writer.WriteStringValue("open-value performs one erased class/opener dispatch per stream, then runs the full item loop as Pump<T,TCore> with a concrete struct core passed by value.");
        writer.WriteStringValue("open-in is the same architecture but opens and passes TCore by readonly reference to measure struct-copy tradeoffs.");
        writer.WriteStringValue("direct-core models a statically-known generated caller entering Pump<T,TCore> without the one-per-stream opener dispatch.");
        writer.WriteStringValue("generated-like64 uses a multi-reference struct core that retains child IRpcCodec<Payload16> interface fields, modeling residual child-codec dispatch.");
        writer.WriteStringValue("1/8/64 item int-open-cost rows quantify opener amortization; the issue matrix rows remain 1k/10k/100k.");
        writer.WriteEndArray();
        writer.WriteStartArray("measurements");

        foreach (var measurement in measurements)
        {
            writer.WriteStartObject();
            writer.WriteString("payload", measurement.Payload);
            writer.WriteNumber("payloadBytes", measurement.PayloadBytes);
            writer.WriteString("profile", measurement.Profile);
            writer.WriteString("operation", measurement.Operation);
            writer.WriteNumber("streamLength", measurement.StreamLength);
            writer.WriteNumber("streamsPerRound", measurement.StreamsPerRound);
            writer.WriteNumber("coreSizeBytes", measurement.CoreSizeBytes);
            writer.WriteNumber("nanosecondsPerItem", measurement.NanosecondsPerItem);
            writer.WriteNumber("cpuNanosecondsPerItem", measurement.CpuNanosecondsPerItem);
            writer.WriteNumber("allocatedBytesPerItem", measurement.AllocatedBytesPerItem);
            writer.WriteNumber("checksum", measurement.Checksum);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Payload16
    {
        public long A;
        public long B;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Payload64
    {
        public long A;
        public long B;
        public long C;
        public long D;
        public long E;
        public long F;
        public long G;
        public long H;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Payload256
    {
        public Payload64 A;
        public Payload64 B;
        public Payload64 C;
        public Payload64 D;
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
        int PayloadBytes,
        string Profile,
        string Operation,
        int StreamLength,
        int StreamsPerRound,
        int CoreSizeBytes,
        double NanosecondsPerItem,
        double CpuNanosecondsPerItem,
        double AllocatedBytesPerItem,
        long Checksum);
}
