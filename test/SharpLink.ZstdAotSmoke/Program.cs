using System;
using System.Buffers;
using System.Linq;
using SharpLink.Compression.Zstd;
using SharpLink.Runtime;

var provider = new SharpLinkZstdCompressionProvider();
var source = new byte[256 * 1024];
var token = "SharpLink-Zstd-NativeAOT-smoke|"u8;
for (var offset = 0; offset < source.Length; offset += token.Length)
    token[..Math.Min(token.Length, source.Length - offset)].CopyTo(source.AsSpan(offset));

using var compressed = new PooledByteBufferWriter(source.Length);
if (!provider.TryCompress(CreateSegmented(source, 997), compressed, source.Length))
    throw new InvalidOperationException("Zstd NativeAOT compression did not fit.");

using var decoded = new PooledByteBufferWriter(source.Length);
provider.Decompress(CreateSegmented(compressed.WrittenMemory.ToArray(), 113), decoded, source.Length);
if (decoded.WrittenCount != source.Length || !decoded.WrittenMemory.Span.SequenceEqual(source))
    throw new InvalidOperationException("Zstd NativeAOT round-trip mismatch.");

Console.WriteLine($"ZSTD_AOT_PASS compressed={compressed.WrittenCount} original={source.Length}");
return;

static ReadOnlySequence<byte> CreateSegmented(byte[] bytes, int segmentSize)
{
    Segment? first = null;
    Segment? last = null;
    for (var offset = 0; offset < bytes.Length; offset += segmentSize)
    {
        var segment = new Segment(bytes.AsMemory(offset, Math.Min(segmentSize, bytes.Length - offset)));
        if (first is null)
            first = segment;
        else
            last!.SetNext(segment);
        last = segment;
    }
    return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
}

sealed class Segment : ReadOnlySequenceSegment<byte>
{
    internal Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

    internal void SetNext(Segment next)
    {
        next.RunningIndex = RunningIndex + Memory.Length;
        Next = next;
    }
}
