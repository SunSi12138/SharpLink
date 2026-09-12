using System.Net;
using SharpLink.Sdk;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class SharpLinkServerDesiredSessionRolloutEpochTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RollingPublicationShouldReturnNewerDesiredSnapshotAfterItsWait(bool structuredApi)
    {
        await using var server = CreateServer();
        var initial = server.DesiredSession;
        var firstConfiguration = new SharpLinkServerDesiredSessionConfiguration
        {
            MaxFramePayloadBytes = initial.Configuration.MaxFramePayloadBytes / 2
        };
        var scanEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server._desiredSessionRolloutTestHook = async (_, token) =>
        {
            scanEntered.TrySetResult();
            await releaseScan.Task.WaitAsync(token).ConfigureAwait(false);
        };

        async Task<SharpLinkServerDesiredSessionSnapshot> PublishFirstAsync()
        {
            if (!structuredApi)
                return await server.PublishDesiredSessionAsync(firstConfiguration, SharpLinkSessionRolloutMode.RollingRefresh);
            var result = await ((ISharpLinkServer)server).TryPublishDesiredSessionAsync(
                firstConfiguration, SharpLinkSessionRolloutMode.RollingRefresh);
            Ensure(result.Succeeded && result.Snapshot.HasValue, "structured publication succeeds with a snapshot");
            return result.Snapshot!.Value;
        }

        var first = PublishFirstAsync();
        try
        {
            await scanEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var newer = await server.PublishDesiredSessionAsync(new SharpLinkServerDesiredSessionConfiguration
            {
                MaxFramePayloadBytes = firstConfiguration.MaxFramePayloadBytes / 2
            });
            Ensure(!first.IsCompleted && newer.Generation == initial.Generation + 2,
                "a newer FutureOnly generation becomes current while the earlier RollingRefresh still waits");

            releaseScan.TrySetResult();
            var returned = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(returned == newer && returned == server.DesiredSession,
                "both publication APIs return the snapshot current after their rollout wait");
        }
        finally
        {
            releaseScan.TrySetResult();
            server._desiredSessionRolloutTestHook = null;
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task SameGenerationRollingRequestDuringActiveScanShouldForceAnotherScan()
    {
        await using var server = CreateServer();
        var current = server.DesiredSession;
        var configuration = new SharpLinkServerDesiredSessionConfiguration
        {
            MaxFramePayloadBytes = Math.Max(
                SharpLinkProtocolOptions.MinMaxFramePayloadBytes,
                current.Configuration.MaxFramePayloadBytes / 2)
        };
        await ((ISharpLinkServer)server).TryPublishDesiredSessionAsync(
            configuration,
            SharpLinkSessionRolloutMode.FutureOnly);

        var firstScanEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scans = 0;
        server._desiredSessionRolloutTestHook = async (_, token) =>
        {
            if (Interlocked.Increment(ref scans) != 1)
                return;
            firstScanEntered.TrySetResult();
            await releaseFirstScan.Task.WaitAsync(token).ConfigureAwait(false);
        };

        try
        {
            var first = ((ISharpLinkServer)server).TryPublishDesiredSessionAsync(
                configuration,
                SharpLinkSessionRolloutMode.RollingRefresh).AsTask();
            await firstScanEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var second = ((ISharpLinkServer)server).TryPublishDesiredSessionAsync(
                configuration,
                SharpLinkSessionRolloutMode.RollingRefresh).AsTask();
            await Task.Yield();

            releaseFirstScan.TrySetResult();
            var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

            Ensure(results[0].Succeeded && results[1].Succeeded,
                "both same-generation RollingRefresh requests should succeed");
            Ensure(scans >= 2,
                "a same-generation RollingRefresh requested during an active scan must force a subsequent stale-session scan");
        }
        finally
        {
            releaseFirstScan.TrySetResult();
            server._desiredSessionRolloutTestHook = null;
        }
    }

    private static SharpLinkServer CreateServer()
        => (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(new NoopListener())
            .Build();

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class NoopListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
