using System.Buffers.Binary;
using System.Collections.Frozen;
using System.IO.Pipelines;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public partial class SharpLinkServerInvocationTests
{
    [Test]
    public async Task HeldFactsServerStreamingCompletionReplacement()
    {
        await HeldShapeTerminalShouldRetainFactsUntilPhysicalJoin(RpcMethodKind.ServerStreaming, false, false);
    }

    [Test]
    public async Task HeldFactsServerStreamingCompletionRemoval()
    {
        await HeldShapeTerminalShouldRetainFactsUntilPhysicalJoin(RpcMethodKind.ServerStreaming, false, true);
    }

    [Test]
    public async Task HeldFactsServerStreamingFailureReplacement()
    {
        await HeldShapeTerminalShouldRetainFactsUntilPhysicalJoin(RpcMethodKind.ServerStreaming, true, false);
    }

    [Test]
    public async Task HeldFactsServerStreamingFailureRemoval()
    {
        await HeldShapeTerminalShouldRetainFactsUntilPhysicalJoin(RpcMethodKind.ServerStreaming, true, true);
    }

    [Test]
    public async Task HeldFactsDuplexStreamingCompletionReplacement()
    {
        await HeldShapeTerminalShouldRetainFactsUntilPhysicalJoin(RpcMethodKind.DuplexStreaming, false, false);
    }

    [Test]
    public async Task HeldFactsDuplexStreamingCompletionRemoval()
    {
        await HeldShapeTerminalShouldRetainFactsUntilPhysicalJoin(RpcMethodKind.DuplexStreaming, false, true);
    }

    [Test]
    public async Task HeldFactsDuplexStreamingFailureReplacement()
    {
        await HeldShapeTerminalShouldRetainFactsUntilPhysicalJoin(RpcMethodKind.DuplexStreaming, true, false);
    }

    [Test]
    public async Task HeldFactsDuplexStreamingFailureRemoval()
    {
        await HeldShapeTerminalShouldRetainFactsUntilPhysicalJoin(RpcMethodKind.DuplexStreaming, true, true);
    }

    private async Task HeldShapeTerminalShouldRetainFactsUntilPhysicalJoin(RpcMethodKind kind, bool fail, bool remove)
    {
        var stream = new HeldTerminalFactStream(fail);
        var facts = new RpcMethodFacts(8, 1, kind, kind == RpcMethodKind.DuplexStreaming ? 1 : 0,
            true, true, hasMethodTimeout: true, methodTimeout: TimeSpan.FromSeconds(90), isIdempotent: true);
        var stub = new CompleteFactsStub(facts) { PumpStream = stream };
        var replacement = new CompleteFactsStub(new RpcMethodFacts(8, 1, kind,
            kind == RpcMethodKind.DuplexStreaming ? 1 : 0, true, true,
            hasMethodTimeout: true, methodTimeout: TimeSpan.FromSeconds(120)));
        var mapper = new IdentityFactsMapper();
        var output = new Pipe();
        var harness = new ServerDispatchHarness(stub, output.Writer, 65536,
            builder => builder.UseExceptionMapper(mapper), useServerBridge: true);
        using var originalServices = harness.AcquireServices();
        Task? dispatch = null;
        Exception? primary = null;
        try
        {
            // This declares the normal stream request shape; no Cancel operation is issued.
            dispatch = harness.Dispatch(7991, ProtocolV2FrameFlags.Cancellable).AsTask();
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var owner = CaptureFactsCallState(harness.Connection, 7991);
            Ensure(!dispatch.IsCompleted && stub.Resolutions == 1 && stub.Invocations == 1 && stub.OriginalFactsForwarded,
                "the actual outbound pump is suspended with its original facts and physical owner");
            var services = new Dictionary<long, ServiceRegistration>();
            if (!remove)
                services.Add(8, ServiceRegistration.CreateSingleton(typeof(ThrowingService), replacement,
                    new ThrowingService(), ownsService: false));
            harness.PublishServices(services.ToFrozenDictionary());
            EnsureSameFactsCallState(owner, CaptureFactsCallState(harness.Connection, 7991));
            stream.ReleaseMoveNext.TrySetResult();
            await stream.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!dispatch.IsCompleted && stream.Disposals == 1,
                "completion or failure cannot finish the request while actual enumerator disposal is held");
            Ensure(harness.GlobalActiveCalls == 1 && harness.Connection.ActiveCalls == 1,
                "both request permits remain held until physical stream disposal joins");
            EnsureSameFactsCallState(owner, CaptureFactsCallState(harness.Connection, 7991));
            Ensure(stub.Resolutions == 1 && replacement.Resolutions == (remove ? 0 : 1) && replacement.Invocations == 0,
                "registry publication never substitutes facts or invocation in the held stream");
            stream.ReleaseDispose.TrySetResult();
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(stream.Disposals == 1 && stub.Invocations == 1 && stub.Resolutions == 1 &&
                   stub.OriginalFactsForwarded && replacement.Invocations == 0 &&
                   replacement.Resolutions == (remove ? 0 : 1),
                "the terminal stream releases exactly once without resolving through either registry generation");
            if (fail)
            {
                var context = mapper.Context ?? throw new InvalidOperationException("Missing asynchronous failure mapping.");
                Ensure(mapper.Calls == 1 && ReferenceEquals(mapper.Exception, stream.Failure) &&
                       context.RequestId == 7991 && ReferenceEquals(context.MethodFacts, facts) &&
                       context.Method == facts.ToDescriptor(),
                    "asynchronous failure retains the exact original exception and full method facts after registry change");
            }
            else
                Ensure(mapper.Calls == 0 && mapper.Context is null, "normal asynchronous completion never falls back to error mapping");
            EnsureFactsDispatchDrained(harness);
            await harness.Session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                var remaining = read.Buffer;
                var terminals = 0;
                var acknowledgements = 0;
                var accepted = 0;
                while (ProtocolV2FrameParser.TryReadFrame(ref remaining, harness.Session.RuntimeContext.Protocol,
                           out var header, out var payload))
                {
                    if (header.RequestId != 7991) continue;
                    if (header.Type == ProtocolV2FrameType.RequestAccepted)
                    {
                        accepted++;
                        Ensure(kind == RpcMethodKind.DuplexStreaming && terminals == 0 && acknowledgements == 0 &&
                               header.Flags == ProtocolV2FrameFlags.None && payload.IsEmpty,
                            "duplex publishes its empty client-route acceptance before any terminal");
                        continue;
                    }
                    if (header.Type == ProtocolV2FrameType.Response)
                    {
                        acknowledgements++;
                        Ensure(terminals == 1 && header.Flags == ProtocolV2FrameFlags.None && payload.IsEmpty,
                            "the no-return dispatch acknowledgement follows the stream terminal and carries no payload or error");
                        continue;
                    }
                    Ensure(header.Type == ProtocolV2FrameType.StreamComplete,
                        $"unexpected request frame {header.Type}; only declared acceptance, terminal and acknowledgement are valid");
                    terminals++;
                    Ensure(BinaryPrimitives.ReadUInt16LittleEndian(payload.ToArray()) == 0,
                        "the terminal belongs to the original outbound stream");
                    Ensure(header.Flags == (fail ? ProtocolV2FrameFlags.Error : ProtocolV2FrameFlags.None),
                        "the unique terminal has the expected success or error status");
                    if (fail)
                    {
                        var error = ProtocolV2PayloadCodec.ReadError(payload.Slice(sizeof(ushort)), header.Flags,
                            harness.Session.RuntimeContext.Protocol.MaxErrorMessageBytes);
                        Ensure(error.Code == SharpLinkErrorCode.Internal, "wire terminal preserves the mapped error code");
                    }
                }
                Ensure(remaining.IsEmpty && terminals == 1 && acknowledgements == 1 &&
                       accepted == (kind == RpcMethodKind.DuplexStreaming ? 1 : 0),
                    "the completed request emits exactly one stream terminal and one normal dispatch acknowledgement");
            }
            finally { output.Reader.AdvanceTo(read.Buffer.End); }
        }
        catch (Exception error) { primary = error; }
        finally
        {
            stream.ReleaseMoveNext.TrySetResult();
            stream.ReleaseDispose.TrySetResult();
            await ServerDispatchContractFixture.CleanupOwnedAsync(primary,
                () => new ValueTask((dispatch ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5))),
                () => { harness.PublishServices(originalServices.Services); return ValueTask.CompletedTask; },
                () => new ValueTask(harness.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))));
        }
    }

    private sealed class HeldTerminalFactStream(bool fail) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseMoveNext { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal InvalidOperationException Failure { get; } = new("held asynchronous stream failure");
        internal int Disposals;
        public int Current => throw new InvalidOperationException("held stream produces no items");
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public async ValueTask<bool> MoveNextAsync()
        {
            Entered.TrySetResult();
            await ReleaseMoveNext.Task;
            if (fail) throw Failure;
            return false;
        }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals);
            DisposeEntered.TrySetResult();
            await ReleaseDispose.Task;
        }
    }
}

