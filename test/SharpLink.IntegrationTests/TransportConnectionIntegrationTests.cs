namespace SharpLink.IntegrationTests;

public partial class TransportConnectionIntegrationTests
{











































    private static ISharpLinkClientAuthenticator CreateClientAuthenticator(string token)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(token);
        return SharpLinkAuthenticator.CreateClient(
            _ => ValueTask.FromResult<ReadOnlyMemory<byte>>(payload));
    }

    private static ISharpLinkServerAuthenticator CreateServerAuthenticator(
        Func<string, SharpLinkAuthenticationResult> authenticate)
    {
        return SharpLinkAuthenticator.CreateServer((request, _) => ValueTask.FromResult(
            authenticate(System.Text.Encoding.UTF8.GetString(request.Payload.Span))));
    }

    private static async Task EnsureThrows<TException>(Task task, string name) where TException : Exception
    {
        try
        {
            await task;
            throw new Exception($"assert failed: {name} should throw {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private static Task EnsureThrows<TException>(ValueTask task, string name) where TException : Exception
        => EnsureThrows<TException>(task.AsTask(), name);

    private static Task EnsureThrowsSharpLink(
        ValueTask task,
        string name,
        SharpLinkErrorCode errorCode,
        string? messageContains = null)
        => EnsureThrowsSharpLink(task.AsTask(), name, errorCode, messageContains);

    private static async Task EnsureThrowsSharpLinkFast(Task task, string name, SharpLinkErrorCode errorCode)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            throw new Exception($"assert failed: {name} should throw");
        }
        catch (TimeoutException)
        {
            throw new Exception($"assert failed: {name} did not fail fast");
        }
        catch (SharpLinkException ex)
        {
            Ensure(ex.Code == errorCode, $"{name} error code");
        }
    }

    private static async Task<SharpLinkException> CaptureSharpLinkException(Task task, string name)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            throw new Exception($"assert failed: {name} should throw");
        }
        catch (TimeoutException)
        {
            throw new Exception($"assert failed: {name} did not fail fast");
        }
        catch (SharpLinkException ex)
        {
            return ex;
        }
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> stream, CancellationToken ct)
    {
        var list = new List<T>();
        await foreach (var item in stream.WithCancellation(ct))
            list.Add(item);
        return list;
    }

    private static async Task EnsureThrowsSharpLink(Task task, string name, SharpLinkErrorCode errorCode, string? messageContains = null)
    {
        try
        {
            await task;
            throw new Exception($"assert failed: {name} should throw SharpLinkException");
        }
        catch (SharpLinkException ex)
        {
            Ensure(ex.Code == errorCode, $"{name} error code");
            if (!string.IsNullOrWhiteSpace(messageContains))
                Ensure(ex.Message.Contains(messageContains, StringComparison.Ordinal), $"{name} error message");
        }
    }

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string GetUniqueUdsPath()
    {
        return Path.Combine(Path.GetTempPath(), $"sharplink-{Guid.NewGuid():N}.sock");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException)
        {
            IgnoreExpectedException(ex);
        }
    }

    private static async Task VerifyReconnectWithNewClientInstanceAsync(TransportKind kind)
    {
        await using var harness1 = await TransportHarness.CreateAsync(kind);
        var svc1 = harness1.Client.Get<IConnectionBehaviorService>();
        Ensure(await svc1.PingAsync(20) == 21, $"{kind} first connect");
        await harness1.DisposeClientOnlyAsync();
        await Task.Delay(120);

        var client2 = BuildClientForEndpoint(harness1.Endpoint);
        try
        {
            await client2.ConnectAsync();
            Ensure(client2.State == SharpLinkConnectionState.Ready, $"{kind} reconnect connect");
            var svc2 = client2.Get<IConnectionBehaviorService>();
            Ensure(await svc2.PingAsync(30) == 31, $"{kind} reconnect ping");
        }
        finally
        {
            await client2.DisposeAsync();
        }
    }

    private static async Task VerifyNegotiatedFrameLimitAsync(int clientLimit, int serverLimit)
    {
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())

            .UseProtocol(options => options.MaxFramePayloadBytes = serverLimit)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = Task.Run(() => server.RunAsync(cts.Token).AsTask(), CancellationToken.None);
        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)

            .UseProtocol(options => options.MaxFramePayloadBytes = clientLimit)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
            .Build();
        try
        {
            await client.ConnectAsync(cts.Token);
            var service = client.Get<IConnectionBehaviorService>();
            await EnsureThrowsSharpLink(
                service.EchoAsync(new string('x', 2_048)).AsTask(),
                "oversized request",
                SharpLinkErrorCode.ResourceExhausted);
            Ensure(await service.PingAsync(1) == 2, "connection remains healthy after request rejection");

            await EnsureThrowsSharpLink(
                service.CreatePayloadAsync(2_048).AsTask(),
                "oversized response",
                SharpLinkErrorCode.ResourceExhausted);
            Ensure(await service.PingAsync(2) == 3, "connection remains healthy after response rejection");
        }
        finally
        {
            await client.DisposeAsync();
            await cts.CancelAsync();
            await server.StopAsync(TimeSpan.Zero);
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    private static ISharpLinkClient BuildClientForEndpoint(TransportEndpoint endpoint)
    {
        var builder = SharpClientBuilder.Create().DisableRequestTimeout()

            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        switch (endpoint.Kind)
        {
            case TransportKind.Tcp:
                builder.UseTcp(IPAddress.Loopback.ToString(), endpoint.Port);
                break;
            case TransportKind.NamedPipe:
                builder.UseNamedPipe(endpoint.PipeName);
                break;
            case TransportKind.Uds:
                builder.UseUds(endpoint.UdsPath);
                break;
            default:
                throw new InvalidOperationException($"Unsupported endpoint kind: {endpoint.Kind}");
        }

        return builder.Build();
    }

    private enum TransportKind
    {
        Tcp,
        NamedPipe,
        Uds
    }

    private readonly record struct TransportEndpoint(TransportKind Kind, int Port, string PipeName, string UdsPath);

    private sealed class ThrowingConnectTransport(Exception exception) : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(exception);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TransportHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        private readonly Action _cleanup;
        private bool _serverDisposed;
        private bool _clientDisposed;

        public ISharpLinkClient Client { get; }
        public TransportEndpoint Endpoint { get; }

        private TransportHarness(
            ISharpLinkServer server,
            Task serverTask,
            CancellationTokenSource serverCts,
            ISharpLinkClient client,
            TransportEndpoint endpoint,
            Action cleanup)
        {
            _server = server;
            _serverTask = serverTask;
            _serverCts = serverCts;
            Client = client;
            Endpoint = endpoint;
            _cleanup = cleanup;
        }

        public static Task<TransportHarness> CreateAsync(TransportKind kind) => CreateAsync(kind, default);

        private static async Task<TransportHarness> CreateAsync(TransportKind kind, TransportEndpoint endpoint)
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()

                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

            var clientBuilder = SharpClientBuilder.Create().DisableRequestTimeout()

                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

            Action cleanup = static () => { };
            TransportEndpoint resolvedEndpoint = endpoint;

            switch (kind)
            {
                case TransportKind.Tcp:
                    {
                        serverBuilder.UseTcp(endpoint.Port, IPAddress.Loopback.ToString());
                        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
                        clientBuilder.UseTcp(IPAddress.Loopback.ToString(), port);
                        resolvedEndpoint = new TransportEndpoint(kind, port, string.Empty, string.Empty);
                        break;
                    }
                case TransportKind.NamedPipe:
                    {
                        var pipeName = string.IsNullOrWhiteSpace(endpoint.PipeName)
                            ? $"sharplink-int-{Guid.NewGuid():N}"
                            : endpoint.PipeName;
                        serverBuilder.UseNamedPipe(pipeName);
                        clientBuilder.UseNamedPipe(pipeName);
                        resolvedEndpoint = new TransportEndpoint(kind, 0, pipeName, string.Empty);
                        break;
                    }
                case TransportKind.Uds:
                    {
                        if (!Socket.OSSupportsUnixDomainSockets)
                            throw new PlatformNotSupportedException("Unix domain sockets are not supported on this platform.");

                        var udsPath = string.IsNullOrWhiteSpace(endpoint.UdsPath)
                            ? GetUniqueUdsPath()
                            : endpoint.UdsPath;
                        serverBuilder.UseUds(udsPath);
                        clientBuilder.UseUds(udsPath);
                        resolvedEndpoint = new TransportEndpoint(kind, 0, string.Empty, udsPath);
                        cleanup = () => TryDeleteFile(udsPath);
                        break;
                    }
            }

            var server = serverBuilder.Build();
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    await server.RunAsync(cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
                {
                    IgnoreExpectedException(ex);
                }
            }, CancellationToken.None);

            var client = clientBuilder.Build();
            await client.ConnectAsync(cts.Token);

            return new TransportHarness(server, serverTask, cts, client, resolvedEndpoint, cleanup);
        }

        public async ValueTask DisposeServerOnlyAsync()
        {
            if (_serverDisposed)
                return;

            _serverDisposed = true;
            try
            {
                await _server.StopAsync(TimeSpan.Zero);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or ArgumentException)
            {
                IgnoreExpectedException(ex);
            }
        }

        public async ValueTask DisposeClientOnlyAsync()
        {
            if (_clientDisposed)
                return;

            _clientDisposed = true;
            try
            {
                await Client.StopAsync();
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or ArgumentException)
            {
                IgnoreExpectedException(ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeClientOnlyAsync();
            try
            {
                await _serverCts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // already disposed by racing cleanup path
            }
            await DisposeServerOnlyAsync();
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            try
            {
                _serverCts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // already disposed by racing cleanup path
            }
            _cleanup();
        }
    }

    private static void IgnoreExpectedException(Exception ex)
    {
        _ = ex.HResult;
    }
}

