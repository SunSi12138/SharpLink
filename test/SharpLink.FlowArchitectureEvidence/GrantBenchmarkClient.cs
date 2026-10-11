using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal readonly record struct CodecMetadata(
    string CodecType, bool CanExactSize, int EncodedBytes, int TextCharacters, ushort Tag,
    int UncompressedStreamDataFrameBytes, string EncodedSha256);

// One immutable workload adapter can be shared by all workers. The payload and input reference
// array are created outside measurement, just like the byte-array benchmark's input fixtures.
internal sealed class GrantBenchmarkClient
{
    private readonly IGrantBenchmarkRpc _rpc;
    private readonly int _itemCount;
    private readonly int _encodedBytes;
    private readonly GrantBenchmarkPayload _expected;
    private readonly GrantBenchmarkPayload[] _values;

    internal GrantBenchmarkClient(IGrantBenchmarkRpc rpc, int itemCount, int targetEncodedBytes)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        _rpc = rpc;
        _itemCount = itemCount;
        _encodedBytes = targetEncodedBytes;
        _expected = GrantBenchmarkPayload.Create(targetEncodedBytes);
        _values = new GrantBenchmarkPayload[itemCount];
        Array.Fill(_values, _expected);
    }

    internal ValueTask<int> ProbeAsync() => _rpc.ProbeAsync();

    internal CodecMetadata ProbeCodec(SharpLinkRuntimeContext runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var codec = runtime.GetManifestCodecProvider(typeof(IGrantBenchmarkRpc).Assembly)
            .GetCodec<GrantBenchmarkPayload>();
        var codecType = codec.GetType().FullName ?? codec.GetType().Name;
        if (codec is not IRpcSizedCodec<GrantBenchmarkPayload> sized || !sized.CanExactSize)
            throw new InvalidOperationException("Generated DTO codec is not exact-size eligible: " + codecType);

        IRpcSizedCodecSnapshot? snapshot = null;
        try
        {
            if (!sized.TryGetEncodedSize(_expected, out var encodedBytes, out snapshot))
                throw new InvalidOperationException("The generated DTO codec declined the actual workload value.");
            if (encodedBytes != _encodedBytes)
                throw new InvalidDataException(
                    $"Generated DTO encoded-size formula failed: measured {encodedBytes}, requested {_encodedBytes}.");

            using var sizedWriter = new PooledByteBufferWriter(Math.Max(32, encodedBytes));
            using var regularWriter = new PooledByteBufferWriter(Math.Max(32, encodedBytes));
            sized.SerializeSized(_expected, sizedWriter, encodedBytes, snapshot);
            codec.Serialize(_expected, regularWriter);
            if (sizedWriter.WrittenCount != encodedBytes || regularWriter.WrittenCount != encodedBytes ||
                !sizedWriter.WrittenSpan.SequenceEqual(regularWriter.WrittenSpan))
                throw new InvalidDataException("Generated sized and regular serialization produced different bytes.");
            var decoded = codec.Deserialize(new ReadOnlySequence<byte>(sizedWriter.WrittenMemory));
            Validate(decoded);
            return new CodecMetadata(codecType, sized.CanExactSize, encodedBytes,
                _expected.Text.Length, _expected.Tag,
                checked(ProtocolV2Constants.HeaderBytes + sizeof(ushort) + encodedBytes),
                Convert.ToHexString(SHA256.HashData(sizedWriter.WrittenSpan)));
        }
        finally
        {
            sized.ReleaseSnapshot(snapshot);
        }
    }

    internal async ValueTask InvokeAsync(string shape)
    {
        if (shape == "Unary")
        {
            Validate(await _rpc.EchoAsync(_expected).ConfigureAwait(false));
            return;
        }
        if (shape == "Client")
        {
            var total = await _rpc.UploadAsync(BenchmarkEnvironment.ToStream(_values)).ConfigureAwait(false);
            var expected = checked((long)_itemCount * _encodedBytes);
            if (total != expected)
                throw new InvalidDataException($"Generated DTO upload total mismatch: {total} != {expected}.");
            return;
        }
        if (shape is not ("Server" or "Duplex"))
            throw new ArgumentException("Unknown generated DTO RPC shape: " + shape, nameof(shape));
        var stream = shape == "Server"
            ? _rpc.DownloadAsync(_itemCount, _encodedBytes)
            : _rpc.DuplexAsync(BenchmarkEnvironment.ToStream(_values));
        var count = 0;
        await foreach (var value in stream.ConfigureAwait(false))
        {
            Validate(value);
            if (++count > _itemCount)
                throw new InvalidDataException("Generated DTO response produced too many items.");
        }
        if (count != _itemCount)
            throw new InvalidDataException($"Generated DTO response item count mismatch: {count} != {_itemCount}.");
    }

    private void Validate(GrantBenchmarkPayload value)
    {
        if (value is null || value.Tag != _expected.Tag ||
            !string.Equals(value.Text, _expected.Text, StringComparison.Ordinal))
            throw new InvalidDataException("Outgoing generated DTO failed complete member validation.");
    }
}
