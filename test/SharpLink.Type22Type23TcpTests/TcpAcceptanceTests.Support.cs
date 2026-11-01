using System.Reflection;
using SharpLink.IntegrationTests;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.Type22Type23TcpTests;

public sealed partial class TcpAcceptanceTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Ensure(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        using var timeout = new CancellationTokenSource(Budget);
        while (!condition())
        {
            if (timeout.IsCancellationRequested) throw new TimeoutException(message);
            await Task.Delay(1);
        }
    }
    private static Task<int[]> CollectAsync(IAsyncEnumerable<int> source)
        => CollectCoreAsync(source).WaitAsync(Budget);
    private static async Task<int[]> CollectCoreAsync(IAsyncEnumerable<int> source)
    {
        var values = new List<int>();
        await foreach (var value in source) values.Add(value);
        return values.ToArray();
    }
    private static async Task ExpectTerminalAsync(Task operation, SharpLinkErrorCode code)
    {
        try { await operation.WaitAsync(Budget); }
        catch (SharpLinkException error) when (error.Code == code) { return; }
        catch (OperationCanceledException) when (code == SharpLinkErrorCode.Cancelled) { return; }
        throw new InvalidOperationException($"Expected terminal {code}.");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private Task? _serverTask;
        private ServerConnectionState? _connection;
        private int _accepted;
        internal TcpAcceptanceService Service { get; } = new();
        internal SharpLinkServer Server { get; }
        internal SharpLinkClient Client { get; }
        internal int Accepted => Volatile.Read(ref _accepted);
        internal ServerConnectionState Connection => Volatile.Read(ref _connection)
            ?? throw new InvalidOperationException("No real Bind result was observed.");
        internal Harness(bool interceptor = false, ManualTimeProvider? clock = null, IServiceProvider? services = null,
            ManualTimeProvider? physicalServerClock = null, ManualTimeProvider? physicalClientClock = null)
        {
            var server = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback)
                .UseRuntime(ConfigureWindows).ReplaceService<ITcpAcceptanceService>(Service);
            if (interceptor) server.AddInterceptor(new PassThroughServer());
            if (clock is not null && (physicalServerClock is not null || physicalClientClock is not null))
                throw new ArgumentException("Shared and endpoint-specific clocks are mutually exclusive.");
            if (physicalClientClock is not null && ReferenceEquals(physicalClientClock, physicalServerClock))
                throw new ArgumentException("Physical endpoint clocks must be independent.");
            if (clock is not null) server.UseTimeProvider(clock);
            if (physicalServerClock is not null)
                server.UseTimeProvider(physicalServerClock)
                    .UseHeartbeat(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3));
            if (services is not null) server.UseServiceProvider(services);
            var port = ((IPEndPoint)server.Transport!.LocalEndPoint!).Port;
            Server = (SharpLinkServer)server.Build();
            Server.BeforeBindingResultForTesting = (connection, _) =>
            { Volatile.Write(ref _connection, connection); return Task.CompletedTask; };
            Server.AfterRequestAcceptedForTesting = (_, _) => Interlocked.Increment(ref _accepted);
            var client = SharpClientBuilder.Create().UseRequestTimeout(Budget)
                .UseRuntime(ConfigureWindows).UseTcp(IPAddress.Loopback.ToString(), port);
            if (clock is not null) client.UseTimeProvider(clock).UseRequestTimeout(TimeSpan.FromSeconds(1));
            if (physicalClientClock is not null)
                client.UseTimeProvider(physicalClientClock)
                    .UseHeartbeat(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3));
            Client = (SharpLinkClient)client.Build();
        }
        private static void ConfigureWindows(SharpLinkRuntimeOptions options)
        { options.FlowControl.StreamReceiveWindowBytes = 128; options.FlowControl.ConnectionReceiveWindowBytes = 512; }
        internal async Task ConnectAsync()
        {
            _serverTask = Server.RunUntilStoppedAsync(_stop.Token).AsTask();
            await Client.ConnectAsync(_stop.Token);
        }
        internal async Task<RpcProxy<ITcpAcceptanceService>> PrepareAsync()
        {
            var proxy = Client.CreateProxy<ITcpAcceptanceService>();
            await ConnectAsync();
            await proxy.PrepareAsync(Budget);
            return proxy;
        }
        internal Task AssertQuiescentAsync() => WaitUntilAsync(() =>
            Client.PendingCallCount == 0 && Client.ActiveClientCallCount == 0 &&
            Client.ActiveClientStreamCount == 0 && Connection.ActiveCalls == 0 &&
            Connection.Session.StreamManager.ActiveStreamCount == 0 &&
            Connection.Session.QueuedSendBytes == 0 &&
            Server.ActiveDecodeCountForDiagnostics == 0 && Server.RetainedCompressedBytesForDiagnostics == 0 &&
            Server.DecodedBytesInFlightForDiagnostics == 0 && Server.PreAdmissionStreamBytesForDiagnostics == 0,
            "Real TCP calls/routes/send queue did not return to baseline.");
        // Used only by the two generated physical-Duplex controls. Preserve the legacy
        // Dispose path below, but never let one failed cleanup hide another original owner.
        internal async ValueTask DisposePhysicalFixtureAsync()
        {
            var failures = new List<Exception>();
            async Task Attempt(Func<Task> action)
            {
                try { await action(); }
                catch (Exception failure) { failures.Add(failure); }
            }
            await Attempt(() => Client.DisposeAsync().AsTask().WaitAsync(Budget));
            await Attempt(() => _stop.CancelAsync().WaitAsync(Budget));
            await Attempt(() => Server.DisposeAsync().AsTask().WaitAsync(Budget));
            if (_serverTask is not null)
                await Attempt(() => _serverTask.WaitAsync(Budget)); // actual original Run task
            try { _stop.Dispose(); }
            catch (Exception failure) { failures.Add(failure); }
            if (failures.Count != 0)
                throw new AggregateException("Independent physical-Duplex harness cleanup failures.", failures);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _stop.CancelAsync();
            await Server.DisposeAsync();
            if (_serverTask is not null)
            {
                try { await _serverTask.WaitAsync(Budget); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            }
            _stop.Dispose();
        }
    }

    private sealed class PassThroughServer : ISharpLinkServerInterceptor
    {
        public ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next) => next(context);
    }

    private sealed class Producer(params int[] values) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private int _enumerators, _moves, _disposed, _index = -1;
        private CancellationToken _token;
        internal bool HoldMove { get; init; }
        internal TaskCompletionSource MoveEntered { get; } = NewGate();
        internal TaskCompletionSource ReleaseMove { get; } = NewGate();
        internal int Enumerators => Volatile.Read(ref _enumerators);
        internal int Moves => Volatile.Read(ref _moves);
        internal int Disposed => Volatile.Read(ref _disposed);
        internal bool TokenOwnerLiveAfterHold { get; private set; }
        public int Current => values[_index];
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _enumerators); _token = cancellationToken; return this; }
        public async ValueTask<bool> MoveNextAsync()
        {
            Interlocked.Increment(ref _moves);
            MoveEntered.TrySetResult();
            if (HoldMove)
            {
                await ReleaseMove.Task;
                // Deliberately noncooperative physical tail; the original CTS must still exist.
                TokenOwnerLiveAfterHold = !_token.WaitHandle.SafeWaitHandle.IsClosed;
            }
            _token.ThrowIfCancellationRequested();
            return ++_index < values.Length;
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref _disposed); return ValueTask.CompletedTask; }
    }

    private sealed class WriterProbe { internal int Entries; }
    private readonly struct PairWriter(Producer first, Producer second, IRpcCodec<int> codec, WriterProbe probe)
        : IRpcClientStreamWriter
    {
        public async ValueTask WriteAsync(IRpcClientStreamSink sink, long requestId, CancellationToken token)
        {
            Interlocked.Increment(ref probe.Entries);
            var one = sink.SendClientStreamAsync(requestId, 1, first, codec, token);
            var two = sink.SendClientStreamAsync(requestId, 2, second, codec, token);
            await Task.WhenAll(one, two);
        }
    }
    private static RpcMethodDescriptor PairDescriptor(ITcpAcceptanceService generatedProxy)
    {
        // Read the actual generator-emitted immutable descriptor; never synthesize method IDs/counts.
        var method = generatedProxy.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(RpcMethodDescriptor))
            .Select(field => (RpcMethodDescriptor)field.GetValue(null)!)
            .Single(value => value.Kind == RpcMethodKind.OneWay && value.ClientStreamCount == 2);
        Ensure(method.HasClientStreams, "generated two-stream shape");
        return method;
    }
}
