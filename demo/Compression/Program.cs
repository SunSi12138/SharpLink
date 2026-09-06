using System.Buffers;
using System.Buffers.Binary;
using DemoBase;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.Sdk;

var port = DemoStream.GetFreePort();
using var app = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var clientCompression = new CountingCompressionProvider(new DemoRleCompressionProvider(maxRunLength: 64));
var serverCompression = new CountingCompressionProvider(new DemoRleCompressionProvider(maxRunLength: 128));

var server = DemoTcp.CreateServer<ICompressionService, CompressionService>(port,
    builder => builder.UseRuntime(options => ConfigureCompression(options, serverCompression)));
var serverTask = DemoTcp.StartServerAsync(server, app.Token);
var client = DemoTcp.CreateClient(port,
    builder => builder.UseRuntime(options => ConfigureCompression(options, clientCompression)));

try
{
    await DemoTcp.EnsureConnectedAsync(client, app.Token);
    var payload = string.Concat(Enumerable.Repeat("SharpLink compression demo. ", 512));
    var response = await client.Get<ICompressionService>().EchoAsync(payload, app.Token);
    if (response != payload)
        throw new InvalidOperationException("Compression round trip changed the payload.");

    Console.WriteLine($"round-trip chars={response.Length}");
    Console.WriteLine($"client compress/decompress={clientCompression.CompressCalls}/{clientCompression.DecompressCalls}");
    Console.WriteLine($"server compress/decompress={serverCompression.CompressCalls}/{serverCompression.DecompressCalls}");
    if (clientCompression.CompressCalls == 0 || serverCompression.DecompressCalls == 0)
        throw new InvalidOperationException("The payload did not exercise negotiated compression.");
}
finally
{
    await DemoTcp.ShutdownAsync(app, serverTask, client, server);
}

static void ConfigureCompression(SharpLinkRuntimeOptions options, ISharpLinkCompressionProvider provider)
{
    options.Compression.MinimumPayloadBytes = 64;
    options.Compression.MinimumSavingsBytes = 8;
    options.Compression.MinimumSavingsRatio = 0;
    options.Compression.Providers.Add(provider);
}

[RpcContract]
public interface ICompressionService : IService
{
    ValueTask<string> EchoAsync(string payload, CancellationToken cancellationToken);
}

[RpcService]
public sealed class CompressionService : ICompressionService
{
    public ValueTask<string> EchoAsync(string payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(payload);
    }
}

public sealed class CountingCompressionProvider(ISharpLinkCompressionProvider inner)
    : ISharpLinkCompressionProvider
{
    private int _compressCalls;
    private int _decompressCalls;

    public string WireProfile => inner.WireProfile;
    public int CompressCalls => Volatile.Read(ref _compressCalls);
    public int DecompressCalls => Volatile.Read(ref _decompressCalls);

    public bool TryCompress(
        ReadOnlySequence<byte> input,
        IBufferWriter<byte> output,
        int maxOutputBytes,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _compressCalls);
        return inner.TryCompress(input, output, maxOutputBytes, cancellationToken);
    }

    public void Decompress(
        ReadOnlySequence<byte> input,
        IBufferWriter<byte> output,
        int maxOutputBytes,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _decompressCalls);
        inner.Decompress(input, output, maxOutputBytes, cancellationToken);
    }
}


public sealed class DemoRleCompressionProvider(int maxRunLength = byte.MaxValue)
    : ISharpLinkCompressionProvider
{
    private const uint Magic = 0x31454C52; // "RLE1" little endian.

    public string WireProfile => "demo.rle/v1";

    public bool TryCompress(
        ReadOnlySequence<byte> input,
        IBufferWriter<byte> output,
        int maxOutputBytes,
        CancellationToken cancellationToken = default)
    {
        if (maxRunLength is < 1 or > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maxRunLength));
        var source = input.ToArray();
        var runs = 0;
        Span<byte> run = stackalloc byte[2];
        for (var offset = 0; offset < source.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = 1;
            while (offset + count < source.Length &&
                   count < maxRunLength &&
                   source[offset + count] == source[offset])
                count++;
            runs++;
            offset += count;
        }
        var required = checked(sizeof(uint) + runs * 2);
        if (required > maxOutputBytes)
            return false;

        Span<byte> magic = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(magic, Magic);
        output.Write(magic);
        for (var offset = 0; offset < source.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = 1;
            while (offset + count < source.Length &&
                   count < maxRunLength &&
                   source[offset + count] == source[offset])
                count++;
            run[0] = checked((byte)count);
            run[1] = source[offset];
            output.Write(run);
            offset += count;
        }
        return true;
    }

    public void Decompress(
        ReadOnlySequence<byte> input,
        IBufferWriter<byte> output,
        int maxOutputBytes,
        CancellationToken cancellationToken = default)
    {
        var source = input.ToArray();
        if (source.Length < sizeof(uint) || (source.Length - sizeof(uint)) % 2 != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic)
            throw new InvalidDataException("The demo RLE payload is malformed.");
        var written = 0;
        for (var offset = sizeof(uint); offset < source.Length; offset += 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = source[offset];
            if (count == 0 || count > maxOutputBytes - written)
                throw new InvalidDataException("The demo RLE payload exceeds its output limit.");
            output.GetSpan(count)[..count].Fill(source[offset + 1]);
            output.Advance(count);
            written += count;
        }
    }
}