internal sealed class SingleConnectionListener(ITransportConnection connection) : IServerTransportListener
{
    private int _accepted;

    public EndPoint? LocalEndPoint => null;

    public async ValueTask<ITransportConnection> AcceptAsync(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _accepted, 1, 0) == 0)
            return connection;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new UnreachableException();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class SingleConnectionClientFactory(ITransportConnection connection) : IClientTransportFactory
{
    private int _connected;

    public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _connected, 1, 0) != 0)
            throw new InvalidOperationException("The test transport only supports one connection.");
        return ValueTask.FromResult(connection);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class CompletionJoiningTransportConnection : ITransportConnection
{
    private readonly System.IO.Pipelines.Pipe _input = new();
    private readonly System.IO.Pipelines.Pipe _output = new();

    internal CompletionJoiningTransportConnection()
        => Reader = new CompletionJoiningPipeReader(_input.Reader);

    public string Id { get; } = "completion-joining";
    public System.IO.Pipelines.PipeReader Input => Reader;
    public System.IO.Pipelines.PipeWriter Output => _output.Writer;
    public EndPoint? LocalEndPoint => null;
    public EndPoint? RemoteEndPoint => null;

    internal CompletionJoiningPipeReader Reader { get; }

    internal async ValueTask InjectAsync(ReadOnlyMemory<byte> payload)
    {
        await _input.Writer.WriteAsync(payload);
        await _input.Writer.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Output.CompleteAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await Reader.CompleteAsync().ConfigureAwait(false);
            }
            finally
            {
                await _input.Writer.CompleteAsync().ConfigureAwait(false);
                await _output.Reader.CompleteAsync().ConfigureAwait(false);
            }
        }
    }
}

