using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpLink.Hosting;

namespace SharpLink.IntegrationTests;

public class HostedServiceProviderIntegrationTests
{
    // Mode 0: callback, 1: late fluent override, 2: Host fallback, 3: application-only dependency.
    [Test]
    [Arguments(SharpLinkServiceLifetime.Singleton, 0)]
    [Arguments(SharpLinkServiceLifetime.Singleton, 1)]
    [Arguments(SharpLinkServiceLifetime.Singleton, 2)]
    [Arguments(SharpLinkServiceLifetime.Singleton, 3)]
    [Arguments(SharpLinkServiceLifetime.Connection, 0)]
    [Arguments(SharpLinkServiceLifetime.Connection, 1)]
    [Arguments(SharpLinkServiceLifetime.Connection, 2)]
    [Arguments(SharpLinkServiceLifetime.Connection, 3)]
    [Arguments(SharpLinkServiceLifetime.Call, 0)]
    [Arguments(SharpLinkServiceLifetime.Call, 1)]
    [Arguments(SharpLinkServiceLifetime.Call, 2)]
    [Arguments(SharpLinkServiceLifetime.Call, 3)]
    public async Task HostedRpcShouldUseSelectedProviderAndHonorOwnership(
        SharpLinkServiceLifetime lifetime, int mode)
    {
        var applicationState = new ProviderState("application");
        var hostState = new ProviderState("host");
        var supersededState = new ProviderState("superseded");
        await using var application = AddDependencies(new ServiceCollection(), applicationState, lifetime)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var superseded = AddDependencies(new ServiceCollection(), supersededState, lifetime)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var hostLifetime = new TestHostLifetime();
        var services = CreateHostServices(hostLifetime);
        if (mode != 3)
            AddDependencies(services, hostState, lifetime);
        var builder = services.AddSharpLinkServer(builder =>
        {
            ConfigureServer(builder, lifetime);
            if (mode != 2)
                builder.UseServiceProvider(mode == 1 ? superseded : application);
        });
        if (mode == 1)
            builder.UseServiceProvider(application);
        var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
        await using var host = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var hosted = host.GetServices<IHostedService>().Single(static service =>
            service.GetType().FullName == "SharpLink.Hosting.SharpLinkServerHostedService");
        var readiness = host.GetRequiredService<ISharpLinkServerReadiness>();
        var selected = mode == 2 ? hostState : applicationState;
        await hosted.StartAsync(CancellationToken.None);
        try
        {
            Ensure(readiness.Status == SharpLinkHealthStatus.Ready, "Host readiness must be published");
            await using var client = SharpClientBuilder.Create()
                .UseTcp(IPAddress.Loopback.ToString(), port).UseRequestTimeout(TimeSpan.FromSeconds(10)).Build();
            await client.ConnectAsync();
            var service = client.Get<IHostedProviderContract>();
            var first = await service.ResolveAsync();
            var second = await service.ResolveAsync();
            Ensure(first.StartsWith(selected.Name + ":", StringComparison.Ordinal) &&
                   second.StartsWith(selected.Name + ":", StringComparison.Ordinal),
                $"expected {selected.Name} dependency; received {first} and {second}");
            Ensure((first == second) == (lifetime != SharpLinkServiceLifetime.Call),
                "singleton/connection must reuse instances; call must create a fresh scoped instance");
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }

        var expectedInstances = lifetime == SharpLinkServiceLifetime.Call ? 2 : 1;
        Ensure(selected.ServicesCreated == expectedInstances && selected.ServicesDisposed == expectedInstances,
            "server-owned service instances must be disposed exactly once at their lifetime boundary");
        Ensure(selected.DependenciesCreated == expectedInstances,
            "the selected root/scope must resolve the actual dependency");
        Ensure(selected.DependenciesDisposed == (lifetime == SharpLinkServiceLifetime.Singleton ? 0 : expectedInstances),
            "scopes own scoped dependencies; singleton dependency remains owned by its external root");
        Ensure(supersededState.ServicesCreated == 0 && supersededState.DependenciesCreated == 0,
            "late explicit configuration must supersede the earlier provider without using it");
        Ensure((mode == 2 ? applicationState : hostState).ServicesCreated == 0 &&
               (mode == 2 ? applicationState : hostState).DependenciesCreated == 0,
            "business resolution must not consult the unselected provider");
        Ensure(ReferenceEquals(application.GetRequiredService<ProviderState>(), applicationState),
            "hosted stop must leave the application root usable");
        Ensure(ReferenceEquals(host.GetRequiredService<IHostApplicationLifetime>(), hostLifetime),
            "hosted stop must leave Host infrastructure and its root usable");
        Ensure(readiness.Status == SharpLinkHealthStatus.Unhealthy && hostLifetime.StopCalls == 0,
            "normal stop must clear Host readiness without reporting an unexpected termination");
        await application.DisposeAsync();
        await host.DisposeAsync();
        Ensure(selected.DependenciesDisposed == expectedInstances,
            "external root owner must dispose any singleton dependency exactly once");
    }

