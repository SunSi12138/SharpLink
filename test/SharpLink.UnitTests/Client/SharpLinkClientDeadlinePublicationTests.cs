using System.Buffers.Binary;
using System.Buffers;
using System.Reflection;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

/// <summary>
/// Covers the client-side deadline guarantees that do not depend on the server budget: a Request is
/// published only after the send queue accepted it, a plain OneWay still fails its caller at its own
/// deadline and tells the peer to stop, and a Request whose deadline won while the payload was still
/// being prepared never reaches the peer.
/// </summary>
public sealed class SharpLinkClientDeadlinePublicationTests
{
    [Test]
    public async Task TimedOneWayClientStreamShouldNotPublishARequestTheSendQueueRefused()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = CreateDeadlineTransport(ProtocolV2Capabilities.CancellationReason);
        var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder
                .UseTimeProvider(timeProvider)
                .UseRuntime(static options => options.FlowControl.MaxSendQueueBytes = 32 * 1024));
        Task? invocation = null;
        var invocationJoined = false;
        var failures = new List<Exception>();
        var cleanupTasks = new List<(string Stage, Task Task)>();
        var diagnostic = new RefusedPublicationDiagnostic();
        try
        {
            await client.ConnectAsync();

            var method = new RpcMethodDescriptor(
                ContractId: 1,
                MethodId: 303,
                Kind: RpcMethodKind.OneWay,
                HasResponsePayload: false,
                HasClientStreams: true,
                HasMethodTimeout: true,
                MethodTimeout: TimeSpan.FromSeconds(5),
                ClientStreamCount: 1);
            var channel = await ClientInvokerTestHelper.CreatePreparedChannelAsync(client);
            var request = default(RpcEmptyRequest);
            var streams = new SilentClientStreams(channel.RuntimeContext.Codecs.GetCodec<int>());
            await DrainSentFramesAsync(transport);

            // The frame is larger than the share of the send queue a normal frame may occupy even when
            // that queue is empty, so admission refuses it and the pump never owns it. The codec's clock
            // advance leaves the pending deadline elapsed without running its timer, so the terminal
            // decision is taken by the refusal path - the path that used to mark the Request published.
            var codec = new DeadlineAdvancingPayloadCodec(
                timeProvider,
                TimeSpan.FromSeconds(5),
                payloadBytes: 30 * 1024,
                runTimers: false);
            invocation = channel.InvokeOneWayAsync(
                method,
                in request,
                codec,
                in streams,
                metadata: null,
                cancellationToken: default).AsTask();

            Exception? callerFailure = null;
            try { await invocation.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { callerFailure = error; }
            invocationJoined = invocation.IsCompleted;
            diagnostic.Record("caller", invocation, callerFailure);
            try
            {
                if (callerFailure is not SharpLinkException failure)
                {
                    if (callerFailure is not null)
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(callerFailure).Throw();
                    throw new InvalidOperationException("Expected SharpLinkException from the refused call.");
                }
                Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
                    "the elapsed deadline must own the terminal reason of a call the send queue refused");
            }
            catch (Exception error) { failures.Add(error); } // Preserve the strict oracle; still inspect wire evidence.

            // Neither half of the pair may reach the peer: the Request was refused, so the cancel that
            // the terminal transition wanted to publish belongs to no Request at all.
            var frames = await ReadSentFramesAsync(transport, TimeSpan.FromMilliseconds(200));
            Ensure(frames.TrueForAll(static frame => frame.Header.Type is not ProtocolV2FrameType.Request and not ProtocolV2FrameType.OneWayStreamRequest),
                "a Request the send queue refused must not reach the peer");
            Ensure(frames.TrueForAll(static frame => frame.Header.Type is not ProtocolV2FrameType.Cancel),
                "a Request that never entered the send queue must never produce a cancel");
            diagnostic.Record("wire-oracles", Task.CompletedTask, null);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            // Diagnostic ownership is explicit: an independent Stop/Dispose failure must not
            // replace the original caller/assertion outcome. No teardown exception is accepted.
            await ObserveCleanupAsync("stop", () => client.StopAsync().AsTask());
            if (invocation is not null && !invocationJoined)
                await ObserveCleanupAsync("original-call-join", () => invocation);
            await ObserveCleanupAsync("dispose", () => client.DisposeAsync().AsTask());
            foreach (var owned in cleanupTasks)
                if (!owned.Task.IsCompleted)
                    failures.Add(new TimeoutException($"Diagnostic {owned.Stage} task did not physically join."));
            if (invocation is not null && !invocation.IsCompleted)
                failures.Add(new TimeoutException("The original refused invocation did not join."));
            try { diagnostic.Write(); }
            catch (Exception error) { failures.Add(new InvalidOperationException("Diagnostic graph publication failed.", error)); }
        }
        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException("Refused publication caller/body and independent cleanup outcomes.", failures);

        async Task ObserveCleanupAsync(string stage, Func<Task> action)
        {
            Task? task = null;
            Exception? failure = null;
            try
            {
                task = action();
                cleanupTasks.Add((stage, task));
                await task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception error)
            {
                failure = error;
                failures.Add(new InvalidOperationException($"Independent diagnostic stage '{stage}' failed.", error));
            }
            try { diagnostic.Record(stage, task, failure); }
            catch (Exception error)
            {
                failures.Add(new InvalidOperationException($"Diagnostic graph stage '{stage}' failed.", error));
            }
        }
    }

    private sealed class RefusedPublicationDiagnostic
    {
        private readonly Dictionary<Exception, int> _ids = new(ReferenceEqualityComparer.Instance);
        private readonly List<object> _nodes = [];
        private readonly List<object> _stages = [];

        internal void Record(string stage, Task? task, Exception? observed)
            => _stages.Add(new
            {
                stage,
                taskStatus = task?.Status.ToString(),
                taskCompleted = task?.IsCompleted,
                observedException = Identify(observed),
                taskException = Identify(task is { IsFaulted: true } ? task.Exception : null)
            });

        private int? Identify(Exception? error)
        {
            if (error is null) return null;
            if (_ids.TryGetValue(error, out var existing)) return existing;
            var id = _nodes.Count;
            _ids.Add(error, id);
            _nodes.Add(new object()); // Register identity before traversing repeated inner references.
            var aggregate = error is AggregateException many
                ? many.InnerExceptions.Select(Identify).ToArray() : Array.Empty<int?>();
            _nodes[id] = new
            {
                id,
                runtimeIdentityHash = RuntimeHelpers.GetHashCode(error),
                type = error.GetType().FullName,
                error.Message,
                error.StackTrace,
                code = (error as SharpLinkException)?.Code.ToString(),
                detailCode = (error as SharpLinkException)?.DetailCode,
                inner = Identify(error.InnerException),
                aggregate
            };
            return id;
        }

        internal void Write()
            => Console.WriteLine("REFUSED_PUBLICATION_DIAGNOSTIC " +
                System.Text.Json.JsonSerializer.Serialize(new { stages = _stages, exceptions = _nodes }));
    }

    [Test]
    public async Task TimedUnaryShouldNotPublishARequestWhenTheDeadlineElapsedWithoutItsTimerRunning()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = CreateDeadlineTransport(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var method = MethodWithTimeout(RpcMethodKind.Unary, methodId: 312, TimeSpan.FromSeconds(5));
        var channel = await ClientInvokerTestHelper.CreatePreparedChannelAsync(client);
        var request = default(RpcEmptyRequest);

        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();
        await DrainSentFramesAsync(transport);

        // The codec moves the clock past the deadline without running its timer, and the frame is
        // small enough for the send queue to accept. The publication gate itself therefore has to be
        // the deadline authority: the scheduler callback is only a wake-up, and a late callback must
        // not let a Request with an already elapsed budget reach the peer.
        var codec = new DeadlineAdvancingPayloadCodec(
            timeProvider,
            TimeSpan.FromSeconds(5),
            runTimers: false);
        var invocation = channel.InvokeUnaryAsync(
            method,
            in request,
            codec,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null,
            cancellationToken: default).AsTask();

        for (var attempt = 0; attempt < 50 && !invocation.IsCompleted; attempt++)
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        Ensure(invocation.IsCompleted,
            "publication must refuse a Request whose deadline elapsed while its timer was still pending");

        // Release the scheduler so the failure below is observable, not blocked, in either ordering.
        timeProvider.Advance(TimeSpan.Zero);
        var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "an elapsed deadline that the scheduler has not reported yet must still fail the call");

        var frames = await ReadSentFramesAsync(transport, TimeSpan.FromMilliseconds(200));
        Ensure(frames.TrueForAll(static frame => frame.Header.Type is not ProtocolV2FrameType.Request),
            "a Request whose budget already elapsed must not be published by the gate that admits it");
        Ensure(frames.TrueForAll(static frame => frame.Header.Type is not ProtocolV2FrameType.Cancel),
            "no cancel may be emitted for a request the gate refused to publish");
    }

    [Test]
    public async Task TimedUnaryShouldRefusePublicationWhenTheDeadlineElapsesInsideTheCompressionProvider()
    {
        var timeProvider = new ManualTimeProvider();
        var provider = new DeadlineAdvancingCompressionProvider(
            timeProvider,
            TimeSpan.FromSeconds(5));
        var transport = CreateDeadlineTransport(
            ProtocolV2Capabilities.CancellationReason | ProtocolV2Capabilities.Compression,
            compressionProfile: provider.WireProfile);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder
                .UseTimeProvider(timeProvider)
                .UseRuntime(options => options.Compression.Providers.Add(provider))
                .UseRequestCompressionPolicy(new SharpLinkCompressionSendPolicy
                {
                    Enabled = true,
                    MinimumPayloadBytes = 0,
                    MinimumSavingsBytes = 0,
                    MinimumSavingsRatio = 0
                }));
        await client.ConnectAsync();

        var channel = await ClientInvokerTestHelper.CreatePreparedChannelAsync(client);
        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();
        await DrainSentFramesAsync(transport);

        // The provider is user code that runs while the frame is being prepared. Its clock advance
        // is a slow compressor, and the deadline it lets elapse has to be able to win before the
        // frame is queued instead of being blocked behind a completion gate.
        var codec = new DeadlineAdvancingCodec(timeProvider, TimeSpan.Zero);
        var requestValue = codec.Value;
        var invocation = channel.InvokeUnaryAsync(
            MethodWithTimeout(RpcMethodKind.Unary, methodId: 305, TimeSpan.FromSeconds(5)),
            in requestValue,
            codec,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null,
            cancellationToken: default).AsTask();
        var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "a deadline that elapses inside the compression provider must fail the call");

        var frames = await ReadSentFramesAsync(transport, TimeSpan.FromMilliseconds(200));
        Ensure(frames.TrueForAll(static frame => frame.Header.Type is not ProtocolV2FrameType.Request),
            "a deadline that won during frame preparation must leave the Request unpublished");
        Ensure(frames.TrueForAll(static frame => frame.Header.Type is not ProtocolV2FrameType.Cancel),
            "no cancel may be emitted for a request that was never published");
    }

    [Test]
    public async Task TimedOneWayClientStreamDeadlineShouldPreserveHeadAndBothTerminalFramesWhileTransportWasStalled()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = CreateDeadlineTransport(ProtocolV2Capabilities.CancellationReason);
        var probe = new ProducerProbe();
        var diagnostic = new StalledOneWayDiagnostic(transport, probe);
        var failures = new List<Exception>();
        try
        {
            var client = diagnostic.SetClient(ClientBuilderTestHelper.Build(
                transport, builder => builder.UseTimeProvider(timeProvider)));
            var heartbeat = client.GetHeartbeatConfigurationSnapshot();
            Ensure(heartbeat.Interval == TimeSpan.FromSeconds(10) && heartbeat.Timeout == TimeSpan.FromSeconds(30),
                "the fixed prefix requires the actual unchanged10s/30s heartbeat configuration");
            await diagnostic.RequireAsync(diagnostic.Own(client.ConnectAsync().AsTask(), "setup-connect"));
            var method = new RpcMethodDescriptor(
                ContractId: 1,
                MethodId: 308,
                Kind: RpcMethodKind.OneWay,
                HasResponsePayload: false,
                HasClientStreams: true,
                HasMethodTimeout: true,
                MethodTimeout: TimeSpan.FromSeconds(5),
                ClientStreamCount: 1);
            var prepare = diagnostic.Own(ClientInvokerTestHelper.CreatePreparedChannelAsync(client), "setup-prepare");
            await diagnostic.RequireAsync(prepare);
            var channel = await prepare;
            var request = default(RpcEmptyRequest);
            var streams = new BlockingClientStreams(probe, channel.RuntimeContext.Codecs.GetCodec<int>());
            await diagnostic.RequireAsync(diagnostic.Own(FlushStartupFramesAsync(client, transport), "setup-startup-flush"));
            var emissionEntered = diagnostic.Own(StallNextEmission(transport, diagnostic.Stall), "output-entered");
            var invocation = diagnostic.Own(channel.InvokeOneWayAsync(
                method,
                in request,
                RpcEmptyRequestCodec.Instance,
                in streams,
                metadata: null,
                cancellationToken: default).AsTask(), "caller");
            diagnostic.InvocationStarted = true;

            await diagnostic.RequireAsync(emissionEntered);
            Ensure(!invocation.IsCompleted,
                "the caller must still be waiting for the emission of an accepted Request");
            // One real raw-owner snapshot, not a wait for readiness installation. Null initial
            // readiness is recorded honestly and does not delay the original manual deadline.
            diagnostic.CaptureBeforeDeadline(GetOnlyReadyConnection(client));

            // The Request was accepted, so the pending entry owns the call. Its deadline has to end
            // the invocation even though the transport never emitted the frame: waiting for emission
            // on the bare caller token would leave the call blocked until the transport recovered.
            timeProvider.Advance(TimeSpan.FromSeconds(5));
            var frozenTimestamp = timeProvider.GetTimestamp(); // No further manual clock advance in this control.
            var failure = await diagnostic.RequireCallerDeadlineAsync(invocation);
            Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
                "a pending-owned OneWay must fail at its deadline while the transport is stalled");
            Ensure(!probe.Started.Task.IsCompleted,
                "the client-stream producer must not start for a call whose deadline already won");
            diagnostic.CaptureAfterCaller();

            diagnostic.Stall.Set();
            await diagnostic.JoinCallPublicationsAsync();
            var session = diagnostic.RequireLiveSession();
            Ensure(client.GetHeartbeatConfigurationSnapshot() == heartbeat && timeProvider.GetTimestamp() == frozenTimestamp,
                "heartbeat generation and provider must remain unchanged before the normal FIFO frontier");
            // The final group fence may precede the pending owner's Cancel callback. Anchor join
            // proves that callback returned; this additional normal force-flush drains its frame.
            await diagnostic.RequireAsync(diagnostic.Own(session.FlushSendQueueAsync().AsTask(), "call-prefix-flush"));
            Ensure(ReferenceEquals(session, diagnostic.RequireLiveSession()) &&
                   client.GetHeartbeatConfigurationSnapshot() == heartbeat && timeProvider.GetTimestamp() == frozenTimestamp,
                "the same live Session and frozen provider must own the sentinel");
            // Ping is progress traffic: only send AFTER the normal FIFO frontier. The pump's
            // progress drain flushes it; no post-Ping marker or elapsed quiet window proves parsing.
            await diagnostic.RequireAsync(diagnostic.Own(session.SendPingWithBackpressureAsync().AsTask(), "prefix-sentinel-admission"));
            var frames = new TestSentFrame?[4];
            for (var index = 0; index < frames.Length; ++index)
            {
                Task<TestSentFrame?>? read = null;
                try
                {
                    read = diagnostic.Own(transport.Connection.TryReadNextSentFrameAsync(TimeSpan.FromSeconds(5)), $"wire-{index + 1}");
                    await diagnostic.RequireAsync(read);
                    frames[index] = await read;
                }
                catch (Exception readFailure)
                {
                    try { diagnostic.RecordUnavailableFrame(index + 1, read, readFailure); }
                    catch (Exception receiptFailure)
                    {
                        failures.Add(new InvalidOperationException($"Unavailable frame{index + 1} receipt failed.", receiptFailure));
                    }
                    throw;
                }
                try { diagnostic.RecordFrame(index + 1, frames[index]); }
                catch (Exception error) { failures.Add(new InvalidOperationException($"Original frame{index + 1} receipt failed.", error)); }
            }
            // Exactly this bounded prefix, not scan-forward or arbitrary extra control traffic.
            diagnostic.ValidateFixedPrefix(frames, frozenTimestamp);
            Ensure(client.GetHeartbeatConfigurationSnapshot() == heartbeat && timeProvider.GetTimestamp() == frozenTimestamp,
                "the provider and heartbeat must remain frozen through the actual parsed sentinel");
            Ensure(!probe.Started.Task.IsCompleted, "the deadline must prevent the producer from starting through the wire frontier");
        }
        catch (Exception failure) { failures.Add(failure); }
        finally
        {
            try { await diagnostic.FinishAsync(failures); }
            catch (Exception failure)
            {
                failures.Add(new InvalidOperationException("OneWay diagnostic teardown infrastructure failed.", failure));
                try { diagnostic.ContainUnexpectedCleanup(); }
                catch (Exception containment) { failures.Add(containment); }
            }
            try { diagnostic.Write(failures); }
            catch (Exception failure) { failures.Add(new InvalidOperationException("OneWay diagnostic serialization failed.", failure)); }
            try { diagnostic.ClearIfQuiescent(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count != 0)
            throw new AggregateException("OneWay caller/wire assertions and independent physical cleanup outcomes.", failures);
    }

    // Per-test diagnostic ownership only. No production hook and no second pending-core consumer.
    private enum StalledCallShape { OneWay, ClientStreaming }

    private sealed class StalledOneWayDiagnostic(TestClientTransportFactory transport, ProducerProbe probe,
        StalledCallShape shape = StalledCallShape.OneWay)
    {
        private readonly bool _clientStreaming = shape switch
        {
            StalledCallShape.OneWay => false,
            StalledCallShape.ClientStreaming => true,
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
        private static readonly List<StalledOneWayDiagnostic> Quarantine = [];
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly object _gate = new();
        private readonly List<Source> _sources = [];
        private readonly List<WaitReceipt> _waits = [];
        private readonly List<object> _phases = [];
        private readonly List<object> _frames = [];
        private readonly List<Exception> _late = [];
        private readonly Dictionary<object, int> _identities = new(ReferenceEqualityComparer.Instance);
        private SharpLinkClient? _client;
        private ClientConnection? _connection;
        private ClientStreamPublicationGroup? _group;
        private Task<bool>? _raw;
        private Task<bool>? _acceptance;
        private long _requestId;
        private ulong _bindingToken;
        private Task? _stopEntry;
        private Task? _stopSource;
        private bool _stopPublished;
        private bool _beforeCaptured;
        private bool _afterCaptured;
        private bool _quarantined;
        private bool _quiescent;
        internal bool InvocationStarted;
        internal ManualResetEventSlim Stall { get; } = new(initialState: false);

        private sealed class Source(string name, Task task)
        {
            internal readonly Task Task = task;
            internal readonly List<string> Names = [name];
            internal bool ActuallyObserved, FailureReported;
            internal Exception? AwaitFailure, FullGraph, AcceptedCaller;
        }
        private sealed record WaitReceipt(Task Source, Task Waiter, Exception? Observed, Exception? FullGraph, Exception? Independent);
        private readonly record struct Observation(Source Source, Exception? IndependentWait);

        internal SharpLinkClient SetClient(SharpLinkClient client) => _client = client;
        internal Task<T> Own<T>(Task<T> task, string name) { _ = Own((Task)task, name); return task; }
        internal Task Own(Task task, string name)
        {
            bool observeLate;
            lock (_gate)
            {
                var existing = _sources.Find(source => ReferenceEquals(source.Task, task));
                if (existing is null) _sources.Add(new Source(name, task));
                else existing.Names.Add(name); // Alias receipt, not another operation or exception filter.
                observeLate = _quarantined;
            }
            if (observeLate) ObserveLate(task);
            return task;
        }
        private Source GetSource(Task task)
        {
            lock (_gate)
                return _sources.Find(source => ReferenceEquals(source.Task, task))
                    ?? throw new InvalidOperationException("Original source was not registered before observation.");
        }
        private Source[] Sources() { lock (_gate) return _sources.ToArray(); }
        private static Exception? Combine(Exception? first, Exception? next)
            => first is null ? next : next is null ? first : new AggregateException(first, next);
        private static void Throw(Exception? failure)
        { if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }

        private async Task ObserveOriginalAsync(Source source)
        {
            if (source.ActuallyObserved || !source.Task.IsCompleted) return;
            // The terminality check permits a nonblocking REAL await, not a status-only join.
            try { await source.Task.ConfigureAwait(false); }
            catch (Exception actual) { source.AwaitFailure = actual; }
            source.FullGraph = source.Task.Exception;
            source.ActuallyObserved = true;
        }
        private async Task<Observation> ObserveAsync(Task task)
        {
            var source = GetSource(task);
            var waiter = task.WaitAsync(Bound); // Timeout only; distinct receipt even when it aliases source.
            Exception? observed = null;
            Exception? independent = null;
            try { await waiter.ConfigureAwait(false); }
            catch (Exception error)
            {
                observed = error;
                var forwarded = task.IsFaulted && waiter.Exception is { } wg && task.Exception is { } sg &&
                    wg.InnerExceptions.SequenceEqual(sg.InnerExceptions, ReferenceEqualityComparer.Instance);
                forwarded |= waiter.IsCanceled && task.IsCanceled; // Timeout cannot cancel this waiter.
                if (!forwarded) independent = waiter.Exception ?? error;
            }
            lock (_gate) _waits.Add(new(task, waiter, observed, waiter.Exception, independent));
            await ObserveOriginalAsync(source).ConfigureAwait(false);
            return new(source, independent);
        }
        internal async Task RequireAsync(Task task)
        {
            var observation = await ObserveAsync(task);
            Throw(Combine(observation.IndependentWait, observation.Source.FullGraph ?? observation.Source.AwaitFailure));
            Ensure(observation.Source.ActuallyObserved, "the original diagnostic source did not physically join");
        }
        internal async Task<SharpLinkException> RequireCallerDeadlineAsync(Task task)
        {
            var observation = await ObserveAsync(task);
            Throw(Combine(observation.IndependentWait, observation.IndependentWait is null ? null :
                observation.Source.FullGraph ?? observation.Source.AwaitFailure));
            var source = observation.Source;
            Ensure(source.ActuallyObserved, "the original caller was not joined");
            // A direct expected caller outcome cannot authorize unrelated actual Task siblings.
            if (source.AwaitFailure is not SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded } failure ||
                source.FullGraph is not AggregateException graph || graph.InnerExceptions.Count != 1 ||
                !ReferenceEquals(graph.InnerExceptions[0], failure))
                throw new InvalidOperationException("Expected one exact original caller DeadlineExceeded outcome.",
                    source.FullGraph ?? source.AwaitFailure);
            source.AcceptedCaller = failure;
            return failure;
        }

        private static TimeSpan Remaining(long started)
        {
            var remaining = Bound - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        private static object? GroupField(ClientStreamPublicationGroup group, string name)
            => typeof(ClientStreamPublicationGroup).GetField(name, PrivateInstance)!.GetValue(group);
        private static int LiveSlots(PendingRequestTable.PendingCall?[] slots)
        {
            var count = 0;
            for (var index = 0; index < slots.Length; ++index)
                if (Volatile.Read(ref slots[index]) is not null) ++count;
            return count;
        }
        private bool TryCaptureOperationVersion(IRpcOperation? operation, long id, out short? version)
        {
            version = null;
            if (_clientStreaming)
            {
                if (operation is not RpcRequestOperation<int> actual || actual.Id != id) return false;
                version = actual.FailureObservationVersion;
            }
            else
            {
                if (operation is not RpcRequestOperation<RpcEmptyRequest> actual || actual.Id != id) return false;
                version = actual.FailureObservationVersion;
            }
            return true; // Original pending gate protects this exact incarnation; no GetResult/AsTask/status.
        }
        internal void CaptureBeforeDeadline(ClientConnection connection)
        {
            _connection = connection;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            Exception? failure = null;
            object? pendingGate = null;
            Lock? groupGate = null;
            var pendingEntered = false;
            var groupEntered = false;
            PendingRequestTable.PendingCall? call = null;
            IRpcOperation? operation = null;
            short? version = null;
            bool? peerAccepted = null, producerStarted = null, acceptanceCompleted = null;
            long id = 0;
            Task? readiness = null;
            Task? anchor = null;
            object? headOwner = null;
            int? headPublication = null, receiptState = null;
            try
            {
                var table = connection.PendingCalls;
                var slots = (PendingRequestTable.PendingCall?[]?)typeof(PendingRequestTable)
                    .GetField("_slots", PrivateInstance)!.GetValue(table);
                var mask = (int)typeof(PendingRequestTable).GetField("_indexMask", PrivateInstance)!.GetValue(table)!;
                id = table.HighestAllocatedRequestId; // Candidate only; exact slot/full identity rechecked below.
                if (slots is null || id <= 0)
                    failure = new InvalidOperationException("The stalled call has no actual pending publication.");
                else
                {
                    var index = (int)(id & mask);
                    call = Volatile.Read(ref slots[index]);
                    if (call is null) failure = new InvalidOperationException("Actual pending slot disappeared before snapshot.");
                    else
                    {
                        pendingGate = call.CompletionGate;
                        pendingEntered = Monitor.TryEnter(pendingGate, Remaining(started));
                        if (!pendingEntered) failure = new TimeoutException("Original pending CompletionGate acquisition exceeded5s.");
                        else if (!ReferenceEquals(Volatile.Read(ref slots[index]), call) || call.Id != id ||
                            LiveSlots(slots) != 1 || call.Kind != (_clientStreaming ? PendingCallKind.ClientStreaming : PendingCallKind.OneWayClientStreaming) ||
                            !call.RequestPublished || !TryCaptureOperationVersion(call.Operation, id, out version) ||
                            call.StreamGroup is not { } group)
                            failure = new InvalidOperationException("Pending incarnation/group/publication differs from the actual stalled call.");
                        else
                        {
                            operation = call.Operation; // Exact type/version validated above, no core consumption.
                            if (_clientStreaming)
                            {
                                _acceptance = call.AcceptanceReady?.Task;
                                peerAccepted = call.PeerAccepted;
                                producerStarted = call.ProducerStarted;
                                acceptanceCompleted = _acceptance?.IsCompleted;
                            }
                            _group = group;
                            groupGate = (Lock)GroupField(group, "_gate")!;
                            groupEntered = groupGate.TryEnter(Remaining(started));
                            if (!groupEntered) failure = new TimeoutException("Original group gate acquisition exceeded5s.");
                            else
                            {
                                _requestId = group.RequestId;
                                _bindingToken = group.Binding.Token;
                                _raw = (Task<bool>?)GroupField(group, "_headEmissionSource");
                                anchor = ((TaskCompletionSource?)GroupField(group, "_supervisionCompletion"))?.Task;
                                readiness = (Task?)GroupField(group, "_originTask");
                                var originAssigned = (bool)GroupField(group, "_originAssigned")!;
                                headOwner = GroupField(group, "_headEmissionOwner");
                                headPublication = (int)GroupField(group, "_headPublication")!;
                                receiptState = (int)GroupField(group, "_logicalReceiptState")!;
                                if (group.RequestId != id || !ReferenceEquals(group.Session, connection.Session) ||
                                    group.StreamCount != 1 || !group.HasProducerLifetime || _raw is null || _raw.IsCompleted ||
                                    anchor is null || originAssigned || Stall.IsSet || headPublication != 2 || receiptState != 0)
                                    failure = new InvalidOperationException("Actual predeadline raw/group ownership prerequisite failed.");
                            }
                        }
                    }
                }
            }
            catch (Exception error) { failure = Combine(failure, error); }
            finally
            {
                if (groupEntered) try { groupGate!.Exit(); } catch (Exception error) { failure = Combine(failure, error); }
                if (pendingEntered) try { Monitor.Exit(pendingGate!); } catch (Exception error) { failure = Combine(failure, error); }
            }
            // No assertions, identity dictionary, serialization or callbacks under production gates.
            if (_acceptance is not null) _ = Own(_acceptance, "peer-acceptance");
            if (_raw is not null) _ = Own(_raw, "raw-head");
            if (readiness is not null) _ = Own(readiness, "initial-readiness");
            if (anchor is not null) _ = Own(anchor, "supervision-anchor");
            _phases.Add(new { phase = "before-deadline", requestId = id, bindingToken = _bindingToken, connection = Identity(_connection), pending = Identity(call),
                operation = Identity(operation), operationVersion = version, group = Identity(_group),
                raw = Identity(_raw), readiness = Identity(readiness), readinessAvailable = readiness is not null,
                anchor = Identity(anchor), headOwner = headOwner?.ToString(), headPublication, receiptState });
            if (_clientStreaming)
            {
                _phases.Add(new { phase = "peer-acceptance-before-deadline", acceptance = Identity(_acceptance),
                    peerAccepted, producerStarted, acceptanceCompleted });
                if (_acceptance is null || peerAccepted != false || producerStarted != false || acceptanceCompleted != false)
                    failure = Combine(failure, new InvalidOperationException("The actual client-streaming call must await ungranted peer acceptance."));
            }
            if (Remaining(started) == TimeSpan.Zero)
                failure = Combine(failure, new TimeoutException("Predeadline ownership snapshot exceeded5s."));
            Throw(failure);
            Ensure(_raw is { IsCompleted: false } && !Stall.IsSet, "raw output ownership changed before manual Advance");
            _beforeCaptured = true;
        }
        internal void CaptureAfterCaller()
        {
            var group = _group ?? throw new InvalidOperationException("Original group was not retained.");
            var gate = (Lock)GroupField(group, "_gate")!;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var entered = false;
            Exception? failure = null;
            Task? physical = null, cleanup = null, anchor = null;
            bool assigned = false, exited = false;
            int? receiptState = null;
            object? reason = null;
            Exception? primary = null;
            try
            {
                entered = gate.TryEnter(Remaining(started));
                if (!entered) failure = new TimeoutException("Postcaller actual group gate acquisition exceeded5s.");
                else
                {
                    assigned = (bool)GroupField(group, "_originAssigned")!;
                    exited = (bool)GroupField(group, "_originExited")!;
                    physical = (Task?)GroupField(group, "_originTask");
                    cleanup = ((TaskCompletionSource?)GroupField(group, "_cleanupCompletion"))?.Task;
                    anchor = ((TaskCompletionSource?)GroupField(group, "_supervisionCompletion"))?.Task;
                    receiptState = (int)GroupField(group, "_logicalReceiptState")!;
                    reason = GroupField(group, "_logicalReason");
                    primary = (Exception?)GroupField(group, "_logicalPrimary");
                    if (!assigned || exited || physical is null || cleanup is null || anchor is null ||
                        _raw is not { IsCompleted: false } || !ReferenceEquals(GroupField(group, "_headEmissionSource"), _raw) || Stall.IsSet)
                        failure = new InvalidOperationException("Actual postcaller physical origins were not available while output stayed held.");
                }
            }
            catch (Exception error) { failure = Combine(failure, error); }
            finally { if (entered) try { gate.Exit(); } catch (Exception error) { failure = Combine(failure, error); } }
            if (physical is not null) _ = Own(physical, "physical-origin");
            if (cleanup is not null) _ = Own(cleanup, "cleanup-original");
            if (anchor is not null) _ = Own(anchor, "postcaller-anchor-alias");
            _phases.Add(new { phase = "after-caller", group = Identity(group), raw = Identity(_raw),
                physical = Identity(physical), cleanup = Identity(cleanup), anchor = Identity(anchor), assigned, exited,
                receiptState, reason = reason?.ToString(), primary = Identity(primary) });
            // Preserve the captured primary graph itself for final serialization, not a stale pooled operation read.
            if (primary is not null) _phaseErrors.Add(primary);
            if (Remaining(started) == TimeSpan.Zero)
                failure = Combine(failure, new TimeoutException("Postcaller ownership snapshot exceeded5s."));
            Throw(failure);
            Ensure(_raw is { IsCompleted: false } && !Stall.IsSet, "actual output was no longer held after origin capture");
            _afterCaptured = true;
        }
        internal async Task JoinCallPublicationsAsync()
        {
            Ensure(_beforeCaptured && _afterCaptured, "both original ownership snapshots are required before the wire frontier");
            Task[] originals;
            lock (_gate)
                originals = new[] { "raw-head", "cleanup-original", "physical-origin", "supervision-anchor" }
                    .Select(name => _sources.Single(source => source.Names.Contains(name)).Task).ToArray();
            Exception? failure = null;
            foreach (var original in originals)
            {
                try { await RequireAsync(original); }
                catch (Exception error) { failure = Combine(failure, error); }
            }
            if (_clientStreaming)
            {
                try
                {
                    var acceptance = _acceptance ?? throw new InvalidOperationException("Original peer acceptance source was not captured.");
                    await RequireAsync(acceptance);
                    var accepted = await acceptance;
                    _phases.Add(new { phase = "peer-acceptance-joined", source = Identity(acceptance), accepted });
                    Ensure(!accepted, "the deadline must complete the original peer acceptance source with false");
                }
                catch (Exception error) { failure = Combine(failure, error); }
            }
            Throw(failure); // Every exact original is independently observed; a timeout never authorizes a sentinel.
            _phases.Add(new { phase = "call-publications-joined", originals = originals.Select(Identity).ToArray() });
        }
        internal RpcSession RequireLiveSession()
        {
            var session = _connection?.Session ?? throw new InvalidOperationException("The original Session was not captured.");
            Ensure(ReferenceEquals(session, _group?.Session) && session.IsConnected &&
                   session.ProtocolPhase == RpcSessionProtocolPhase.Ready && _stopEntry is null,
                "the fixed wire prefix requires the original live Ready Session before Stop");
            return session;
        }
        internal void ValidateFixedPrefix(TestSentFrame?[] frames, long timestamp)
        {
            Ensure(frames.Length == 4 && frames.All(frame => frame.HasValue), "all four original frame results are required");
            var head = frames[0]!.Value;
            var headFlags = (_clientStreaming ? ProtocolV2FrameFlags.HasReturn : ProtocolV2FrameFlags.OneWay) |
                ProtocolV2FrameFlags.Cancellable | ProtocolV2FrameFlags.HasTimeBudget;
            var headType = _clientStreaming ? ProtocolV2FrameType.Request : ProtocolV2FrameType.OneWayStreamRequest;
            var prefixBytes = _clientStreaming ? 16 : 18;
            Ensure(head.Header.Type == headType && head.Header.Flags == headFlags &&
                   head.Header.RequestId == unchecked((ulong)_requestId) && _requestId > 0 && head.Payload.Length == prefixBytes + sizeof(long),
                "the fixed prefix must start with the exact accepted shape-specific head");
            var payload = head.Payload.AsSpan();
            Ensure(BinaryPrimitives.ReadUInt64LittleEndian(payload) == _bindingToken &&
                   BinaryPrimitives.ReadInt64LittleEndian(payload[8..]) == (_clientStreaming ? 309 : 308) &&
                   (_clientStreaming || BinaryPrimitives.ReadUInt16LittleEndian(payload[16..]) == 1) &&
                   BinaryPrimitives.ReadInt64LittleEndian(payload[prefixBytes..]) == TimeSpan.FromSeconds(5).Ticks,
                "the head must preserve the actual binding, method, declared stream and original stamped budget");
            var second = frames[1]!.Value;
            var third = frames[2]!.Value;
            Ensure((second.Header.Type == ProtocolV2FrameType.Cancel && third.Header.Type == ProtocolV2FrameType.StreamComplete) ||
                   (second.Header.Type == ProtocolV2FrameType.StreamComplete && third.Header.Type == ProtocolV2FrameType.Cancel),
                "the two terminal frames must be exactly one Cancel and one StreamComplete, in either source-permitted order");
            var cancel = second.Header.Type == ProtocolV2FrameType.Cancel ? second : third;
            var terminal = second.Header.Type == ProtocolV2FrameType.StreamComplete ? second : third;
            Ensure(cancel.Header.Flags == ProtocolV2FrameFlags.None && cancel.Header.RequestId == head.Header.RequestId &&
                   cancel.Payload.Length == 1 && ProtocolV2PayloadCodec.ReadCancelReason(new ReadOnlySequence<byte>(cancel.Payload)) == ProtocolV2CancelReason.DeadlineExceeded,
                "the exact correlated Cancel must carry only the DeadlineExceeded reason");
            Ensure(terminal.Header.Flags == ProtocolV2FrameFlags.Error && terminal.Header.RequestId == head.Header.RequestId &&
                   terminal.Payload.Length >= sizeof(ushort) && BinaryPrimitives.ReadUInt16LittleEndian(terminal.Payload) == 1,
                "the cleanup terminal must belong to declared stream1 on this exact request");
            var error = ProtocolV2PayloadCodec.ReadError(new ReadOnlySequence<byte>(terminal.Payload).Slice(sizeof(ushort)),
                terminal.Header.Flags, RequireLiveSession().RuntimeContext.Protocol.MaxErrorMessageBytes);
            Ensure(error.Code == SharpLinkErrorCode.Cancelled && error.DetailCode == 0 &&
                   error.Message == "Client stream terminated before every declared stream completed.",
                "the complete cleanup error must retain its exact source-defined payload");
            var sentinel = frames[3]!.Value;
            Ensure(sentinel.Header.Type == ProtocolV2FrameType.Ping && sentinel.Header.Flags == ProtocolV2FrameFlags.None &&
                   sentinel.Header.RequestId == 0 && sentinel.Payload.Length == sizeof(long) &&
                   BinaryPrimitives.ReadInt64LittleEndian(sentinel.Payload) == timestamp,
                "the fourth original frame must be the exact post-call frozen-timestamp parser sentinel");
        }
        private readonly List<Exception> _phaseErrors = [];
        internal void RecordFrame(int sequence, TestSentFrame? frame)
        {
            if (frame is not { } actual) { _frames.Add(new { sequence, missing = true }); return; }
            _frames.Add(new { sequence, missing = false, type = actual.Header.Type.ToString(),
                typeValue = (byte)actual.Header.Type, flags = actual.Header.Flags.ToString(),
                flagsValue = (byte)actual.Header.Flags, requestId = actual.Header.RequestId,
                payloadLength = actual.Payload.Length, payloadHex = Convert.ToHexString(actual.Payload) });
        }

        internal void RecordUnavailableFrame(int sequence, Task? readSource, Exception failure)
        {
            _phaseErrors.Add(failure);
            _frames.Add(new { sequence, unavailable = true, readSource = Identity(readSource),
                readFailureIdentity = Identity(failure) });
        }

        private async Task JoinCleanupAsync(Task task, List<Exception> failures)
        {
            try
            {
                var observation = await ObserveAsync(task);
                if (observation.IndependentWait is { } waitFailure) failures.Add(waitFailure);
                ReportSourceFailure(observation.Source, failures);
            }
            catch (Exception infrastructure) { failures.Add(infrastructure); }
        }
        private static void ReportSourceFailure(Source source, List<Exception> failures)
        {
            if (!source.ActuallyObserved || source.FailureReported) return;
            source.FailureReported = true;
            if (source.AcceptedCaller is { } accepted && source.FullGraph is AggregateException graph &&
                graph.InnerExceptions.Count == 1 && ReferenceEquals(graph.InnerExceptions[0], accepted)) return;
            if ((source.FullGraph ?? source.AwaitFailure) is { } failure) failures.Add(failure);
        }
        internal async Task FinishAsync(List<Exception> failures)
        {
            try { Stall.Set(); } catch (Exception error) { failures.Add(error); }
            try { probe.Release.TrySetResult(); } catch (Exception error) { failures.Add(error); }
            try
            {
                // One entry worker, one actual Stop invocation, no async wrapper replacing its source.
                _stopEntry = Own(Task.Run(() =>
                {
                    var source = _client is { } client ? client.StopAsync().AsTask() : transport.DisposeAsync().AsTask();
                    _ = Own(source, _client is null ? "transport-dispose-source" : "stop-source");
                    lock (_gate) { _stopSource = source; _stopPublished = true; }
                }), "stop-entry");
            }
            catch (Exception startFailure) { failures.Add(startFailure); }
            if (_stopEntry is not null) await JoinCleanupAsync(_stopEntry, failures);
            // Sticky latch BEFORE source snapshot; a later entry completion cannot authorize release.
            var entryTerminal = _stopEntry is { IsCompleted: true };
            bool sourcePublished;
            lock (_gate) sourcePublished = _stopPublished;
            var snapshot = Sources();
            foreach (var source in snapshot) await JoinCleanupAsync(source.Task, failures);
            var allTerminal = entryTerminal && sourcePublished && snapshot.All(source => source.Task.IsCompleted);
            if (!allTerminal || InvocationStarted && (!_beforeCaptured || !_afterCaptured))
            {
                failures.Add(new TimeoutException("OneWay diagnostic could not certify all original owners; containment retained."));
                Retain();
                return;
            }
            // Actual terminal source await + complete graph sweep closes races with prior bounded waits.
            foreach (var source in snapshot)
            {
                try { await ObserveOriginalAsync(source); ReportSourceFailure(source, failures); }
                catch (Exception error) { failures.Add(error); }
            }
            if (snapshot.Any(source => !source.ActuallyObserved))
            {
                failures.Add(new InvalidOperationException("A terminal status did not become an actual source observation."));
                Retain();
                return;
            }
            try { Stall.Dispose(); } catch (Exception error) { failures.Add(error); }
            _quiescent = true;
        }
        internal void ContainUnexpectedCleanup() { _quiescent = false; Retain(); }
        private void Retain()
        {
            Source[] snapshot;
            lock (_gate)
            {
                if (_quarantined) return;
                _quarantined = true;
                snapshot = _sources.ToArray();
            }
            lock (Quarantine) Quarantine.Add(this);
            foreach (var source in snapshot) ObserveLate(source.Task); // Include terminal-racing originals.
        }
        private void ObserveLate(Task task)
            => _ = task.ContinueWith(done =>
            {
                Exception? outcome = done.Exception;
                if (done.IsCanceled)
                    try { done.GetAwaiter().GetResult(); } catch (Exception actual) { outcome = actual; }
                if (outcome is not null) lock (_gate) _late.Add(outcome);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        private int? Identity(object? value)
        {
            if (value is null) return null;
            lock (_gate)
            {
                if (_identities.TryGetValue(value, out var existing)) return existing;
                var id = _identities.Count + 1;
                _identities.Add(value, id);
                return id;
            }
        }
        internal void Write(List<Exception> failures)
        {
            var nodes = new List<object>();
            var errors = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance);
            int? Error(Exception? error)
            {
                if (error is null) return null;
                if (errors.TryGetValue(error, out var existing)) return existing;
                var id = nodes.Count + 1;
                errors.Add(error, id);
                nodes.Add(new object());
                nodes[id - 1] = new { id, identity = Identity(error), type = error.GetType().FullName,
                    error.Message, error.StackTrace, code = (error as SharpLinkException)?.Code.ToString(),
                    detailCode = (error as SharpLinkException)?.DetailCode,
                    inner = Error(error.InnerException),
                    aggregate = error is AggregateException aggregate ? aggregate.InnerExceptions.Select(Error).ToArray() : Array.Empty<int?>(),
                    physicalRaw = error is PhysicalTaskFailureException physical ? Error(physical.RawException) : null,
                    canceledTask = error is TaskCanceledException canceled ? Identity(canceled.Task) : null };
                return id;
            }
            (Source Source, string[] Names)[] sources; WaitReceipt[] waits; Exception[] late;
            lock (_gate)
            {
                sources = _sources.Select(source => (Source: source, Names: source.Names.ToArray())).ToArray();
                waits = _waits.ToArray(); late = _late.ToArray();
            }
            var sourceRows = sources.Select(row => new { id = Identity(row.Source.Task), names = row.Names,
                status = row.Source.Task.Status.ToString(), completed = row.Source.Task.IsCompleted, observed = row.Source.ActuallyObserved,
                awaited = Error(row.Source.AwaitFailure), fullGraph = Error(row.Source.FullGraph),
                acceptedCaller = Error(row.Source.AcceptedCaller) }).ToArray();
            var waitRows = waits.Select(wait => new { source = Identity(wait.Source), waiter = Identity(wait.Waiter),
                status = wait.Waiter.Status.ToString(), observed = Error(wait.Observed),
                fullGraph = Error(wait.FullGraph), independent = Error(wait.Independent) }).ToArray();
            var body = failures.Select(Error).ToArray();
            var lateRows = late.Select(Error).ToArray();
            var phaseErrors = _phaseErrors.Select(Error).ToArray();
            Console.WriteLine("STALLED_ONEWAY_DIAGNOSTIC " + System.Text.Json.JsonSerializer.Serialize(new
            {
                phases = _phases, frames = _frames, sources = sourceRows, waits = waitRows, failures = body,
                late = lateRows, phaseErrors, exceptions = nodes, quiescent = _quiescent, quarantined = _quarantined,
                stopEntry = Identity(_stopEntry), stopSource = Identity(_stopSource), stopPublished = _stopPublished,
                producerStarted = probe.Started.Task.IsCompleted, beforeCaptured = _beforeCaptured, afterCaptured = _afterCaptured
            }));
        }
        internal void ClearIfQuiescent()
        {
            if (!_quiescent) return;
            lock (_gate) { _sources.Clear(); _waits.Clear(); _identities.Clear(); _late.Clear(); }
            _phases.Clear(); _frames.Clear(); _phaseErrors.Clear();
            _client = null; _connection = null; _group = null; _raw = null; _acceptance = null; _stopEntry = null; _stopSource = null;
        }
    }

    [Test]
    public async Task TimedClientStreamingDeadlineShouldPreserveHeadAndBothTerminalFramesWhileTransportWasStalled()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = CreateDeadlineTransport(ProtocolV2Capabilities.CancellationReason);
        var probe = new ProducerProbe();
        var diagnostic = new StalledOneWayDiagnostic(transport, probe, StalledCallShape.ClientStreaming);
        var failures = new List<Exception>();
        try
        {
            var client = diagnostic.SetClient(ClientBuilderTestHelper.Build(
                transport, builder => builder.UseTimeProvider(timeProvider)));
            var heartbeat = client.GetHeartbeatConfigurationSnapshot();
            Ensure(heartbeat.Interval == TimeSpan.FromSeconds(10) && heartbeat.Timeout == TimeSpan.FromSeconds(30),
                "the fixed prefix requires the actual unchanged10s/30s heartbeat configuration");
            await diagnostic.RequireAsync(diagnostic.Own(client.ConnectAsync().AsTask(), "setup-connect"));
            var method = new RpcMethodDescriptor(
                ContractId: 1,
                MethodId: 309,
                Kind: RpcMethodKind.ClientStreaming,
                HasResponsePayload: true,
                HasClientStreams: true,
                HasMethodTimeout: true,
                MethodTimeout: TimeSpan.FromSeconds(5),
                ClientStreamCount: 1);
            var prepare = diagnostic.Own(ClientInvokerTestHelper.CreatePreparedChannelAsync(client), "setup-prepare");
            await diagnostic.RequireAsync(prepare);
            var channel = await prepare;
            var request = default(RpcEmptyRequest);
            var streams = new BlockingClientStreams(probe, channel.RuntimeContext.Codecs.GetCodec<int>());
            await diagnostic.RequireAsync(diagnostic.Own(FlushStartupFramesAsync(client, transport), "setup-startup-flush"));
            var emissionEntered = diagnostic.Own(StallNextEmission(transport, diagnostic.Stall), "output-entered");
            var invocation = diagnostic.Own(channel.InvokeClientStreamingAsync(
                method,
                in request,
                RpcEmptyRequestCodec.Instance,
                channel.RuntimeContext.Codecs.GetCodec<int>(),
                in streams,
                metadata: null,
                cancellationToken: default).AsTask(), "caller");
            diagnostic.InvocationStarted = true;

            await diagnostic.RequireAsync(emissionEntered);
            Ensure(!invocation.IsCompleted,
                "the caller must still be waiting for the emission of an accepted Request");
            // One real raw-owner snapshot, not a wait for readiness installation. Null initial
            // readiness is recorded honestly and does not delay the original manual deadline.
            diagnostic.CaptureBeforeDeadline(GetOnlyReadyConnection(client));

            // No RequestAccepted is injected: the pending-owned deadline must win while the
            // physical head and peer producer permission are both unresolved.
            // The Request was accepted, so the pending entry owns the call. Its deadline has to end
            // the invocation even though the transport never emitted the frame: waiting for emission
            // on the bare caller token would leave the call blocked until the transport recovered.
            timeProvider.Advance(TimeSpan.FromSeconds(5));
            var frozenTimestamp = timeProvider.GetTimestamp(); // No further manual clock advance in this control.
            var failure = await diagnostic.RequireCallerDeadlineAsync(invocation);
            Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
                "a pending-owned client-streaming call must fail at its deadline while the transport is stalled");
            Ensure(!probe.Started.Task.IsCompleted,
                "the client-stream producer must not start for a call whose deadline already won");
            diagnostic.CaptureAfterCaller();

            diagnostic.Stall.Set();
            await diagnostic.JoinCallPublicationsAsync();
            var session = diagnostic.RequireLiveSession();
            Ensure(client.GetHeartbeatConfigurationSnapshot() == heartbeat && timeProvider.GetTimestamp() == frozenTimestamp,
                "heartbeat generation and provider must remain unchanged before the normal FIFO frontier");
            // The final group fence may precede the pending owner's Cancel callback. Anchor join
            // proves that callback returned; this additional normal force-flush drains its frame.
            await diagnostic.RequireAsync(diagnostic.Own(session.FlushSendQueueAsync().AsTask(), "call-prefix-flush"));
            Ensure(ReferenceEquals(session, diagnostic.RequireLiveSession()) &&
                   client.GetHeartbeatConfigurationSnapshot() == heartbeat && timeProvider.GetTimestamp() == frozenTimestamp,
                "the same live Session and frozen provider must own the sentinel");
            // Ping is progress traffic: only send AFTER the normal FIFO frontier. The pump's
            // progress drain flushes it; no post-Ping marker or elapsed quiet window proves parsing.
            await diagnostic.RequireAsync(diagnostic.Own(session.SendPingWithBackpressureAsync().AsTask(), "prefix-sentinel-admission"));
            var frames = new TestSentFrame?[4];
            for (var index = 0; index < frames.Length; ++index)
            {
                Task<TestSentFrame?>? read = null;
                try
                {
                    read = diagnostic.Own(transport.Connection.TryReadNextSentFrameAsync(TimeSpan.FromSeconds(5)), $"wire-{index + 1}");
                    await diagnostic.RequireAsync(read);
                    frames[index] = await read;
                }
                catch (Exception readFailure)
                {
                    try { diagnostic.RecordUnavailableFrame(index + 1, read, readFailure); }
                    catch (Exception receiptFailure)
                    {
                        failures.Add(new InvalidOperationException($"Unavailable frame{index + 1} receipt failed.", receiptFailure));
                    }
                    throw;
                }
                try { diagnostic.RecordFrame(index + 1, frames[index]); }
                catch (Exception error) { failures.Add(new InvalidOperationException($"Original frame{index + 1} receipt failed.", error)); }
            }
            // Exactly this bounded prefix, not scan-forward or arbitrary extra control traffic.
            diagnostic.ValidateFixedPrefix(frames, frozenTimestamp);
            Ensure(client.GetHeartbeatConfigurationSnapshot() == heartbeat && timeProvider.GetTimestamp() == frozenTimestamp,
                "the provider and heartbeat must remain frozen through the actual parsed sentinel");
            Ensure(!probe.Started.Task.IsCompleted, "the deadline must prevent the producer from starting through the wire frontier");
        }
        catch (Exception failure) { failures.Add(failure); }
        finally
        {
            try { await diagnostic.FinishAsync(failures); }
            catch (Exception failure)
            {
                failures.Add(new InvalidOperationException("ClientStreaming diagnostic teardown infrastructure failed.", failure));
                try { diagnostic.ContainUnexpectedCleanup(); }
                catch (Exception containment) { failures.Add(containment); }
            }
            try { diagnostic.Write(failures); }
            catch (Exception failure) { failures.Add(new InvalidOperationException("ClientStreaming diagnostic serialization failed.", failure)); }
            try { diagnostic.ClearIfQuiescent(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count != 0)
            throw new AggregateException("ClientStreaming caller/wire assertions and independent physical cleanup outcomes.", failures);
    }

    [Test]
    public async Task TimedDuplexDeadlineShouldPreserveCallerFailureAndJoinPhysicalWorkThroughTerminalPrefix()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = CreateDeadlineTransport(ProtocolV2Capabilities.CancellationReason);
        var probe = new ProducerProbe();
        var owner = new StalledDuplexDiagnostic(transport, probe);
        var failures = new List<Exception>();
        try
        {
            var client = owner.SetClient(ClientBuilderTestHelper.Build(transport, builder => builder.UseTimeProvider(timeProvider)));
            var heartbeat = client.GetHeartbeatConfigurationSnapshot();
            Ensure(heartbeat.Interval == TimeSpan.FromSeconds(10) && heartbeat.Timeout == TimeSpan.FromSeconds(30), "actual10s/30s heartbeat prerequisite required");
            await owner.Require(owner.Own(client.ConnectAsync().AsTask(), "connect"));
            var method = new RpcMethodDescriptor(ContractId: 1, MethodId: 310, Kind: RpcMethodKind.DuplexStreaming,
                HasResponsePayload: true, HasClientStreams: true, HasMethodTimeout: true, MethodTimeout: TimeSpan.FromSeconds(5), ClientStreamCount: 1);
            var prepare = owner.Own(ClientInvokerTestHelper.CreatePreparedChannelAsync(client), "prepare");
            await owner.Require(prepare); var channel = await prepare;
            var site = (SharpLinkClient.ContractCallSite)channel;
            Ensure(site.CaptureInterceptorGenerationForInvocation().Count == 0, "the hard origin-attachment proof requires the actual empty interceptor generation");
            var request = default(RpcEmptyRequest);
            var streams = new BlockingClientStreams(probe, channel.RuntimeContext.Codecs.GetCodec<int>());
            await owner.Require(owner.Own(FlushStartupFramesAsync(client, transport), "startup-flush"));
            var entered = owner.Own(StallNextEmission(transport, owner.Stall), "output-entered");
            var enumerator = owner.SetEnumerator(channel.InvokeDuplexStreamingAsync(method, in request, RpcEmptyRequestCodec.Instance,
                channel.RuntimeContext.Codecs.GetCodec<int>(), in streams, metadata: null, cancellationToken: default).GetAsyncEnumerator());
            var move = owner.SetMove(enumerator.MoveNextAsync().AsTask()); // Consume original public MoveNext only once.
            await owner.Require(entered);
            owner.CaptureBefore(GetOnlyReadyConnection(client)); // Eager Core attached original origin before public return; no installation wait.
            timeProvider.Advance(TimeSpan.FromSeconds(5));
            var frozenTimestamp = timeProvider.GetTimestamp();
            await owner.RequireDeadline(move);
            Ensure(!probe.Started.Task.IsCompleted, "the duplex producer must not start after its pending deadline won");
            owner.CaptureAfter();
            // Original physical origin owns cleanup/fence work and cannot finish while output is held.
            // The old unrelated-name absence poll did not join it. Do not manufacture a raw head Task.
            owner.Stall.Set();
            await owner.JoinPublications();
            var session = owner.LiveSession();
            Ensure(client.GetHeartbeatConfigurationSnapshot() == heartbeat && timeProvider.GetTimestamp() == frozenTimestamp, "frozen provider/heartbeat required before frontier");
            await owner.Require(owner.Own(session.FlushSendQueueAsync().AsTask(), "normal-prefix-flush"));
            Ensure(ReferenceEquals(session, owner.LiveSession()) && client.GetHeartbeatConfigurationSnapshot() == heartbeat &&
                timeProvider.GetTimestamp() == frozenTimestamp, "same live Session and frozen heartbeat required before progress sentinel");
            await owner.Require(owner.Own(session.SendPingWithBackpressureAsync().AsTask(), "sentinel-admission"));
            var frames = new TestSentFrame?[4];
            for (var index = 0; index < frames.Length; ++index)
            {
                Task<TestSentFrame?>? read = null;
                try
                {
                    read = owner.Own(transport.Connection.TryReadNextSentFrameAsync(TimeSpan.FromSeconds(5)), $"frame-{index + 1}");
                    await owner.Require(read); frames[index] = await read;
                }
                catch (Exception error)
                {
                    try { owner.Frame(index + 1, null, read, error); } catch (Exception receipt) { failures.Add(receipt); }
                    throw;
                }
                try { owner.Frame(index + 1, frames[index]); } catch (Exception receipt) { failures.Add(receipt); }
            }
            owner.Validate(frames, frozenTimestamp);
            Ensure(!probe.Started.Task.IsCompleted && client.GetHeartbeatConfigurationSnapshot() == heartbeat &&
                timeProvider.GetTimestamp() == frozenTimestamp, "producer0 and frozen provider must persist through parsed frontier");
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            try { await owner.Finish(failures); }
            catch (Exception error) { failures.Add(error); try { owner.Contain(); } catch (Exception containment) { failures.Add(containment); } }
            try { owner.Write(failures); } catch (Exception error) { failures.Add(error); }
            try { owner.Clear(); } catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count != 0) throw new AggregateException("Duplex caller, wire and independently owned cleanup outcomes.", failures);
    }

    // Duplex-only: no pending RPC result core or group-owned raw head Task exists on CallerWait.
    private sealed class StalledDuplexDiagnostic(TestClientTransportFactory transport, ProducerProbe probe)
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
        private static readonly List<StalledDuplexDiagnostic> Quarantine = [];
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly object _gate = new();
        private readonly List<Source> _sources = [];
        private readonly List<WaitReceipt> _waits = [];
        private readonly List<object> _frames = [], _phases = [];
        private readonly List<Exception> _receiptErrors = [];
        private readonly List<(Task Task, Exception? Error)> _late = [];
        private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
        private SharpLinkClient? _client;
        private ClientConnection? _connection;
        private ClientStreamPublicationGroup? _group;
        private Task? _origin, _cleanup, _anchor, _responseDrain, _disposeEntry, _disposeSource, _stopEntry, _stopSource;
        private Task<bool>? _acceptance, _move;
        private IAsyncEnumerator<int>? _enumerator;
        private bool _before, _after, _disposePublished, _stopPublished, _quarantined, _quiescent;
        private long _requestId;
        private ulong _binding;
        internal ManualResetEventSlim Stall { get; } = new(false);
        private sealed class Source(Task task, string name)
        {
            internal readonly Task Task = task;
            internal string Name = name;
            internal bool Joined, Reported, Expected;
            internal Exception? Awaited, Graph;
        }
        private sealed record WaitReceipt(Task Source, Task Waiter, Exception? Observed, Exception? Graph, Exception? Independent);
        internal SharpLinkClient SetClient(SharpLinkClient client) => _client = client;
        internal IAsyncEnumerator<int> SetEnumerator(IAsyncEnumerator<int> enumerator)
        {
            _enumerator = enumerator;
            _phases.Add(new { phase = "public-enumerator", enumerator = Id(enumerator) });
            return enumerator;
        }
        internal Task<bool> SetMove(Task<bool> task)
        {
            _move = task; _ = Own(task, "move-next");
            _phases.Add(new { phase = "public-move-next", enumerator = Id(_enumerator), source = Id(task) });
            return task;
        }
        internal Task<T> Own<T>(Task<T> task, string name) { _ = Own((Task)task, name); return task; }
        internal Task Own(Task task, string name)
        {
            bool observeLate;
            lock (_gate)
            {
                var prior = _sources.Find(entry => ReferenceEquals(entry.Task, task));
                if (prior is null) _sources.Add(new(task, name)); else prior.Name += "/" + name;
                observeLate = _quarantined;
            }
            if (observeLate) ObserveLate(task);
            return task;
        }
        private Source Entry(Task task) { lock (_gate) return _sources.Single(entry => ReferenceEquals(entry.Task, task)); }
        private Source[] Sources() { lock (_gate) return _sources.ToArray(); }
        private static Exception? Both(Exception? first, Exception? next)
            => first is null ? next : next is null ? first : new AggregateException(first, next);
        private static void Throw(Exception? error) { if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw(); }
        private async Task ObserveOriginal(Source entry)
        {
            if (entry.Joined || !entry.Task.IsCompleted) return;
            try { await entry.Task.ConfigureAwait(false); } catch (Exception error) { entry.Awaited = error; }
            entry.Graph = entry.Task.Exception; entry.Joined = true;
        }
        private async Task<Exception?> Observe(Task task)
        {
            var entry = Entry(task); var waiter = task.WaitAsync(Bound); Exception? independent = null, observed = null;
            try { await waiter.ConfigureAwait(false); }
            catch (Exception error)
            {
                observed = error;
                var forwarded = task.IsFaulted && waiter.Exception is { } wg && task.Exception is { } sg &&
                    wg.InnerExceptions.SequenceEqual(sg.InnerExceptions, ReferenceEqualityComparer.Instance);
                forwarded |= waiter.IsCanceled && task.IsCanceled; // Timeout-only waiter cannot independently cancel.
                if (!forwarded) independent = waiter.Exception ?? error;
            }
            lock (_gate) _waits.Add(new(task, waiter, observed, waiter.Exception, independent));
            await ObserveOriginal(entry); return independent;
        }
        internal async Task Require(Task task)
        {
            var wait = await Observe(task); var entry = Entry(task); var source = entry.Expected ? null : entry.Graph ?? entry.Awaited;
            if (source is not null) entry.Reported = true; // This full original stage is handed to the body, not discarded.
            Throw(Both(wait, source)); Ensure(entry.Joined, "the original Duplex source was not physically joined");
        }
        internal async Task RequireDeadline(Task<bool> task)
        {
            var wait = await Observe(task); var entry = Entry(task);
            if (wait is not null) { entry.Reported = entry.Graph is not null || entry.Awaited is not null; Throw(Both(wait, entry.Graph ?? entry.Awaited)); }
            Ensure(entry.Joined, "the original MoveNext did not finish");
            if (entry.Awaited is not SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded } error ||
                entry.Graph is not AggregateException graph || graph.InnerExceptions.Count != 1 || !ReferenceEquals(graph.InnerExceptions[0], error))
                throw new InvalidOperationException("Expected one exact original MoveNext DeadlineExceeded outcome.", entry.Graph ?? entry.Awaited);
            entry.Expected = true;
        }
        private static object? Field(ClientStreamPublicationGroup group, string name)
            => typeof(ClientStreamPublicationGroup).GetField(name, Private)!.GetValue(group);
        private static TimeSpan Remaining(long start)
        { var left = Bound - System.Diagnostics.Stopwatch.GetElapsedTime(start); return left > TimeSpan.Zero ? left : TimeSpan.Zero; }
        internal void CaptureBefore(ClientConnection connection)
        {
            _connection = connection; var start = System.Diagnostics.Stopwatch.GetTimestamp(); object? pendingGate = null; Lock? groupGate = null;
            bool pendingHeld = false, groupHeld = false, valid = false; Exception? failure = null; object? core = null; long lease = 0;
            try
            {
                var table = connection.PendingCalls;
                var slots = (PendingRequestTable.PendingCall?[])typeof(PendingRequestTable).GetField("_slots", Private)!.GetValue(table)!;
                var mask = (int)typeof(PendingRequestTable).GetField("_indexMask", Private)!.GetValue(table)!;
                var id = table.HighestAllocatedRequestId; var call = Volatile.Read(ref slots[(int)(id & mask)]);
                if (id <= 0 || call is null) throw new InvalidOperationException("No actual Duplex pending incarnation.");
                pendingGate = call.CompletionGate; pendingHeld = Monitor.TryEnter(pendingGate, Remaining(start));
                if (!pendingHeld) failure = new TimeoutException("Duplex CompletionGate acquisition exceeded5s.");
                else if (!ReferenceEquals(Volatile.Read(ref slots[(int)(id & mask)]), call) || call.Id != id ||
                    slots.Count(value => value is not null) != 1 || call.Kind != PendingCallKind.DuplexStreaming || call.Operation is not null ||
                    !call.RequestPublished || call.Dispatcher is not PooledAsyncStreamDispatcher<int> dispatcher || call.StreamGroup is not { } group)
                    failure = new InvalidOperationException("Duplex pending/core/group identity differs from the selected invocation.");
                else
                {
                    _group = group; core = dispatcher; lease = dispatcher.CaptureOriginalLease(); _acceptance = call.AcceptanceReady?.Task;
                    var unaccepted = !call.PeerAccepted && !call.ProducerStarted && _acceptance is { IsCompleted: false };
                    groupGate = (Lock)Field(group, "_gate")!; groupHeld = groupGate.TryEnter(Remaining(start));
                    if (!groupHeld) failure = new TimeoutException("Duplex group gate acquisition exceeded5s.");
                    else
                    {
                        _origin = (Task?)Field(group, "_originTask"); _anchor = ((TaskCompletionSource?)Field(group, "_supervisionCompletion"))?.Task;
                        _requestId = group.RequestId; _binding = group.Binding.Token;
                        valid = unaccepted && _origin is { IsCompleted: false } && _anchor is not null &&
                            (bool)Field(group, "_originAssigned")! && !(bool)Field(group, "_originExited")! &&
                            ((int)Field(group, "_responseRoutePublication")! & 3) == 3 && group.ResponseDispatcherLease == lease && lease != 0 &&
                            group.HasProducerLifetime && group.StreamCount == 1 && group.RequestId == id && ReferenceEquals(group.Session, connection.Session) &&
                            (int)Field(group, "_headPublication")! == 2 && Field(group, "_headEmissionSource") is null && !Stall.IsSet;
                    }
                }
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (groupHeld) try { groupGate!.Exit(); } catch (Exception error) { failure = Both(failure, error); }
                if (pendingHeld) try { Monitor.Exit(pendingGate!); } catch (Exception error) { failure = Both(failure, error); }
            }
            if (_origin is not null) _ = Own(_origin, "physical-origin"); if (_anchor is not null) _ = Own(_anchor, "supervision-anchor");
            if (_acceptance is not null) _ = Own(_acceptance, "peer-acceptance");
            _phases.Add(new { phase = "before", requestId = _requestId, binding = _binding, core = Id(core), lease, origin = Id(_origin), anchor = Id(_anchor), rawHead = "unavailable: CallerWait", valid });
            if (Remaining(start) == TimeSpan.Zero) failure = Both(failure, new TimeoutException("Duplex snapshot exceeded5s."));
            Throw(failure); Ensure(valid, "actual Duplex predeadline ownership prerequisite failed"); _before = true;
        }
        internal void CaptureAfter()
        {
            var group = _group ?? throw new InvalidOperationException("No captured Duplex group."); var gate = (Lock)Field(group, "_gate")!;
            var start = System.Diagnostics.Stopwatch.GetTimestamp(); var held = false; Exception? failure = null;
            bool claimed = false, assigned = false, exited = false;
            try
            {
                held = gate.TryEnter(Remaining(start)); if (!held) throw new TimeoutException("Duplex terminal snapshot gate exceeded5s.");
                _cleanup = ((TaskCompletionSource?)Field(group, "_cleanupCompletion"))?.Task;
                _responseDrain = (Task?)Field(group, "_responseDrainTask"); claimed = (bool)Field(group, "_responseDrainClaimed")!;
                assigned = (bool)Field(group, "_responseDrainAssigned")!; exited = (bool)Field(group, "_responseDrainExited")!;
            }
            catch (Exception error) { failure = error; }
            finally { if (held) try { gate.Exit(); } catch (Exception error) { failure = Both(failure, error); } }
            if (_cleanup is not null) _ = Own(_cleanup, "cleanup-original"); if (_responseDrain is not null) _ = Own(_responseDrain, "response-drain");
            _phases.Add(new { phase = "terminal", cleanup = Id(_cleanup), responseDrain = Id(_responseDrain), claimed, assigned, exited,
                unavailableChildOwnedByAnchor = _responseDrain is null });
            if (Remaining(start) == TimeSpan.Zero) failure = Both(failure, new TimeoutException("Duplex terminal snapshot exceeded5s."));
            Throw(failure); Ensure(_cleanup is not null && !Stall.IsSet && _origin is { IsCompleted: false }, "Duplex physical owner no longer held"); _after = true;
        }
        internal async Task JoinPublications()
        {
            Ensure(_before && _after, "both Duplex ownership snapshots are required"); Exception? failure = null;
            foreach (var task in new[] { _origin, _cleanup, _responseDrain, _anchor, _acceptance })
                if (task is not null) try { await Require(task); } catch (Exception error) { failure = Both(failure, error); }
            Throw(failure); var acceptance = _acceptance;
            bool? accepted = acceptance is null ? null : await acceptance;
            _phases.Add(new { phase = "peer-acceptance-joined", source = Id(acceptance), accepted });
            Ensure(accepted == false, "Duplex acceptance must actually resolve false");
        }
        internal RpcSession LiveSession()
        {
            var session = _connection?.Session ?? throw new InvalidOperationException("Original Duplex Session missing.");
            Ensure(ReferenceEquals(session, _group?.Session) && session.IsConnected && session.ProtocolPhase == RpcSessionProtocolPhase.Ready && _stopEntry is null,
                "the original Duplex Session must remain live through the parsed frontier"); return session;
        }
        internal void Frame(int sequence, TestSentFrame? frame, Task? read = null, Exception? error = null)
        {
            if (error is not null) _receiptErrors.Add(error);
            _frames.Add(frame is { } actual ? new { sequence, available = true, type = (byte)actual.Header.Type, flags = (byte)actual.Header.Flags,
                id = actual.Header.RequestId, length = actual.Payload.Length, payload = Convert.ToHexString(actual.Payload) } :
                (object)new { sequence, available = false, returnedNull = error is null, read = Id(read), readFailureIdentity = Id(error) });
        }
        internal void Validate(TestSentFrame?[] frames, long timestamp)
        {
            Ensure(frames.Length == 4 && frames.All(frame => frame.HasValue), "all four Duplex prefix results are required"); var head = frames[0]!.Value;
            Ensure(head.Header.Type == ProtocolV2FrameType.Request && head.Header.Flags == (ProtocolV2FrameFlags.Cancellable | ProtocolV2FrameFlags.HasTimeBudget) &&
                head.Header.RequestId == (ulong)_requestId && _requestId > 0 && head.Payload.Length == 24, "exact Duplex Request/flags36/payload24 required");
            Ensure(BinaryPrimitives.ReadUInt64LittleEndian(head.Payload) == _binding && BinaryPrimitives.ReadInt64LittleEndian(head.Payload.AsSpan(8)) == 310 &&
                BinaryPrimitives.ReadInt64LittleEndian(head.Payload.AsSpan(16)) == TimeSpan.FromSeconds(5).Ticks, "actual Duplex binding/method/budget required; no on-wire count");
            var a = frames[1]!.Value; var b = frames[2]!.Value;
            Ensure((a.Header.Type == ProtocolV2FrameType.Cancel && b.Header.Type == ProtocolV2FrameType.StreamComplete) ||
                (b.Header.Type == ProtocolV2FrameType.Cancel && a.Header.Type == ProtocolV2FrameType.StreamComplete), "exact terminal bijection required");
            var cancel = a.Header.Type == ProtocolV2FrameType.Cancel ? a : b; var terminal = a.Header.Type == ProtocolV2FrameType.StreamComplete ? a : b;
            Ensure(cancel.Header.RequestId == head.Header.RequestId && cancel.Header.Flags == ProtocolV2FrameFlags.None && cancel.Payload.Length == 1 &&
                ProtocolV2PayloadCodec.ReadCancelReason(new ReadOnlySequence<byte>(cancel.Payload)) == ProtocolV2CancelReason.DeadlineExceeded, "exact correlated deadline Cancel required");
            Ensure(terminal.Header.RequestId == head.Header.RequestId && terminal.Header.Flags == ProtocolV2FrameFlags.Error && terminal.Payload.Length >= 2 &&
                BinaryPrimitives.ReadUInt16LittleEndian(terminal.Payload) == 1, "exact outgoing stream1 cleanup required");
            var error = ProtocolV2PayloadCodec.ReadError(new ReadOnlySequence<byte>(terminal.Payload).Slice(2), terminal.Header.Flags, LiveSession().RuntimeContext.Protocol.MaxErrorMessageBytes);
            Ensure(error.Code == SharpLinkErrorCode.Cancelled && error.DetailCode == 0 && error.Message == "Client stream terminated before every declared stream completed.", "complete exact cleanup error required");
            var ping = frames[3]!.Value; Ensure(ping.Header.Type == ProtocolV2FrameType.Ping && ping.Header.Flags == ProtocolV2FrameFlags.None && ping.Header.RequestId == 0 &&
                ping.Payload.Length == 8 && BinaryPrimitives.ReadInt64LittleEndian(ping.Payload) == timestamp, "exact frozen-timestamp fourth parser sentinel required");
        }
        private async Task Join(Task task, List<Exception> failures)
        {
            var wait = await Observe(task); if (wait is not null) failures.Add(wait); Report(Entry(task), failures);
        }
        private static void Report(Source entry, List<Exception> failures)
        {
            if (!entry.Expected && !entry.Reported && (entry.Graph ?? entry.Awaited) is { } error) { failures.Add(error); entry.Reported = true; }
        }
        internal async Task Finish(List<Exception> failures)
        {
            try { Stall.Set(); } catch (Exception error) { failures.Add(error); }
            try { probe.Release.TrySetResult(); } catch (Exception error) { failures.Add(error); }
            foreach (var entry in Sources()) try { await Join(entry.Task, failures); } catch (Exception error) { failures.Add(error); }
            var moveJoined = _move is null ? _enumerator is null : Entry(_move).Joined;
            var enumerator = _enumerator;
            if (enumerator is not null && moveJoined)
            {
                try
                {
                    _disposeEntry = Task.Run(() => { var source = enumerator.DisposeAsync().AsTask(); _ = Own(source, "enumerator-dispose"); lock (_gate) { _disposeSource = source; _disposePublished = true; } });
                    _ = Own(_disposeEntry, "dispose-entry"); await Join(_disposeEntry, failures);
                }
                catch (Exception error) { failures.Add(error); }
            }
            var disposeLatched = _enumerator is null || (_disposeEntry is not null && Entry(_disposeEntry).Joined && _disposePublished);
            try
            {
                _stopEntry = Task.Run(() => { var source = _client is { } client ? client.StopAsync().AsTask() : transport.DisposeAsync().AsTask(); _ = Own(source, "stop-source"); lock (_gate) { _stopSource = source; _stopPublished = true; } });
                _ = Own(_stopEntry, "stop-entry"); await Join(_stopEntry, failures);
            }
            catch (Exception error) { failures.Add(error); }
            var stopLatched = _stopEntry is not null && Entry(_stopEntry).Joined && _stopPublished; // Sticky before final source snapshot; late publication cannot authorize release.
            var snapshot = Sources(); foreach (var entry in snapshot) try { await Join(entry.Task, failures); } catch (Exception error) { failures.Add(error); }
            foreach (var entry in Sources()) { await ObserveOriginal(entry); Report(entry, failures); }
            if (!moveJoined || !disposeLatched || !stopLatched || (_enumerator is not null && (!_before || !_after)) || Sources().Any(entry => !entry.Joined))
            { failures.Add(new InvalidOperationException("Duplex physical ownership incomplete; retained in quarantine.")); Contain(); return; }
            try { Stall.Dispose(); } catch (Exception error) { failures.Add(error); } _quiescent = true;
        }
        private void ObserveLate(Task task)
            => _ = task.ContinueWith(done => { Exception? error = null; try { done.GetAwaiter().GetResult(); } catch (Exception actual) { error = actual; }
                lock (_gate) _late.Add((done, done.Exception ?? error)); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        internal void Contain()
        {
            lock (_gate) { if (_quarantined) return; _quarantined = true; }
            lock (Quarantine) Quarantine.Add(this);
            foreach (var entry in Sources()) ObserveLate(entry.Task);
            foreach (var task in new[] { _move, _origin, _cleanup, _anchor, _acceptance, _responseDrain, _disposeEntry, _disposeSource, _stopEntry, _stopSource })
                if (task is not null) ObserveLate(task);
        }
        private int? Id(object? value)
        {
            if (value is null) return null; lock (_gate) { if (_ids.TryGetValue(value, out var id)) return id; id = _ids.Count + 1; _ids.Add(value, id); return id; }
        }
        internal void Write(List<Exception> failures)
        {
            var nodes = new List<object>(); var seen = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance);
            int? Error(Exception? error)
            {
                if (error is null) return null; if (seen.TryGetValue(error, out var existing)) return existing;
                var id = nodes.Count + 1; seen.Add(error, id); nodes.Add(new object());
                nodes[id - 1] = new { id, identity = Id(error), type = error.GetType().FullName, error.Message, error.StackTrace, inner = Error(error.InnerException),
                    code = (error as SharpLinkException)?.Code.ToString(), detailCode = (error as SharpLinkException)?.DetailCode,
                    aggregate = error is AggregateException aggregate ? aggregate.InnerExceptions.Select(Error).ToArray() : Array.Empty<int?>(),
                    physicalRaw = error is PhysicalTaskFailureException physical ? Error(physical.RawException) : null, canceledTask = error is TaskCanceledException canceled ? Id(canceled.Task) : null };
                return id;
            }
            var sources = Sources().Select(entry => new { task = Id(entry.Task), entry.Name, status = entry.Task.Status.ToString(), entry.Joined, entry.Expected,
                awaited = Error(entry.Awaited), graph = Error(entry.Graph) }).ToArray();
            var body = failures.Select(Error).ToArray(); WaitReceipt[] waits; (Task Task, Exception? Error)[] late;
            lock (_gate) { waits = _waits.ToArray(); late = _late.ToArray(); }
            var waitRows = waits.Select(wait => new { source = Id(wait.Source), waiter = Id(wait.Waiter), observed = Error(wait.Observed), graph = Error(wait.Graph), independent = Error(wait.Independent) }).ToArray();
            var receiptErrors = _receiptErrors.Select(Error).ToArray();
            var lateRows = late.Select(item => new { task = Id(item.Task), error = Error(item.Error) }).ToArray();
            Console.WriteLine("STALLED_DUPLEX_DIAGNOSTIC " + System.Text.Json.JsonSerializer.Serialize(new { sources, failures = body, exceptions = nodes,
                disposeEntry = Id(_disposeEntry), disposeSource = Id(_disposeSource), disposePublished = _disposePublished,
                stopEntry = Id(_stopEntry), stopSource = Id(_stopSource), stopPublished = _stopPublished,
                waits = waitRows, frames = _frames, phases = _phases, receiptErrors, late = lateRows, rawHead = "unavailable: CallerWait", quiescent = _quiescent, quarantined = _quarantined }));
        }
        internal void Clear()
        {
            if (!_quiescent) return; lock (_gate) { _sources.Clear(); _waits.Clear(); _late.Clear(); _ids.Clear(); }
            _frames.Clear(); _phases.Clear(); _receiptErrors.Clear(); _client = null; _connection = null; _group = null; _enumerator = null;
            _origin = _cleanup = _anchor = _responseDrain = _disposeEntry = _disposeSource = _stopEntry = _stopSource = null; _acceptance = _move = null;
        }
    }

    [Test]
    public async Task TimedPlainOneWayShouldNotPublishARequestWhoseDeadlineElapsesInsideTheCompressionProvider()
    {
        var timeProvider = new ManualTimeProvider();
        var provider = new DeadlineAdvancingCompressionProvider(
            timeProvider,
            TimeSpan.FromSeconds(5));
        var transport = CreateDeadlineTransport(
            ProtocolV2Capabilities.CancellationReason | ProtocolV2Capabilities.Compression,
            compressionProfile: provider.WireProfile);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder
                .UseTimeProvider(timeProvider)
                .UseRuntime(options => options.Compression.Providers.Add(provider))
                .UseRequestCompressionPolicy(new SharpLinkCompressionSendPolicy
                {
                    Enabled = true,
                    MinimumPayloadBytes = 0,
                    MinimumSavingsBytes = 0,
                    MinimumSavingsRatio = 0
                }));
        await client.ConnectAsync();

        var method = MethodWithTimeout(RpcMethodKind.OneWay, methodId: 311, TimeSpan.FromSeconds(5));
        var channel = await ClientInvokerTestHelper.CreatePreparedChannelAsync(client);
        var request = default(RpcEmptyRequest);
        var streams = default(RpcNoClientStreams);

        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();
        await DrainSentFramesAsync(transport);

        // The codec is fast, so the pre-compression check still sees a live budget. The provider -
        // user code that runs while the frame is being prepared - is what consumes the budget, which
        // makes the linearization point the only place that can still refuse this Request. The codec
        // still writes a payload: an empty one is skipped by the compression policy before the
        // provider is ever asked.
        var codec = new DeadlineAdvancingPayloadCodec(timeProvider, TimeSpan.Zero, payloadBytes: 8);
        var invocation = channel.InvokeOneWayAsync(
            method,
            in request,
            codec,
            in streams,
            metadata: null,
            cancellationToken: default).AsTask();
        var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "a plain OneWay whose budget elapsed during preparation must fail at its deadline");

        var frames = await ReadSentFramesAsync(transport, TimeSpan.FromMilliseconds(200));
        Ensure(frames.TrueForAll(static frame => frame.Header.Type is not ProtocolV2FrameType.Request),
            "a Request that expired before admission must never be published");
        Ensure(frames.TrueForAll(static frame => frame.Header.Type is not ProtocolV2FrameType.Cancel),
            "no cancel may be emitted for a request that was never published");
    }

    [Test]
    public async Task TimedPlainOneWayShouldCancelTheRemoteCallWhenItsDeadlineWinsTheEmissionRace()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = CreateDeadlineTransport(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var method = MethodWithTimeout(RpcMethodKind.OneWay, methodId: 306, TimeSpan.FromSeconds(5));
        var channel = await ClientInvokerTestHelper.CreatePreparedChannelAsync(client);
        var request = default(RpcEmptyRequest);
        var streams = default(RpcNoClientStreams);

        await FlushStartupFramesAsync(client, transport);
        using var stall = new ManualResetEventSlim(initialState: false);
        var emissionEntered = StallNextEmission(transport, stall);

        try
        {
            var invocation = channel.InvokeOneWayAsync(
                method,
                in request,
                RpcEmptyRequestCodec.Instance,
                in streams,
                metadata: null,
                cancellationToken: default).AsTask();

            await emissionEntered.WaitAsync(TimeSpan.FromSeconds(5));
            timeProvider.Advance(TimeSpan.FromSeconds(5));
            var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
                "a timed plain OneWay must fail its caller at its own deadline");

            // Nothing else tracks this shape, so the caller owns telling the peer to stop. The Request is
            // already in the send queue ahead of the cancel, so the peer reads them in that order.
            stall.Set();
            var published = await transport.Connection.TryReadNextSentFrameAsync(TimeSpan.FromSeconds(5));
            var cancelled = await transport.Connection.TryReadNextSentFrameAsync(TimeSpan.FromSeconds(5));
            Ensure(published?.Header.Type == ProtocolV2FrameType.Request,
                "the Request must be published before its cancel");
            Ensure(ReadTimeBudget(published!.Value) == TimeSpan.FromSeconds(5),
                "the published Request must still carry the budget sampled when the frame was created");
            Ensure(cancelled?.Header.Type == ProtocolV2FrameType.Cancel,
                "the deadline must be published as a cancel behind its own Request");
            Ensure(cancelled!.Value.Header.RequestId == published.Value.Header.RequestId,
                "the cancel must target the Request it follows");
            Ensure(ProtocolV2PayloadCodec.ReadCancelReason(new ReadOnlySequence<byte>(cancelled.Value.Payload))
                    == ProtocolV2CancelReason.DeadlineExceeded,
                "the cancel must carry the deadline reason the peer negotiated");
        }
        finally
        {
            stall.Set();
            await client.StopAsync();
        }
    }

    [Test]
    public async Task TimedPlainOneWayShouldNotPublishARequestWhoseDeadlineElapsedDuringSerialization()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = CreateDeadlineTransport(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var method = MethodWithTimeout(RpcMethodKind.OneWay, methodId: 307, TimeSpan.FromSeconds(5));
        var channel = await ClientInvokerTestHelper.CreatePreparedChannelAsync(client);
        var request = default(RpcEmptyRequest);
        var streams = default(RpcNoClientStreams);
        var codec = new DeadlineAdvancingPayloadCodec(timeProvider, TimeSpan.FromSeconds(5));

        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();

        var invocation = channel.InvokeOneWayAsync(
            method,
            in request,
            codec,
            in streams,
            metadata: null,
            cancellationToken: default).AsTask();
        var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "a deadline that elapses while a plain OneWay is serialized must fail the call");

        var frames = await ReadSentFramesAsync(transport, TimeSpan.FromMilliseconds(200));
        Ensure(frames.TrueForAll(static frame => frame.Header.Type is not ProtocolV2FrameType.Request),
            "an already expired plain OneWay must not be published at all");
        Ensure(frames.TrueForAll(static frame => frame.Header.Type is not ProtocolV2FrameType.Cancel),
            "no cancel may follow a Request that was never published");
    }

    [Test]
    public async Task TimedPlainOneWayShouldFailAtItsDeadlineWhileTheTransportIsStalled()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = CreateDeadlineTransport();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var method = MethodWithTimeout(RpcMethodKind.OneWay, methodId: 301, TimeSpan.FromSeconds(5));
        var channel = await ClientInvokerTestHelper.CreatePreparedChannelAsync(client);
        var request = default(RpcEmptyRequest);
        var streams = default(RpcNoClientStreams);

        // Drain everything ConnectAsync published, then stall the pump inside the transport write
        // so the Request can be queued but never flushed while the deadline elapses.
        await FlushStartupFramesAsync(client, transport);
        using var stall = new ManualResetEventSlim(initialState: false);
        var emissionEntered = StallNextEmission(transport, stall);

        try
        {
            var invocation = channel.InvokeOneWayAsync(
                method,
                in request,
                RpcEmptyRequestCodec.Instance,
                in streams,
                metadata: null,
                cancellationToken: default).AsTask();

            await emissionEntered.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!invocation.IsCompleted,
                "the caller must still be waiting for emission before the deadline elapses");

            timeProvider.Advance(TimeSpan.FromSeconds(5));
            var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
                "a timed plain OneWay must fail its caller at its own deadline even without a pending entry");

            // The Request was published before the deadline won, so the transport still owns it: the
            // local deadline stops the caller from blocking, it does not retract the frame.
            stall.Set();
            var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);
            Ensure(ReadTimeBudget(sent) == TimeSpan.FromSeconds(5),
                "the stalled Request must still carry the budget sampled when the frame was created");
        }
        finally
        {
            stall.Set();
            await client.StopAsync();
        }
    }

    [Test]
    public async Task TimedUnaryShouldNotPublishARequestAfterItsDeadlineWonDuringSerialization()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = CreateDeadlineTransport(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var method = MethodWithTimeout(RpcMethodKind.Unary, methodId: 302, TimeSpan.FromSeconds(5));
        var channel = await ClientInvokerTestHelper.CreatePreparedChannelAsync(client);
        var codec = new DeadlineAdvancingCodec(timeProvider, TimeSpan.FromSeconds(5));

        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();

        var requestValue = codec.Value;
        var invocation = channel.InvokeUnaryAsync(
            method,
            in requestValue,
            codec,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null,
            cancellationToken: default).AsTask();

        var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "a deadline that elapses while the request is still being serialized must fail the call");

        // The deadline owned the call before the frame could be published, so neither half of the
        // pair may reach the peer: a Request after its cancel would be dispatched by the server,
        // and a cancel for an unpublished Request is discarded by it.
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.Request,
                TimeSpan.FromMilliseconds(100)),
            "an expired request must never be published after its deadline won during serialization");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.Cancel,
                TimeSpan.FromMilliseconds(100)),
            "no cancel may be emitted for a request that was never published");
    }

    private static TestClientTransportFactory CreateDeadlineTransport(
        ProtocolV2Capabilities negotiatedCapabilities = ProtocolV2Capabilities.None,
        string? compressionProfile = null)
        => new(negotiatedCapabilities,
            contractManifest: [new(ManualClientContractFixture.ContractId, ManualClientContractFixture.AssemblyHash)],
            compressionProfile: compressionProfile,
            acceptManualContractBindings: true);

    private static RpcMethodDescriptor MethodWithTimeout(
        RpcMethodKind kind,
        long methodId,
        TimeSpan timeout)
        => new(
            ContractId: 1,
            MethodId: methodId,
            Kind: kind,
            HasResponsePayload: kind is not RpcMethodKind.OneWay,
            HasClientStreams: false,
            HasMethodTimeout: true,
            MethodTimeout: timeout);

    private static TimeSpan ReadTimeBudget(TestSentFrame sent)
        => TimeSpan.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(
            sent.Payload.AsSpan(ProtocolV2Constants.RequestPrefixBytes, sizeof(long))));

    private static Task<int> InvokeTimedUnaryAsync(IRpcContractChannel channel, RpcMethodDescriptor method)
    {
        var request = default(RpcEmptyRequest);
        return channel.InvokeUnaryAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null,
            cancellationToken: default).AsTask();
    }

    /// <summary>Reads every frame the client writes until the transport stays quiet for one period.</summary>
    private static async Task<List<TestSentFrame>> ReadSentFramesAsync(
        TestClientTransportFactory transport,
        TimeSpan quietPeriod)
    {
        var frames = new List<TestSentFrame>();
        while (await transport.Connection.TryReadNextSentFrameAsync(quietPeriod) is { } frame)
            frames.Add(frame);
        return frames;
    }

    private static async Task DrainSentFramesAsync(TestClientTransportFactory transport)
        => _ = await ReadSentFramesAsync(transport, TimeSpan.FromMilliseconds(20));

    private static async Task FlushStartupFramesAsync(
        SharpLinkClient client,
        TestClientTransportFactory transport)
    {
        await GetOnlyReadyConnection(client).Session.FlushSendQueueAsync();
        // Flush completes before the transport observer necessarily parses the startup frames.
        // Its FIFO Ping proves that the observer consumed all preceding handshake output.
        _ = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Ping)
            .WaitAsync(TimeSpan.FromSeconds(5));
        // Preparation also emitted the exact contract Bind after startup; no control frame may
        // be mistaken for the business head once the output-write barrier is armed.
        await DrainSentFramesAsync(transport);
    }

    private static Task StallNextEmission(
        TestClientTransportFactory transport,
        ManualResetEventSlim stall)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Connection.RunOnNextOutputBufferRequest(() =>
        {
            entered.TrySetResult();
            Ensure(stall.Wait(TimeSpan.FromSeconds(30)), "test did not release request emission");
        });
        return entered.Task;
    }

    private static ClientConnection GetOnlyReadyConnection(SharpLinkClient client)
    {
        var connections = (ClientConnection[])(typeof(SharpLinkClient).GetField(
                "_readyConnections",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(client) ?? throw new Exception("cannot find ready connection selection snapshot"));
        Ensure(connections.Length == 1, "expected exactly one ready connection");
        return connections[0];
    }

    private static async Task<SharpLinkException> CaptureSharpLinkExceptionAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
        throw new Exception("expected SharpLinkException");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    /// <summary>
    /// Serializes a payload only after letting the call's own deadline elapse, which is what a slow
    /// user codec does when it competes with the pending deadline scheduler.
    /// </summary>
    private sealed class DeadlineAdvancingCodec : IRpcCodec<int>
    {
        private readonly ManualTimeProvider _timeProvider;
        private readonly TimeSpan _advance;

        internal DeadlineAdvancingCodec(ManualTimeProvider timeProvider, TimeSpan advance)
        {
            _timeProvider = timeProvider;
            _advance = advance;
        }

        internal int Value => 7;

        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            buffer.Advance(sizeof(int));
            _timeProvider.Advance(_advance);
        }

        public int Deserialize(in ReadOnlySequence<byte> buffer)
            => BinaryPrimitives.ReadInt32LittleEndian(buffer.FirstSpan);
    }

    /// <summary>
    /// Writes the requested payload, but only after letting the call's own deadline elapse - what a
    /// slow user codec does while a Request is still being built.
    /// </summary>
    private sealed class DeadlineAdvancingPayloadCodec : IRpcCodec<RpcEmptyRequest>
    {
        private readonly ManualTimeProvider _timeProvider;
        private readonly TimeSpan _advance;
        private readonly int _payloadBytes;
        private readonly bool _runTimers;

        internal DeadlineAdvancingPayloadCodec(
            ManualTimeProvider timeProvider,
            TimeSpan advance,
            int payloadBytes = 0,
            bool runTimers = true)
        {
            _timeProvider = timeProvider;
            _advance = advance;
            _payloadBytes = payloadBytes;
            _runTimers = runTimers;
        }

        public void Serialize(in RpcEmptyRequest value, IBufferWriter<byte> buffer)
        {
            if (_payloadBytes > 0)
            {
                var span = buffer.GetSpan(_payloadBytes);
                span[.._payloadBytes].Clear();
                buffer.Advance(_payloadBytes);
            }

            if (_runTimers)
                _timeProvider.Advance(_advance);
            else
                _timeProvider.AdvanceWithoutRunningTimers(_advance);
        }

        public RpcEmptyRequest Deserialize(in ReadOnlySequence<byte> buffer)
            => default;
    }

    /// <summary>
    /// Registers the declared physical producer slot even when its token is already cancelled.
    /// No items are produced; the actual sink owns cancellation and enumerator disposal.
    /// </summary>
    private readonly struct SilentClientStreams(IRpcCodec<int> codec) : IRpcClientStreamWriter
    {
        public ValueTask WriteAsync(
            IRpcClientStreamSink sink,
            long requestId,
            CancellationToken cancellationToken)
            => new(sink.SendClientStreamAsync(requestId, 1, Wait(cancellationToken), codec, cancellationToken));

        private static async IAsyncEnumerable<int> Wait([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            yield break;
        }
    }

    /// <summary>Records whether the runtime ever started producing client stream items.</summary>
    private sealed class ProducerProbe
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Only signals that the producer ran, then waits for the test to release it. None of the calls
    /// these tests cover may start their producer, and waiting on the call's own token keeps an
    /// unexpected start from hanging the test run instead of failing the assertion.
    /// </summary>
    private readonly struct BlockingClientStreams(ProducerProbe probe, IRpcCodec<int> codec) : IRpcClientStreamWriter
    {
        public ValueTask WriteAsync(
            IRpcClientStreamSink sink,
            long requestId,
            CancellationToken cancellationToken)
        {
            probe.Started.TrySetResult();
            return new(sink.SendClientStreamAsync(requestId, 1, Wait(probe, cancellationToken), codec, cancellationToken));
        }

        private static async IAsyncEnumerable<int> Wait(ProducerProbe probe,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await probe.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield break;
        }
    }

    /// <summary>
    /// Waits for a named framework task to leave the supervisor's active set: a call that failed at
    /// its deadline must not leave its start task parked behind a stalled transport write.
    /// </summary>
    private static async Task<bool> WaitForFrameworkTaskToFinishAsync(
        SharpLinkClient client,
        string operation)
    {
        var supervisor = (FrameworkTaskSupervisor)(typeof(SharpLinkClient).GetField(
                "_frameworkTasks",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(client) ?? throw new Exception("cannot find the framework task supervisor"));
        for (var attempt = 0; attempt < 250; attempt++)
        {
            var active = false;
            foreach (var entry in supervisor.CaptureSnapshot().Operations)
            {
                if (entry.Operation == operation)
                {
                    active = true;
                    break;
                }
            }

            if (!active)
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        return false;
    }

    /// <summary>
    /// Reports that it cannot compress, but only after letting the call's deadline elapse - what a
    /// slow user-provided compression provider does while a frame is being prepared.
    /// </summary>
    private sealed class DeadlineAdvancingCompressionProvider : ISharpLinkCompressionProvider
    {
        private readonly ManualTimeProvider _timeProvider;
        private readonly TimeSpan _advance;

        internal DeadlineAdvancingCompressionProvider(ManualTimeProvider timeProvider, TimeSpan advance)
        {
            _timeProvider = timeProvider;
            _advance = advance;
        }

        public string WireProfile => "test.deadline/v1";

        public bool TryCompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            _timeProvider.Advance(_advance);
            return false;
        }

        public void Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("the deadline provider never compresses");
    }
}
