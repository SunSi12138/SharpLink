using System;
using System.Buffers;
using System.Collections.Generic;
using SharpLink.Abstractions;

namespace SharpLink.StreamCodecStructCoreEvidence;

internal static partial class GeneratedCodecBindingEvidenceRunner
{
    private static readonly int[] StateLayoutStreamLengths = [10_000, 100_000];

    private static void AddStateLayouts(string shape, List<Measurement> rows)
    {
        var child = new Payload16Codec();
        var value = CreateValue<Nested64>();
        var payload = CreatePayload(in value);
        var writer = new ScratchBufferWriter();

        AddStateLayout(
            shape,
            rows,
            "generated-like64-concrete4",
            new FourConcreteCodec(child, child, child, child),
            value,
            payload,
            writer);

        AddStateLayout(
            shape,
            rows,
            "generated-like64-state-interface",
            new StateInterfaceCodec(child, child, child, child),
            value,
            payload,
            writer);

        AddStateLayout(
            shape,
            rows,
            "generated-like64-state-concrete",
            new StateConcreteCodec(child, child, child, child),
            value,
            payload,
            writer);
    }

    private static void AddStateLayout<TCore>(
        string shape,
        List<Measurement> rows,
        string payloadName,
        IStateLayoutCodec<TCore> codec,
        Nested64 value,
        ReadOnlySequence<byte> payload,
        ScratchBufferWriter writer)
        where TCore : struct, IRpcCodec<Nested64>, IRpcSizedCodec<Nested64>
    {
        var binding = new StateLayoutBinding<TCore>(codec, codec.Core);
        foreach (var length in StateLayoutStreamLengths)
        {
            rows.Add(Measure(
                payloadName,
                "stream-sized-serialize",
                length,
                count => binding.RunSizedStreams(shape, value, writer, length, count)));
            rows.Add(Measure(
                payloadName,
                "stream-deserialize",
                length,
                count => binding.RunDeserializeStreams(shape, payload, length, count)));
        }
    }

    private interface IStateLayoutCodec<TCore> : IRpcCodec<Nested64>, IRpcSizedCodec<Nested64>
        where TCore : struct, IRpcCodec<Nested64>, IRpcSizedCodec<Nested64>
    {
        TCore Core { get; }
    }

