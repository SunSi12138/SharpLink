using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Hosting;
using SharpLink.Runtime;
using SharpLink.Server;

namespace SharpLink.UnitTests.Hosting;

public sealed class HostingBuilderRegistrationTests
{
    [Test]
    public async Task RepeatedServerRegistrationMustConfigureTheActuallyRegisteredBuilder()
    {
        var services = new ServiceCollection();
        var first = services.AddSharpLinkServer(builder =>
        {
            builder.UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty);
            builder.UseTransport(new NoopTransport());
        });
        var configured = false;
        var second = services.AddSharpLinkServer(builder =>
        {
            configured = true;
            builder.UseBufferWriterPool(static options => options.InitialCapacity = 2048);
        });

        Ensure(configured && ReferenceEquals(first, second),
            "repeated Server setup must configure/return the previously registered Builder");
        Ensure(CountHosted<SharpLinkServerHostedService>(services) == 1,
            "repeated Server setup must not register another hosted service");

        await using var provider = services.BuildServiceProvider();
        Ensure(ReferenceEquals(provider.GetRequiredService<SharpLinkServerBuilder>(), first),
            "DI must resolve the Builder receiving the second configuration callback");
        Ensure(HealthCheckCount(provider, "sharplink_server") == 1,
            "repeated Server setup must not register two health checks with one name");

        await using var server = first.Build();
        var context = (SharpLinkRuntimeContext)(server.GetType()
            .GetField("_runtimeContext", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(server) ?? throw new Exception("Server runtime context missing."));
        Ensure(context.Buffers.InitialCapacity == 2048,
            "second Server configure callback must influence materialized writer pool capacity");
    }

    [Test]
    public async Task RepeatedClientRegistrationMustConfigureTheActuallyRegisteredBuilder()
    {
        var services = new ServiceCollection();
        var first = services.AddSharpLinkClient(builder =>
        {
            builder.UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty);
            builder.UseTransport(new NoopTransport());
            builder.DisableRequestTimeout();
        });
        var configured = false;
        var second = services.AddSharpLinkClient(builder =>
        {
            configured = true;
            builder.UseBufferWriterPool(static options => options.InitialCapacity = 3072);
        });

        Ensure(configured && ReferenceEquals(first, second),
            "repeated Client setup must configure/return the previously registered Builder");
        Ensure(CountHosted<SharpLinkClientHostedService>(services) == 1,
            "repeated Client setup must not register another hosted service");

        await using var provider = services.BuildServiceProvider();
        Ensure(ReferenceEquals(provider.GetRequiredService<SharpClientBuilder>(), first),
            "DI must resolve the Builder receiving the second configuration callback");
        Ensure(HealthCheckCount(provider, "sharplink_remote") == 1,
            "repeated Client setup must not register two health checks with one name");

