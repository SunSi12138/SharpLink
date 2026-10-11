using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

/// <summary>
/// Test-only setup with the same transport/default-window/default-flush choices as
/// BenchmarkEnvironment. A replacement implementation is needed for 64 B server output
/// and full-byte verification; the generated IBenchmarkRpc proxy/dispatcher is unchanged.
/// </summary>
internal sealed class EvidenceEnvironment : IAsyncDisposable
{
    private readonly ISharpLinkClient _client;
    private readonly ISharpLinkServer _server;
    internal IBenchmarkRpc Rpc { get; }
    internal EvidenceBenchmarkService Service { get; }
    internal SharpLinkClient Client => (SharpLinkClient)_client;
    internal SharpLinkServer Server => (SharpLinkServer)_server;
    internal SharpLinkRuntimeContext Runtime => (SharpLinkRuntimeContext)Client.RuntimeContext;

    private EvidenceEnvironment(
        ISharpLinkClient client, ISharpLinkServer server, EvidenceBenchmarkService service)
    {
        _client = client;
        _server = server;
        Service = service;
        Rpc = client.Get<IBenchmarkRpc>();
    }

    internal static async Task<EvidenceEnvironment> CreateAsync(
        string transport, int itemCount, byte[] expectedPayload)
    {
        var service = new EvidenceBenchmarkService(itemCount, expectedPayload);
        var serverBuilder = SharpLinkServerBuilder.Create();
        SharpClientBuilder clientBuilder;
        if (transport == "tcp")
        {
            serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            clientBuilder = SharpClientBuilder.Create().UseTcp(IPAddress.Loopback.ToString(), port);
        }
        else if (transport == "sharedmemory")
        {
            var name = "sharplink-flow-evidence-" + Guid.NewGuid().ToString("N");
            serverBuilder.UseTransport(new SharedMemoryServerTransportListener(name))
                .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
            clientBuilder = SharpClientBuilder.Create().UseSharedMemory(name)
                .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        }
        else
        {
            throw new ArgumentException("Unknown transport: " + transport);
        }

        // Inherited benchmark policy, recorded in every result. No deadline/window/flush
        // option is changed by the mode. The external collector owns the 60 s deadline.
        clientBuilder.DisableRequestTimeout();
        serverBuilder.ReplaceService<IBenchmarkRpc>(service);
        ISharpLinkServer? server = null;
        ISharpLinkClient? client = null;
        try
        {
            // A client build failure must still dispose an already-built server.
            // Each builder owns rollback of its own unsuccessful Build transaction.
            server = serverBuilder.Build();
            client = clientBuilder.Build();
            await server.StartAsync().ConfigureAwait(false);
            await client.ConnectAsync().ConfigureAwait(false);
            var environment = new EvidenceEnvironment(client, server, service);
            environment.RequireOneConnection();
            return environment;
        }
        catch (Exception exception)
        {
            var failures = await CleanupResourcesAsync(client, server).ConfigureAwait(false);
            ThrowCleanupFailures(failures, exception);
            throw;
        }
    }

    internal void RequireOneConnection()
    {
        if (Client.ReadyConnectionCount != 1)
            throw new InvalidOperationException(
                $"Expected one physical ready connection, observed {Client.ReadyConnectionCount}.");
    }

    internal async Task<string> ProbeSessionAsync()
    {
        RequireOneConnection();
        if (await Rpc.AddAsync(20, 22).ConfigureAwait(false) != 42)
            throw new InvalidDataException("Generated RPC session probe failed.");
        return Service.LastProbeSessionId
            ?? throw new InvalidOperationException("Session probe did not observe a server session.");
    }

    internal async Task WaitForIdleAsync()
    {
        // No enlarged production timeout. If settlement cannot finish, the collector
        // records the bounded-process timeout and retains the started/log artifacts.
        while (Client.PendingCallCount != 0 ||
               Client.ActiveClientCallCount != 0 ||
               Client.ActiveClientStreamCount != 0 ||
               Server.ActiveCallCountForDiagnostics != 0)
        {
            await Task.Delay(1).ConfigureAwait(false);
        }
        RequireOneConnection();
    }