    private sealed class StateLayoutBinding<TCore>
        where TCore : struct, IRpcCodec<Nested64>, IRpcSizedCodec<Nested64>
    {
        private readonly IRpcCodec<Nested64> _resolved;
        private readonly IRpcSizedCodec<Nested64> _sized;
        private readonly TCore _core;

        internal StateLayoutBinding(IStateLayoutCodec<TCore> resolved, TCore core)
        {
            _resolved = resolved;
            _sized = resolved;
            _core = core;
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
                checksum += shape == "interface"
                    ? PumpInterfaceSized(_resolved, _sized, in value, writer, length)
                    : PumpSized<Nested64, TCore>(_core, in value, writer, length);
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
                checksum += shape == "interface"
                    ? PumpInterfaceDeserialize(_resolved, in payload, length)
                    : PumpDeserialize<Nested64, TCore>(_core, in payload, length);
            }
            return checksum;
        }
    }

    private sealed class FourConcreteCodec : IStateLayoutCodec<FourConcreteCore>
    {
        private readonly FourConcreteCore _core;

        internal FourConcreteCodec(
            Payload16Codec a,
            Payload16Codec b,
            Payload16Codec c,
            Payload16Codec d)
            => _core = new FourConcreteCore(a, b, c, d);

        public FourConcreteCore Core => _core;
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

    private readonly struct FourConcreteCore : IRpcCodec<Nested64>, IRpcSizedCodec<Nested64>
    {
        private readonly Payload16Codec _a;
        private readonly Payload16Codec _b;
        private readonly Payload16Codec _c;
        private readonly Payload16Codec _d;

        internal FourConcreteCore(
            Payload16Codec a,
            Payload16Codec b,
            Payload16Codec c,
            Payload16Codec d)
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
            _a.SerializeSized(in value.A, buffer, 16, null);
            _b.SerializeSized(in value.B, buffer, 16, null);
            _c.SerializeSized(in value.C, buffer, 16, null);
            _d.SerializeSized(in value.D, buffer, 16, null);
        }

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot) { }
    }

    private sealed class InterfaceState
    {
        internal readonly IRpcCodec<Payload16> A;
        internal readonly IRpcCodec<Payload16> B;
        internal readonly IRpcCodec<Payload16> C;
        internal readonly IRpcCodec<Payload16> D;

        internal InterfaceState(
            IRpcCodec<Payload16> a,
            IRpcCodec<Payload16> b,
            IRpcCodec<Payload16> c,
            IRpcCodec<Payload16> d)
            => (A, B, C, D) = (a, b, c, d);
    }

    private sealed class StateInterfaceCodec : IStateLayoutCodec<StateInterfaceCore>
    {
        private readonly StateInterfaceCore _core;

        internal StateInterfaceCodec(
            IRpcCodec<Payload16> a,
            IRpcCodec<Payload16> b,
            IRpcCodec<Payload16> c,
            IRpcCodec<Payload16> d)
            => _core = new StateInterfaceCore(new InterfaceState(a, b, c, d));

        public StateInterfaceCore Core => _core;
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

    private readonly struct StateInterfaceCore : IRpcCodec<Nested64>, IRpcSizedCodec<Nested64>
    {
        private readonly InterfaceState _state;

        internal StateInterfaceCore(InterfaceState state) => _state = state;

        public bool CanExactSize => true;
        public void Serialize(in Nested64 value, IBufferWriter<byte> buffer)
        {
            _state.A.Serialize(in value.A, buffer);
            _state.B.Serialize(in value.B, buffer);
            _state.C.Serialize(in value.C, buffer);
            _state.D.Serialize(in value.D, buffer);
        }

        public Nested64 Deserialize(in ReadOnlySequence<byte> buffer)
            => new()
            {
                A = _state.A.Deserialize(buffer.Slice(0, 16)),
                B = _state.B.Deserialize(buffer.Slice(16, 16)),
                C = _state.C.Deserialize(buffer.Slice(32, 16)),
                D = _state.D.Deserialize(buffer.Slice(48, 16))
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
            ((IRpcSizedCodec<Payload16>)_state.A).SerializeSized(in value.A, buffer, 16, null);
            ((IRpcSizedCodec<Payload16>)_state.B).SerializeSized(in value.B, buffer, 16, null);
            ((IRpcSizedCodec<Payload16>)_state.C).SerializeSized(in value.C, buffer, 16, null);
            ((IRpcSizedCodec<Payload16>)_state.D).SerializeSized(in value.D, buffer, 16, null);
        }

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot) { }
    }

    private sealed class ConcreteState
    {
        internal readonly Payload16Codec A;
        internal readonly Payload16Codec B;
        internal readonly Payload16Codec C;
        internal readonly Payload16Codec D;

        internal ConcreteState(
            Payload16Codec a,
            Payload16Codec b,
            Payload16Codec c,
            Payload16Codec d)
            => (A, B, C, D) = (a, b, c, d);
    }

    private sealed class StateConcreteCodec : IStateLayoutCodec<StateConcreteCore>
    {
        private readonly StateConcreteCore _core;

        internal StateConcreteCodec(
            Payload16Codec a,
            Payload16Codec b,
            Payload16Codec c,
            Payload16Codec d)
            => _core = new StateConcreteCore(new ConcreteState(a, b, c, d));

        public StateConcreteCore Core => _core;
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

    private readonly struct StateConcreteCore : IRpcCodec<Nested64>, IRpcSizedCodec<Nested64>
    {
        private readonly ConcreteState _state;

        internal StateConcreteCore(ConcreteState state) => _state = state;

        public bool CanExactSize => true;
        public void Serialize(in Nested64 value, IBufferWriter<byte> buffer)
        {
            _state.A.Serialize(in value.A, buffer);
            _state.B.Serialize(in value.B, buffer);
            _state.C.Serialize(in value.C, buffer);
            _state.D.Serialize(in value.D, buffer);
        }

        public Nested64 Deserialize(in ReadOnlySequence<byte> buffer)
            => new()
            {
                A = _state.A.Deserialize(buffer.Slice(0, 16)),
                B = _state.B.Deserialize(buffer.Slice(16, 16)),
                C = _state.C.Deserialize(buffer.Slice(32, 16)),
                D = _state.D.Deserialize(buffer.Slice(48, 16))
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
            _state.A.SerializeSized(in value.A, buffer, 16, null);
            _state.B.SerializeSized(in value.B, buffer, 16, null);
            _state.C.SerializeSized(in value.C, buffer, 16, null);
            _state.D.SerializeSized(in value.D, buffer, 16, null);
        }

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot) { }
    }
}
