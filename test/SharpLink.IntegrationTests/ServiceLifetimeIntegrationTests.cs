namespace SharpLink.IntegrationTests;

public sealed class ServiceLifetimeIntegrationTests
{
    [Test]
    public async Task ConnectionLifetimeShouldReusePerConnectionAndDisposeOnDisconnect()
    {
        ConnectionLifetimeProbe.Reset();
        using var serverCancellation = new CancellationTokenSource();
        var builder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
        var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
        await using var server = builder.Build();
        var serverTask = server.RunAsync(serverCancellation.Token).AsTask();
        await using var firstClient = CreateClient(port);
        await using var secondClient = CreateClient(port);
        await firstClient.ConnectAsync();
        await secondClient.ConnectAsync();

        var first = firstClient.Get<IConnectionLifetimeProbe>();
        var second = secondClient.Get<IConnectionLifetimeProbe>();
        var firstId = await first.GetInstanceIdAsync();
        Ensure(await first.GetInstanceIdAsync() == firstId, "one instance is reused on a connection");
        var secondId = await second.GetInstanceIdAsync();
        Ensure(secondId != firstId, "different physical connections receive different instances");
        Ensure(ConnectionLifetimeProbe.Created == 2, "two connection instances created");

        await firstClient.StopAsync();
        await WaitUntilAsync(() => ConnectionLifetimeProbe.Disposed == 1);
        Ensure(await second.GetInstanceIdAsync() == secondId, "other connection remains usable");

        await secondClient.StopAsync();
        await WaitUntilAsync(() => ConnectionLifetimeProbe.Disposed == 2);
        await server.StopAsync(TimeSpan.FromSeconds(2));
        await serverCancellation.CancelAsync();
        await IgnoreStopAsync(serverTask);
    }

    [Test]
    public async Task CallLifetimeShouldCreateOneInstancePerUnaryOrWholeStream()
    {
        CallLifetimeProbe.Reset();
        using var serverCancellation = new CancellationTokenSource();
        var builder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
        var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
        await using var server = builder.Build();
        var serverTask = server.RunAsync(serverCancellation.Token).AsTask();
        await using var client = CreateClient(port);
        await client.ConnectAsync();
        var service = client.Get<ICallLifetimeProbe>();

        var first = await service.GetInstanceIdAsync();
        var second = await service.GetInstanceIdAsync();
        Ensure(first != second, "unary calls receive independent instances");
        Ensure(CallLifetimeProbe.Created == 2 && CallLifetimeProbe.Disposed == 2,
            "unary call scopes end with each invocation");

        var stream = service.StreamInstanceIdAsync().GetAsyncEnumerator();
        try
        {
            Ensure(await stream.MoveNextAsync(), "stream first item");
            var streamId = stream.Current;
            Ensure(CallLifetimeProbe.Created == 3 && CallLifetimeProbe.Disposed == 2,
                "stream instance remains alive while server enumeration is active");
            CallLifetimeProbe.ReleaseStream();
            Ensure(await stream.MoveNextAsync() && stream.Current == streamId,
                "whole stream uses one service instance");
            Ensure(!await stream.MoveNextAsync(), "stream completes");
        }
        finally
        {
            CallLifetimeProbe.ReleaseStream();
            await stream.DisposeAsync();
        }
        await WaitUntilAsync(() => CallLifetimeProbe.Disposed == 3);

        await client.StopAsync();
        await server.StopAsync(TimeSpan.FromSeconds(2));
        await serverCancellation.CancelAsync();
        await IgnoreStopAsync(serverTask);
    }

    [Test]
    public async Task BuilderFiltersShouldBeValidatedAndIsolatedPerServer()
    {
        using var firstCancellation = new CancellationTokenSource();
        var firstBuilder = SharpLinkServerBuilder.Create()
            .DisableAutomaticServiceRegistration()
            .EnableService<ICallLifetimeProbe>()
            .ExcludeService<IMissingLifetimeProbe>()
            .UseTcp(0, IPAddress.Loopback.ToString());
        var firstPort = ((IPEndPoint)firstBuilder.Transport!.LocalEndPoint!).Port;
        await using var firstServer = firstBuilder.Build();
        var firstServerTask = firstServer.RunAsync(firstCancellation.Token).AsTask();
        await using var firstClient = CreateClient(firstPort);
        await firstClient.ConnectAsync();
        _ = await firstClient.Get<ICallLifetimeProbe>().GetInstanceIdAsync();
        try
        {
            _ = await firstClient.Get<IConnectionLifetimeProbe>().GetInstanceIdAsync();
            throw new Exception("assert failed: disabled automatic registration must hide non-enabled services");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.Unimplemented, "filtered service route");
        }
        await firstClient.StopAsync();
        await firstServer.StopAsync(TimeSpan.FromSeconds(2));
        await firstCancellation.CancelAsync();
        await IgnoreStopAsync(firstServerTask);

