using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpLink.Hosting;
using SharpLink.Server;

namespace SharpLink.UnitTests.Hosting;

// Validation-only fixture. Both products receive the same scheduling-only capture seam.
[NotInParallel("anonymous-pipe-frozen-capture-evidence")]
public class AnonymousPipeFrozenCaptureProbe
{
    private const string HookKey = "SharpLink.Validation.FrozenAllocatorCapture";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AcceptedConfigurationMustSurviveHostedBuild(bool configureBeforeCapture)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IHostApplicationLifetime>(new Lifetime());
        var builder = services.AddSharpLinkServer()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty);
        var transport = new AllocatorTransport();
        if (configureBeforeCapture)
            builder.UseTransport(transport);
        await using var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IAnonymousPipeAllocatorAccessor>();
        var hosted = provider.GetServices<IHostedService>().Single(value => value is SharpLinkServerHostedService);
        using var captured = new ManualResetEventSlim();
        using var releaseCapture = new ManualResetEventSlim();
        AppContext.SetData(HookKey, (Action)(() =>
        {
            captured.Set();
            Ensure(releaseCapture.Wait(TimeSpan.FromSeconds(15)), "validation capture release timed out");
        }));
        var start = LongRunningTestWorker.RunAsync(async () =>
        {
            try
            {
                await hosted.StartAsync(CancellationToken.None);
                return (Exception?)null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });
        Exception? configurationFailure = null;
        Task<Exception?>? configure = null;
        try
        {
            Ensure(captured.Wait(TimeSpan.FromSeconds(10)), "hosted startup did not reach capture barrier");
            if (!configureBeforeCapture)
            {
                configure = LongRunningTestWorker.Run(() =>
                {
                    try
                    {
                        builder.UseTransport(transport);
                        return (Exception?)null;
                    }
                    catch (Exception exception)
                    {
                        return exception;
                    }
                });
                configurationFailure = await configure.WaitAsync(TimeSpan.FromSeconds(5));
            }
            releaseCapture.Set();
            var startFailure = await start.WaitAsync(TimeSpan.FromSeconds(10));
            if (configurationFailure is null)
            {
                Ensure(startFailure is null, "an accepted transport must build successfully");
                Ensure(ReferenceEquals(accessor.AnonymousPipeAllocator, transport),
                    "accepted transport was lost across hosted Build");
            }
            else
            {
                Ensure(!configureBeforeCapture && configurationFailure is InvalidOperationException
                    { Message: "This SharpLink builder has already been consumed." },
                    "frozen capture must reject only the losing configuration with the consumed contract");
                Ensure(startFailure is InvalidOperationException
                    { Message: "Transport must be set before building the server." },
                    "unconfigured frozen Build must preserve its validation failure");
                Ensure(accessor.AnonymousPipeAllocator is null && transport.DisposeCount == 0,
                    "rejected transport must remain uncaptured and caller-owned");
            }
            Ensure(builder.Transport is null, "both successful and failed builds must consume the builder");
        }
        finally
        {
            releaseCapture.Set();
            await LongRunningTestWorker.JoinAsync(start, TimeSpan.FromSeconds(10));
            if (configure is not null)
                await LongRunningTestWorker.JoinAsync(configure, TimeSpan.FromSeconds(10));
            AppContext.SetData(HookKey, null);
            await hosted.StopAsync(CancellationToken.None);
            if (configurationFailure is not null)
                await transport.DisposeAsync();
        }
        Ensure(transport.DisposeCount == 1, "the winning owner must release the transport exactly once");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class AllocatorTransport : IServerTransportListener, IAnonymousPipeAllocator
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public System.Net.EndPoint? LocalEndPoint => null;
        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
        public ValueTask<AnonymousPipeOffer> AllocateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
