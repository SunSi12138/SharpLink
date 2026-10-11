using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal static class PayloadEvidence
{
    internal static string VerifyAndDescribe(Options options, EvidenceEnvironment environment, byte[] payload)
    {
        var builder = new StringBuilder();
        if (options.UsesSizedDto)
        {
            var metadata = environment.GrantRpc.ProbeCodec(environment.Runtime);
            Add(builder, "actualItemCodec", metadata.CodecType);
            Add(builder, "actualItemCanExactSize", metadata.CanExactSize ? "true" : "false");
            Add(builder, "actualEncodedItemBytes", metadata.EncodedBytes);
            Add(builder, "actualDtoTextCharacters", metadata.TextCharacters);
            Add(builder, "actualDtoTag", metadata.Tag);
            Add(builder, "actualUncompressedStreamDataFrameBytes", metadata.UncompressedStreamDataFrameBytes);
            Add(builder, "actualEncodedItemSha256", metadata.EncodedSha256);
            Add(builder, "codecPreflight", "generated-sized-vs-regular-byte-identity-and-full-roundtrip");
        }
        else
        {
            var codec = environment.Runtime.GetManifestCodecProvider(typeof(IBenchmarkRpc).Assembly)
                .GetCodec<byte[]>();
            var sized = codec as IRpcSizedCodec<byte[]>;
            if (sized?.CanExactSize == true)
                throw new InvalidOperationException("The declared byte-array fallback control unexpectedly became sized.");
            using var writer = new PooledByteBufferWriter(checked(payload.Length + 64));
            codec.Serialize(payload, writer);
            var decoded = codec.Deserialize(new ReadOnlySequence<byte>(writer.WrittenMemory));
            if (!decoded.AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("The actual byte-array codec failed full preflight validation.");
            Add(builder, "actualItemCodec", codec.GetType().FullName ?? codec.GetType().Name);
            Add(builder, "actualItemCanExactSize", "false");
            Add(builder, "actualEncodedItemBytes", writer.WrittenCount);
            Add(builder, "actualUncompressedStreamDataFrameBytes",
                checked(ProtocolV2Constants.HeaderBytes + sizeof(ushort) + writer.WrittenCount));
            Add(builder, "actualEncodedItemSha256", Convert.ToHexString(SHA256.HashData(writer.WrittenSpan)));
            Add(builder, "codecPreflight", "original-unsized-codec-full-roundtrip");
        }
        Add(builder, "codecPreflightIncludedInMeasurement", "false");
        return builder.ToString();
    }

    private static void Add(StringBuilder builder, string key, string value)
        => builder.Append(key).Append('=').AppendLine(value);

    private static void Add(StringBuilder builder, string key, long value)
        => Add(builder, key, value.ToString(CultureInfo.InvariantCulture));
}
