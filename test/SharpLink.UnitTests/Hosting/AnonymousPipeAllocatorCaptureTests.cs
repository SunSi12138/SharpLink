using System.Reflection;
using SharpLink.Hosting;
using SharpLink.Server;

namespace SharpLink.UnitTests.Hosting;

public class AnonymousPipeAllocatorCaptureTests
{
    [Test]
    public async Task ConfigurationWinningBeforeBuildMustMatchTheCapturedAndBuiltTransport()
    {
        var builder = CreateBuilder();
        var transport = new TrackingAllocatorTransport();
        var accessor = new AnonymousPipeAllocatorAccessor(builder);
        using var configureAllowed = new ManualResetEventSlim();
        using var configured = new ManualResetEventSlim();
        var configure = LongRunningTestWorker.Run(() =>
        {
            Ensure(configureAllowed.Wait(TimeSpan.FromSeconds(5)), "configuration was not released");
            builder.UseTransport(transport);
            configured.Set();
        });
        var build = LongRunningTestWorker.Run(() =>
        {
            Ensure(configured.Wait(TimeSpan.FromSeconds(5)), "configuration did not finish before Build");
            return builder.BuildWithTransportCapture(accessor.CaptureTransport);
        });
        ISharpLinkServer? server = null;
        try
        {
            configureAllowed.Set();
            await configure.WaitAsync(TimeSpan.FromSeconds(5));
            server = await build.WaitAsync(TimeSpan.FromSeconds(5));
            var runtimeTransport = typeof(SharpLinkServer)
                .GetField("_transportListener", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server);
            Ensure(builder.Transport is null, "Build must release its mutable transport reference");
            Ensure(ReferenceEquals(runtimeTransport, transport) &&
                   ReferenceEquals(accessor.AnonymousPipeAllocator, runtimeTransport),
                "an accepted transport must be identical in the frozen runtime and the accessor");
        }
        finally
        {
            configureAllowed.Set();
            await LongRunningTestWorker.JoinAsync(configure, TimeSpan.FromSeconds(5));
            await LongRunningTestWorker.JoinAsync(build, TimeSpan.FromSeconds(5));
            server ??= build.IsCompletedSuccessfully ? build.Result : null;
            if (server is not null)
                await server.DisposeAsync();
            else if (transport.DisposeCount == 0)
                await transport.DisposeAsync();
        }
        Ensure(transport.DisposeCount == 1, "the built server must dispose its listener exactly once");
    }

    [Test]
    public async Task BuildWinningBeforeFirstTransportConfigurationMustRejectTheConfiguration()
    {
        var builder = CreateBuilder();
        var transport = new TrackingAllocatorTransport();
        var accessor = new AnonymousPipeAllocatorAccessor(builder);
        using var captured = new ManualResetEventSlim();
        using var releaseCapture = new ManualResetEventSlim();
        ISharpLinkServer? server = null;
        var build = LongRunningTestWorker.Run(() => Capture(() => server = builder.BuildWithTransportCapture(snapshot =>
        {
            accessor.CaptureTransport(snapshot);
            captured.Set();
            Ensure(releaseCapture.Wait(TimeSpan.FromSeconds(15)), "capture was not released");
        })));
        Task<Exception?>? configure = null;
        try
        {
            Ensure(captured.Wait(TimeSpan.FromSeconds(5)), "Build did not reach frozen capture");
            // This must complete while capture is blocked: the callback must not hold the
            // configuration gate, and the already frozen builder must reject the new listener.
            configure = LongRunningTestWorker.Run(() => Capture(() => builder.UseTransport(transport)));
            var configureFailure = await configure.WaitAsync(TimeSpan.FromSeconds(5));
            releaseCapture.Set();
            var buildFailure = await build.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(configureFailure is InvalidOperationException
            { Message: "This SharpLink builder has already been consumed." },
                "a configuration racing frozen capture must fail with the existing consumed contract");
            Ensure(buildFailure is InvalidOperationException
            { Message: "Transport must be set before building the server." },
                "the unconfigured build must preserve its validation failure");
            Ensure(accessor.AnonymousPipeAllocator is null && transport.DisposeCount == 0,
                "a rejected listener must not be captured or disposed by the builder");
        }
        finally
        {
            releaseCapture.Set();
            await LongRunningTestWorker.JoinAsync(build, TimeSpan.FromSeconds(5));
            if (configure is not null)
                await LongRunningTestWorker.JoinAsync(configure, TimeSpan.FromSeconds(5));
            if (server is not null)
                await server.DisposeAsync();
            else if (transport.DisposeCount == 0)
                await transport.DisposeAsync();
        }
        Ensure(transport.DisposeCount == 1, "the rejected listener remains caller-owned");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void CaptureFailureMustRollBackOnceAndPreserveCleanupFailures(bool cleanupFails)
    {
        var transport = new TrackingAllocatorTransport { CleanupFails = cleanupFails };
        var builder = CreateBuilder().UseTransport(transport);
        var accessor = new AnonymousPipeAllocatorAccessor(builder);
        var captureFailure = new InvalidOperationException("frozen transport capture failed");
        var failure = Capture(() => builder.BuildWithTransportCapture(snapshot =>
        {
            accessor.CaptureTransport(snapshot);
            throw captureFailure;
        }));

        if (cleanupFails)
        {
            Ensure(failure is AggregateException aggregate &&
                   aggregate.InnerExceptions.Count == 2 &&
                   ReferenceEquals(aggregate.InnerExceptions[0], captureFailure) &&
                   aggregate.InnerExceptions[1] is IOException { Message: "listener cleanup failed" },
                "capture and rollback failures must both remain observable in order");
        }
        else
        {
            Ensure(ReferenceEquals(failure, captureFailure), "the original capture failure must be preserved");
        }
        Ensure(builder.Transport is null && transport.DisposeCount == 1,
            "capture failure must consume the builder and roll back its owned listener exactly once");
        Ensure(ReferenceEquals(accessor.AnonymousPipeAllocator, transport),
            "the accessor keeps only its non-owning reference after rollback");
        Ensure(Capture(() => builder.BuildWithTransportCapture(accessor.CaptureTransport)) is InvalidOperationException
        { Message: "This SharpLink builder has already been consumed." },
            "retrying a failed capture must not rebuild a consumed builder");
        Ensure(transport.DisposeCount == 1, "a rejected retry must not repeat listener cleanup");
    }

    private static SharpLinkServerBuilder CreateBuilder() => SharpLinkServerBuilder.Create()
        .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty);

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class TrackingAllocatorTransport : IServerTransportListener, IAnonymousPipeAllocator
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public bool CleanupFails { get; init; }
        public System.Net.EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<AnonymousPipeOffer> AllocateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return CleanupFails
                ? ValueTask.FromException(new IOException("listener cleanup failed"))
                : ValueTask.CompletedTask;
        }
    }
}