    [Test]
    public async Task ExplicitProviderShouldNotFallBackToHostForMissingBusinessDependency()
    {
        await using var application = new ServiceCollection().BuildServiceProvider();
        var hostState = new ProviderState("host");
        using var hostLifetime = new TestHostLifetime();
        var services = AddDependencies(CreateHostServices(hostLifetime), hostState, SharpLinkServiceLifetime.Call);
        var builder = services.AddSharpLinkServer(builder =>
        {
            ConfigureServer(builder, SharpLinkServiceLifetime.Call);
            builder.UseServiceProvider(application);
        });
        var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
        await using var host = services.BuildServiceProvider();
        var hosted = host.GetServices<IHostedService>().Single(static service =>
            service.GetType().FullName == "SharpLink.Hosting.SharpLinkServerHostedService");
        await hosted.StartAsync(CancellationToken.None);
        try
        {
            await using var client = SharpClientBuilder.Create()
                .UseTcp(IPAddress.Loopback.ToString(), port).UseRequestTimeout(TimeSpan.FromSeconds(10)).Build();
            await client.ConnectAsync();
            Exception? failure = null;
            try { _ = await client.Get<IHostedProviderContract>().ResolveAsync(); }
            catch (Exception exception) { failure = exception; }
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.Internal },
                "missing dependency in the explicit provider must fail the real RPC without Host fallback");
            Ensure(hostState.DependenciesCreated == 0 && hostState.ServicesCreated == 0,
                "the Host's available business dependency must remain unused");
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    private static void ConfigureServer(SharpLinkServerBuilder builder, SharpLinkServiceLifetime lifetime)
        => builder.UseTcp(0, IPAddress.Loopback)
            .DisableAutomaticServiceRegistration()
            .ReplaceService<IHostedProviderContract>(
                static provider => new HostedProviderService(provider.GetRequiredService<ProviderDependency>()), lifetime);

    private static ServiceCollection CreateHostServices(TestHostLifetime lifetime)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services;
    }

    private static ServiceCollection AddDependencies(
        ServiceCollection services, ProviderState state, SharpLinkServiceLifetime lifetime)
    {
        services.AddSingleton(state);
        if (lifetime == SharpLinkServiceLifetime.Singleton)
            services.AddSingleton(_ => new ProviderDependency(state));
        else
            services.AddScoped(_ => new ProviderDependency(state));
        return services;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class ProviderState(string name)
    {
        internal string Name { get; } = name;
        internal int DependenciesCreated;
        internal int DependenciesDisposed;
        internal int ServicesCreated;
        internal int ServicesDisposed;
    }

    private sealed class ProviderDependency(ProviderState state) : IAsyncDisposable
    {
        internal ProviderState State { get; } = state;
        internal int Id { get; } = Interlocked.Increment(ref state.DependenciesCreated);
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref State.DependenciesDisposed);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HostedProviderService(ProviderDependency dependency) : IHostedProviderContract, IAsyncDisposable
    {
        private readonly int _id = Interlocked.Increment(ref dependency.State.ServicesCreated);
        public ValueTask<string> ResolveAsync()
            => ValueTask.FromResult($"{dependency.State.Name}:{dependency.Id}:{_id}");
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref dependency.State.ServicesDisposed);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestHostLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        internal int StopCalls;
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication()
        {
            Interlocked.Increment(ref StopCalls);
            _stopping.Cancel();
        }
        public void Dispose() => _stopping.Dispose();
    }
}

[RpcContract]
public interface IHostedProviderContract : IService
{
    [NonCancellable]
    ValueTask<string> ResolveAsync();
}