        using var secondCancellation = new CancellationTokenSource();
        var secondBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString());
        var secondPort = ((IPEndPoint)secondBuilder.Transport!.LocalEndPoint!).Port;
        await using var secondServer = secondBuilder.Build();
        var secondServerTask = secondServer.RunAsync(secondCancellation.Token).AsTask();
        await using var secondClient = CreateClient(secondPort);
        await secondClient.ConnectAsync();
        _ = await secondClient.Get<IConnectionLifetimeProbe>().GetInstanceIdAsync();
        await secondClient.StopAsync();
        await secondServer.StopAsync(TimeSpan.FromSeconds(2));
        await secondCancellation.CancelAsync();
        await IgnoreStopAsync(secondServerTask);

        var missingBuilder = SharpLinkServerBuilder.Create()
            .DisableAutomaticServiceRegistration()
            .EnableService<IMissingLifetimeProbe>()
            .UseTcp(0, IPAddress.Loopback.ToString());
        try
        {
            _ = missingBuilder.Build();
            throw new Exception("assert failed: enabling a missing generated service must fail Build");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains(typeof(IMissingLifetimeProbe).FullName!, StringComparison.Ordinal),
                "missing enabled service diagnostic");
        }
        await missingBuilder.Transport!.DisposeAsync();
    }

    private static ISharpLinkClient CreateClient(int port)
        => SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
            .Build();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 3d);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException("Lifetime condition was not reached.");
            await Task.Delay(10);
        }
    }

    private static async Task IgnoreStopAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
    }
}

[RpcContract]
public interface IMissingLifetimeProbe : IService
{
    [NonCancellable]
    ValueTask<int> MissingAsync();
}

[RpcContract]
public interface IConnectionLifetimeProbe : IService
{
    [NonCancellable]
    ValueTask<int> GetInstanceIdAsync();
}

[RpcService(Lifetime = SharpLinkServiceLifetime.Connection)]
public sealed class ConnectionLifetimeProbe : IConnectionLifetimeProbe, IAsyncDisposable
{
    private static int _nextId;
    private static int _created;
    private static int _disposed;
    private readonly int _id;

    public ConnectionLifetimeProbe()
    {
        _id = Interlocked.Increment(ref _nextId);
        Interlocked.Increment(ref _created);
    }

    internal static int Created => Volatile.Read(ref _created);
    internal static int Disposed => Volatile.Read(ref _disposed);

    internal static void Reset()
    {
        Volatile.Write(ref _nextId, 0);
        Volatile.Write(ref _created, 0);
        Volatile.Write(ref _disposed, 0);
    }

    public ValueTask<int> GetInstanceIdAsync() => ValueTask.FromResult(_id);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposed);
        return ValueTask.CompletedTask;
    }
}

[RpcContract]
public interface ICallLifetimeProbe : IService
{
    [NonCancellable]
    ValueTask<int> GetInstanceIdAsync();

    [NonCancellable]
    IAsyncEnumerable<int> StreamInstanceIdAsync();
}

[RpcService(Lifetime = SharpLinkServiceLifetime.Call)]
public sealed class CallLifetimeProbe : ICallLifetimeProbe, IAsyncDisposable
{
    private static TaskCompletionSource _streamRelease = NewSignal();
    private static int _nextId;
    private static int _created;
    private static int _disposed;
    private readonly int _id;

    public CallLifetimeProbe()
    {
        _id = Interlocked.Increment(ref _nextId);
        Interlocked.Increment(ref _created);
    }

    internal static int Created => Volatile.Read(ref _created);
    internal static int Disposed => Volatile.Read(ref _disposed);

    internal static void Reset()
    {
        Volatile.Write(ref _streamRelease, NewSignal());
        Volatile.Write(ref _nextId, 0);
        Volatile.Write(ref _created, 0);
        Volatile.Write(ref _disposed, 0);
    }

    internal static void ReleaseStream() => Volatile.Read(ref _streamRelease).TrySetResult();

    public ValueTask<int> GetInstanceIdAsync() => ValueTask.FromResult(_id);

    public async IAsyncEnumerable<int> StreamInstanceIdAsync()
    {
        yield return _id;
        await Volatile.Read(ref _streamRelease).Task.ConfigureAwait(false);
        yield return _id;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposed);
        return ValueTask.CompletedTask;
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
