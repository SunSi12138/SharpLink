namespace SharpLink.IntegrationTests;

public class InterceptorIntegrationTests
{
    [Test]
    public async Task ClientAndServerInterceptorsShouldObserveGeneratedContext()
    {
        var clientInterceptor = new RecordingClientInterceptor();
        var serverInterceptor = new RecordingServerInterceptor();
        await using var harness = await InterceptorHarness.CreateAsync(
            clientInterceptor: clientInterceptor,
            serverInterceptor: serverInterceptor);

        var service = harness.Client.Get<IInterceptorTestService>();
        var result = await service.DescribeAsync(17, default);

        Ensure(result.Contains("client-interceptor", StringComparison.Ordinal), "client metadata reached service");
        Ensure(clientInterceptor.Method.IsIdempotent, "client descriptor idempotent marker");
        Ensure(clientInterceptor.StatusAfterNext == SharpLinkInvocationStatus.Succeeded, "client status after next");
        Ensure(serverInterceptor.Context is { RequestId: > 0 }, "server request ID");
        Ensure(serverInterceptor.Context!.Method.IsIdempotent, "server descriptor idempotent marker");
        Ensure(serverInterceptor.Context.RemoteEndPoint is not null, "server peer endpoint");
        Ensure(serverInterceptor.StatusAfterNext == SharpLinkInvocationStatus.Succeeded, "server status after next");
    }

