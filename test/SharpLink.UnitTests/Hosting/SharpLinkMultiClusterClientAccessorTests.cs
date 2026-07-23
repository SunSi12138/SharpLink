using System.Reflection;
using System.Threading;
using SharpLink.Hosting;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Hosting;

public sealed class SharpLinkMultiClusterClientAccessorTests
{
    [Test]
    public async Task StopShouldRejectLaterPublicationAndReads()
    {
        var accessor = new SharpLinkMultiClusterClientAccessor();
        accessor.Stop();

        await EnsureThrows<InvalidOperationException>(async () => await accessor.GetClientAsync());
        await EnsureThrows<InvalidOperationException>(() =>
        {
            accessor.SetClient(new FakeMultiClusterClient());
            return Task.CompletedTask;
        });
    }

    private static async Task EnsureThrows<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new Exception($"Expected {typeof(TException).Name}.");
    }

    private sealed class FakeMultiClusterClient : ISharpLinkMultiClusterClient
    {
        public SharpLinkMultiClusterState State => SharpLinkMultiClusterState.Ready;

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public TContract Get<TContract>() where TContract : IService => throw new NotSupportedException();

        public SharpLinkConnectionState GetClusterState(SharpLinkClusterKey cluster) => SharpLinkConnectionState.Ready;

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            SharpLinkClusterKey cluster,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Ready));

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(SharpLinkClusterKey cluster, Assembly assembly)
            => SharpLinkAssemblyRegistrationResult.Success();

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            SharpLinkClusterKey cluster,
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = true });

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            SharpLinkClusterKey cluster,
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkAssemblyReplacementResult
            {
                Succeeded = true,
                ReferencesReleased = true
            });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
