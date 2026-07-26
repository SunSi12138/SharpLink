using System.Net;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using SharpLink.Client;
using SharpLink.RollbackPlugin;
using SharpLink.Sdk;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Builder;

[NotInParallel]
public class BuilderOwnershipRollbackTests
{
    [Test]
    public void DirectClientProfileFailureShouldDisposeTransportAndPreserveBothFailures()
    {
        var transport = new TrackingClientTransport(
            bindingFailure: "direct Client profile binding failed",
            cleanupFailure: "direct Client transport cleanup failed");

        var failure = Capture(() => SharpClientBuilder.Create()
            .UseTransport(transport)
            .Build());

        Ensure(Contains(failure, "direct Client profile binding failed"),
            "direct Client build retains profile failure");
        Ensure(Contains(failure, "direct Client transport cleanup failed"),
            "direct Client build retains transport cleanup failure");
        Ensure(transport.DisposeCount == 1, "direct Client build disposes its transport once");
    }

    [Test]
    public void DirectClientConstructionFailureShouldDisposeTransportAndPreserveBothFailures()
    {
        var transport = new TrackingClientTransport(
            bindingFailure: null,
            cleanupFailure: "direct Client construction transport cleanup failed");

        var failure = Capture(() => SharpClientBuilder.Create()
            .UseTransport(transport)
            .UseLoggerFactory(new ThrowingLoggerFactory("direct Client logger construction failed"))
            .Build());

        Ensure(Contains(failure, "direct Client logger construction failed"),
            "direct Client build retains constructor failure");
        Ensure(Contains(failure, "direct Client construction transport cleanup failed"),
            "direct Client construction retains transport cleanup failure");
        Ensure(transport.DisposeCount == 1, "failed direct Client construction disposes its transport once");
    }

    [Test]
    public void DynamicResolverValidationFailureShouldDisposeResolverAndPreserveBothFailures()
    {
        var resolver = new TrackingResolver("dynamic resolver cleanup failed");

        var failure = Capture(() => SharpClientBuilder.Create()
            .UseEndpointResolver(resolver, static _ => new NoopClientTransport())
            .UseConnectionPool(static _ => { })
            .Build());

        Ensure(Contains(failure, "UseConnectionPool is only available"),
            "dynamic Client build retains validation failure");
        Ensure(Contains(failure, "dynamic resolver cleanup failed"),
            "dynamic Client build retains resolver cleanup failure");
        Ensure(resolver.DisposeCount == 1, "failed dynamic Client build disposes its resolver once");
    }

    [Test]
    public void ServerValidationFailureShouldPreserveRuntimeContextCleanupFailure()
    {
        RollbackState.TestIsolation.Wait();
        try
        {
            WithRollbackManifest(() =>
            {
                var failure = Capture(() => SharpLinkServerBuilder.Create()
                    .UseTransport(new NoopServerTransport())
                    .EnableService<IMissingService>()
                    .Build());

                Ensure(Contains(failure, "required contract"), "Server build retains service validation failure");
                Ensure(Contains(failure, "rollback Adapter scope cleanup failed"),
                    "Server build retains Runtime Context cleanup failure");
                Ensure(RollbackState.ScopeDisposeCount == 1, "Server validation rollback disposes Context once");
            });
        }
        finally
        {
            RollbackState.TestIsolation.Release();
        }
    }

    [Test]
    public void ServerConstructorFailureShouldDisposeRuntimeContextAndPreserveBothFailures()
    {
        RollbackState.TestIsolation.Wait();
        try
        {
            WithRollbackManifest(() =>
            {
                var failure = Capture(() => SharpLinkServerBuilder.Create()
                    .UseTransport(new NoopServerTransport())
                    .UseLoggerFactory(new ThrowingLoggerFactory("Server logger construction failed"))
                    .Build());

                Ensure(Contains(failure, "Server logger construction failed"),
                    "Server build retains constructor failure");
                Ensure(Contains(failure, "rollback Adapter scope cleanup failed"),
                    "Server constructor rollback retains Runtime Context cleanup failure");
                Ensure(RollbackState.ScopeDisposeCount == 1, "Server constructor rollback disposes Context once");
            });
        }
        finally
        {
            RollbackState.TestIsolation.Release();
        }
    }

    private static void WithRollbackManifest(Action action)
    {
        Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", "builder-rollback-schema");
        RollbackState.ScopeDisposeCount = 0;
        var manifest = new RollbackManifest();
        SharpLinkGeneratedAssemblyCatalog.Register(manifest);
        try
        {
            action();
        }
        finally
        {
            RollbackTestIsolation.RemoveManifestFromCatalog(manifest);
            Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", null);
            GC.KeepAlive(manifest);
        }
    }

    private static Exception Capture(Action action)
    {
        try { action(); throw new Exception("expected build failure"); }
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

    private interface IMissingService : IService;

    private sealed class TrackingClientTransport(string? bindingFailure, string? cleanupFailure) :
        IClientTransportFactory,
        IPerformanceProfileAwareTransport
    {
        public int DisposeCount { get; private set; }

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
        {
            if (bindingFailure is not null)
                throw new InvalidOperationException(bindingFailure);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return cleanupFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(new InvalidOperationException(cleanupFailure));
        }
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

    private sealed class TrackingResolver(string cleanupFailure) : ISharpLinkEndpointResolver
    {
        public int DisposeCount { get; private set; }

        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromException<SharpLinkEndpointSnapshot>(new NotSupportedException());

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.FromException(new InvalidOperationException(cleanupFailure));
        }
    }

    private sealed class ThrowingLoggerFactory(string failure) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => throw new InvalidOperationException(failure);
        public void Dispose() { }
    }
}
