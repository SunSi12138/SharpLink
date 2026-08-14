using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.DynamicPlugin;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

public enum ServerFeatureScenario
{
    StaticDefault,
    AdmissionImmediate,
    ServerInterceptor,
    ServerInterceptor2,
    ServerInterceptor4,
    ServerInterceptor8,
    MetricsClientAndServer,
    ServerTraceOnePercent,
    ServerTraceAll,
    DynamicRegisteredStaticHit,
    DynamicServiceActual
}

public enum ClientFeatureScenario
{
    FixedDefault,
    StaticTwoEndpoints,
    StaticFourEndpoints,
    StaticSixteenEndpoints,
    DynamicFourEndpoints,
    RetryFirstSuccess,
    AlwaysAcceptAdmission,
    ClosedCircuitBreaker,
    ClientInterceptor,
    ClientInterceptor2,
    ClientInterceptor4,
    ClientInterceptor8,
    ClientShortCircuit,
    ClientInterceptorAsyncBeforeNext,
    ClientInterceptorAsyncAfterNext,
    ClientInterceptorAsyncBeforeAndAfter,
    MetricsClientAndServer,
    ClientTraceOnePercent,
    ClientTraceAll
}

internal sealed class FeatureBenchmarkCase : IAsyncDisposable
{
    private static readonly TimeSpan SHeartbeatInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan SHeartbeatTimeout = TimeSpan.FromHours(2);
    private readonly BenchmarkEnvironment _environment;
    private readonly FeatureTelemetryScope _telemetry;
    private readonly Func<ValueTask<int>> _invoke;

    private FeatureBenchmarkCase(
        BenchmarkEnvironment environment,
        FeatureTelemetryScope telemetry,
        Func<ValueTask<int>> invoke,
        int expectedResult)
    {
        _environment = environment;
        _telemetry = telemetry;
        _invoke = invoke;
        ExpectedResult = expectedResult;
    }

    public int ExpectedResult { get; }

    public ValueTask<int> InvokeAsync() => _invoke();

    public ValueTask InvokeOneWayAsync()
        => _environment.Rpc.PublishEventAsync(7, Environment.TickCount64, "jit-probe");

    public static async Task<FeatureBenchmarkCase> CreateAsync(ServerFeatureScenario scenario)
    {
        var telemetry = FeatureTelemetryScope.ForServer(scenario);
        try
        {
            var dynamicRegistration = scenario is
                ServerFeatureScenario.DynamicRegisteredStaticHit or
                ServerFeatureScenario.DynamicServiceActual;
            var environment = await BenchmarkEnvironment.CreateAsync(
                configureServer: builder => ConfigureServer(builder, scenario),
                createClientBuilder: static port => CreateFixedClient(port),
                configureBuiltServer: dynamicRegistration ? RegisterDynamicServices : null)
                .ConfigureAwait(false);

            if (scenario == ServerFeatureScenario.DynamicServiceActual)
            {
                var proxy = environment.Get<IDynamicPluginService>();
                return new FeatureBenchmarkCase(
                    environment,
                    telemetry,
                    () => proxy.UnaryAsync(10, CancellationToken.None),
                    expectedResult: 11);
            }

            return new FeatureBenchmarkCase(
                environment,
                telemetry,
                () => environment.Rpc.AddAsync(10, 20),
                expectedResult: 30);
        }
        catch
        {
            telemetry.Dispose();
            throw;
        }
    }

