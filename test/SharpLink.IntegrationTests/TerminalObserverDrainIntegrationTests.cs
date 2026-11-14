using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Reflection;
using System.Collections.Concurrent;
using System.Text.Json;

namespace SharpLink.IntegrationTests;

public sealed class TerminalObserverDrainIntegrationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel]
    public async Task PublicDisconnectShouldDrainDespiteApplicationClockFailure(bool faultClock)
    {
        var clock = new DisconnectClock();
        var serverBuilder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback);
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        await using var server = serverBuilder.Build();
        using var serverCancellation = new CancellationTokenSource();
        var serverRun = server.RunUntilStoppedAsync(serverCancellation.Token).AsTask();
        var transport = new FaultingTransport(new SocketClientTransportFactory(new IPEndPoint(IPAddress.Loopback, port)));
        var client = SharpClientBuilder.Create().UseRequestTimeout(TimeSpan.FromHours(1))
            .UseTimeProvider(clock).UseTransport(transport).Build();
        // The server run has already captured a different ExecutionContext. Only this
        // client's pump/queued cleanup may claim the process-wide measurement gate.
        var metricScope = new AsyncLocal<bool> { Value = true };
        ClientConnection? connection = null;
        var operations = new List<Task<int>>();
        Exception? callbackFailure = null;
        var negativeSeen = 0;
        var observe = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "SharpLink" && instrument.Name == "sharplink.send.queue.bytes")
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, bytes, _, _) =>
        {
            if (!metricScope.Value || bytes >= 0 || Volatile.Read(ref observe) == 0 || Interlocked.Exchange(ref negativeSeen, 1) != 0)
                return;
            if (faultClock && !clock.FailAllAttempted.Wait(TimeSpan.FromSeconds(5)))
                callbackFailure = new Exception("real public disconnect did not reach queued FailAll before the pump observer");
        });
        listener.Start();
        try
        {
            await client.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            var proxy = client.Get<ITerminalObserverDrainProbe>();
            Ensure(await proxy.EchoAsync(41).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) == 42, "healthy generated TCP RPC prerequisite");
            var output = transport.Output!;
            // Observation only: the connection was created by public Connect/handshake. No private
            // construction, callback installation, pending registration or completion is used here.
            connection = ((ClientConnection[])typeof(SharpLinkClient).GetField("_readyConnections", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(client)!)[0];
            output.Arm();
            operations.Add(proxy.EchoAsync(1).AsTask());
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            operations.Add(proxy.EchoAsync(2).AsTask());
            operations.Add(proxy.EchoAsync(3).AsTask());
            Ensure(connection.PendingCalls.Count == 3, "three public unary calls must be registered before the fault");
            Volatile.Write(ref observe, 1);
            clock.Armed = faultClock;
            output.Release();
            await clock.OrDisconnected(connection).WaitAsync(TimeSpan.FromSeconds(5));
            var sessionFailure = await Capture(connection.Session.DisposeAsync().AsTask());
            // Generated telemetry/invocation wrappers finish asynchronously after the operation
            // itself becomes terminal. Bound observation without confusing continuation delay
            // with an orphaned operation, and retain a baseline timeout as a state observation.
            await Task.WhenAny(Task.WhenAll(operations.Select(task => task.ContinueWith(completed => { _ = completed.Exception; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default))), Task.Delay(TimeSpan.FromSeconds(5)));
            var completedBeforeSalvage = operations.Count(task => task.IsCompleted);
            var countBeforeSalvage = connection.PendingCalls.Count;
            var activeBeforeSalvage = connection.PendingCalls.ActiveCount;
            var bytesBeforeSalvage = connection.Session.QueuedSendBytes;
            var operationCauses = operations.Where(task => task.IsCompleted).Select(task => task.Exception?.InnerException).ToArray();
            // Stop is the public joined lifetime for the real queued connection cleanup. This
            // timestamp fault may belong to FailAll rather than the pump if FailAll retired first.
            clock.Armed = false;
            if (countBeforeSalvage != 0)
                connection.PendingCalls.FailAllPendingRequests(new IOException("baseline-only salvage after observations"));
            var stopFailure = await Capture(client.StopAsync().AsTask());
            Console.WriteLine("TERMINAL_OBSERVER_PUBLIC_V1 " + JsonSerializer.Serialize(new
            {
                faultClock,
                rpc = "public TCP Connect + generated unary",
                healthyRpc = true,
                pending = countBeforeSalvage,
                active = activeBeforeSalvage,
                completed = completedBeforeSalvage,
                queuedBytes = bytesBeforeSalvage,
                negativeSeen,
                clockThrows = clock.Throws,
                failAllObserved = clock.FailAllAttempted.IsSet,
                emissionObserved = clock.Trace.Any(route => route.Contains("OnRequestEmissionFailure", StringComparison.Ordinal)),
                operationCauses = operationCauses.Select(error => error?.ToString()).ToArray(),
                sessionFailure = sessionFailure?.ToString(),
                stopFailure = stopFailure?.ToString(),
                callbackFailure = callbackFailure?.ToString(),
                trace = clock.Trace.ToArray()
            }));
            Ensure(callbackFailure is null && negativeSeen != 0, "fixed public-entry window must be observed");
            Ensure(countBeforeSalvage == 0 && activeBeforeSalvage == 0 && completedBeforeSalvage == 3 && bytesBeforeSalvage == 0,
                "disconnect must retire all public pending operations and queued physical frames before salvage");
            Ensure(operationCauses.All(error => error is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed } closed &&
                ReferenceEquals(closed.InnerException, output.Failure)), "original output failure must remain every operation's cause");
            if (faultClock)
                Ensure(Contains(sessionFailure, clock.Failure) || Contains(stopFailure, clock.Failure), "joined cleanup must expose the original clock failure");
            else
                Ensure(sessionFailure is null && stopFailure is null && clock.Throws == 0, "healthy-clock disconnect must remain clean");
        }
        finally
        {
            metricScope.Value = false;
            Volatile.Write(ref observe, 0);
            clock.Armed = false;
            transport.Output?.Release();
            if (connection is not null)
            {
                try { connection.PendingCalls.FailAllPendingRequests(new IOException("post-observation test cleanup")); } catch { }
            }
            _ = await Capture(client.DisposeAsync().AsTask());
            foreach (var operation in operations.Where(task => task.IsCompleted)) _ = operation.Exception;
            await server.StopAsync(TimeSpan.Zero);
            await serverCancellation.CancelAsync();
            try { await serverRun.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }

    private static bool Contains(Exception? error, Exception target)
        => ReferenceEquals(error, target) || error is AggregateException aggregate && aggregate.InnerExceptions.Any(child => Contains(child, target)) ||
           error?.InnerException is { } inner && Contains(inner, target);

    private static async Task<Exception?> Capture(Task task)
    {
        await task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(5));
        try { await task; return null; } catch (Exception error) { return error; }
    }

    private static void Ensure(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private sealed class DisconnectClock : TimeProvider
    {
        public volatile bool Armed;
        public int Throws;
        public Exception Failure { get; } = new InvalidOperationException("public disconnect timestamp failure");
        public ConcurrentQueue<string> Trace { get; } = new();
        public ManualResetEventSlim FailAllAttempted { get; } = new();
        public override long GetTimestamp()
        {
            if (!Armed) return Stopwatch.GetTimestamp();
            var stack = new StackTrace().ToString();
            Trace.Enqueue(stack);
            if (stack.Contains("FailAllPendingRequests", StringComparison.Ordinal)) FailAllAttempted.Set();
            Interlocked.Increment(ref Throws);
            throw Failure;
        }

        public async Task OrDisconnected(ClientConnection connection)
        {
            var timeout = Stopwatch.StartNew();
            while (connection.State != ClientConnectionState.Closed)
            {
                Ensure(timeout.Elapsed < TimeSpan.FromSeconds(5), "public disconnect cleanup did not start");
                await Task.Delay(1);
            }
        }
    }

    private sealed class FaultingTransport(IClientTransportFactory inner) : IClientTransportFactory
    {
        public FaultingOutput? Output { get; private set; }
        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var connection = await inner.ConnectAsync(cancellationToken);
            Output = new FaultingOutput(connection.Output);
            return new WrappedConnection(connection, Output);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class WrappedConnection(ITransportConnection inner, FaultingOutput output) : ITransportConnection
    {
        public string Id => inner.Id;
        public PipeReader Input => inner.Input;
        public PipeWriter Output => output;
        public EndPoint? LocalEndPoint => inner.LocalEndPoint;
        public EndPoint? RemoteEndPoint => inner.RemoteEndPoint;
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class FaultingOutput(PipeWriter inner) : PipeWriter
    {
        private int _armed;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception Failure { get; } = new IOException("original public TCP output failure");
        public void Arm() => Volatile.Write(ref _armed, 1);
        public void Release() => _release.TrySetResult();
        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 0) return await inner.FlushAsync(cancellationToken);
            Entered.TrySetResult();
            await _release.Task;
            throw Failure;
        }
    }
}

[RpcContract]
public interface ITerminalObserverDrainProbe : IService
{
    [NonCancellable]
    ValueTask<int> EchoAsync(int value);
}

[RpcService]
public sealed class TerminalObserverDrainProbe : ITerminalObserverDrainProbe
{
    public ValueTask<int> EchoAsync(int value) => ValueTask.FromResult(value + 1);
}
