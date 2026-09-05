namespace SharpLink.IntegrationTests;

public class ExceptionMapperInterceptorBoundaryTests
{
    [Test]
    public async Task MapperMustRemainForResponseProducerFailuresOutsideInterceptorCatch()
    {
        var interceptor = new TranslatingServerInterceptor();
        var mapper = new RecordingExceptionMapper();
        await using var harness = await Harness.CreateAsync(interceptor, mapper);
        var service = harness.Client.Get<IExceptionMappingBoundaryService>();

        var unary = await CaptureSharpLinkException(service.FailUnaryAsync().AsTask());
        Ensure(unary is { Code: SharpLinkErrorCode.FailedPrecondition, Message: "interceptor-domain" },
            "unary domain exception should be translated by the interceptor");
        Ensure(interceptor.CaughtCount == 1, "unary failure must unwind through interceptor next");
        Ensure(mapper.DomainExceptionCount == 0,
            "handled unary failure must not reach mapper as the raw domain exception");

        await service.FailOneWayAsync();
        await interceptor.OneWayCaught.WaitAsync(TimeSpan.FromSeconds(3));
        Ensure(interceptor.CaughtCount == 2, "one-way failure must unwind through interceptor next");
        Ensure(mapper.DomainExceptionCount == 0,
            "handled one-way failure must not reach mapper as the raw domain exception");

        var clientStream = await CaptureSharpLinkException(
            service.FailClientStreamAsync(Input(), CancellationToken.None).AsTask());
        Ensure(clientStream is { Code: SharpLinkErrorCode.FailedPrecondition, Message: "interceptor-domain" },
            "client-stream domain exception should be translated by the interceptor");
        Ensure(interceptor.CaughtCount == 3, "client-stream service failure must unwind through interceptor next");
        Ensure(mapper.DomainExceptionCount == 0,
            "handled client-stream failure must not reach mapper as the raw domain exception");

        var serverStream = service.FailServerStreamAsync().GetAsyncEnumerator();
        try
        {
            Ensure(await serverStream.MoveNextAsync() && serverStream.Current == 1,
                "server stream first item");
            var serverStreamFailure = await CaptureSharpLinkException(serverStream.MoveNextAsync().AsTask());
            Ensure(serverStreamFailure is { Code: SharpLinkErrorCode.ResourceExhausted, Message: "mapper-stream-boundary" },
                "server-stream producer failure should be translated by the terminal mapper boundary");
        }
        finally
        {
            await serverStream.DisposeAsync();
        }
        Ensure(interceptor.CaughtCount == 3,
            "server-stream producer failure must not be rethrown through interceptor next");
        Ensure(mapper.DomainExceptionCount == 1,
            "server-stream producer failure must reach mapper as the raw domain exception exactly once");

        var duplex = service.FailDuplexAsync(Input(), CancellationToken.None).GetAsyncEnumerator();
        try
        {
            Ensure(await duplex.MoveNextAsync() && duplex.Current == 42, "duplex first item");
            var duplexFailure = await CaptureSharpLinkException(duplex.MoveNextAsync().AsTask());
            Ensure(duplexFailure is { Code: SharpLinkErrorCode.ResourceExhausted, Message: "mapper-stream-boundary" },
                "duplex producer failure should be translated by the terminal mapper boundary");
        }
        finally
        {
            await duplex.DisposeAsync();
        }
        Ensure(interceptor.CaughtCount == 3,
            "duplex producer failure must not be rethrown through interceptor next");
        Ensure(mapper.DomainExceptionCount == 2,
            "both response-producer failures must reach mapper as raw domain exceptions exactly once");
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

    private static async IAsyncEnumerable<int> Input()
    {
        yield return 42;
        await Task.Yield();
    }

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }

    private sealed class TranslatingServerInterceptor : ISharpLinkServerInterceptor
    {
        private readonly TaskCompletionSource _oneWayCaught =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _caughtCount;

        public int CaughtCount => Volatile.Read(ref _caughtCount);
        public Task OneWayCaught => _oneWayCaught.Task;

        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                Interlocked.Increment(ref _caughtCount);
                if (context.Method.Kind == RpcMethodKind.OneWay)
                    _oneWayCaught.TrySetResult();
                throw new SharpLinkException(
                    SharpLinkErrorCode.FailedPrecondition,
                    "interceptor-domain",
                    exception);
            }
        }
    }

    private sealed class RecordingExceptionMapper : IRpcExceptionMapper
    {
        private int _domainExceptionCount;

        public int DomainExceptionCount => Volatile.Read(ref _domainExceptionCount);

        public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
        {
            if (exception is SharpLinkException structured)
                return structured;

            Interlocked.Increment(ref _domainExceptionCount);
            return exception is InvalidOperationException
                ? new SharpLinkException(
                    SharpLinkErrorCode.ResourceExhausted,
                    "mapper-stream-boundary",
                    exception)
                : new SharpLinkException(
                    SharpLinkErrorCode.Internal,
                    "mapper-internal",
                    exception);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;

        private Harness(
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

        public ISharpLinkClient Client { get; }

        public static async Task<Harness> CreateAsync(
            ISharpLinkServerInterceptor interceptor,
            IRpcExceptionMapper mapper)
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
            serverBuilder.AddInterceptor(interceptor);
            serverBuilder.UseExceptionMapper(mapper);

            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = Task.Run(() => server.RunAsync(cts.Token).AsTask(), CancellationToken.None);

            var client = SharpClientBuilder.Create()
                .DisableRequestTimeout()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
                .Build();
            await client.ConnectAsync(cts.Token);
            return new Harness(cts, serverTask, server, client);
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
public interface IExceptionMappingBoundaryService : IService
{
    [NonCancellable]
    ValueTask<int> FailUnaryAsync();

    [Oneway]
    [NonCancellable]
    ValueTask FailOneWayAsync();

    ValueTask<int> FailClientStreamAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken);

    [NonCancellable]
    IAsyncEnumerable<int> FailServerStreamAsync();

    IAsyncEnumerable<int> FailDuplexAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken);
}

[RpcService]
public sealed class ExceptionMappingBoundaryService : IExceptionMappingBoundaryService
{
    public ValueTask<int> FailUnaryAsync()
        => throw new InvalidOperationException("domain-failure");

    public ValueTask FailOneWayAsync()
        => throw new InvalidOperationException("domain-failure");

    public async ValueTask<int> FailClientStreamAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken)
    {
        await foreach (var _ in values.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
        }
        throw new InvalidOperationException("domain-failure");
    }

    public async IAsyncEnumerable<int> FailServerStreamAsync()
    {
        yield return 1;
        await Task.Yield();
        throw new InvalidOperationException("domain-failure");
    }

    public async IAsyncEnumerable<int> FailDuplexAsync(
        IAsyncEnumerable<int> values,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var value in values.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return value;
        throw new InvalidOperationException("domain-failure");
    }
}