    public static async Task<FeatureBenchmarkCase> CreateAsync(ClientFeatureScenario scenario)
    {
        var telemetry = FeatureTelemetryScope.ForClient(scenario);
        try
        {
            var expectedConnections = GetExpectedConnections(scenario);
            var environment = await BenchmarkEnvironment.CreateAsync(
                configureServer: static builder => builder.UseHeartbeat(
                    SHeartbeatInterval,
                    SHeartbeatTimeout),
                createClientBuilder: port => CreateClient(port, scenario),
                expectedReadyConnections: expectedConnections)
                .ConfigureAwait(false);
            return new FeatureBenchmarkCase(
                environment,
                telemetry,
                () => environment.Rpc.AddAsync(10, 20),
                expectedResult: 30);
        }
        catch
        {
            telemetry.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _environment.DisposeAsync().ConfigureAwait(false);
        _telemetry.Dispose();
    }

    private static void ConfigureServer(
        SharpLinkServerBuilder builder,
        ServerFeatureScenario scenario)
    {
        builder.UseHeartbeat(SHeartbeatInterval, SHeartbeatTimeout);
        if (scenario is ServerFeatureScenario.DynamicRegisteredStaticHit or
            ServerFeatureScenario.DynamicServiceActual)
        {
            builder.UseServiceProvider(DynamicServiceProvider.Instance);
        }
        switch (scenario)
        {
            case ServerFeatureScenario.AdmissionImmediate:
                builder.UseAdmissionControl(options => options.Global.UseConcurrency(1024));
                break;
            case ServerFeatureScenario.ServerInterceptor:
                builder.AddInterceptor(PassThroughServerInterceptor.Instance);
                break;
            case ServerFeatureScenario.ServerInterceptor2:
                builder.AddInterceptor(PassThroughServerInterceptor.Instance);
                builder.AddInterceptor(PassThroughServerInterceptor.Instance);
                break;
            case ServerFeatureScenario.ServerInterceptor4:
                for (var index = 0; index < 4; index++)
                    builder.AddInterceptor(PassThroughServerInterceptor.Instance);
                break;
            case ServerFeatureScenario.ServerInterceptor8:
                for (var index = 0; index < 8; index++)
                    builder.AddInterceptor(PassThroughServerInterceptor.Instance);
                break;
        }
    }

    private static SharpClientBuilder CreateFixedClient(int port)
        => SharpClientBuilder.Create()
            .UseHeartbeat(SHeartbeatInterval, SHeartbeatTimeout)
            .UseTcp(IPAddress.Loopback.ToString(), port);

    private static SharpClientBuilder CreateClient(
        int port,
        ClientFeatureScenario scenario)
    {
        SharpClientBuilder builder;
        switch (scenario)
        {
            case ClientFeatureScenario.StaticTwoEndpoints:
                builder = CreateStaticClient(port, 2);
                break;
            case ClientFeatureScenario.StaticFourEndpoints:
                builder = CreateStaticClient(port, 4);
                break;
            case ClientFeatureScenario.StaticSixteenEndpoints:
                builder = CreateStaticClient(port, 16);
                break;
            case ClientFeatureScenario.DynamicFourEndpoints:
                builder = CreateDynamicClient(port, 4);
                break;
            case ClientFeatureScenario.RetryFirstSuccess:
                builder = CreateStaticClient(port, 2).UseRetry();
                break;
            case ClientFeatureScenario.AlwaysAcceptAdmission:
                builder = CreateStaticClient(port, 2)
                    .UseEndpointAdmission(AlwaysAcceptAdmissionPolicy.Instance);
                break;
            case ClientFeatureScenario.ClosedCircuitBreaker:
                builder = CreateStaticClient(port, 2)
                    .UseCircuitBreaker(static _ => { });
                break;
            default:
                builder = CreateFixedClient(port);
                break;
        }

        if (scenario == ClientFeatureScenario.ClientInterceptor)
            builder.AddInterceptor(PassThroughClientInterceptor.Instance);
        if (scenario == ClientFeatureScenario.ClientInterceptor2)
        {
            builder.AddInterceptor(PassThroughClientInterceptor.Instance);
            builder.AddInterceptor(PassThroughClientInterceptor.Instance);
        }
        if (scenario == ClientFeatureScenario.ClientInterceptor4)
        {
            for (var index = 0; index < 4; index++)
                builder.AddInterceptor(PassThroughClientInterceptor.Instance);
        }
        if (scenario == ClientFeatureScenario.ClientInterceptor8)
        {
            for (var index = 0; index < 8; index++)
                builder.AddInterceptor(PassThroughClientInterceptor.Instance);
        }
        if (scenario == ClientFeatureScenario.ClientShortCircuit)
            builder.AddInterceptor(ShortCircuitClientInterceptor.Instance);
        if (scenario == ClientFeatureScenario.ClientInterceptorAsyncBeforeNext)
            builder.AddInterceptor(AsyncBeforeNextClientInterceptor.Instance);
        if (scenario == ClientFeatureScenario.ClientInterceptorAsyncAfterNext)
            builder.AddInterceptor(AsyncAfterNextClientInterceptor.Instance);
        if (scenario == ClientFeatureScenario.ClientInterceptorAsyncBeforeAndAfter)
            builder.AddInterceptor(AsyncBeforeAndAfterClientInterceptor.Instance);
        return builder;
    }

    private static SharpClientBuilder CreateStaticClient(int port, int endpointCount)
        => SharpClientBuilder.Create()
            .UseHeartbeat(SHeartbeatInterval, SHeartbeatTimeout)
            .UseEndpoints(CreateEndpoints(port, endpointCount), SharpLinkTransportFactories.Sockets())
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = endpointCount;
                options.MaxConnections = endpointCount;
                options.MaxConnectionsPerEndpoint = 1;
                options.MaxRetiringConnections = endpointCount;
            });

    private static SharpClientBuilder CreateDynamicClient(int port, int endpointCount)
        => SharpClientBuilder.Create()
            .UseHeartbeat(SHeartbeatInterval, SHeartbeatTimeout)
            .UseEndpointResolver(
                new StableEndpointResolver(CreateEndpoints(port, endpointCount)),
                SharpLinkTransportFactories.Sockets())
            .UseCluster(options =>
            {
                options.MaxEndpoints = endpointCount;
                options.MinReadyEndpoints = endpointCount;
                options.MaxConnections = endpointCount;
                options.MaxConnectionsPerEndpoint = 1;
                options.MaxRetiringConnections = endpointCount;
            });

    private static SharpLinkEndpoint[] CreateEndpoints(int port, int count)
    {
        var endpoints = new SharpLinkEndpoint[count];
        for (var index = 0; index < count; index++)
        {
            endpoints[index] = new SharpLinkEndpoint
            {
                Id = $"benchmark-{index}",
                Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), port)
            };
        }
        return endpoints;
    }

    private static int GetExpectedConnections(ClientFeatureScenario scenario)
        => scenario switch
        {
            ClientFeatureScenario.StaticTwoEndpoints or
            ClientFeatureScenario.RetryFirstSuccess or
            ClientFeatureScenario.AlwaysAcceptAdmission or
            ClientFeatureScenario.ClosedCircuitBreaker => 2,
            ClientFeatureScenario.StaticFourEndpoints or
            ClientFeatureScenario.DynamicFourEndpoints => 4,
            ClientFeatureScenario.StaticSixteenEndpoints => 16,
            _ => 1
        };

    private static void RegisterDynamicServices(ISharpLinkServer server)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "SharpLink.DynamicPlugin.Services.dll");
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        var result = server.RegisterAssembly(assembly);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Dynamic benchmark service registration failed: {result.Error}");
    }

    private sealed class StableEndpointResolver(IReadOnlyList<SharpLinkEndpoint> endpoints)
        : ISharpLinkEndpointResolver
    {
        private readonly SharpLinkEndpointSnapshot _snapshot = new(1, endpoints);

        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_snapshot);
        }

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PassThroughClientInterceptor : ISharpLinkClientInterceptor
    {
        internal static PassThroughClientInterceptor Instance { get; } = new();

        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
            => next(context);
    }

    private sealed class ShortCircuitClientInterceptor : ISharpLinkClientInterceptor
    {
        internal static ShortCircuitClientInterceptor Instance { get; } = new();

        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
            => ValueTask.FromResult(new SharpLinkClientInvocationResult(30));
    }

    private sealed class AsyncBeforeNextClientInterceptor : ISharpLinkClientInterceptor
    {
        internal static AsyncBeforeNextClientInterceptor Instance { get; } = new();

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            await Task.Yield();
            return await next(context).ConfigureAwait(false);
        }
    }

    private sealed class AsyncAfterNextClientInterceptor : ISharpLinkClientInterceptor
    {
        internal static AsyncAfterNextClientInterceptor Instance { get; } = new();

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            var result = await next(context).ConfigureAwait(false);
            await Task.Yield();
            return result;
        }
    }

    private sealed class AsyncBeforeAndAfterClientInterceptor : ISharpLinkClientInterceptor
    {
        internal static AsyncBeforeAndAfterClientInterceptor Instance { get; } = new();

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            await Task.Yield();
            var result = await next(context).ConfigureAwait(false);
            await Task.Yield();
            return result;
        }
    }

    private sealed class PassThroughServerInterceptor : ISharpLinkServerInterceptor
    {
        internal static PassThroughServerInterceptor Instance { get; } = new();

        public ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
            => next(context);
    }

    private sealed class AlwaysAcceptAdmissionPolicy : ISharpLinkEndpointAdmissionPolicy
    {
        internal static AlwaysAcceptAdmissionPolicy Instance { get; } = new();

        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
            => new(true, Token: 1, RetryAfter: null);

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
        }
    }

    private static class DynamicServiceProvider
    {
        internal static IServiceProvider Instance { get; } = new ServiceCollection()
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}

