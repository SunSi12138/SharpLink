using System.Buffers;
using System.IO.Compression;
using System.Text.Json;
using SharpLink.Compression.Zstd;

var records = new List<object>();
var sizes = new[] { 4 * 1024, 64 * 1024, 256 * 1024, 1024 * 1024 };
var patterns = new[] { "dto", "mixed", "random" };
foreach (var size in sizes)
{
    foreach (var pattern in patterns)
    {
        var payload = CreatePayload(size, pattern);
        var sharp = CompressSharp(payload);
        var bcl = CompressBcl(payload);
        DecodeSharp(sharp, payload);
        DecodeBcl(sharp, payload);
        DecodeSharp(bcl, payload);
        DecodeBcl(bcl, payload);
        records.Add(new { size, pattern, sharpBytes = sharp.Length, bclBytes = bcl.Length, sharpToSharp = "pass", sharpToBcl = "pass", bclToSharp = "pass", bclToBcl = "pass" });
    }
}
var probe = CreatePayload(64 * 1024, "dto");
var sharpProbe = CompressSharp(probe);
var bclProbe = CompressBcl(probe);
AssertRejectsBoth(MutateChecksum(sharpProbe), probe.Length, "checksum corruption");
AssertRejectsBoth(sharpProbe[..^1], probe.Length, "truncated frame");
AssertRejectsBoth([.. sharpProbe, 0x00], probe.Length, "trailing byte");
AssertRejectsBoth([.. sharpProbe, .. sharpProbe], probe.Length, "concatenated frame");
AssertRejectsBoth(MutateChecksum(bclProbe), probe.Length, "BCL checksum corruption");
AssertRejectsBoth(bclProbe[..^1], probe.Length, "BCL truncated frame");
AssertRejectsBoth([.. bclProbe, 0x00], probe.Length, "BCL trailing byte");
AssertRejectsBoth([.. bclProbe, .. bclProbe], probe.Length, "BCL concatenated frame");
AssertSharpBound(sharpProbe, probe.Length - 1);
AssertBclBound(sharpProbe, probe.Length - 1);
var report = new { sdk = Environment.Version.ToString(), profile = SharpLinkZstdCompressionProvider.Profile, windowLog2 = SharpLinkZstdCompressionProvider.WindowLog2, platform = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, records, corruption = "pass", truncation = "pass", trailing = "pass", concatenated = "pass", boundedDecompression = "pass" };
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
var output = Environment.GetEnvironmentVariable("SHARPLINK_ZSTD_BCL_EVIDENCE");
if (!string.IsNullOrWhiteSpace(output)) File.WriteAllText(output, json);
Console.Error.WriteLine("ZSTD_BCL_INTEROP_PASS");

static byte[] CompressSharp(byte[] source)
{
    var provider = new SharpLinkZstdCompressionProvider(3);
    var writer = new ArrayBufferWriter<byte>(source.Length + 1024);
    if (!provider.TryCompress(new ReadOnlySequence<byte>(source), writer, checked(source.Length + 1024))) throw new InvalidOperationException("SharpLink Zstd candidate did not fit test bound.");
    return writer.WrittenSpan.ToArray();
}
static byte[] CompressBcl(byte[] source)
{
    using var output = new MemoryStream();
    var options = new ZstandardCompressionOptions { Quality = 3, WindowLog2 = SharpLinkZstdCompressionProvider.WindowLog2, AppendChecksum = true };
    using (var stream = new ZstandardStream(output, options, leaveOpen: true)) stream.Write(source);
    return output.ToArray();
}
static void DecodeSharp(byte[] compressed, byte[] expected)
{
    var writer = new ArrayBufferWriter<byte>(expected.Length);
    new SharpLinkZstdCompressionProvider().Decompress(new ReadOnlySequence<byte>(compressed), writer, expected.Length);
    if (!writer.WrittenSpan.SequenceEqual(expected)) throw new InvalidDataException("SharpLink backend produced mismatched bytes.");
}
static void DecodeBcl(byte[] compressed, byte[] expected)
{
    using var decoder = new ZstandardDecoder(SharpLinkZstdCompressionProvider.WindowLog2);
    var output = new byte[expected.Length + 1];
    var status = decoder.Decompress(compressed, output, out var consumed, out var written);
    if (status != OperationStatus.Done || consumed != compressed.Length || written != expected.Length || !output.AsSpan(0, written).SequenceEqual(expected)) throw new InvalidDataException($"BCL strict decode failed: status={status}, consumed={consumed}/{compressed.Length}, written={written}/{expected.Length}.");
}
static void AssertRejectsBoth(byte[] compressed, int outputLength, string scenario)
{
    var sharpRejected = false;
    try { var writer = new ArrayBufferWriter<byte>(outputLength); new SharpLinkZstdCompressionProvider().Decompress(new ReadOnlySequence<byte>(compressed), writer, outputLength); }
    catch (InvalidDataException) { sharpRejected = true; }
    if (!sharpRejected) throw new InvalidOperationException($"SharpLink backend accepted {scenario}.");
    using var decoder = new ZstandardDecoder(SharpLinkZstdCompressionProvider.WindowLog2);
    var output = new byte[outputLength + 1];
    var status = decoder.Decompress(compressed, output, out var consumed, out _);
    if (status == OperationStatus.Done && consumed == compressed.Length) throw new InvalidOperationException($"BCL strict wrapper accepted {scenario}.");
}
static void AssertSharpBound(byte[] compressed, int maxOutput)
{
    try { var writer = new ArrayBufferWriter<byte>(maxOutput); new SharpLinkZstdCompressionProvider().Decompress(new ReadOnlySequence<byte>(compressed), writer, maxOutput); }
    catch (InvalidDataException) { return; }
    throw new InvalidOperationException("SharpLink backend did not enforce decompression bound.");
}
static void AssertBclBound(byte[] compressed, int maxOutput)
{
    using var decoder = new ZstandardDecoder(SharpLinkZstdCompressionProvider.WindowLog2);
    var output = new byte[maxOutput];
    var status = decoder.Decompress(compressed, output, out _, out _);
    if (status != OperationStatus.DestinationTooSmall) throw new InvalidOperationException($"BCL backend did not surface output bound: {status}.");
}
static byte[] MutateChecksum(byte[] compressed) { var result = compressed.ToArray(); result[^1] ^= 0x40; return result; }
static byte[] CreatePayload(int size, string pattern)
{
    var payload = new byte[size];
    switch (pattern)
    {
        case "dto":
            var token = "{\"id\":12345,\"name\":\"SharpLink\",\"region\":\"ap-northeast-1\",\"enabled\":true,\"tags\":[\"rpc\",\"zstd\"]}"u8;
            for (var offset = 0; offset < payload.Length; offset += token.Length) token[..Math.Min(token.Length, payload.Length - offset)].CopyTo(payload.AsSpan(offset));
            break;
        case "mixed":
            var random = new Random(0x430 + size); var mixedToken = "SharpLink|rpc|mixed|payload|"u8;
            for (var offset = 0; offset < payload.Length; offset += 256) { var block = payload.AsSpan(offset, Math.Min(256, payload.Length - offset)); var structured = Math.Min(192, block.Length); for (var inner = 0; inner < structured; inner += mixedToken.Length) mixedToken[..Math.Min(mixedToken.Length, structured - inner)].CopyTo(block[inner..]); if (structured < block.Length) random.NextBytes(block[structured..]); }
            break;
        case "random": new Random(0x5A17 + size).NextBytes(payload); break;
    }
    return payload;
}
