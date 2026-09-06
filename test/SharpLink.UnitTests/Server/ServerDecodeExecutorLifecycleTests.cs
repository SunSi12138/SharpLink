using SharpLink.Server;
using System.Net;
using System.Threading;

namespace SharpLink.UnitTests.Server;

public class ServerDecodeExecutorLifecycleTests
{
    [Test]
    public async Task CompressionServerShouldSupervisePersistentDecodeWorkersThroughStop()
    {
        var listener = new BlockingListener();
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseRuntime(options =>
            {
                options.FlowControl.MaxConcurrentDecodesPerServer = 2;
                options.Compression.Providers.Add(new TestCompressionProvider());
            })
            .UseTransport(listener)
            .Build();

        var runTask = server.RunAsync().AsTask();
        await listener.AcceptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(server.DecodeWorkerCountForDiagnostics is > 0 and <= 2,
            "compression-enabled server must start a bounded persistent decode worker set");
        Ensure(server.DecodeQueueDepthForDiagnostics == 0,
            "idle persistent decode workers must begin with an empty queue");

        await server.StopAsync(TimeSpan.FromSeconds(2)).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(server.DecodeQueueDepthForDiagnostics == 0,
            "successful Stop must drain the persistent decode executor");
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    private sealed class BlockingListener : IServerTransportListener
    {
        internal TaskCompletionSource AcceptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EndPoint? LocalEndPoint => null;

        public async ValueTask<ITransportConnection> AcceptAsync(
            CancellationToken cancellationToken = default)
        {
            AcceptStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled accept must not continue.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
