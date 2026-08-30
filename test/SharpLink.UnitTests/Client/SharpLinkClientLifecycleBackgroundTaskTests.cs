using SharpLink.Client;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleSharedSupport;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientLifecycleBackgroundTaskTests
{
    [Test]
    public async Task StopShouldPreserveAnUnexpectedCompletedFrameworkFailure()
    {
        var client = ClientBuilderTestHelper.Build(new NonConnectingFactory());
        client.TrackFrameworkTask(
            Task.FromException(new InvalidOperationException("unexpected reconnect cleanup failure")),
            "ReconnectLoop");

        Exception failure;
        try
        {
            await client.StopAsync();
            throw new Exception("expected stop failure");
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(ContainsException(failure, static exception =>
                exception is InvalidOperationException { Message: "unexpected reconnect cleanup failure" }),
            "shutdown cancellation must not hide an unexpected completed reconnect failure");
        Ensure(client.State == SharpLinkConnectionState.Stopped,
            "client cleanup must still reach the stopped state when it reports the failure");
    }

    [Test]
    public async Task FrameworkSupervisorShouldNotHideAnUnexpectedNestedFailure()
    {
        var client = ClientBuilderTestHelper.Build(new NonConnectingFactory());
        var expected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var unexpected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mixed = Task.WhenAll(expected.Task, unexpected.Task);
        client.TrackFrameworkTask(mixed, "MixedClientWorker");
        await Task.Yield();
        expected.TrySetException(new IOException("expected background transport closure"));
        unexpected.TrySetException(new InvalidOperationException("unexpected background nested failure"));

        Exception? failure = null;
        try
        {
            await client.StopAsync();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(failure is not null && ContainsException(failure, static exception =>
                exception is InvalidOperationException { Message: "unexpected background nested failure" }),
            "an expected background close must not hide an unexpected nested task failure");
    }

    [Test]
    public async Task StaticClusterSupervisorShouldNotHideAnUnexpectedNestedFailure()
    {
        var client = (SharpLinkClient)CreateClientBuilder()
            .UseEndpoints(
                [CreateEndpoint("first", 5001), CreateEndpoint("second", 5002)],
                _ => new NonConnectingFactory())
            .Build();
        var expected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var unexpected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.TrackFrameworkTask(
            Task.WhenAll(expected.Task, unexpected.Task),
            "StaticClusterReconnect");
        await Task.Yield();
        expected.TrySetException(new IOException("expected static worker transport closure"));
        unexpected.TrySetException(new InvalidOperationException("unexpected static worker nested failure"));

        Exception? failure = null;
        try
        {
            await client.StopAsync();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(failure is not null && ContainsException(failure, static exception =>
                exception is InvalidOperationException { Message: "unexpected static worker nested failure" }),
            "an expected static worker close must not hide an unexpected nested task failure");
    }
}