    public ValueTask DisposeAsync() => DisposeAsync(primaryFailure: null);

    internal async ValueTask DisposeAsync(Exception? primaryFailure)
    {
        var failures = await CleanupResourcesAsync(_client, _server).ConfigureAwait(false);
        ThrowCleanupFailures(failures, primaryFailure);
    }

    private static async ValueTask<List<Exception>?> CleanupResourcesAsync(
        ISharpLinkClient? client, ISharpLinkServer? server)
    {
        List<Exception>? failures = null;
        if (client is not null)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(new InvalidOperationException("Client disposal failed.", exception));
            }
        }
        if (server is not null)
        {
            try
            {
                await server.StopAsync(TimeSpan.Zero).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(new InvalidOperationException("Server stop failed.", exception));
            }
            try
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(new InvalidOperationException("Server disposal failed.", exception));
            }
        }
        return failures;
    }

    private static void ThrowCleanupFailures(List<Exception>? failures, Exception? primaryFailure)
    {
        if (failures is null)
            return;
        if (primaryFailure is not null)
            failures.Insert(0, primaryFailure);
        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException("RPC environment cleanup failed; primary failure is preserved first.", failures);
    }
}

/// <summary>
/// Reimplements only the workload members of the existing interface; no new RPC API,
/// wire protocol, generated registration, serializer policy, or production type is added.
/// </summary>
internal sealed class EvidenceBenchmarkService : BenchmarkRpcService, IBenchmarkRpc
{
    private readonly int _expectedItemCount;
    private readonly byte[] _expectedPayload;
    internal string? LastProbeSessionId { get; private set; }

    internal EvidenceBenchmarkService(int itemCount, byte[] expectedPayload)
    {
        _expectedItemCount = itemCount;
        _expectedPayload = expectedPayload;
    }

    public new ValueTask<int> AddAsync(int left, int right)
    {
        LastProbeSessionId = SharpLinkCallContext.Current?.SessionId;
        return ValueTask.FromResult(left + right);
    }

    public new ValueTask<byte[]> EchoBytesAsync(byte[] value)
    {
        Validate(value);
        return ValueTask.FromResult(value);
    }

    public new async ValueTask<long> UploadPayloadsAsync(IAsyncEnumerable<byte[]> payloads)
    {
        var count = 0;
        long score = 0;
        await foreach (var payload in payloads.ConfigureAwait(false))
        {
            Validate(payload);
            if (++count > _expectedItemCount)
                throw new InvalidDataException("Client stream produced too many items.");
            score += GetPayloadScore(payload);
        }
        RequireCount(count);
        return score;
    }

    public new async IAsyncEnumerable<byte[]> DownloadPayloadsAsync(int count, int payloadSize)
    {
        RequireCount(count);
        if (payloadSize != _expectedPayload.Length)
            throw new InvalidDataException("Server stream payload-size request mismatch.");
        for (var index = 0; index < count; index++)
        {
            yield return _expectedPayload;
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    public new async IAsyncEnumerable<byte[]> DuplexPayloadsAsync(IAsyncEnumerable<byte[]> payloads)
    {
        var count = 0;
        await foreach (var payload in payloads.ConfigureAwait(false))
        {
            Validate(payload);
            if (++count > _expectedItemCount)
                throw new InvalidDataException("Duplex input produced too many items.");
            yield return payload;
        }
        RequireCount(count);
    }

    private void Validate(byte[] payload)
    {
        if (!payload.AsSpan().SequenceEqual(_expectedPayload))
            throw new InvalidDataException("Incoming RPC payload failed complete byte comparison.");
    }

    private void RequireCount(int actual)
    {
        if (actual != _expectedItemCount)
            throw new InvalidDataException(
                $"RPC item count mismatch: expected {_expectedItemCount}, observed {actual}.");
    }
}

