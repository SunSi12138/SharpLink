using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Collections.Generic;
using SharpLink.Client;
using SharpLink.RollbackPlugin;
using SharpLink.Server;

namespace SharpLink.UnitTests.Runtime;

// Every test in this fixture coordinates through RollbackState and SHARPLINK_ROLLBACK_* process state.
[NotInParallel("rollback-plugin")]
public class DynamicRollbackTests
{
    [Test]
    public async Task HugeDynamicDrainTimeoutShouldRemainPendingUntilLeaseRelease()
    {
        await RollbackState.TestIsolation.WaitAsync();
        Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", "1");
        var client = SharpClientBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableRequestTimeout()
            .UseTransport(new NoopClientTransport()).Build();
        SharpLinkDynamicModuleLease lease = default;
        var leaseReleased = false;
        try
        {
            var assembly = typeof(RollbackMarker).Assembly;
            Ensure(client.RegisterAssembly(assembly).Succeeded, "dynamic Client registration");
            var modules = (Dictionary<Assembly, SharpLinkDynamicModule>)(typeof(SharpLinkClient)
                .GetField("_dynamicModules", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(client)!);
            var module = modules[assembly];
            Ensure(module.TryAcquire(stream: false, out lease), "dynamic module lease");

            var unregister = client.UnregisterAssemblyAsync(assembly, TimeSpan.MaxValue).AsTask();
            await Task.Delay(50);
            var completedBeforeDrain = unregister.IsCompleted;
            lease.Dispose();
            leaseReleased = true;
            Exception? failure = null;
            SharpLinkAssemblyUnregisterResult? result = null;
            try
            {
                result = await unregister.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Ensure(!completedBeforeDrain,
                "a huge positive graceful timeout must not overflow the native delay range");
            Ensure(failure is null && result is { ReferencesReleased: true },
                "the unregister operation must complete after its active lease drains");
            module.AssertAccountingInvariant();
            Ensure(module.RemainingCalls == 0 && module.RemainingStreams == 0,
                "successful unregister must release every retained dynamic-module lease exactly once");
        }
        finally
        {
            if (!leaseReleased && lease.IsAcquired)
                lease.Dispose();
            try { await client.DisposeAsync(); } catch { }
            ClearEnvironment();
            RollbackState.TestIsolation.Release();
        }
    }

    [Test]
    public async Task ClientUnregisterRetainedLeaseShouldUseOnlyItsRuntimeContextProvider()
    {
        await RollbackState.TestIsolation.WaitAsync();
        Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", "1");
        var ownerProvider = new ManualTimeProvider();
        var unrelatedProvider = new ManualTimeProvider();
        var client = SharpClientBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableRequestTimeout()
            .UseTimeProvider(ownerProvider)
            .UseTransport(new NoopClientTransport())
            .Build();
        SharpLinkDynamicModuleLease lease = default;
        try
        {
            var assembly = typeof(RollbackMarker).Assembly;
            Ensure(client.RegisterAssembly(assembly).Succeeded, "dynamic Client registration");
            var modules = (Dictionary<Assembly, SharpLinkDynamicModule>)typeof(SharpLinkClient)
                .GetField("_dynamicModules", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(client)!;
            var module = modules[assembly];
            Ensure(module.TryAcquire(stream: false, out lease), "retained Client module lease");
            var forcedCancellationCount = 0;
            using var registration = module.ForcedCancellation.Register(
                () => Interlocked.Increment(ref forcedCancellationCount));

            var unregister = client.UnregisterAssemblyAsync(
                assembly,
                TimeSpan.FromSeconds(5)).AsTask();
            unrelatedProvider.Advance(TimeSpan.FromDays(1));
            ownerProvider.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
            await Task.Yield();

            Ensure(!unregister.IsCompleted && forcedCancellationCount == 0,
                "an unrelated clock and the owner tick before the deadline must not force Client calls");
            Ensure(ownerProvider.ActiveTimerCount == 1 && unrelatedProvider.ActiveTimerCount == 0,
                "the retained Client lease timeout must be owned only by its RuntimeContext provider");

            ownerProvider.Advance(TimeSpan.FromTicks(1));
            var result = await unregister;
            Ensure(result is { ReferencesReleased: false, RemainingCalls: 1 } &&
                   forcedCancellationCount == 1,
                "exact equality must force-cancel the retained Client lease once and report deferred release");

            lease.Dispose();
            lease = default;
            await module.WaitForDrainAsync();
            module.AssertAccountingInvariant();
            Ensure(module.RemainingCalls == 0 && module.RemainingStreams == 0,
                "the Client unregister drain must leave both module counters exactly zero");
            await client.StopAsync();
            await ownerProvider.WaitForTimersDrainedAsync();
            Ensure(module.State == SharpLinkDynamicModuleState.Released && !modules.ContainsKey(assembly),
                "Client module must be released after its retained lease and framework owner drain");
            Ensure(ownerProvider.ActiveTimerCount == 0 && forcedCancellationCount == 1,
                "Client deferred release must leave no provider timer or duplicate forced cancellation");
        }
        finally
        {
            if (lease.IsAcquired)
                lease.Dispose();
            try { await client.DisposeAsync(); } catch { }
            ClearEnvironment();
            RollbackState.TestIsolation.Release();
        }
    }

    [Test]
    public async Task ServerUnregisterRetainedLeaseShouldUseOnlyItsRuntimeContextProvider()
    {
        await RollbackState.TestIsolation.WaitAsync();
        Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", "1");
        var ownerProvider = new ManualTimeProvider();
        var unrelatedProvider = new ManualTimeProvider();
        var server = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTimeProvider(ownerProvider)
            .UseTransport(new NoopServerTransport())
            .Build();
        SharpLinkDynamicModuleLease lease = default;
        try
        {
            var assembly = typeof(RollbackMarker).Assembly;
            Ensure(server.RegisterAssembly(assembly).Succeeded, "dynamic Server registration");
            var registry = (ServerServiceModuleRegistry)typeof(SharpLinkServer)
                .GetField("_serviceModuleRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server)!;
            Ensure(registry.DynamicModules.TryGetValue(assembly, out var module), "registered Server module");
            Ensure(module.TryAcquire(stream: false, out lease), "retained Server module lease");
            var forcedCancellationCount = 0;
            using var registration = module.ForcedCancellation.Register(
                () => Interlocked.Increment(ref forcedCancellationCount));

            var unregister = server.UnregisterAssemblyAsync(
                assembly,
                TimeSpan.FromSeconds(5)).AsTask();
            unrelatedProvider.Advance(TimeSpan.FromDays(1));
            ownerProvider.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
            await Task.Yield();

            Ensure(!unregister.IsCompleted && forcedCancellationCount == 0,
                "an unrelated clock and the owner tick before the deadline must not force Server calls");
            Ensure(ownerProvider.ActiveTimerCount == 1 && unrelatedProvider.ActiveTimerCount == 0,
                "the retained Server lease timeout must be owned only by its RuntimeContext provider");

            ownerProvider.Advance(TimeSpan.FromTicks(1));
            var result = await unregister;
            Ensure(result is { ReferencesReleased: false, RemainingCalls: 1 } &&
                   forcedCancellationCount == 1,
                "exact equality must force-cancel the retained Server lease once and report deferred release");

            lease.Dispose();
            lease = default;
            await module.WaitForDrainAsync();
            module.AssertAccountingInvariant();
            Ensure(module.RemainingCalls == 0 && module.RemainingStreams == 0,
                "the Server unregister drain must leave both module counters exactly zero");
            await server.StopAsync(TimeSpan.Zero);
            await ownerProvider.WaitForTimersDrainedAsync();
            Ensure(module.State == SharpLinkDynamicModuleState.Released && !registry.DynamicModules.ContainsKey(assembly),
                "Server module must be released after its retained lease and framework owner drain");
            Ensure(ownerProvider.ActiveTimerCount == 0 && forcedCancellationCount == 1,
                "Server deferred release must leave no provider timer or duplicate forced cancellation");
        }
        finally
        {
            if (lease.IsAcquired)
                lease.Dispose();
            try { await server.DisposeAsync(); } catch { }
            ClearEnvironment();
            RollbackState.TestIsolation.Release();
        }
    }

    [Test]
    public async Task ClientRegistrationRollbackShouldPreserveConflictAndAdapterCleanupFailure()
    {
        await RollbackState.TestIsolation.WaitAsync();
        try
        {
            var client = SharpClientBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .DisableRequestTimeout()
                .UseTransport(new NoopClientTransport()).Build();
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
            var server = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).UseTransport(new NoopServerTransport()).Build();
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
            var client = SharpClientBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .DisableRequestTimeout()
                .UseTransport(new NoopClientTransport()).Build();
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
            var server = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).UseTransport(new NoopServerTransport()).Build();
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
            try
            {
                var failure = Capture(() => SharpLinkServerBuilder.Create()
                    .UseGeneratedManifestSource(new FixedGeneratedManifestSource([manifest]))
                    .UseTransport(new ThrowingProfileServerTransport())
                    .Build());

                Ensure(Contains(failure, "server profile binding failed"), "Server build retains profile failure");
                Ensure(Contains(failure, "rollback Adapter scope cleanup failed"), "Server build retains Context cleanup failure");
                Ensure(RollbackState.ScopeDisposeCount == 1, "Server build disposes Runtime Context once");
            }
            finally
            {
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
