#if SHARPLINK_STATIC_BINDING_PROTOTYPE
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Compression.Zstd;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

internal static class BoundaryCompressionChecks
{
    internal static async Task RunAsync(string output)
    {
        var rows = new List<object>();
        var failed = 0;
        foreach (var transport in new[] { "tcp", "sharedmemory" })
            foreach (var oneWay in new[] { false, true })
                foreach (var metadata in new[] { false, true })
                {
                    var provider = new CountingProvider();
                    var service = new Observer(metadata);
                    string? error = null;
                    try
                    {
                        await ExerciseAsync(transport, oneWay, metadata, provider, service);
                    }
                    catch (Exception exception)
                    {
                        error = exception.ToString();
                        failed++;
                    }
                    rows.Add(new { Transport = transport, OneWay = oneWay, Metadata = metadata,
                        Passed = error is null, Decompressions = provider.Count,
                        BusinessEntered = service.Entered.Task.IsCompletedSuccessfully, Error = error });
                }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Passed = failed == 0, Cases = rows }));
        StaticBindingEvidence.Require(failed == 0, $"Compressed static binding cases failed: {failed}");
    }

    private static async Task ExerciseAsync(string transport, bool oneWay, bool metadata,
        CountingProvider provider, Observer service)
    {
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseRuntime(options => options.Compression.Providers.Add(provider));
        var clientBuilder = SharpClientBuilder.Create()
            .DisableRequestTimeout()
            .UseConnectionPool(options => { options.MinConnections = 1; options.MaxConnections = 1; })
            .UseRuntime(options => options.Compression.Providers.Add(new SharpLinkZstdCompressionProvider()))
            .UseRequestCompressionPolicy(new SharpLinkCompressionSendPolicy
            { MinimumPayloadBytes = 0, MinimumSavingsBytes = 0, MinimumSavingsRatio = 0 });
        if (transport == "tcp")
        {
            serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());
            clientBuilder.UseTcp(IPAddress.Loopback.ToString(),
                ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port);
        }
        else
        {
            var name = $"binding-compression-{Guid.NewGuid():N}";
            serverBuilder.UseTransport(new SharedMemoryServerTransportListener(name));
            clientBuilder.UseSharedMemory(name);
        }
        serverBuilder.ReplaceService<IBenchmarkRpc>(service);
        await using var server = serverBuilder.Build();
        await using var client = (SharpLinkClient)clientBuilder.Build();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.StartAsync(budget.Token);
        await client.ConnectAsync(budget.Token);
        var rpc = metadata ? client.GetWithMetadata<IBenchmarkRpc>(new SharpLinkMetadata(
            new KeyValuePair<string, string>("boundary", "compressed"))) : client.Get<IBenchmarkRpc>();
        await client.PrepareStaticBindingAsync<IBenchmarkRpc>(budget.Token);
        if (oneWay)
            await rpc.PublishEventAsync(7, 42, new string('x', 8192)).AsTask().WaitAsync(budget.Token);
        else
        {
            var payload = Enumerable.Repeat((byte)42, 8192).ToArray();
            var returned = await rpc.EchoBytesAsync(payload).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            StaticBindingEvidence.Require(returned.SequenceEqual(payload), "Compressed Unary payload");
        }
        await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        StaticBindingEvidence.Require(provider.Count == 1, "Exactly one real request decompression");
        StaticBindingEvidence.Require(client.StaticBindingDiagnostics<IBenchmarkRpc>() == (1, 1L, 0),
            "Compression does not rebind or repeat Bind");
    }

    private sealed class Observer(bool expectsMetadata) : BenchmarkRpcService, IBenchmarkRpc
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void Enter()
        {
            var metadata = SharpLinkCallContext.Current?.Metadata;
            StaticBindingEvidence.Require(expectsMetadata
                ? metadata is not null && metadata.Any(pair => pair.Key == "boundary" && pair.Value == "compressed")
                : metadata is null || metadata.Count == 0, "Business metadata preserved");
            Entered.TrySetResult();
        }

        ValueTask<byte[]> IBenchmarkRpc.EchoBytesAsync(byte[] value)
        {
            Enter();
            return ValueTask.FromResult(value);
        }

        ValueTask IBenchmarkRpc.PublishEventAsync(int code, long ticks, string tag)
        {
            StaticBindingEvidence.Require(code == 7 && ticks == 42 && tag == new string('x', 8192),
                "Compressed OneWay payload");
            Enter();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingProvider : ISharpLinkCompressionProvider
    {
        private readonly SharpLinkZstdCompressionProvider _inner = new();
        private int _count;
        internal int Count => Volatile.Read(ref _count);
        public string WireProfile => _inner.WireProfile;
        public bool TryCompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output,
            int maxOutputBytes, CancellationToken cancellationToken = default)
            => _inner.TryCompress(input, output, maxOutputBytes, cancellationToken);
        public void Decompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output,
            int maxOutputBytes, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            _inner.Decompress(input, output, maxOutputBytes, cancellationToken);
        }
    }
}
#endif
