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

internal static class PersistentBindingChecks
{
    internal static async Task RunAsync(string output)
    {
        var rows = new List<Row>();
        foreach (var transport in new[] { "tcp", "sharedmemory" })
            foreach (var metadata in new[] { false, true })
                foreach (var mode in new[] { "sync-small", "sync-large", "persistent-large" })
                {
                    var row = new Row
                    {
                        Transport = transport,
                        Metadata = metadata,
                        Mode = mode,
                        PayloadBytes = mode == "sync-small" ? 8192 : 2 * 1024 * 1024,
                        DeadlineEnabled = mode == "persistent-large"
                    };
                    try
                    {
                        await ExerciseAsync(row);
                    }
                    catch (Exception exception)
                    {
                        row.InfrastructureError = exception.ToString();
                    }
                    rows.Add(row);
                }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var passed = rows.All(row => row.Passed);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            Schema = 1, Magic = (int)ProtocolV2Constants.Magic, Passed = passed, Cases = rows
        }));
        StaticBindingEvidence.Require(passed, "Persistent binding regression failed; all rows retained.");
    }

    private static async Task ExerciseAsync(Row row)
    {
        var provider = new CountingProvider();
        var service = new Observer(row);
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseRuntime(options => options.Compression.Providers.Add(provider));
        var clientBuilder = SharpClientBuilder.Create()
            .UseConnectionPool(options => { options.MinConnections = 1; options.MaxConnections = 1; })
            .UseRuntime(options => options.Compression.Providers.Add(new SharpLinkZstdCompressionProvider()))
            .UseRequestCompressionPolicy(new SharpLinkCompressionSendPolicy
            { MinimumPayloadBytes = 1024, MinimumSavingsBytes = 0, MinimumSavingsRatio = 0 });
        if (row.DeadlineEnabled)
            clientBuilder.UseRequestTimeout(TimeSpan.FromSeconds(20));
        else
            clientBuilder.DisableRequestTimeout();
        if (row.Transport == "tcp")
        {
            serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());
            clientBuilder.UseTcp(IPAddress.Loopback.ToString(),
                ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port);
        }
        else
        {
            var name = $"persistent-binding-{Guid.NewGuid():N}";
            serverBuilder.UseTransport(new SharedMemoryServerTransportListener(name));
            clientBuilder.UseSharedMemory(name);
        }
        serverBuilder.ReplaceService<IBenchmarkRpc>(service);
        await using var server = (SharpLinkServer)serverBuilder.Build();
        await using var client = (SharpLinkClient)clientBuilder.Build();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await server.StartAsync(budget.Token);
        await client.ConnectAsync(budget.Token);
        var rpc = row.Metadata ? client.GetWithMetadata<IBenchmarkRpc>(new SharpLinkMetadata(
            new KeyValuePair<string, string>("persistent", "binding"))) : client.Get<IBenchmarkRpc>();
        await client.PrepareStaticBindingAsync<IBenchmarkRpc>(budget.Token);
        var bytes = new byte[row.PayloadBytes];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 31);
        try
        {
            var returned = await rpc.EchoBytesAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(4));
            row.PayloadMatched = returned.SequenceEqual(bytes);
        }
        catch (Exception exception)
        {
            row.CallError = exception.ToString();
        }
        row.Decompressions = provider.Count;
        row.WorkerStarts = server.DecodeStartedWorkCountForDiagnostics;
        row.BusinessCalls = service.Calls;
        row.ContextMatched = service.ContextMatched;
        try
        {
            row.HealthyAfter = await rpc.AddAsync(20, 22).AsTask().WaitAsync(TimeSpan.FromSeconds(2)) == 42;
        }
        catch (Exception exception)
        {
            row.RecoveryError = exception.ToString();
        }
        var binding = client.StaticBindingDiagnostics<IBenchmarkRpc>();
        row.BindingUnchanged = binding == (1, 1L, 0);
        await client.StopAsync();
        await server.StopAsync(TimeSpan.FromSeconds(2)).AsTask().WaitAsync(TimeSpan.FromSeconds(4));
        row.Drained = server.ActiveCallCountForDiagnostics == 0 &&
            server.DecodeQueueDepthForDiagnostics == 0 && server.DecodeQueueReservationsForDiagnostics == 0 &&
            server.DecodeScheduledConnectionCountForDiagnostics == 0;
    }

    private sealed class Observer(Row row) : BenchmarkRpcService, IBenchmarkRpc
    {
        internal int Calls;
        internal bool ContextMatched;
        ValueTask<byte[]> IBenchmarkRpc.EchoBytesAsync(byte[] value)
        {
            var context = SharpLinkCallContext.Current;
            var metadata = context?.Metadata;
            ContextMatched = context is not null && context.LocalRpcDeadline.HasValue == row.DeadlineEnabled &&
                (row.Metadata ? metadata is not null && metadata.Any(pair => pair.Key == "persistent" && pair.Value == "binding")
                    : metadata is null || metadata.Count == 0);
            Interlocked.Increment(ref Calls);
            StaticBindingEvidence.Require(ContextMatched, "Persistent decode preserves metadata and deadline presence.");
            return ValueTask.FromResult(value);
        }
    }

    private sealed class Row
    {
        public string Transport { get; set; } = "";
        public bool Metadata { get; set; }
        public string Mode { get; set; } = "";
        public int PayloadBytes { get; set; }
        public bool DeadlineEnabled { get; set; }
        public int Decompressions { get; set; }
        public int WorkerStarts { get; set; }
        public int BusinessCalls { get; set; }
        public bool PayloadMatched { get; set; }
        public bool ContextMatched { get; set; }
        public bool HealthyAfter { get; set; }
        public bool BindingUnchanged { get; set; }
        public bool Drained { get; set; }
        public string? CallError { get; set; }
        public string? RecoveryError { get; set; }
        public string? InfrastructureError { get; set; }
        public bool Passed => InfrastructureError is null && CallError is null && RecoveryError is null &&
            Decompressions == 1 && WorkerStarts == (DeadlineEnabled ? 1 : 0) && BusinessCalls == 1 &&
            PayloadMatched && ContextMatched && HealthyAfter && BindingUnchanged && Drained;
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