    [Test]
    public async Task ClientInterceptorShouldShortCircuitWithoutAConnection()
    {
        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), GetFreePort())
            .AddInterceptor(new ShortCircuitClientInterceptor(777))
            .Build();
        try
        {
            var service = client.Get<IInterceptorTestService>();
            Ensure(await service.DescribeNumberAsync(1) == 777, "short-circuit response");
            Ensure(client.State == SharpLinkConnectionState.Created, "short circuit should not connect");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task ServerInterceptorShouldRejectWithStructuredStatus()
    {
        await using var harness = await InterceptorHarness.CreateAsync(
            serverInterceptor: new RejectingServerInterceptor());
        var service = harness.Client.Get<IInterceptorTestService>();

        var exception = await CaptureSharpLinkException(service.DescribeNumberAsync(1).AsTask());
        Ensure(exception.Code == SharpLinkErrorCode.PermissionDenied, "server interceptor status");
        Ensure(exception.Message.Contains("policy", StringComparison.Ordinal), "server interceptor public message");
    }

    [Test]
    public async Task StructuredCancelledInterceptorFailuresShouldRecordCancelledStatus()
    {
        var clientInterceptor = new CancellingClientInterceptor();
        await using (var clientHarness = await InterceptorHarness.CreateAsync(
                         clientInterceptor: clientInterceptor))
        {
            var service = clientHarness.Client.Get<IInterceptorTestService>();
            var exception = await CaptureSharpLinkException(service.DescribeNumberAsync(1).AsTask());
            Ensure(exception.Code == SharpLinkErrorCode.Cancelled, "client structured cancellation code");
            Ensure(clientInterceptor.Context?.Status == SharpLinkInvocationStatus.Cancelled,
                "client structured cancellation status");
            Ensure(ReferenceEquals(clientInterceptor.Context?.Exception, exception),
                "client structured cancellation exception identity");
        }

        var serverInterceptor = new CancellingServerInterceptor();
        await using (var serverHarness = await InterceptorHarness.CreateAsync(
                         serverInterceptor: serverInterceptor))
        {
            var service = serverHarness.Client.Get<IInterceptorTestService>();
            var exception = await CaptureSharpLinkException(service.DescribeNumberAsync(1).AsTask());
            Ensure(exception.Code == SharpLinkErrorCode.Cancelled, "server structured cancellation code");
            Ensure(serverInterceptor.Context?.Status == SharpLinkInvocationStatus.Cancelled,
                "server structured cancellation status");
            Ensure(serverInterceptor.Context?.ErrorCode == SharpLinkErrorCode.Cancelled,
                "server structured cancellation context code");
        }
    }

    [Test]
    public async Task AsyncServerInterceptorShouldOwnArgumentsUntilNextCompletes()
    {
        var interceptor = new DelayedFirstServerInterceptor();
        await using var harness = await InterceptorHarness.CreateAsync(serverInterceptor: interceptor);
        var service = harness.Client.Get<IInterceptorTestService>();

        var delayed = service.DescribeNumberAsync(123_456).AsTask();
        await interceptor.Entered.WaitAsync(TimeSpan.FromSeconds(3));

        var churn = new Task<int>[256];
        for (var index = 0; index < churn.Length; index++)
            churn[index] = service.DescribeNumberAsync(index).AsTask();

        interceptor.Release();
        Ensure(await delayed.WaitAsync(TimeSpan.FromSeconds(3)) == 123_457, "delayed arguments remain owned");
        var values = await Task.WhenAll(churn).WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < values.Length; index++)
            Ensure(values[index] == index + 1, $"churn response {index}");
    }

    [Test]
    public async Task InterceptorContinuationShouldExecuteEachTerminalAtMostOnce()
    {
        InterceptorTestService.ResetInvocationCount();
        Exception? clientFailure;
        await using (var clientHarness = await InterceptorHarness.CreateAsync(
                         clientInterceptor: new DoubleNextClientInterceptor()))
        {
            var service = clientHarness.Client.Get<IInterceptorTestService>();
            clientFailure = await CaptureException(service.CountInvocationAsync().AsTask());
        }
        var clientInvocationCount = InterceptorTestService.InvocationCount;

        InterceptorTestService.ResetInvocationCount();
        Exception? serverFailure;
        await using (var serverHarness = await InterceptorHarness.CreateAsync(
                         serverInterceptor: new DoubleNextServerInterceptor()))
        {
            var service = serverHarness.Client.Get<IInterceptorTestService>();
            serverFailure = await CaptureException(service.CountInvocationAsync().AsTask());
        }
        var serverInvocationCount = InterceptorTestService.InvocationCount;

        Ensure(clientInvocationCount == 1 && serverInvocationCount == 1,
            $"continuations must execute one terminal each; client={clientInvocationCount}, server={serverInvocationCount}");
        Ensure(clientFailure is InvalidOperationException,
            "duplicate client continuation should fail locally");
        Ensure(serverFailure is SharpLinkException { Code: SharpLinkErrorCode.Internal },
            "duplicate server continuation should return a structured internal failure");
    }

    [Test]
    public async Task DefaultExceptionMapperShouldHideServiceDetails()
    {
        await using var harness = await InterceptorHarness.CreateAsync();
        var service = harness.Client.Get<IInterceptorTestService>();

        var exception = await CaptureSharpLinkException(service.FailAsync().AsTask());
        Ensure(exception.Code == SharpLinkErrorCode.Internal, "default mapper status");
        Ensure(!exception.Message.Contains("secret-service-detail", StringComparison.Ordinal), "default mapper hides detail");
    }

    [Test]
    public async Task DetailedErrorsShouldRequireExplicitOptIn()
    {
        await using var harness = await InterceptorHarness.CreateAsync(enableDetailedErrors: true);
        var service = harness.Client.Get<IInterceptorTestService>();

        var exception = await CaptureSharpLinkException(service.FailAsync().AsTask());
        Ensure(exception.Code == SharpLinkErrorCode.Internal, "detailed mapper status");
        Ensure(exception.Message == "secret-service-detail", "detailed mapper message");
    }

    [Test]
    public async Task CustomExceptionMapperShouldMapUnaryAndStreamingFailures()
    {
        await using var harness = await InterceptorHarness.CreateAsync(
            exceptionMapper: new TestExceptionMapper());
        var service = harness.Client.Get<IInterceptorTestService>();

        var unary = await CaptureSharpLinkException(service.FailAsync().AsTask());
        Ensure(unary.Code == SharpLinkErrorCode.FailedPrecondition, "custom unary status");
        Ensure(unary.Message == "public-failure", "custom unary message");

        var stream = service.FailStreamAsync().GetAsyncEnumerator();
        try
        {
            Ensure(await stream.MoveNextAsync() && stream.Current == 1, "stream first item");
            var streamFailure = await CaptureSharpLinkException(stream.MoveNextAsync().AsTask());
            Ensure(streamFailure.Code == SharpLinkErrorCode.FailedPrecondition, "custom stream status");
            Ensure(streamFailure.Message == "public-failure", "custom stream message");
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    private static async Task<SharpLinkException> CaptureSharpLinkException(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            throw new Exception("assert failed: expected SharpLinkException");
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
    }

    private static async Task<Exception?> CaptureException(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }

    private sealed class RecordingClientInterceptor : ISharpLinkClientInterceptor
    {
        public RpcMethodDescriptor Method { get; private set; }
        public SharpLinkInvocationStatus StatusAfterNext { get; private set; }

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            Method = context.Method;
            context.Options = context.Options with
            {
                Metadata = new SharpLinkMetadata(
                    new KeyValuePair<string, string>("source", "client-interceptor"))
            };
            var result = await next(context);
            StatusAfterNext = context.Status;
            return result;
        }
    }

    private sealed class ShortCircuitClientInterceptor(int value) : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
            => ValueTask.FromResult(new SharpLinkClientInvocationResult(value));
    }

    private sealed class CancellingClientInterceptor : ISharpLinkClientInterceptor
    {
        public SharpLinkClientInvocationContext? Context { get; private set; }

        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            Context = context;
            return ValueTask.FromException<SharpLinkClientInvocationResult>(new SharpLinkException(
                SharpLinkErrorCode.Cancelled,
                "cancelled by client interceptor"));
        }
    }

    private sealed class DoubleNextClientInterceptor : ISharpLinkClientInterceptor
    {
        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            var result = await next(context).ConfigureAwait(false);
            _ = await next(context).ConfigureAwait(false);
            return result;
        }
    }

    private sealed class RecordingServerInterceptor : ISharpLinkServerInterceptor
    {
        public SharpLinkServerInvocationContext? Context { get; private set; }
        public SharpLinkInvocationStatus StatusAfterNext { get; private set; }

        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            Context = context;
            await next(context);
            StatusAfterNext = context.Status;
        }
    }

    private sealed class RejectingServerInterceptor : ISharpLinkServerInterceptor
    {
        public ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
            => ValueTask.FromException(new SharpLinkException(
                SharpLinkErrorCode.PermissionDenied,
                "Rejected by policy."));
    }

    private sealed class CancellingServerInterceptor : ISharpLinkServerInterceptor
    {
        public SharpLinkServerInvocationContext? Context { get; private set; }

        public ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            Context = context;
            return ValueTask.FromException(new SharpLinkException(
                SharpLinkErrorCode.Cancelled,
                "cancelled by server interceptor"));
        }
    }

    private sealed class DoubleNextServerInterceptor : ISharpLinkServerInterceptor
    {
        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            await next(context).ConfigureAwait(false);
            await next(context).ConfigureAwait(false);
        }
    }

    private sealed class DelayedFirstServerInterceptor : ISharpLinkServerInterceptor
    {
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _first;

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult(true);

        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            if (Interlocked.CompareExchange(ref _first, 1, 0) == 0)
            {
                _entered.TrySetResult(true);
                await _release.Task.ConfigureAwait(false);
            }
            await next(context).ConfigureAwait(false);
        }
    }

    private sealed class TestExceptionMapper : IRpcExceptionMapper
    {
        public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
            => exception is InvalidOperationException
                ? new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "public-failure", exception)
                : new SharpLinkException(SharpLinkErrorCode.Internal, "internal", exception);
    }

    private sealed class InterceptorHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        public ISharpLinkClient Client { get; }

        private InterceptorHarness(
            CancellationTokenSource serverCts,
            Task serverTask,
            ISharpLinkServer server,
            ISharpLinkClient client)
        {
            _serverCts = serverCts;
            _serverTask = serverTask;
            _server = server;
            Client = client;
        }

        public static async Task<InterceptorHarness> CreateAsync(
            ISharpLinkClientInterceptor? clientInterceptor = null,
            ISharpLinkServerInterceptor? serverInterceptor = null,
            IRpcExceptionMapper? exceptionMapper = null,
            bool enableDetailedErrors = false)
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())

                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
            if (serverInterceptor is not null)
                serverBuilder.AddInterceptor(serverInterceptor);
            if (exceptionMapper is not null)
                serverBuilder.UseExceptionMapper(exceptionMapper);
            if (enableDetailedErrors)
                serverBuilder.EnableDetailedErrors();

            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = Task.Run(() => server.RunAsync(cts.Token).AsTask(), CancellationToken.None);

            var clientBuilder = SharpClientBuilder.Create()
                .UseTcp(IPAddress.Loopback.ToString(), port)

                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
            if (clientInterceptor is not null)
                clientBuilder.AddInterceptor(clientInterceptor);
            var client = clientBuilder.Build();
            await client.ConnectAsync(cts.Token);
            return new InterceptorHarness(cts, serverTask, server, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _serverCts.CancelAsync();
            await _server.DisposeAsync();
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }
    }
}

