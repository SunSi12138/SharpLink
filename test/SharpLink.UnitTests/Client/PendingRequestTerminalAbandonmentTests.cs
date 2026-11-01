using System.Buffers.Binary;
using System.Linq;
using System.Reflection;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

// Real prepared contract/binding plus the actual pending publication gate. These controls
// distinguish a terminal-owned non-attempt from a live attempted Full; none invents a token.
public sealed class PendingRequestTerminalAbandonmentTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const int OversizedNormalPayload = 30 * 1024;

    [Test]
    [Arguments(true, 8)]
    [Arguments(true, OversizedNormalPayload)]
    [Arguments(false, 8)]
    [Arguments(false, OversizedNormalPayload)]
    public Task OriginalTerminalBeforeTransferMustNotBecomeAnEnqueueFailure(bool expire, int payloadBytes)
        => RunAsync(async f =>
    {
        using var cancellation = new CancellationTokenSource();
        var response = f.Rent(cancellation.Token, out var id);
        var writer = f.Head(id, payloadBytes);
        var prepared = f.Connection.Session.PrepareOutboundFrame(writer, CancellationToken.None);
        if (expire) f.Clock.AdvanceWithoutRunningTimers(Timeout);
        else cancellation.Cancel();
        var observer = new Observer();

        var accepted = f.Connection.PendingCalls.TryPublishRequest(id, f.Connection.Session,
            prepared, true, cancellation.Token, observer, out var emission, out var rejection, f.Binding);

        Ensure(!accepted && rejection is null && emission.IsCompletedSuccessfully,
            "a terminal-owned non-attempt must not synthesize Full or Closed");
        var terminal = await f.OperationFailure(response);
        if (expire)
            Ensure(terminal is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                "the original five-second deadline must remain the exact logical terminal");
        else
            Ensure(terminal is OperationCanceledException cancelled && cancelled.CancellationToken == cancellation.Token,
                "the original caller token must remain the exact cancellation owner");
        Ensure(observer.Calls == 0, "clean terminal abandonment is not an emission failure callback");
        Ensure(writer.ReturnCount == 1 && f.Connection.Session.QueuedSendBytes == 0,
            "both reserved and unreserved preparation must return/refund exactly once");
        await f.AssertNoHeadOrCancelAsync();
    });

    [Test]
    public Task LiveEligibleFullMustRemainAnActualCapacityRejection()
        => RunAsync(async f =>
    {
        var response = f.Rent(default, out var id);
        var writer = f.Head(id, OversizedNormalPayload);
        var prepared = f.Connection.Session.PrepareOutboundFrame(writer, CancellationToken.None);
        var observer = new Observer();
        var accepted = f.Connection.PendingCalls.TryPublishRequest(id, f.Connection.Session,
            prepared, true, default, observer, out _, out var rejection, f.Binding);

        Ensure(!accepted && IsCapacity(rejection), "a live attempted Full must retain send_queue_capacity");
        Ensure(observer.Calls == 1 && observer.RequestId == id && ReferenceEquals(observer.Error, rejection),
            "the real refusal must reach its original failure observer exactly once");
        Ensure(writer.ReturnCount == 1 && f.Connection.Session.QueuedSendBytes == 0,
            "real Full still returns its unreserved writer exactly once");
        f.Connection.PendingCalls.TryComplete(id, PendingCallCompletionReason.SendFailure, rejection);
        Ensure(ReferenceEquals(await f.OperationFailure(response), rejection), "the caller owns the original real refusal");
        await f.AssertNoHeadOrCancelAsync();
    });

    [Test]
    [Arguments(8)]
    [Arguments(OversizedNormalPayload)]
    public Task TerminalAbandonmentMustRetainAnIndependentWriterReturnFailure(int payloadBytes)
        => RunAsync(async f =>
    {
        var response = f.Rent(default, out var id);
        var returnFailure = new InvalidOperationException("owned writer return failed");
        var writer = f.Head(id, payloadBytes, returnFailure);
        var prepared = f.Connection.Session.PrepareOutboundFrame(writer, CancellationToken.None);
        f.Clock.AdvanceWithoutRunningTimers(Timeout);
        var observer = new Observer();
        Exception? actual = null;
        try
        {
            f.Connection.PendingCalls.TryPublishRequest(id, f.Connection.Session,
                prepared, true, default, observer, out _, out _, f.Binding);
        }
        catch (Exception error) { actual = error; }

        Ensure(ReferenceEquals(actual, returnFailure), "the independent return failure must survive unchanged");
        Ensure(observer.Calls == 1 && observer.RequestId == id && ReferenceEquals(observer.Error, returnFailure),
            "cleanup failure observation must not gain a synthetic Full/Closed cause");
        Ensure(await f.OperationFailure(response) is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "cleanup failure must not replace the original pending deadline");
        Ensure(writer.ReturnCount == 1 && f.Connection.Session.QueuedSendBytes == 0,
            "throwing cleanup must still close its single owner and refund all reserved bytes");
        await f.AssertNoHeadOrCancelAsync();
    });

    [Test]
    public Task RealFullMustNotLoseAnIndependentFailureObserverException()
        => RunAsync(async f =>
    {
        var response = f.Rent(default, out var id);
        var writer = f.Head(id, OversizedNormalPayload);
        var prepared = f.Connection.Session.PrepareOutboundFrame(writer, CancellationToken.None);
        var callbackFailure = new InvalidOperationException("owned failure observer failed");
        var observer = new Observer(callbackFailure);
        Exception? actual = null;
        Exception? rejection = null;
        try
        {
            f.Connection.PendingCalls.TryPublishRequest(id, f.Connection.Session,
                prepared, true, default, observer, out _, out rejection, f.Binding);
        }
        catch (Exception error) { actual = error; }

        Ensure(ReferenceEquals(actual, callbackFailure) && IsCapacity(rejection),
            "the real capacity rejection and independent callback failure must both survive");
        Ensure(observer.Calls == 1 && observer.RequestId == id && ReferenceEquals(observer.Error, rejection), "callback observes the original real refusal");
        Ensure(writer.ReturnCount == 1 && f.Connection.Session.QueuedSendBytes == 0, "callback failure cannot bypass owner cleanup");
        f.Connection.PendingCalls.TryComplete(id, PendingCallCompletionReason.SendFailure, rejection);
        Ensure(ReferenceEquals(await f.OperationFailure(response), rejection), "real capacity remains the logical call failure");
        await f.AssertNoHeadOrCancelAsync();
    });

    [Test]
    public async Task PreparationProviderFailureMustSurviveEvenWhenThePendingDeadlineAlreadyElapsed()
    {
        var expected = new InvalidOperationException("original compression provider failure");
        var provider = new ThrowingCompressionProvider(expected);
        await RunAsync(async f =>
        {
            var response = f.Rent(default, out var id);
            var writer = f.Head(id, 64);
            f.Clock.AdvanceWithoutRunningTimers(Timeout);
            Exception? actual = null;
            try { _ = f.Connection.Session.PrepareOutboundFrame(writer, CancellationToken.None); }
            catch (Exception error) { actual = error; }
            Ensure(actual is SharpLinkCompressionProviderException { Code: SharpLinkErrorCode.Internal } &&
                   ReferenceEquals(actual.InnerException, expected) && provider.Calls == 1,
                "the existing provider wrapper and original cause must not be rewritten as terminal abandonment");
            Ensure(writer.ReturnCount == 1, "throwing preparation still owns and returns its input");
            f.Clock.Advance(TimeSpan.Zero);
            Ensure(await f.OperationFailure(response) is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                "the original pending deadline remains independently observable");
            await f.AssertNoHeadOrCancelAsync();
        }, provider);
    }

    [Test]
    [Arguments(RpcMethodKind.OneWay)]
    [Arguments(RpcMethodKind.ClientStreaming)]
    public Task ProducingLiveFullMustBeCallerOwnedWithoutReplayThroughStopOrDispose(RpcMethodKind kind)
        => RunAsync(async f =>
    {
        var method = new RpcMethodDescriptor(1, kind == RpcMethodKind.OneWay ? 303 : 309, kind,
            HasResponsePayload: kind == RpcMethodKind.ClientStreaming, HasClientStreams: true, HasMethodTimeout: true,
            MethodTimeout: Timeout, ClientStreamCount: 1);
        var request = default(RpcEmptyRequest);
        var probe = new ProducerProbe();
        var streams = new EmptyProducer(probe, f.Client.RuntimeContext.Codecs.GetCodec<int>());
        Task invocation = kind == RpcMethodKind.OneWay
            ? f.Channel.InvokeOneWayAsync(method, in request, new OversizedPayloadCodec(),
                in streams, metadata: null).AsTask()
            : f.Channel.InvokeClientStreamingAsync(method, in request, new OversizedPayloadCodec(),
                f.Client.RuntimeContext.Codecs.GetCodec<int>(), in streams, metadata: null).AsTask();
        var failure = await f.OperationFailure(invocation);
        Ensure(IsCapacity(failure), "a live producing Full must return the exact capacity error, not an aggregate or deadline");
        Ensure(probe.Calls == 0, "a head that never transferred must not start its producer");
        await f.AssertNoHeadOrCancelAsync();
        // RunAsync independently requires both real Stop and Dispose to succeed. No filter
        // accepts a separately replayed startup failure from either physical join.
    });

    private static async Task RunAsync(Func<Fixture, Task> body, ThrowingCompressionProvider? provider = null)
    {
        var f = await Fixture.CreateAsync(provider);
        var failures = new List<Exception>();
        var cleanupTasks = new List<(string Stage, Task Task, bool Observed)>();
        try { await body(f); }
        catch (Exception primary) { failures.Add(primary); }
        finally
        {
            // Keep body/caller evidence and every independent real cleanup outcome. No
            // await-using unwind may replace an earlier oracle with a later Stop failure.
            await ObserveCleanup("Stop", () => f.Client.StopAsync().AsTask(), failures, cleanupTasks);
            await ObserveCleanup("Dispose", () => f.Client.DisposeAsync().AsTask(), failures, cleanupTasks);
            foreach (var response in f.Operations.ToArray())
                if (!response.Value)
                    await ObserveCleanup("unobserved original response", () => response.Key, failures, cleanupTasks);
            RetainUnjoinedCleanup(failures, cleanupTasks);
        }
        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    private static async Task ObserveCleanup(string stage, Func<Task> start, List<Exception> failures,
        List<(string Stage, Task Task, bool Observed)> cleanupTasks)
    {
        Task? task = null;
        var observed = false;
        try
        {
            task = start();
            await task.WaitAsync(Timeout);
            observed = true;
        }
        catch (Exception error)
        {
            // Await exposes only one exception from a multi-fault Task. Retain the actual
            // owner's complete fault set; a timeout is separate observation evidence.
            if (task?.Exception is { } faults)
            {
                if (error is TimeoutException)
                    failures.Add(new InvalidOperationException(stage + " observation deadline", error));
                foreach (var fault in faults.InnerExceptions)
                    failures.Add(new InvalidOperationException(stage, fault));
                observed = true;
            }
            else
            {
                observed = task?.IsCompleted == true && error is not TimeoutException;
                failures.Add(new InvalidOperationException(stage, error));
            }
        }
        finally
        {
            if (task is not null)
            {
                cleanupTasks.Add((stage, task, observed));
                _ = task.ContinueWith(static completed => { _ = completed.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private static void RetainUnjoinedCleanup(List<Exception> failures,
        List<(string Stage, Task Task, bool Observed)> cleanupTasks)
    {
        foreach (var owned in cleanupTasks)
        {
            if (!owned.Task.IsCompleted)
                failures.Add(new TimeoutException($"{owned.Stage} still owns its original physical task."));
            else if (!owned.Observed && owned.Task.Exception is { } late)
                failures.Add(new InvalidOperationException($"{owned.Stage} completed after its observation deadline.", late));
        }
    }

    private static bool IsCapacity(Exception? error)
        => error is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted,
            DetailCode: SharpLinkErrorDetails.ResourceExhausted.SendQueueCapacity };

    private static async Task<Exception?> Failure(Task task)
    {
        try { await task.WaitAsync(Timeout); return null; }
        catch (Exception error) when (error is not TimeoutException) { return error; }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture
    {
        internal ManualTimeProvider Clock { get; } = new();
        internal Dictionary<Task, bool> Operations { get; } = [];

        internal async Task<Exception?> OperationFailure(Task response)
        {
            Operations.TryAdd(response, false);
            var failure = await Failure(response);
            Operations[response] = true;
            return failure;
        }
        internal TestClientTransportFactory Transport { get; private set; } = null!;
        internal SharpLinkClient Client { get; private set; } = null!;
        internal IRpcContractChannel Channel { get; private set; } = null!;
        internal ClientConnection Connection { get; private set; } = null!;
        internal RpcBindingPublication Binding { get; private set; } = null!;

        internal static async Task<Fixture> CreateAsync(ThrowingCompressionProvider? provider = null)
        {
            var f = new Fixture();
            f.Transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason |
                (provider is null ? ProtocolV2Capabilities.None : ProtocolV2Capabilities.Compression),
                contractManifest: [new(ManualClientContractFixture.ContractId, ManualClientContractFixture.AssemblyHash)],
                compressionProfile: provider?.WireProfile, acceptManualContractBindings: true);
            f.Client = ClientBuilderTestHelper.Build(f.Transport, builder =>
            {
                builder.UseTimeProvider(f.Clock);
                builder.UseRuntime(options =>
                {
                    options.FlowControl.MaxSendQueueBytes = 32 * 1024;
                    if (provider is not null) options.Compression.Providers.Add(provider);
                });
                if (provider is not null)
                    builder.UseRequestCompressionPolicy(new SharpLinkCompressionSendPolicy
                    { Enabled = true, MinimumPayloadBytes = 0, MinimumSavingsBytes = 0, MinimumSavingsRatio = 0 });
            });
            Task? connect = null;
            try
            {
                connect = f.Client.ConnectAsync().AsTask();
                await connect.WaitAsync(Timeout);
                f.Channel = await ClientInvokerTestHelper.CreatePreparedChannelAsync(f.Client);
                f.Connection = ((ClientConnection[])typeof(SharpLinkClient).GetField("_readyConnections",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Client)!).Single();
                f.Binding = DirectClientStreamTestGroup.CapturePreparedBinding(f.Channel, f.Connection);
                await f.Connection.Session.FlushSendQueueAsync().AsTask().WaitAsync(Timeout);
                while (await f.Transport.Connection.TryReadNextSentFrameAsync(TimeSpan.FromMilliseconds(20)) is not null) { }
                return f;
            }
            catch (Exception primary)
            {
                var failures = new List<Exception> { primary };
                var cleanupTasks = new List<(string Stage, Task Task, bool Observed)>();
                await ObserveCleanup("setup Stop", () => f.Client.StopAsync().AsTask(), failures, cleanupTasks);
                await ObserveCleanup("setup Dispose", () => f.Client.DisposeAsync().AsTask(), failures, cleanupTasks);
                if (connect is not null)
                    await ObserveCleanup("original setup Connect", () => connect, failures, cleanupTasks);
                RetainUnjoinedCleanup(failures, cleanupTasks);
                if (failures.Count > 1) throw new AggregateException(failures);
                throw;
            }
        }

        internal Task<int> Rent(CancellationToken cancellationToken, out long id)
        {
            var operation = Connection.PendingCalls.Rent(Client.RuntimeContext.Codecs.GetCodec<int>(), PendingCallKind.Unary,
                RpcDeadline.Create(Timeout, Clock), cancellationToken, out id, hasResponsePayload: true, responseNullable: false);
            var response = operation.AsValueTask().AsTask();
            Operations.Add(response, false);
            // Tests assert the outcome directly. Also observe a fault if an earlier assertion aborts.
            _ = response.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return response;
        }

        internal TrackingWriter Head(long id, int payloadBytes, Exception? returnFailure = null)
        {
            var writer = new TrackingWriter(returnFailure);
            using (writer.BeginPacketScope(ProtocolV2FrameType.Request,
                ProtocolV2FrameFlags.HasReturn | ProtocolV2FrameFlags.Cancellable | ProtocolV2FrameFlags.HasTimeBudget,
                unchecked((ulong)id)))
            {
                var prefix = writer.GetSpan(24);
                BinaryPrimitives.WriteUInt64LittleEndian(prefix, Binding.Token);
                BinaryPrimitives.WriteInt64LittleEndian(prefix[8..], 2);
                BinaryPrimitives.WriteInt64LittleEndian(prefix[16..], Timeout.Ticks);
                writer.Advance(24);
                writer.GetSpan(payloadBytes)[..payloadBytes].Clear();
                writer.Advance(payloadBytes);
            }
            return writer;
        }

        internal async Task AssertNoHeadOrCancelAsync()
        {
            await Connection.Session.FlushSendQueueAsync().AsTask().WaitAsync(Timeout);
            while (await Transport.Connection.TryReadNextSentFrameAsync(TimeSpan.FromMilliseconds(50)) is { } frame)
                Ensure(frame.Header.Type is not (ProtocolV2FrameType.Request or ProtocolV2FrameType.OneWayStreamRequest or ProtocolV2FrameType.Cancel),
                    "a never-transferred head must emit neither request kind nor orphan cancel");
        }

    }

    private sealed class TrackingWriter(Exception? returnFailure) : IRpcByteBufferWriter
    {
        private readonly PooledByteBufferWriter _inner = new();
        internal int ReturnCount;
        public int WrittenCount => _inner.WrittenCount;
        public ReadOnlyMemory<byte> WrittenMemory => _inner.WrittenMemory;
        public Span<byte> WrittenSpan => _inner.WrittenSpan;
        public int Capacity => _inner.Capacity;
        public void Advance(int count) => _inner.Advance(count);
        public Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
        public void Clear() => _inner.Clear();
        public void Dispose()
        {
            ReturnCount++;
            _inner.Dispose();
            if (returnFailure is not null) throw returnFailure;
        }
    }

    private sealed class Observer(Exception? callbackFailure = null) : IRequestEmissionFailureObserver
    {
        internal int Calls;
        internal long RequestId;
        internal Exception? Error;
        public void OnRequestEmissionFailure(long requestId, Exception exception)
        {
            Calls++;
            RequestId = requestId;
            Error = exception;
            if (callbackFailure is not null) throw callbackFailure;
        }
    }

    private sealed class ThrowingCompressionProvider(Exception failure) : ISharpLinkCompressionProvider
    {
        internal int Calls;
        public string WireProfile => "test.terminal-abandonment/v1";
        public bool TryCompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw failure;
        }
        public void Decompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ProducerProbe { internal int Calls; }
    private readonly struct EmptyProducer(ProducerProbe probe, IRpcCodec<int> codec) : IRpcClientStreamWriter
    {
        public ValueTask WriteAsync(IRpcClientStreamSink sink, long requestId, CancellationToken cancellationToken)
        {
            probe.Calls++;
            return new(sink.SendClientStreamAsync(requestId, 1, Empty(), codec, cancellationToken));
        }
        private static async IAsyncEnumerable<int> Empty() { await Task.CompletedTask; yield break; }
    }
    private sealed class OversizedPayloadCodec : IRpcCodec<RpcEmptyRequest>
    {
        public void Serialize(in RpcEmptyRequest value, IBufferWriter<byte> writer)
        {
            writer.GetSpan(OversizedNormalPayload)[..OversizedNormalPayload].Clear();
            writer.Advance(OversizedNormalPayload);
        }
        public RpcEmptyRequest Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }
}
