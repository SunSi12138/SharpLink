using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public class EnterpriseHostingIntegrationTests
{
    [Test]
    public async Task ScopedTypeServiceShouldResolveDependenciesAndLiveThroughStream()
    {
        LifetimeServiceState.Reset();
        await using var provider = new ServiceCollection()
            .AddSingleton(new LifetimeDependency(41))
            .AddScoped<EnterpriseLifetimeService>()
            .BuildServiceProvider();
        await using var harness = await EnterpriseHarness.CreateAsync(builder => builder
            .UseServiceProvider(provider)
            .AddService<IEnterpriseLifetimeService, EnterpriseLifetimeService>(ServiceLifetime.Scoped));
        var service = harness.Client.Get<IEnterpriseLifetimeService>();

        var first = await service.GetValueAsync();
        var second = await service.GetValueAsync();
        Ensure(first != second, "scoped service instance per unary call");
        Ensure(LifetimeServiceState.Created == 2, "two scoped instances created");
        Ensure(LifetimeServiceState.Disposed == 2, "unary scopes disposed");

        var stream = service.StreamAsync().GetAsyncEnumerator();
        try
        {
            Ensure(await stream.MoveNextAsync(), "first stream item");
            Ensure(LifetimeServiceState.Disposed == 2, "stream scope remains alive");
            LifetimeServiceState.ReleaseStream();
            Ensure(await stream.MoveNextAsync(), "second stream item");
            Ensure(!await stream.MoveNextAsync(), "stream completion");
        }
        finally
        {
            await stream.DisposeAsync();
        }
        await WaitUntilAsync(() => LifetimeServiceState.Disposed == 3);
    }

    [Test]
    public async Task ScopedFactoryAndCallerOwnedSingletonShouldHonorOwnership()
    {
        LifetimeServiceState.Reset();
        await using var provider = new ServiceCollection()
            .AddSingleton(new LifetimeDependency(7))
            .BuildServiceProvider();
        await using (var harness = await EnterpriseHarness.CreateAsync(builder => builder
            .UseServiceProvider(provider)
            .AddService<IEnterpriseLifetimeService>(
                static services => new EnterpriseLifetimeService(
                    services.GetRequiredService<LifetimeDependency>()),
                ServiceLifetime.Scoped)))
        {
            _ = await harness.Client.Get<IEnterpriseLifetimeService>().GetValueAsync();
            Ensure(LifetimeServiceState.Disposed == 1, "factory scoped service disposed");
        }

        LifetimeServiceState.Reset();
        var instance = new EnterpriseLifetimeService(new LifetimeDependency(3));
        await using (var harness = await EnterpriseHarness.CreateAsync(builder =>
            builder.AddService<IEnterpriseLifetimeService>(instance)))
        {
            _ = await harness.Client.Get<IEnterpriseLifetimeService>().GetValueAsync();
        }
        Ensure(LifetimeServiceState.Disposed == 0, "caller-owned singleton is not disposed");
        await instance.DisposeAsync();
    }

    [Test]
    public async Task TransientAndServerOwnedSingletonShouldDisposeAtTheirBoundaries()
    {
        LifetimeServiceState.Reset();
        await using var provider = new ServiceCollection()
            .AddSingleton(new LifetimeDependency(5))
            .AddTransient<EnterpriseLifetimeService>()
            .BuildServiceProvider();
        await using (var transientHarness = await EnterpriseHarness.CreateAsync(builder => builder
            .UseServiceProvider(provider)
            .AddService<IEnterpriseLifetimeService, EnterpriseLifetimeService>(ServiceLifetime.Transient)))
        {
            var service = transientHarness.Client.Get<IEnterpriseLifetimeService>();
            _ = await service.GetValueAsync();
            _ = await service.GetValueAsync();
            Ensure(LifetimeServiceState.Created == 2, "two transient instances created");
            Ensure(LifetimeServiceState.Disposed == 2, "transient instances disposed per call");
        }

        LifetimeServiceState.Reset();
        await using (var singletonHarness = await EnterpriseHarness.CreateAsync(builder => builder
            .AddService<IEnterpriseLifetimeService>(
                static _ => new EnterpriseLifetimeService(new LifetimeDependency(9)),
                ServiceLifetime.Singleton)))
        {
            var singleton = singletonHarness.Client.Get<IEnterpriseLifetimeService>();
            var first = await singleton.GetValueAsync();
            var second = await singleton.GetValueAsync();
            Ensure(first == second, "server-owned singleton reused");
            Ensure(LifetimeServiceState.Disposed == 0, "server-owned singleton alive before stop");
        }
        Ensure(LifetimeServiceState.Disposed == 1, "server-owned singleton disposed with server");
    }

    [Test]
    public async Task HealthControlFrameAndServerReadinessShouldTrackDrainLifecycle()
    {
        LifetimeServiceState.Reset();
        await using var harness = await EnterpriseHarness.CreateAsync(builder =>
            builder.AddService<IEnterpriseLifetimeService, EnterpriseLifetimeService>());

        Ensure(harness.Server.HealthStatus == SharpLinkHealthStatus.Ready, "server ready after listen");
        var health = await harness.Client.CheckHealthAsync();
        Ensure(health.Status == SharpLinkHealthStatus.Ready, "remote health ready");

        var stream = harness.Client.Get<IEnterpriseLifetimeService>().StreamAsync().GetAsyncEnumerator();
        Ensure(await stream.MoveNextAsync(), "active stream started");
        var stopTask = harness.Server.StopAsync(TimeSpan.FromSeconds(5)).AsTask();
        await WaitUntilAsync(() => harness.Server.HealthStatus == SharpLinkHealthStatus.Draining);
        Ensure(!stopTask.IsCompleted, "stop waits for active stream");

        LifetimeServiceState.ReleaseStream();
        while (await stream.MoveNextAsync())
        {
        }
        await stream.DisposeAsync();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(harness.Server.HealthStatus == SharpLinkHealthStatus.Unhealthy,
            "stopped server is unhealthy");
    }

    [Test]
    [NotInParallel]
    public async Task StopShouldBeBoundedAndDeferOwnedServiceDisposalForUncooperativeCall()
    {
        UncooperativeLifetimeService.Reset();
        await using var harness = await EnterpriseHarness.CreateAsync(builder => builder
            .AddService<IUncooperativeLifetimeService>(
                static _ => new UncooperativeLifetimeService(),
                ServiceLifetime.Singleton));
        var service = harness.Client.Get<IUncooperativeLifetimeService>();

        var invocation = service.WaitForReleaseAsync(CancellationToken.None).AsTask();
        await UncooperativeLifetimeService.WaitForStartAsync().WaitAsync(TimeSpan.FromSeconds(3));

        var started = Stopwatch.GetTimestamp();
        await harness.Server.StopAsync(TimeSpan.FromMilliseconds(100));
        var elapsed = Stopwatch.GetElapsedTime(started);

        Ensure(elapsed < TimeSpan.FromSeconds(2), "uncooperative user call must not hold server stop");
        Ensure(harness.Server.HealthStatus == SharpLinkHealthStatus.Unhealthy, "stopped server is unhealthy");
        Ensure(UncooperativeLifetimeService.Disposed == 0,
            "server-owned service must remain alive while its invocation is still running");
        await EnsureConnectionClosedAsync(invocation);

        UncooperativeLifetimeService.Release();
        await UncooperativeLifetimeService.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => UncooperativeLifetimeService.Disposed == 1);
    }

    private static async Task EnsureConnectionClosedAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            throw new Exception("assert failed: invocation should fail when its connection closes");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ConnectionClosed)
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new Exception("timed out waiting for condition");
            await Task.Delay(10);
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
    }

    private sealed class EnterpriseHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        public ISharpLinkServer Server { get; }
        public ISharpLinkClient Client { get; }

        private EnterpriseHarness(
            CancellationTokenSource serverCts,
            Task serverTask,
            ISharpLinkServer server,
            ISharpLinkClient client)
        {
            _serverCts = serverCts;
            _serverTask = serverTask;
            Server = server;
            Client = client;
        }

        public static async Task<EnterpriseHarness> CreateAsync(
            Action<SharpLinkServerBuilder> configure)
        {
            var serverCts = new CancellationTokenSource();
            var builder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
            configure(builder);
            var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
            var server = builder.Build();
            var serverTask = Task.Run(() => server.RunAsync(serverCts.Token).AsTask());
            var client = SharpClientBuilder.Create()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
                .Build();
            await client.ConnectAsync(serverCts.Token);
            return new EnterpriseHarness(serverCts, serverTask, server, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _serverCts.CancelAsync();
            await Server.DisposeAsync();
            await Task.WhenAny(_serverTask, Task.Delay(1000));
            _serverCts.Dispose();
        }
    }
}