internal sealed class FeatureTelemetryScope : IDisposable
{
    private readonly MeterListener? _meterListener;
    private readonly ActivityListener? _activityListener;
    private readonly int _samplePercent;

    private FeatureTelemetryScope(
        bool metrics,
        ActivitySource? activitySource,
        int samplePercent)
    {
        _samplePercent = samplePercent;
        if (metrics)
        {
            _meterListener = new MeterListener();
            _meterListener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, SharpLinkTelemetry.Meter))
                    listener.EnableMeasurementEvents(instrument);
            };
            _meterListener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
            _meterListener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
            _meterListener.Start();
        }

        if (activitySource is not null)
        {
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => ReferenceEquals(source, activitySource),
                Sample = Sample,
                SampleUsingParentId = SampleUsingParentId
            };
            ActivitySource.AddActivityListener(_activityListener);
        }
    }

    public static FeatureTelemetryScope ForServer(ServerFeatureScenario scenario)
        => scenario switch
        {
            ServerFeatureScenario.MetricsClientAndServer => new(true, null, 0),
            ServerFeatureScenario.ServerTraceOnePercent => new(false, SharpLinkTelemetry.ServerActivitySource, 1),
            ServerFeatureScenario.ServerTraceAll => new(false, SharpLinkTelemetry.ServerActivitySource, 100),
            _ => new(false, null, 0)
        };

    public static FeatureTelemetryScope ForClient(ClientFeatureScenario scenario)
        => scenario switch
        {
            ClientFeatureScenario.MetricsClientAndServer => new(true, null, 0),
            ClientFeatureScenario.ClientTraceOnePercent => new(false, SharpLinkTelemetry.ClientActivitySource, 1),
            ClientFeatureScenario.ClientTraceAll => new(false, SharpLinkTelemetry.ClientActivitySource, 100),
            _ => new(false, null, 0)
        };

    public void Dispose()
    {
        _activityListener?.Dispose();
        _meterListener?.Dispose();
    }

    private ActivitySamplingResult Sample(ref ActivityCreationOptions<ActivityContext> options)
        => ShouldRecord(options.TraceId)
            ? ActivitySamplingResult.AllDataAndRecorded
            : ActivitySamplingResult.PropagationData;

    private ActivitySamplingResult SampleUsingParentId(ref ActivityCreationOptions<string> options)
        => _samplePercent >= 100
            ? ActivitySamplingResult.AllDataAndRecorded
            : ActivitySamplingResult.PropagationData;

    private bool ShouldRecord(ActivityTraceId traceId)
        => _samplePercent >= 100 ||
           (_samplePercent > 0 && (uint)traceId.GetHashCode() % 100 < _samplePercent);
}
