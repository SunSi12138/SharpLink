using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpLink.Hosting;
using SharpLink.Server;

namespace SharpLink.UnitTests.Hosting;

public class AnonymousPipeAllocatorAccessorTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ConfiguredAllocatorShouldRemainAvailableAcrossStartup(
        bool configureLate, bool resolveAfterStartup)
    {
        var transport = new AllocatorTransport();
        var services = CreateServices();
        var builder = configureLate
            ? services.AddSharpLinkServer().UseTransport(transport)
            : services.AddSharpLinkServer(value => value.UseTransport(transport));
        builder.UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty);
        await using var provider = services.BuildServiceProvider();
        var hosted = GetHostedService(provider);
        IAnonymousPipeAllocatorAccessor? accessor = null;
        try
        {
            if (!resolveAfterStartup)
            {
                accessor = provider.GetRequiredService<IAnonymousPipeAllocatorAccessor>();
                Ensure(ReferenceEquals(accessor.AnonymousPipeAllocator, transport),
                    "the configured allocator must be available before startup");
            }

            await hosted.StartAsync(CancellationToken.None);
            Ensure(builder.Transport is null, "Build must release its transport reference");
            var resolved = provider.GetRequiredService<IAnonymousPipeAllocatorAccessor>();
            Ensure(accessor is null || ReferenceEquals(accessor, resolved),
                "startup must preserve the singleton accessor identity");
            Ensure(ReferenceEquals(resolved.AnonymousPipeAllocator, transport),
                "the allocator must remain available when first accessed after Build");
            Ensure(transport.DisposeCount == 0, "the accessor must not dispose a running transport");
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
        await hosted.StopAsync(CancellationToken.None);
        await provider.DisposeAsync();
        Ensure(transport.DisposeCount == 1, "stop and DI disposal must release the transport exactly once");
    }

    [Test]
    public async Task AccessorResolvedBeforeLateConfigurationShouldObserveTheFinalTransport()
    {
        var services = CreateServices();
        var builder = services.AddSharpLinkServer()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty);
        await using var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IAnonymousPipeAllocatorAccessor>();
        Ensure(accessor.AnonymousPipeAllocator is null, "an unconfigured builder has no allocator");
        var transport = new AllocatorTransport();
        builder.UseTransport(transport);
        Ensure(ReferenceEquals(accessor.AnonymousPipeAllocator, transport),
            "an already resolved accessor must observe later fluent configuration");
        var hosted = GetHostedService(provider);
        try
        {
            await hosted.StartAsync(CancellationToken.None);
            Ensure(ReferenceEquals(accessor.AnonymousPipeAllocator, transport),
                "startup must retain the final transport reference");
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NonAnonymousTransportShouldExposeANullAllocator(bool resolveAfterStartup)
    {
        var services = CreateServices();
        services.AddSharpLinkServer(builder => builder
            .UseTransport(new BlockingTransport())
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty));
        await using var provider = services.BuildServiceProvider();
        var hosted = GetHostedService(provider);
        try
        {
            if (!resolveAfterStartup)
                Ensure(provider.GetRequiredService<IAnonymousPipeAllocatorAccessor>().AnonymousPipeAllocator is null,
                    "a non-anonymous transport must expose null before startup");
            await hosted.StartAsync(CancellationToken.None);
            Ensure(provider.GetRequiredService<IAnonymousPipeAllocatorAccessor>().AnonymousPipeAllocator is null,
                "a non-anonymous transport must expose null after startup");
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task RepeatedRegistrationShouldUseOneAccessorForTheRegisteredBuilder()
    {
        var services = CreateServices();
        var transport = new AllocatorTransport();
        var registered = SharpLinkServerBuilder.Create()
            .UseTransport(transport)
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty);
        services.AddSingleton(registered);
        var first = services.AddSharpLinkServer();
        var second = services.AddSharpLinkServer();
        Ensure(ReferenceEquals(registered, first) && ReferenceEquals(first, second),
            "repeated registration must configure and return the registered builder");
        await using var provider = services.BuildServiceProvider();
        var accessor = provider.GetServices<IAnonymousPipeAllocatorAccessor>().Single();
        Ensure(ReferenceEquals(accessor, provider.GetRequiredService<IAnonymousPipeAllocatorAccessor>()),
            "repeated registration must expose one singleton accessor");
        Ensure(ReferenceEquals(accessor.AnonymousPipeAllocator, transport),
            "the accessor must read the builder actually registered in DI");
        var hosted = GetHostedService(provider);
        try
        {
            await hosted.StartAsync(CancellationToken.None);
            Ensure(ReferenceEquals(accessor.AnonymousPipeAllocator, transport),
                "the registered allocator must survive Build");
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
        Ensure(transport.DisposeCount == 1, "the hosted server must retain sole transport ownership");
    }

    [Test]
    public async Task ExistingInterfaceRegistrationShouldBePreserved()
    {
        var services = CreateServices();
        var custom = new CustomAccessor();
        services.AddSingleton<IAnonymousPipeAllocatorAccessor>(custom);
        var transport = new AllocatorTransport();
        services.AddSharpLinkServer(builder => builder
            .UseTransport(transport)
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty));
        await using var provider = services.BuildServiceProvider();
        Ensure(ReferenceEquals(provider.GetServices<IAnonymousPipeAllocatorAccessor>().Single(), custom),
            "AddSharpLinkServer must preserve a caller-provided interface registration");
        var hosted = GetHostedService(provider);
        try
        {
            await hosted.StartAsync(CancellationToken.None);
            Ensure(ReferenceEquals(provider.GetRequiredService<IAnonymousPipeAllocatorAccessor>(), custom),
                "startup must not replace the caller-provided accessor");
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
        Ensure(transport.DisposeCount == 1, "a custom accessor must not change transport cleanup");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BuildFailureShouldRetainSoleTransportOwnership(bool failDuringMaterialization)
    {
        var services = CreateServices();
        var transport = new AllocatorTransport { FailDuringMaterialization = failDuringMaterialization };
        var builder = services.AddSharpLinkServer().UseTransport(transport)
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty);
        if (!failDuringMaterialization)
            builder.RequireAuthentication();
        await using var provider = services.BuildServiceProvider();
        var hosted = GetHostedService(provider);
        Exception? failure = null;
        try
        {
            await hosted.StartAsync(CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            failure = exception;
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
        Ensure(failure?.Message == (failDuringMaterialization
                ? "allocator materialization failed"
                : "RequireAuthentication needs an ISharpLinkServerAuthenticator."),
            "the original compile or materialization failure must remain observable");
        Ensure(builder.Transport is null, "failed Build must release its transport reference");
        var accessor = provider.GetRequiredService<IAnonymousPipeAllocatorAccessor>();
        Ensure(ReferenceEquals(accessor.AnonymousPipeAllocator, transport),
            "the accessor retains only a non-owning reference after failed Build");
        await provider.DisposeAsync();
        Ensure(transport.DisposeCount == 1, "failed Build and DI disposal must dispose the transport exactly once");
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IHostApplicationLifetime>(new TestHostApplicationLifetime());
        return services;
    }

    private static IHostedService GetHostedService(IServiceProvider provider) =>
        provider.GetServices<IHostedService>().Single(service => service is SharpLinkServerHostedService);

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private class BlockingTransport : IServerTransportListener
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public System.Net.EndPoint? LocalEndPoint => null;

        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AllocatorTransport : BlockingTransport, IAnonymousPipeAllocator, IPerformanceProfileAwareTransport
    {
        public bool FailDuringMaterialization { get; init; }

        public ValueTask<AnonymousPipeOffer> AllocateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
        {
            if (FailDuringMaterialization)
                throw new InvalidOperationException("allocator materialization failed");
        }
    }

    private sealed class CustomAccessor : IAnonymousPipeAllocatorAccessor
    {
        public IAnonymousPipeAllocator? AnonymousPipeAllocator => null;
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