[RpcContract]
public interface IUncooperativeLifetimeService : IService
{
    ValueTask WaitForReleaseAsync(CancellationToken cancellationToken);
}

[RpcService]
public sealed class UncooperativeLifetimeService : IUncooperativeLifetimeService, IAsyncDisposable
{
    private static TaskCompletionSource<bool> s_started = CreateCompletionSource();
    private static TaskCompletionSource<bool> s_release = CreateCompletionSource();
    private static TaskCompletionSource<bool> s_completed = CreateCompletionSource();
    private static int s_disposed;

    internal static int Disposed => Volatile.Read(ref s_disposed);

    internal static Task WaitForStartAsync() => Volatile.Read(ref s_started).Task;

    internal static Task WaitForCompletionAsync() => Volatile.Read(ref s_completed).Task;

    internal static void Release() => Volatile.Read(ref s_release).TrySetResult(true);

    internal static void Reset()
    {
        Volatile.Write(ref s_started, CreateCompletionSource());
        Volatile.Write(ref s_release, CreateCompletionSource());
        Volatile.Write(ref s_completed, CreateCompletionSource());
        Volatile.Write(ref s_disposed, 0);
    }

    public async ValueTask WaitForReleaseAsync(CancellationToken cancellationToken)
    {
        Volatile.Read(ref s_started).TrySetResult(true);
        await Volatile.Read(ref s_release).Task.ConfigureAwait(false);
        Volatile.Read(ref s_completed).TrySetResult(true);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref s_disposed);
        return ValueTask.CompletedTask;
    }

    private static TaskCompletionSource<bool> CreateCompletionSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed record LifetimeDependency(int Value);