internal sealed class CompletionJoiningPipeReader(System.IO.Pipelines.PipeReader inner)
    : System.IO.Pipelines.PipeReader
{
    private readonly Lock _completionGate = new();
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _completionTask;
    private int _outstandingRead;

    internal TaskCompletionSource CompleteStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal bool CompleteObservedOutstandingRead { get; private set; }

    public override void AdvanceTo(SequencePosition consumed)
        => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        inner.AdvanceTo(consumed, examined);
        Volatile.Write(ref _outstandingRead, 0);
    }

    public override void CancelPendingRead() => inner.CancelPendingRead();

    public override void Complete(Exception? exception = null)
        => _ = CompleteAsync(exception);

    public override ValueTask CompleteAsync(Exception? exception = null)
    {
        lock (_completionGate)
        {
            _completionTask ??= CompleteAfterReleaseAsync(exception);
            return new ValueTask(_completionTask);
        }
    }

    public override bool TryRead(out System.IO.Pipelines.ReadResult result)
    {
        if (!inner.TryRead(out result))
            return false;
        Volatile.Write(ref _outstandingRead, 1);
        return true;
    }

    public override async ValueTask<System.IO.Pipelines.ReadResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await inner.ReadAsync(cancellationToken);
        Volatile.Write(ref _outstandingRead, 1);
        return result;
    }

    internal void ReleaseCompletion() => _release.TrySetResult();

    private async Task CompleteAfterReleaseAsync(Exception? exception)
    {
        CompleteObservedOutstandingRead = Volatile.Read(ref _outstandingRead) != 0;
        CompleteStarted.TrySetResult();
        await _release.Task;
        await inner.CompleteAsync(exception);
    }
}

