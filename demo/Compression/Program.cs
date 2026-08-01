using System.Buffers;
using System.IO.Compression;
using DemoBase;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.Sdk;

var port = DemoStream.GetFreePort();
using var app = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var clientCompression = new CountingCompressionProvider(
    SharpLinkCompressionProviders.CreateBrotli(CompressionLevel.Fastest));
var serverCompression = new CountingCompressionProvider(
    SharpLinkCompressionProviders.CreateBrotli(CompressionLevel.Optimal));

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

    public SharpLinkCompressionResult Compress(
        ReadOnlySequence<byte> input,
        IBufferWriter<byte> output,
        int maxOutputBytes,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _compressCalls);
        return inner.Compress(input, output, maxOutputBytes, cancellationToken);
    }

    public SharpLinkCompressionResult Decompress(
        ReadOnlySequence<byte> input,
        IBufferWriter<byte> output,
        int maxOutputBytes,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _decompressCalls);
        return inner.Decompress(input, output, maxOutputBytes, cancellationToken);
    }
}
