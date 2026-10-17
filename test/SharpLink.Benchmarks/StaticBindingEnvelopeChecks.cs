#if SHARPLINK_STATIC_BINDING_PROTOTYPE
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

internal static class StaticBindingEnvelopeChecks
{
    internal static int Run(RpcSession session, RpcMethodDescriptor descriptor)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            StaticBindingEvidence.Require(condition, message);
            checks++;
        }
        var method = new StaticBindingMethod(descriptor, false);
        var previousMethod = new StaticBindingMethod(descriptor, true);
        var metadata = new SharpLinkMetadata(new KeyValuePair<string, string>("tenant", "envelope"));
        foreach (var deadline in new[] { default(RpcDeadline), RpcDeadline.FromTimestamp(long.MaxValue),
            RpcDeadline.Create(TimeSpan.FromTicks(73), long.MaxValue - 1, 10000000) })
            foreach (var headers in new SharpLinkMetadata?[] { null, metadata })
                foreach (var previous in new StaticBindingMethod?[] { null, previousMethod })
                    foreach (var id in new[] { 0L, long.MinValue, long.MaxValue, 0x100000001L })
                        foreach (var split in new[] { -1, 0, 3, 8 })
                        {
                            var bytes = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
                            var arguments = Sequence(bytes, split);
                            var original = new ServerRequestEnvelope(id, descriptor.MethodId, arguments, deadline, headers)
                            { BoundMethod = previous };
                            var expected = original with { BoundMethod = method };
                            var actual = new ServerRequestEnvelope(original.InterfaceHash, original.MethodHash,
                                original.Arguments, original.RpcDeadline, original.Metadata, method);
                            Check(actual.Equals(expected), "Resolved constructor must equal the original with expression");
                            Check(ReferenceEquals(actual.BoundMethod, method) && ReferenceEquals(original.BoundMethod, previous),
                                "Exact method identity and immutable original");
                            Check(ReferenceEquals(actual.Metadata, headers) && actual.RpcDeadline.Equals(deadline),
                                "Metadata identity and all monotonic deadline fields survive reconstruction");
                            Check(actual.Arguments.Start.Equals(arguments.Start) && actual.Arguments.End.Equals(arguments.End),
                                "Argument sequence positions and owner are retained, not copied");
                            bytes[7] = 99;
                            Check(actual.Arguments.ToArray()[7] == 99, "Argument slice still aliases its original owner");
                        }
        foreach (var timed in new[] { false, true })
            foreach (var hasMetadata in new[] { false, true })
                foreach (var split in new[] { -1, 0, 8, 16 })
                {
                    var flags = ProtocolV2FrameFlags.Compressed | ProtocolV2FrameFlags.HasReturn;
                    if (timed) flags |= ProtocolV2FrameFlags.HasTimeBudget;
                    if (hasMetadata) flags |= ProtocolV2FrameFlags.HasMetadata;
                    var encoded = Sequence(Build(timed, hasMetadata, 4), split);
                    var decodedBytes = Build(timed, hasMetadata, 12);
                    var decoded = new ReadOnlySequence<byte>(decodedBytes);
                    var original = ServerRequestEnvelopeReader.Read(session, encoded, flags, 65536, TimeProvider.System);
                    original = new ServerRequestEnvelope(original.InterfaceHash, original.MethodHash,
                        original.Arguments, original.RpcDeadline, original.Metadata, method);
                    var result = ServerRequestEnvelopeReader.ReadDecoded(session, decoded, encoded, in original,
                        flags, 65536, TimeProvider.System);
                    var expected = ServerRequestEnvelopeReader.Read(session, decoded, flags, 65536,
                        TimeProvider.System, original.RpcDeadline);
                    Check(result.InterfaceHash == expected.InterfaceHash && result.MethodHash == expected.MethodHash,
                        "Decoded routing fields survive metadata reuse and fragmented fallback");
                    Check(ReferenceEquals(result.BoundMethod, method) && result.RpcDeadline.Equals(original.RpcDeadline),
                        "Decode preserves validated method identity and exact original deadline");
                    Check(result.Arguments.ToArray().SequenceEqual(expected.Arguments.ToArray()), "Decoded payload slice");
                    Check((result.Metadata is null && expected.Metadata is null) ||
                        (result.Metadata is not null && expected.Metadata is not null && result.Metadata.SequenceEqual(expected.Metadata)),
                        "Decoded metadata content");
                    decodedBytes[^1] = 77;
                    Check(result.Arguments.ToArray()[^1] == 77, "Decode aliases decoded owner, not encoded owner");
                }
        Console.WriteLine($"STATIC_BINDING_ENVELOPE_PASS checks={checks}");
        return checks;
    }

    private static byte[] Build(bool timed, bool hasMetadata, int count)
    {
        var writer = new ArrayBufferWriter<byte>();
        var prefix = writer.GetSpan(16);
        BinaryPrimitives.WriteInt64LittleEndian(prefix, 0x100000001L);
        BinaryPrimitives.WriteInt64LittleEndian(prefix[8..], 456);
        writer.Advance(16);
        if (timed)
        {
            BinaryPrimitives.WriteInt64LittleEndian(writer.GetSpan(8), TimeSpan.TicksPerSecond);
            writer.Advance(8);
        }
        if (hasMetadata)
        {
            var metadata = new ArrayBufferWriter<byte>();
            ProtocolV2PayloadCodec.WriteMetadata(metadata,
                new SharpLinkMetadata(new KeyValuePair<string, string>("tenant", "envelope")));
            ProtocolV2PayloadCodec.WriteVarUInt32(writer, (uint)metadata.WrittenCount);
            writer.Write(metadata.WrittenSpan);
        }
        writer.Write(new byte[count]);
        return writer.WrittenSpan.ToArray();
    }

    private static ReadOnlySequence<byte> Sequence(byte[] bytes, int split)
    {
        if (split < 0) return new ReadOnlySequence<byte>(bytes);
        var first = new Segment(bytes.AsMemory(0, split));
        var last = first.Append(bytes.AsMemory(split));
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        internal Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
#endif