[RpcContract]
public interface IInterceptorTestService : IService
{
    [Idempotent]
    [NonCancellable]
    ValueTask<string> DescribeAsync(int value, SharpLinkCallOptions options);
    [NonCancellable]
    ValueTask<int> DescribeNumberAsync(int value);
    [NonCancellable]
    ValueTask<int> FailAsync();
    [NonCancellable]
    ValueTask<int> CountInvocationAsync();
    [NonCancellable]
    IAsyncEnumerable<int> FailStreamAsync();
}

[RpcService]
public sealed class InterceptorTestService : IInterceptorTestService
{
    private static int _invocationCount;

    public static int InvocationCount => Volatile.Read(ref _invocationCount);

    public static void ResetInvocationCount() => Volatile.Write(ref _invocationCount, 0);

    public ValueTask<string> DescribeAsync(int value, SharpLinkCallOptions options)
    {
        var source = options.Metadata is { Count: > 0 } metadata
            ? metadata[0].Value
            : "missing";
        var context = SharpLinkCallContext.Current;
        return ValueTask.FromResult($"{value}|{source}|{context?.SessionId}");
    }

    public ValueTask<int> DescribeNumberAsync(int value) => ValueTask.FromResult(value + 1);

    public ValueTask<int> FailAsync()
        => throw new InvalidOperationException("secret-service-detail");

    public ValueTask<int> CountInvocationAsync()
        => ValueTask.FromResult(Interlocked.Increment(ref _invocationCount));

    public async IAsyncEnumerable<int> FailStreamAsync()
    {
        yield return 1;
        await Task.Yield();
        throw new InvalidOperationException("secret-service-detail");
    }
}