internal static class LifetimeServiceState
{
    private static TaskCompletionSource<bool> _streamRelease = CreateRelease();
    private static int _nextId;
    private static int _created;
    private static int _disposed;

    public static int Created => Volatile.Read(ref _created);
    public static int Disposed => Volatile.Read(ref _disposed);

    public static int CreateInstance()
    {
        Interlocked.Increment(ref _created);
        return Interlocked.Increment(ref _nextId);
    }

    public static Task WaitForStreamReleaseAsync() => Volatile.Read(ref _streamRelease).Task;

    public static void ReleaseStream() => Volatile.Read(ref _streamRelease).TrySetResult(true);

    public static void RecordDispose() => Interlocked.Increment(ref _disposed);

    public static void Reset()
    {
        Volatile.Write(ref _nextId, 0);
        Volatile.Write(ref _created, 0);
        Volatile.Write(ref _disposed, 0);
        Volatile.Write(ref _streamRelease, CreateRelease());
    }

    private static TaskCompletionSource<bool> CreateRelease()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

[RpcContract]
public interface IEnterpriseLifetimeService : IService
{
    [NonCancellable]
    ValueTask<int> GetValueAsync();
    [NonCancellable]
    IAsyncEnumerable<int> StreamAsync();
}

[RpcService]
public sealed class EnterpriseLifetimeService : IEnterpriseLifetimeService, IAsyncDisposable
{
    private readonly LifetimeDependency _dependency;
    private readonly int _instanceId = LifetimeServiceState.CreateInstance();

    public EnterpriseLifetimeService() : this(new LifetimeDependency(0))
    {
    }

    public EnterpriseLifetimeService(LifetimeDependency dependency)
    {
        _dependency = dependency;
    }

    public ValueTask<int> GetValueAsync()
        => ValueTask.FromResult((_dependency.Value * 10_000) + _instanceId);

    public async IAsyncEnumerable<int> StreamAsync()
    {
        yield return _instanceId;
        await LifetimeServiceState.WaitForStreamReleaseAsync();
        yield return _instanceId;
    }

    public ValueTask DisposeAsync()
    {
        LifetimeServiceState.RecordDispose();
        return ValueTask.CompletedTask;
    }
}
