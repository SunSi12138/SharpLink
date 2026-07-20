using System.Threading;
using System.Reflection;
using SharpLink.Hosting;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Hosting;

public class SharpLinkClientAccessorTests
{
    [Test]
    public async Task GetClientAsyncShouldWaitUntilClientIsPublished()
    {
        var accessor = new SharpLinkClientAccessor();
        var wait = accessor.GetClientAsync();

        Ensure(!wait.IsCompleted, "client wait should remain pending before publication");

        var client = new FakeSharpLinkClient();
        accessor.SetClient(client);

        var resolved = await wait;
        Ensure(ReferenceEquals(client, resolved), "wait should resolve the published client instance");
    }

    [Test]
    public async Task GetClientAsyncShouldCompleteSynchronouslyWhenClientAlreadyExists()
    {
        var accessor = new SharpLinkClientAccessor();
        var client = new FakeSharpLinkClient();
        accessor.SetClient(client);

        var wait = accessor.GetClientAsync();

        Ensure(wait.IsCompletedSuccessfully, "client wait should complete synchronously after publication");
        Ensure(ReferenceEquals(client, await wait), "resolved client should be the published instance");
    }

    [Test]
    public async Task GetClientAsyncShouldFailAfterStopWhenClientWasNeverPublished()
    {
        var accessor = new SharpLinkClientAccessor();
        accessor.Stop();

        try
        {
            await accessor.GetClientAsync();
            throw new Exception("expected GetClientAsync to throw after stop");
        }
        catch (InvalidOperationException ex)
        {
            Ensure(ex.Message.Contains("host has already stopped", StringComparison.Ordinal), "exception message should describe the stopped host");
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class FakeSharpLinkClient : ISharpLinkClient
    {
        public SharpLinkConnectionState State => SharpLinkConnectionState.Ready;

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Ready));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public T Get<T>() where T : IService
            => throw new NotSupportedException();

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
            => default;

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = true });

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkAssemblyReplacementResult
            {
                Succeeded = true,
                ReferencesReleased = true
            });
    }
}
