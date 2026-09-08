using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientRuntimeRpcSessionFlushTests
{
    [Test]
    public async Task InvalidNoOpAndStoppedUpdatesShouldPreserveGeneration()
    {
        var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        try
        {
            var initial = client.GetRpcSessionFlushPolicySnapshot();
            Ensure(initial.Generation == 0, "initial flush generation");

            EnsureThrows<ArgumentOutOfRangeException>(() =>
                client.UpdateRpcSessionFlushPolicy(0, TimeSpan.FromMilliseconds(1)));
            EnsureThrows<ArgumentOutOfRangeException>(() =>
                client.UpdateRpcSessionFlushPolicy(1024, TimeSpan.Zero));
            Ensure(client.GetRpcSessionFlushPolicySnapshot() == initial,
                "invalid flush candidates must not publish");

            client.UpdateRpcSessionFlushPolicy(4096, TimeSpan.FromMilliseconds(5));
            var published = client.GetRpcSessionFlushPolicySnapshot();
            Ensure(published.Generation == 1 &&
                   published.FlushSizeThreshold == 4096 &&
                   published.MaxLatency == TimeSpan.FromMilliseconds(5),
                "valid flush publication snapshot");

            client.UpdateRpcSessionFlushPolicy(4096, TimeSpan.FromMilliseconds(5));
            Ensure(client.GetRpcSessionFlushPolicySnapshot() == published,
                "same flush pair must be a generation no-op");

            await client.StopAsync();
            EnsureThrows<InvalidOperationException>(() =>
                client.UpdateRpcSessionFlushPolicy(2048, TimeSpan.FromMilliseconds(2)));
            Ensure(client.GetRpcSessionFlushPolicySnapshot() == published,
                "Stop-rejected flush update must preserve the last generation");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    private static void EnsureThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
