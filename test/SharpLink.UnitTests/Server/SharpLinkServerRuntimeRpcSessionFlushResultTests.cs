using System.Net;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class SharpLinkServerRuntimeRpcSessionFlushResultTests
{
    [Test]
    public async Task TryUpdateShouldPublishOrReturnLifecycleClosedWithoutPartialMutation()
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .Build();
        var publicServer = (ISharpLinkServer)server;
        var initial = publicServer.GetRpcSessionFlushPolicySnapshot();

        var invalid = CaptureException(() =>
            publicServer.TryUpdateRpcSessionFlushPolicy(0, TimeSpan.FromMilliseconds(1)));
        Ensure(invalid is ArgumentOutOfRangeException,
            "invalid structured flush candidate must remain a parameter exception");
        Ensure(publicServer.GetRpcSessionFlushPolicySnapshot() == initial,
            "invalid structured flush candidate must not publish");

        var publishedResult = publicServer.TryUpdateRpcSessionFlushPolicy(
            4096,
            TimeSpan.FromMilliseconds(5));
        Ensure(publishedResult.Succeeded &&
               publishedResult.FailureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.None,
            "valid structured server flush update should succeed");
        var published = publicServer.GetRpcSessionFlushPolicySnapshot();
        Ensure(published.Generation == initial.Generation + 1 &&
               published.FlushSizeThreshold == 4096 &&
               published.MaxLatency == TimeSpan.FromMilliseconds(5),
            "structured server flush update should atomically publish one generation");

        await server.StopAsync(TimeSpan.Zero);
        var rejected = publicServer.TryUpdateRpcSessionFlushPolicy(
            2048,
            TimeSpan.FromMilliseconds(2));
        Ensure(!rejected.Succeeded &&
               rejected.FailureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.LifecycleClosed,
            "post-Stop structured server flush update should return LifecycleClosed");
        Ensure(publicServer.GetRpcSessionFlushPolicySnapshot() == published,
            "structured lifecycle rejection must preserve the published server flush generation");
    }

    private static Exception CaptureException(Action action)
    {
        try
        {
            action();
            throw new Exception("expected exception");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
    }

    private sealed class IdleListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