        await using var client = first.Build();
        var context = (SharpLinkRuntimeContext)((IRpcChannel)client).RuntimeContext;
        Ensure(context.Buffers.InitialCapacity == 3072,
            "second Client configure callback must influence materialized writer pool capacity");
    }

    [Test]
    public void RepeatedMultiClusterRegistrationMustReuseTheRealCoordinatorBuilder()
    {
        var services = new ServiceCollection();
        var first = services.AddSharpLinkMultiClusterClient(static builder =>
            builder.DisableRequestTimeout());
        var configured = false;
        var second = services.AddSharpLinkMultiClusterClient(builder =>
        {
            configured = true;
            builder.Configure(static options => options.MaxClusters = 7);
        });

        Ensure(configured && ReferenceEquals(first, second),
            "multi-cluster repeated configuration must return the registered coordinator Builder");
        Ensure(CountHosted<SharpLinkMultiClusterClientHostedService>(services) == 1,
            "repeated multi-cluster setup must not register another hosted service");

        using var provider = services.BuildServiceProvider();
        Ensure(ReferenceEquals(provider.GetRequiredService<SharpLinkMultiClusterClientBuilder>(), first),
            "DI must expose the actual configured multi-cluster Builder");
        var options = first.GetType().GetField("_options",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(first) as SharpLinkMultiClusterOptions;
        Ensure(options?.MaxClusters == 7,
            "second coordinator configure callback must change the registered Builder's real options");
    }

    [Test]
    public void ExistingFactoryBasedBuilderRegistrationsMustRejectLateConfiguration()
    {
        var serverServices = new ServiceCollection();
        serverServices.AddSingleton<SharpLinkServerBuilder>(
            static _ => SharpLinkServerBuilder.Create());
        var serverCallbackCalled = false;
        var serverError = Capture(() => serverServices.AddSharpLinkServer(_ => serverCallbackCalled = true));

        Ensure(serverError is InvalidOperationException &&
               serverError.Message.Contains("type or factory", StringComparison.Ordinal) &&
               !serverCallbackCalled &&
               CountHosted<SharpLinkServerHostedService>(serverServices) == 0,
            "a factory-registered Server Builder cannot silently receive configuration on a throwaway instance");

        var clientServices = new ServiceCollection();
        clientServices.AddSingleton<SharpClientBuilder>(
            static _ => SharpClientBuilder.Create());
        var clientCallbackCalled = false;
        var clientError = Capture(() => clientServices.AddSharpLinkClient(_ => clientCallbackCalled = true));

        Ensure(clientError is InvalidOperationException &&
               clientError.Message.Contains("type or factory", StringComparison.Ordinal) &&
               !clientCallbackCalled &&
               CountHosted<SharpLinkClientHostedService>(clientServices) == 0,
            "a factory-registered Client Builder cannot silently receive configuration on a throwaway instance");

        var multiServices = new ServiceCollection();
        multiServices.AddSingleton<SharpLinkMultiClusterClientBuilder>(
            static _ => SharpLinkMultiClusterClientBuilder.Create());
        var multiCallbackCalled = false;
        var multiError = Capture(() => multiServices.AddSharpLinkMultiClusterClient(_ => multiCallbackCalled = true));

        Ensure(multiError is InvalidOperationException &&
               multiError.Message.Contains("type or factory", StringComparison.Ordinal) &&
               !multiCallbackCalled &&
               CountHosted<SharpLinkMultiClusterClientHostedService>(multiServices) == 0,
            "a factory-registered coordinator Builder cannot silently receive configuration on a throwaway instance");
    }

    [Test]
    public void AnExistingInstanceRegistrationMustBeReusedEvenOnFirstHostRegistration()
    {
        var registered = SharpLinkServerBuilder.Create();
        var services = new ServiceCollection();
        services.AddSingleton(registered);

        var configured = false;
        var actual = services.AddSharpLinkServer(_ => configured = true);

        Ensure(configured && ReferenceEquals(actual, registered),
            "an application-provided singleton Builder instance must be the one configured by Hosting");
        Ensure(CountHosted<SharpLinkServerHostedService>(services) == 1,
            "a manually registered Builder must still receive Hosting infrastructure");
    }

    [Test]
    public void MultiplePreexistingUnkeyedBuildersMustFailInsteadOfChoosingOneArbitrarily()
    {
        var services = new ServiceCollection();
        services.AddSingleton(SharpClientBuilder.Create());
        services.AddSingleton(SharpClientBuilder.Create());

        var callbackCalled = false;
        var error = Capture(() => services.AddSharpLinkClient(_ => callbackCalled = true));
        Ensure(error is InvalidOperationException &&
               error.Message.Contains("More than one", StringComparison.Ordinal) &&
               !callbackCalled,
            "an ambiguous Builder configuration must fail rather than silently choosing a DI registration");
    }

    private static int CountHosted<T>(IServiceCollection services) where T : IHostedService
        => services.Count(descriptor =>
            !descriptor.IsKeyedService &&
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(T));

    private static int HealthCheckCount(IServiceProvider provider, string name)
        => provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.Count(registration => registration.Name == name);

    private static Exception? Capture(Action action)
    {
        try { action(); }
        catch (Exception exception) { return exception; }
        return null;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class NoopTransport : IClientTransportFactory, IServerTransportListener
    {
        public System.Net.EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
