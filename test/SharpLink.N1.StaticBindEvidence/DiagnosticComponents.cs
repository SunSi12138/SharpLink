using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.N1.StaticBindEvidence;
internal static partial class Program
{
    private static async Task DiagnosticComponentsAsync()
    {
        const string secret = "DO-NOT-LOG-payload-or-credential";
        using var logs = new FailureDiagnosticLoggerFactory();
        var logger = logs.CreateLogger("N1.fixture");
        logger.Log(LogLevel.Error, new EventId(775), secret, new InvalidOperationException(secret),
            static (state, error) => throw new InvalidOperationException("Formatter must never execute."));
        var first = JsonSerializer.Serialize(logs.Capture());
        Check(!first.Contains(secret, StringComparison.Ordinal) && first.Contains(nameof(InvalidOperationException), StringComparison.Ordinal),
            "diagnostic-fault-type-retained-without-message-or-state");
        for (var i = 0; i < 99; i++) logger.LogWarning(secret);
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(logs.Capture())))
        {
            var root = document.RootElement; var records = root.GetProperty("retained");
            Check(root.GetProperty("total").GetInt32() == 100 && records.GetArrayLength() == 64 && root.GetProperty("dropped").GetInt32() == 36,
                "diagnostic-fault-history-bounded64");
            Check(records[0].GetProperty("Sequence").GetInt32() == 1 && records[63].GetProperty("Sequence").GetInt32() == 100,
                "diagnostic-history-retains-first-and-latest");
        }
        var gate = new TransportFrameGate((ProtocolV2FrameType)255, false);
        var payload = new byte[16 + Encoding.UTF8.GetByteCount(secret)];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, (1UL << 32) | 1);
        Encoding.UTF8.GetBytes(secret).CopyTo(payload, 16);
        _ = gate.Observe("actual-observer-component", true, new(ProtocolV2FrameType.Request, ProtocolV2FrameFlags.None, 1), new ReadOnlySequence<byte>(payload));
        var frames = FailureDiagnostics.CaptureFrames(gate);
        Check(frames.Length == 1 && frames[0].BindingToken == (1UL << 32 | 1) && frames[0].PayloadBytes == payload.Length,
            "diagnostic-frame-keeps-control-token-and-size");
        Check(!JsonSerializer.Serialize(frames).Contains(secret, StringComparison.Ordinal), "diagnostic-frame-excludes-business-bytes");
        // A one-byte managed Pipe is a pure transport-decorator control, not an RPC Session/frame.
        var pipe = new System.IO.Pipelines.Pipe(new System.IO.Pipelines.PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1, useSynchronizationContext: false));
        var flushGate = new TransportFrameGate((ProtocolV2FrameType)255, false);
        var decorated = new GatedPipeWriter(pipe.Writer, flushGate, "managed-pipe-component");
        Exception? pipePrimary = null;
        try
        {
            decorated.GetSpan(1)[0] = 1; decorated.Advance(1);
            var flush = decorated.FlushAsync().AsTask();
            using (var pending = JsonDocument.Parse(JsonSerializer.Serialize(flushGate.CaptureFlushes())))
                Check(pending.RootElement.GetProperty("sessions")[0].GetProperty("Phase").GetString() == "AwaitInnerFlush" && !flush.IsCompleted,
                    "diagnostic-decorator-observes-actual-pending-inner-flush");
            var read = await pipe.Reader.ReadAsync().AsTask().GuardedWaitAsync(Guard).ConfigureAwait(false);
            pipe.Reader.AdvanceTo(read.Buffer.End);
            _ = await flush.GuardedWaitAsync(Guard).ConfigureAwait(false);
            using (var done = JsonDocument.Parse(JsonSerializer.Serialize(flushGate.CaptureFlushes())))
                Check(done.RootElement.GetProperty("sessions")[0].GetProperty("Phase").GetString() == "Returned" && done.RootElement.GetProperty("sessions")[0].GetProperty("sourceStatus").GetInt32() == (int)TaskStatus.RanToCompletion,
                    "diagnostic-decorator-forwards-actual-flush-result");
        }
        catch (Exception exception) { pipePrimary = exception; }
        try { await pipe.Reader.CompleteAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { pipePrimary = pipePrimary is null ? cleanup : new AggregateException(pipePrimary, cleanup); }
        try { await decorated.CompleteAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { pipePrimary = pipePrimary is null ? cleanup : new AggregateException(pipePrimary, cleanup); }
        if (pipePrimary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(pipePrimary).Throw();
        var client = SharpClientBuilder.Create().DisableRequestTimeout().UseTcp("127.0.0.1", 1).Build();
        Exception? primary = null;
        try
        {
            var concrete = (SharpLinkClient)client; concrete.EnableContractBindingTraceForDiagnostics();
            var proxy = client.CreateProxy<IProbe>();
            var trace = concrete.CaptureContractBindingTrace();
            Check(trace.ReadySessions.Length == 0 && trace.ClosedSessions.Length == 0 && trace.Sites.Length == 1 && trace.Sites[0].Targets.Length == 0,
                "diagnostic-unconnected-site-does-not-invent-session-or-binding");
            GC.KeepAlive(proxy);
        }
        catch (Exception exception) { primary = exception; }
        try { await client.StopAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { primary = primary is null ? cleanup : new AggregateException(primary, cleanup); }
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        Check(((SharpLinkClient)client).CaptureContractBindingTrace().StopPhase == "Stopped", "diagnostic-stop-phase-tracks-actual-no-rpc-cleanup");
    }
    private static async Task ClusterStopComponentsAsync()
    {
        var closed = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "control lifetime ended");
        Check(SharpLinkClient.IsExpectedContractRefreshStopExit(true, closed) && !SharpLinkClient.IsExpectedContractRefreshStopExit(false, closed),
            "refresh-connectionclosed-is-expected-only-after-explicit-stop");
        Check(!SharpLinkClient.IsExpectedContractRefreshStopExit(true, new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "original budget")) &&
            !SharpLinkClient.IsExpectedContractRefreshStopExit(true, new InvalidOperationException("unexpected")),
            "refresh-stop-filter-retains-deadline-and-unexpected-source-faults");
        var endpoints = new[] { new SharpLinkEndpoint { Id = "A", Address = new SharpLinkTcpAddress("127.0.0.1", 1) },
            new SharpLinkEndpoint { Id = "B", Address = new SharpLinkTcpAddress("127.0.0.1", 2) } };
        var client = SharpClientBuilder.Create().DisableRequestTimeout().UseEndpoints(endpoints,
            endpoint => new SocketClientTransportFactory(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, ((SharpLinkTcpAddress)endpoint.Address).Port))).Build();
        Exception? primary = null; RpcProxy<IProbe>? proxy = null;
        try
        {
            proxy = client.CreateProxy<IProbe>();
            Check(((SharpLinkClient)client).CaptureContractBindingDiagnostics().DemandSites == 1, "cluster-stop-control-holds-real-unconnected-site");
            var empty = await ThrowsAsync<SharpLinkException>(() => proxy.PrepareAsync(TimeSpan.FromMilliseconds(25)).AsTask(), "cluster-stop-empty-prepare-before-stop").ConfigureAwait(false);
            Check(empty.Code == SharpLinkErrorCode.DeadlineExceeded, "cluster-stop-control-remains-pure-no-connect");
        }
        catch (Exception exception) { primary = exception; }
        try { await client.StopAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { primary = primary is null ? cleanup : new AggregateException(primary, cleanup); }
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        var ledger = ((SharpLinkClient)client).CaptureContractBindingDiagnostics();
        Check(ledger.DemandSites == 0 && ledger.PrepareWaiters == 0 && ledger.Bindings == 0 && ledger.Operations == 0,
            "cluster-stop-returns-original-site-and-waiter-ledger-without-gc");
        GC.KeepAlive(proxy);
    }

}
