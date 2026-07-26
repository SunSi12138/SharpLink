using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using SharpLink.Client;
using SharpLink.RollbackPlugin;
using SharpLink.Server;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel]
public class DynamicRollbackTests
{
    [Test]
    public async Task ClientRegistrationRollbackShouldPreserveConflictAndAdapterCleanupFailure()
    {
        await RollbackState.TestIsolation.WaitAsync();
        try
        {
            var client = SharpClientBuilder.Create().UseTransport(new NoopClientTransport()).Build();
            using var loaded = LoadPlugin("client-registration");
            try
            {
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", "first-schema");
                Ensure(client.RegisterAssembly(typeof(RollbackMarker).Assembly).Succeeded, "first Client registration");
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", "second-schema");

                var failure = Capture(() => client.RegisterAssembly(loaded.Assembly));

                Ensure(Contains(failure, "Codec conflict"), "Client rollback retains the structured conflict");
                Ensure(Contains(failure, "rollback Adapter scope cleanup failed"), "Client rollback retains Adapter cleanup failure");
            }
            finally
            {
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", null);
                try { await client.DisposeAsync(); } catch { }
            }
        }
        finally
        {
            RollbackState.TestIsolation.Release();
        }
    }

    [Test]
    public async Task ServerRegistrationRollbackShouldPreserveConflictAndAdapterCleanupFailure()
    {
        await RollbackState.TestIsolation.WaitAsync();
        try
        {
            var server = SharpLinkServerBuilder.Create().UseTransport(new NoopServerTransport()).Build();
            using var loaded = LoadPlugin("server-registration");
            try
            {
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", "first-schema");
                Ensure(server.RegisterAssembly(typeof(RollbackMarker).Assembly).Succeeded, "first Server registration");
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", "second-schema");

                var failure = Capture(() => server.RegisterAssembly(loaded.Assembly));

                Ensure(Contains(failure, "Codec conflict"), "Server rollback retains the structured conflict");
                Ensure(Contains(failure, "rollback Adapter scope cleanup failed"), "Server rollback retains Adapter cleanup failure");
            }
            finally
            {
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", null);
                try { await server.DisposeAsync(); } catch { }
            }
        }
        finally
        {
            RollbackState.TestIsolation.Release();
        }
    }

    [Test]
    public async Task ClientReplacementRollbackShouldPreserveConflictAndAdapterCleanupFailure()
    {
        await RollbackState.TestIsolation.WaitAsync();
        try
        {
            var client = SharpClientBuilder.Create().UseTransport(new NoopClientTransport()).Build();
            using var oldPlugin = LoadPlugin("client-old");
            using var newPlugin = LoadPlugin("client-new");
            try
            {
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", "retained-schema");
                Ensure(client.RegisterAssembly(typeof(RollbackMarker).Assembly).Succeeded, "retained Client registration");
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", "1");
                Ensure(client.RegisterAssembly(oldPlugin.Assembly).Succeeded, "old Client registration");
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", null);
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", "incoming-schema");

                var failure = Capture(() => client.ReplaceAssemblyAsync(
                    oldPlugin.Assembly, newPlugin.Assembly, TimeSpan.Zero).AsTask().GetAwaiter().GetResult());

                Ensure(Contains(failure, "Codec conflict"), "Client replacement retains the structured conflict");
                Ensure(Contains(failure, "rollback Adapter scope cleanup failed"), "Client replacement retains Adapter cleanup failure");
            }
            finally
            {
                ClearEnvironment();
                try { await client.DisposeAsync(); } catch { }
            }
        }
        finally
        {
            RollbackState.TestIsolation.Release();
        }
    }

    [Test]
    public async Task ServerReplacementRollbackShouldPreserveConflictAndAdapterCleanupFailure()
    {
        await RollbackState.TestIsolation.WaitAsync();
        try
        {
            var server = SharpLinkServerBuilder.Create().UseTransport(new NoopServerTransport()).Build();
            using var oldPlugin = LoadPlugin("server-old");
            using var newPlugin = LoadPlugin("server-new");
            try
            {
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", "retained-schema");
                Ensure(server.RegisterAssembly(typeof(RollbackMarker).Assembly).Succeeded, "retained Server registration");
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", "1");
                Ensure(server.RegisterAssembly(oldPlugin.Assembly).Succeeded, "old Server registration");
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", null);
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", "incoming-schema");

                var failure = Capture(() => server.ReplaceAssemblyAsync(
                    oldPlugin.Assembly, newPlugin.Assembly, TimeSpan.Zero).AsTask().GetAwaiter().GetResult());

                Ensure(Contains(failure, "Codec conflict"), "Server replacement retains the structured conflict");
                Ensure(Contains(failure, "rollback Adapter scope cleanup failed"), "Server replacement retains Adapter cleanup failure");
            }
            finally
            {
                ClearEnvironment();
                try { await server.DisposeAsync(); } catch { }
            }
        }
        finally
        {
            RollbackState.TestIsolation.Release();
        }
    }

    [Test]
    public void ServerProfileBindingFailureShouldDisposeRuntimeContextAndPreserveBothFailures()
    {
        RollbackState.TestIsolation.Wait();
        try
        {
            Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", "server-build-schema");
            RollbackState.ScopeDisposeCount = 0;
            var manifest = new RollbackManifest();
            SharpLinkGeneratedAssemblyCatalog.Register(manifest);
            try
            {
                var failure = Capture(() => SharpLinkServerBuilder.Create()
                    .UseTransport(new ThrowingProfileServerTransport())
                    .Build());

                Ensure(Contains(failure, "server profile binding failed"), "Server build retains profile failure");
                Ensure(Contains(failure, "rollback Adapter scope cleanup failed"), "Server build retains Context cleanup failure");
                Ensure(RollbackState.ScopeDisposeCount == 1, "Server build disposes Runtime Context once");
            }
            finally
            {
                RollbackTestIsolation.RemoveManifestFromCatalog(manifest);
                ClearEnvironment();
                GC.KeepAlive(manifest);
            }
        }
        finally
        {
            RollbackState.TestIsolation.Release();
        }
    }

    private static void ClearEnvironment()
    {
        Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", null);
        Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", null);
    }

    private static LoadedPlugin LoadPlugin(string name)
    {
        var context = new PluginLoadContext(name);
        return new LoadedPlugin(context, context.LoadFromAssemblyPath(typeof(RollbackMarker).Assembly.Location));
    }

    private static Exception Capture(Action action)
    {
        try { action(); throw new Exception("expected rollback failure"); }
        catch (Exception exception) { return exception; }
    }

    private static bool Contains(Exception exception, string text)
    {
        if (exception.Message.Contains(text, StringComparison.Ordinal)) return true;
        if (exception is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions) if (Contains(inner, text)) return true;
        return exception.InnerException is { } nested && Contains(nested, text);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class PluginLoadContext(string name) : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }

    private sealed record LoadedPlugin(AssemblyLoadContext Context, Assembly Assembly) : IDisposable
    {
        public void Dispose() => Context.Unload();
    }

    private sealed class NoopClientTransport : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopServerTransport : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingProfileServerTransport : IServerTransportListener, IPerformanceProfileAwareTransport
    {
        public EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
            => throw new InvalidOperationException("server profile binding failed");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