[RpcContract]
public interface IConnectionBehaviorService : IService
{
    [NonCancellable]
    ValueTask<int> PingAsync(int value);
    [NonCancellable]
    ValueTask<string> EchoAsync(string value);
    [NonCancellable]
    ValueTask<string> GetEndpointIdAsync();
    [NonCancellable]
    ValueTask<string> CreatePayloadAsync(int length);
    ValueTask<int> SlowAsync(int delayMs, CancellationToken cancellationToken = default);
    IAsyncEnumerable<int> SlowRangeAsync(int count, int delayMs, CancellationToken cancellationToken = default);
    [NonCancellable]
    ValueTask<string> GetAuthenticationSummaryAsync();
    [NonCancellable]
    ValueTask<string> GetAuthenticationDetailsAsync();
    [NonCancellable]
    ValueTask<int> RequireScopeAsync(string scope);
    [NonCancellable]
    ValueTask<int> RequireTenantAsync(string tenantId);
    [NonCancellable]
    ValueTask<int> RequireActiveTokenAsync(long unixSeconds);
}

[RpcService]
public sealed class ConnectionBehaviorService : IConnectionBehaviorService
{
    public string EndpointId { get; set; } = "default";
    public TaskCompletionSource<string>? SlowCallStarted { get; set; }
    public TaskCompletionSource<string>? SlowUnaryStarted { get; set; }

    public ValueTask<int> PingAsync(int value) => ValueTask.FromResult(value + 1);

    public ValueTask<string> EchoAsync(string value) => ValueTask.FromResult(value);

    public ValueTask<string> GetEndpointIdAsync() => ValueTask.FromResult(EndpointId);

    public ValueTask<string> CreatePayloadAsync(int length)
        => ValueTask.FromResult(new string('x', length));

    public async ValueTask<int> SlowAsync(int delayMs, CancellationToken cancellationToken = default)
    {
        SlowCallStarted?.TrySetResult(EndpointId);
        SlowUnaryStarted?.TrySetResult(EndpointId);
        await Task.Delay(delayMs, cancellationToken);
        return delayMs;
    }

    public async IAsyncEnumerable<int> SlowRangeAsync(
        int count,
        int delayMs,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        SlowCallStarted?.TrySetResult(EndpointId);
        for (var value = 0; value < count; value++)
        {
            yield return value;
            await Task.Delay(delayMs, cancellationToken);
        }
    }

    public ValueTask<string> GetAuthenticationSummaryAsync()
    {
        var context = SharpLinkCallContext.Current?.Authentication;
        var subject = context?.Subject ?? "<null>";
        var role = context?.GetClaim("role") ?? "<null>";
        return ValueTask.FromResult($"{subject}|{role}");
    }

    public ValueTask<string> GetAuthenticationDetailsAsync()
    {
        var context = SharpLinkCallContext.Current?.Authentication;
        var tenantId = context?.TenantId ?? "<null>";
        var hasRead = context?.HasScope("rpc.read") ?? false;
        var hasWrite = context?.HasScope("rpc.write") ?? false;
        var expiresAt = context?.ExpiresAt?.ToString("O") ?? "<null>";
        return ValueTask.FromResult($"{tenantId}|{hasRead}|{hasWrite}|{expiresAt}");
    }

    public ValueTask<int> RequireScopeAsync(string scope)
    {
        SharpLinkAuthorization.RequireScope(scope);
        return ValueTask.FromResult(1);
    }

    public ValueTask<int> RequireTenantAsync(string tenantId)
    {
        SharpLinkAuthorization.RequireTenant(tenantId);
        return ValueTask.FromResult(1);
    }

    public ValueTask<int> RequireActiveTokenAsync(long unixSeconds)
    {
        SharpLinkAuthorization.RequireActiveToken(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        return ValueTask.FromResult(1);
    }
}
