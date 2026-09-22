#if GENERATED_BINDING_PAIRED
using System.Buffers;
using System.Runtime.CompilerServices;
using SharpLink.Abstractions;

namespace SharpLink.StreamCodecStructCoreEvidence;

internal static partial class GeneratedCodecBindingEvidenceRunner
{
    private sealed class Post727Nested64Codec :
        IRpcCodec<Nested64>,
        IRpcSizedCodec<Nested64>
    {
        internal readonly Payload16Codec A;
        internal readonly Payload16Codec B;
        internal readonly Payload16Codec C;
        internal readonly Payload16Codec D;

        internal Post727Nested64Codec(
            Payload16Codec a,
            Payload16Codec b,
            Payload16Codec c,
            Payload16Codec d)
        {
            A = a;
            B = b;
            C = c;
            D = d;
        }

        internal Post727Nested64Core Core => new(A, B, C, D);
        internal Post727StateRefNested64Core OwnerCore => new(this);

        public bool CanExactSize => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(in Nested64 value, IBufferWriter<byte> buffer)
        {
            A.Serialize(in value.A, buffer);
            B.Serialize(in value.B, buffer);
            C.Serialize(in value.C, buffer);
            D.Serialize(in value.D, buffer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Nested64 Deserialize(in ReadOnlySequence<byte> buffer)
            => new()
            {
                A = A.Deserialize(buffer.Slice(0, 16)),
                B = B.Deserialize(buffer.Slice(16, 16)),
                C = C.Deserialize(buffer.Slice(32, 16)),
                D = D.Deserialize(buffer.Slice(48, 16))
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SerializeSized(
            in Nested64 value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
        {
            A.SerializeSized(in value.A, buffer, 16, null);
            B.SerializeSized(in value.B, buffer, 16, null);
            C.SerializeSized(in value.C, buffer, 16, null);
            D.SerializeSized(in value.D, buffer, 16, null);
        }

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
        }
    }

    private readonly struct Post727Nested64Core :
        IRpcCodec<Nested64>,
        IRpcSizedCodec<Nested64>
    {
        private readonly Payload16Codec _a;
        private readonly Payload16Codec _b;
        private readonly Payload16Codec _c;
        private readonly Payload16Codec _d;

        internal Post727Nested64Core(
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(in Nested64 value, IBufferWriter<byte> buffer)
        {
            _a.Serialize(in value.A, buffer);
            _b.Serialize(in value.B, buffer);
            _c.Serialize(in value.C, buffer);
            _d.Serialize(in value.D, buffer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
        }
    }

    private readonly struct Post727StateRefNested64Core :
        IRpcCodec<Nested64>,
        IRpcSizedCodec<Nested64>
    {
        private readonly Post727Nested64Codec _owner;

        internal Post727StateRefNested64Core(Post727Nested64Codec owner)
            => _owner = owner;

        public bool CanExactSize => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(in Nested64 value, IBufferWriter<byte> buffer)
        {
            _owner.A.Serialize(in value.A, buffer);
            _owner.B.Serialize(in value.B, buffer);
            _owner.C.Serialize(in value.C, buffer);
            _owner.D.Serialize(in value.D, buffer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Nested64 Deserialize(in ReadOnlySequence<byte> buffer)
            => new()
            {
                A = _owner.A.Deserialize(buffer.Slice(0, 16)),
                B = _owner.B.Deserialize(buffer.Slice(16, 16)),
                C = _owner.C.Deserialize(buffer.Slice(32, 16)),
                D = _owner.D.Deserialize(buffer.Slice(48, 16))
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SerializeSized(
            in Nested64 value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
        {
            _owner.A.SerializeSized(in value.A, buffer, 16, null);
            _owner.B.SerializeSized(in value.B, buffer, 16, null);
            _owner.C.SerializeSized(in value.C, buffer, 16, null);
            _owner.D.SerializeSized(in value.D, buffer, 16, null);
        }

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
        {
        }
    }


}
#endif
