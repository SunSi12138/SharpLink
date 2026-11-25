using System.Net;
using SharpLink.Client;
using SharpLink.LoadTestBase;
using SharpLink.Server;

namespace SharpLink.LoadTest.Tests;

public class Qualified32WorkloadTests
{
    [Test]
    public void ColdRunRequiresASeparateSingleConnectionUnaryTrial()
    {
        var cold = LoadTestOptions.Parse(["--operation", "unary32", "--concurrency", "1",
            "--cold-first-rpc-samples", "12501", "--json-output", "cold.json"]);
        if (cold.ColdFirstRpcSamples != 12_501 || cold.PayloadSize != 32 ||
            LoadTestOptions.Parse(["--operation", "unary32"]).ColdFirstRpcSamples != 0)
            throw new InvalidOperationException("Cold observations must be kept separate from the steady workload.");
        try
        {
            _ = LoadTestOptions.Parse(["--operation", "unary32", "--concurrency", "8",
                "--cold-first-rpc-samples", "1000", "--json-output", "cold.json"]);
        }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("The cold trial must reject concurrent first calls.");
    }

    [Test]
    [Arguments("unary32", "tcp")]
    [Arguments("oneway32", "sharedmemory")]
    public void OperationKeepsTheFixedSizeAndFormalRecorder(string operation, string transport)
    {
        var options = LoadTestOptions.Parse(["--operation", operation, "--transport", transport]);
        if (options.PayloadSize != 32 || options.Operation != operation || options.RecordingMode != LatencyRecordingMode.Formal)
            throw new InvalidOperationException("The measured workload did not retain its fixed configuration.");
    }

    [Test]
    public void BoundedOfferRateIsOptInAndKeepsWholeBatchesPerWorker()
    {
        var options = LoadTestOptions.Parse(["--operation", "oneway32", "--concurrency", "1,8",
            "--oneway32-rate", "80000"]);
        if (options.OneWay32Rate != 80_000 ||
            LoadTestOptions.Parse(["--operation", "oneway32"]).OneWay32Rate != 0)
            throw new InvalidOperationException("The bounded load must not replace existing unpaced callers.");
        ExpectRejected(["--operation", "unary32", "--oneway32-rate", "80000"]);
        ExpectRejected(["--operation", "oneway32", "--concurrency", "8", "--oneway32-rate", "80001"]);

        static void ExpectRejected(string[] args)
        {
            try { _ = LoadTestOptions.Parse(args); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("Unsupported paced workload was accepted.");
        }
    }

    [Test]
    public async Task RemoteCounterBelongsOnlyToTheDedicatedOneWayHandler()
    {
        var service = new LoadTestService();
        await service.NotifyAsync(7, 9);
        if (await Qualified32Workload.ObserveRemoteAsync(service, null, 1) != 0)
            throw new InvalidOperationException("The existing OneWay workload must not affect this counter.");

        await service.Notify32Async(new string('x', Qualified32Workload.PayloadChars));
        if (await Qualified32Workload.ObserveRemoteAsync(service, 1, 1) != 1)
            throw new InvalidOperationException("The successful handler completion must be counted exactly once.");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GeneratedPathsRoundTripThePayloadAndCountRemoteOneWay(bool useSharedMemory)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = guard.Token;
        var memory = $"sharplink-qualified32-{Guid.NewGuid():N}";
        var serverBuilder = SharpLinkServerBuilder.Create().ReplaceService<ILoadTestService>(new LoadTestService());
        var clientBuilder = SharpClientBuilder.Create().UseRequestTimeout();
        if (useSharedMemory)
        {
            serverBuilder.UseSharedMemory(memory);
            clientBuilder.UseSharedMemory(memory);
        }
        else
        {
            serverBuilder.UseTcp(0, IPAddress.Loopback);
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            clientBuilder.UseTcp(IPAddress.Loopback.ToString(), port);
        }

        await using var server = serverBuilder.Build();
        await using var client = clientBuilder.Build();
        await server.StartAsync(ct);
        await client.ConnectAsync(ct);
        var proxy = client.CreateProxy<ILoadTestService>();
        await proxy.PrepareAsync(TimeSpan.FromSeconds(4), ct);
        var service = proxy.Service;
        var payload = new string('x', Qualified32Workload.PayloadChars);
        if (await service.EchoAsync(payload).AsTask().WaitAsync(ct) != payload)
            throw new InvalidOperationException("The actual generated unary path must return the 32-byte payload.");
        var before = await Qualified32Workload.ObserveRemoteAsync(service, null, 2);
        await service.Notify32Async(payload).AsTask().WaitAsync(ct);
        if (before != 0 || await Qualified32Workload.ObserveRemoteAsync(service, 1, 2) != 1)
            throw new InvalidOperationException("After the separate local send, the real remote handler must complete once.");
    }
}
