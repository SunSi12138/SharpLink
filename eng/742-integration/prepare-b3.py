#!/usr/bin/env python3
"""Build the B3 integration candidate in a disposable checkout, without publishing it."""
from pathlib import Path
import hashlib
import json
import subprocess

ROOT = Path(__file__).resolve().parents[2]
RUNTIME = ROOT / "src/SharpLink.Runtime"
INPUTS = {
    "ReadyWriterCoordinator.cs": "2e122226e03edbbcc33904535493347b3b5f460d",
    "ReadyWriterCoordinator.Admission.cs": "7138347d84a4ec5e4d70d3264cd42ae741115189",
    "ReadyWriterCoordinator.Cancellation.cs": "e5cc51f30e01b5475fdf22fe391966b72283c826",
    "ReadyWriterCoordinator.Lifecycle.cs": "0f53f71689ab9ed0216ff5b546a2c330077e9dbc",
    "ReadyWriterCoordinator.Wire.cs": "2a955835184ea1eea33c5c2aa8696172bdd37a10",
}

def once(text, old, new):
    if text.count(old) != 1:
        raise RuntimeError(f"Expected one anchor, got {text.count(old)}: {old[:140]}")
    return text.replace(old, new, 1)

def edit(path, old, new):
    path.write_text(once(path.read_text(), old, new))

def main():
    for name, expected in INPUTS.items():
        data = (ROOT / "test/SharpLink.Benchmarks" / name).read_bytes()
        actual = hashlib.sha1(f"blob {len(data)}\0".encode() + data).hexdigest()
        if actual != expected:
            raise RuntimeError(f"Research source drift: {name}: {actual}")
    subprocess.run(["python3", "eng/742-integration/prepare-core.py"], cwd=ROOT, check=True)
    subprocess.run(["python3", "eng/prepare-ready-writer.py", "--apply"], cwd=ROOT, check=True)
    for name in INPUTS:
        text = (ROOT / "test/SharpLink.Benchmarks" / name).read_text()
        text = text.replace("#if SHARPLINK_READY_WRITER_EXPERIMENT\n", "").replace("#endif", "")
        text = text.replace("namespace SharpLink.Benchmarks;", "namespace SharpLink.Runtime;")
        text = text.replace("ReadyWriterCoordinator", "WriterOwnedStreamCoordinator")
        (RUNTIME / name.replace("ReadyWriterCoordinator", "WriterOwnedStreamCoordinator")).write_text(text)
    for name in ["WriterOwnedStreamCoordinator.RpcAdapter.cs", "RpcSession.WriterOwnedStreams.cs"]:
        (RUNTIME / name).write_bytes((ROOT / "eng/742-integration" / (name + ".in")).read_bytes())

    main_path = RUNTIME / "WriterOwnedStreamCoordinator.cs"
    text = main_path.read_text()
    text = once(text, "        internal int Taken, Released;", """        internal int Taken, Released;
        internal int RpcScopeUsers;
        internal TaskCompletionSource? RpcDrained;
        internal bool RpcWirePending, RpcAbortRefunded;
        internal long RpcPendingWireCredit;""")
    text = text.replace("_context.Protocol.MaxFramePayloadBytes", "(_rpcMode ? _rpcMaxFramePayloadBytes : _context.Protocol.MaxFramePayloadBytes)")
    text = once(text, "                _context.Buffers.Return(packet);\n\n", """                try { _context.Buffers.Return(packet); }
                finally { ReleaseRpcBudget(creditBytes); }

""")
    text = once(text, "                Volatile.Write(ref stream!.ProducerBusy, 0);", """                Volatile.Write(ref stream!.ProducerBusy, 0);
                TryCompleteRpcDrain(stream);""")
    text = once(text, "            Volatile.Read(ref _retirementRequested) != 0 || Volatile.Read(ref _notificationsPending) != 0 ||", """            Volatile.Read(ref _rpcWirePending) != 0 ||
            Volatile.Read(ref _retirementRequested) != 0 || Volatile.Read(ref _notificationsPending) != 0 ||""")
    text = once(text, "    private void DrainNotifications()\n    {", """    private void DrainNotifications()
    {
        if (_rpcMode) DrainRpcWireUpdates();""")
    text = once(text, "                frame = new ReadyStreamFrame(packet, stream.Index, creditBytes, stream.ReleaseTarget);", """                frame = new ReadyStreamFrame(packet, stream.Index, creditBytes, stream.ReleaseTarget,
                    ForceFlush: stream.RpcDrained is not null && stream.Frames.Count == 0);""")
    text = once(text, "            stream.Released++; _releases++;", """            stream.Released++; _releases++;
            ReleaseRpcBudget(creditBytes);
            TryCompleteRpcDrain(stream);""")
    main_path.write_text(text)

    lifecycle = RUNTIME / "WriterOwnedStreamCoordinator.Lifecycle.cs"
    text = lifecycle.read_text()
    text = once(text, "    private readonly Dictionary<StreamIdentity, Stream> _identities = new();", "    private readonly System.Collections.Concurrent.ConcurrentDictionary<StreamIdentity, Stream> _identities = new();")
    text = once(text, "            _identities.Add(identity, stream);", """            stream.RpcScopeUsers = _rpcMode ? 1 : 0;
            stream.RpcDrained = null;
            stream.RpcWirePending = false;
            stream.RpcPendingWireCredit = 0;
            stream.RpcAbortRefunded = false;
            if (!_identities.TryAdd(identity, stream)) throw new InvalidOperationException("Duplicate stream publication.");""")
    text = once(text, "            if (!_identities.Remove(new StreamIdentity(stream.RequestId, stream.StreamId)))", "            if (!_identities.TryRemove(new StreamIdentity(stream.RequestId, stream.StreamId), out _))")
    text = once(text, "            if (stream.CleanupFailed || stream.ProducerBusy != 0 || stream.HasHeldSpace || stream.NotificationPending ||", """            if (stream.RpcScopeUsers != 0 || stream.RpcWirePending || stream.RpcDrained is not null ||
                stream.CleanupFailed || stream.ProducerBusy != 0 || stream.HasHeldSpace || stream.NotificationPending ||""")
    lifecycle.write_text(text)
    edit(main_path, "            else _identities.Add(new StreamIdentity(i + 1, 1), stream);", "            else if (!_identities.TryAdd(new StreamIdentity(i + 1, 1), stream)) throw new InvalidOperationException(\"Duplicate fixed stream.\");")

    cancel = RUNTIME / "WriterOwnedStreamCoordinator.Cancellation.cs"
    text = cancel.read_text()
    text = once(text, "            stream.AbortRequested = error;", """            stream.AbortRequested = error;
            FailRpcDrainLocked(stream, error);""")
    text = once(text, "            CloseStreamOnWriter(handle);", """            RefundRpcAbort(stream);
            CloseStreamOnWriter(handle);""")
    text = once(text, "            if (_reference is not null)\n            {", """            try { ReleaseRpcBudget(prepared.CreditBytes); }
            catch (Exception error) { record(error); }
            if (_reference is not null)
            {""")
    text = once(text, "        _notifications.Writer.TryComplete(error);", """        _notifications.Writer.TryComplete(error);
        _rpcWireQueue?.Writer.TryComplete(error);""")
    text = once(text, "                // Idempotent with early cancellation. Writer-owned frames are no longer", """                FailRpcDrainLocked(stream, error);
                // Idempotent with early cancellation. Writer-owned frames are no longer""")
    cancel.write_text(text)

    hook = RUNTIME / "RpcSession.ReadyWriter.cs"
    text = hook.read_text()
    text = once(text, "int CreditBytes, IReadyFrameCompletion Completion);", "int CreditBytes, IReadyFrameCompletion Completion, bool ForceFlush = false);")
    text = once(text, "    bool HasWork { get; }", "    bool HasWork { get; }\n    bool MustFlushPending => true;")
    hook.write_text(text)
    edit(RUNTIME / "OwnedFrame.cs", "        : this(ready.Packet, false, null, false)", "        : this(ready.Packet, ready.ForceFlush, null, false)")
    edit(RUNTIME / "RpcSession.SendPump.cs", "                    if (Volatile.Read(ref _readyWriterExperiment) is null &&", "                    if (!(Volatile.Read(ref _readyWriterExperiment)?.MustFlushPending ?? false) &&")

    flow = RUNTIME / "StreamFlowController.cs"
    edit(flow, "    internal long SendConnectionCredit", """    internal int WriterStreamWindow => _streamWindow;
    internal int WriterConnectionWindow => _connectionWindow;
    internal int WriterStreamLimit => _maxConcurrentStreams;

    internal long SendConnectionCredit""")
    session = RUNTIME / "RpcSession.cs"
    text = session.read_text()
    text = once(text, "        controller.ApplyWindowUpdate(requestId, update.StreamId, checked((int)update.Credit));", """        if (Volatile.Read(ref _writerOwnedStreams) is { } writerOwner)
            writerOwner.PostRpcWindowUpdate(requestId, update.StreamId, checked((int)update.Credit));
        else
            controller.ApplyWindowUpdate(requestId, update.StreamId, checked((int)update.Credit));""")
    text = once(text, "        => Volatile.Read(ref _protocolState).FlowController?.CompleteSendStream(requestId, streamId, exception);", """    {
        if (Volatile.Read(ref _writerOwnedStreams) is { } writerOwner)
            writerOwner.CompleteRpcStream(requestId, streamId, exception);
        else
            Volatile.Read(ref _protocolState).FlowController?.CompleteSendStream(requestId, streamId, exception);
    }""")
    text = once(text, "        => Volatile.Read(ref _protocolState).FlowController?.AbortSendStreams(requestId, exception);", """    {
        if (Volatile.Read(ref _writerOwnedStreams) is { } writerOwner)
            writerOwner.AbortRpcRequest(requestId, exception);
        else
            Volatile.Read(ref _protocolState).FlowController?.AbortSendStreams(requestId, exception);
    }""")
    session.write_text(text)

    server = RUNTIME / "RpcSession.GeneratedServerBridge.cs"
    text = server.read_text()
    text = once(text, "        var sendCreditLease = default(StreamFlowController.ResolvedSendCreditLease);\n        try\n        {", """        var sendCreditLease = default(StreamFlowController.ResolvedSendCreditLease);
        try
        {
            sendCreditLease = await OpenWriterOwnedStreamAsync(requestId, streamId,
                lifetimeCancellation.Token, deadline, deadlineTimeProvider).ConfigureAwait(false);""")
    text = once(text, "            ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);\n            SendGeneratedStreamComplete", """            await DrainWriterOwnedStreamAsync(sendCreditLease, lifetimeCancellation.Token,
                deadline, deadlineTimeProvider).ConfigureAwait(false);
            ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
            SendGeneratedStreamComplete""")
    text = once(text, "        finally\n        {\n            TryCancelGeneratedLifetime(lifetimeCancellation);", """        catch (Exception error)
        {
            AbortWriterOwnedStream(sendCreditLease, error);
            throw;
        }
        finally
        {
            ReleaseWriterOwnedStream(sendCreditLease);
            TryCancelGeneratedLifetime(lifetimeCancellation);""")
    text = once(text, "    {\n        if (exactSizeCodec is not null &&\n            exactSizeCodec.TryGetEncodedSize", """    {
        if (creditLease.State is ReadyRpcStreamLease)
            return SendWriterOwnedStreamItemAsync(creditLease, item, codec, exactSizeCodec,
                cancellationToken, deadline, deadlineTimeProvider);
        if (exactSizeCodec is not null &&
            exactSizeCodec.TryGetEncodedSize""")
    server.write_text(text)

    client_publication = RUNTIME / "RpcSession.ClientStreamPublication.cs"
    edit(client_publication, "        ThrowIfClientStreamPublicationRejected(deadline, timeProvider, terminalToken);\n\n        if (exactSizeCodec is not null &&", """        ThrowIfClientStreamPublicationRejected(deadline, timeProvider, terminalToken);

        if (creditLease.State is ReadyRpcStreamLease)
            return SendWriterOwnedStreamItemAsync(creditLease, item, codec, exactSizeCodec,
                terminalToken, deadline, timeProvider);
        if (exactSizeCodec is not null &&""")

    client = ROOT / "src/SharpLink.Client/ClientConnection.cs"
    text = client.read_text()
    start = text.index("    public async Task SendClientStreamAsync<T>(")
    end = text.index("    public ValueTask OnConsumerAbandonedAsync(", start)
    method = text[start:end]
    method = once(method, "        try\n        {", """        var sendCreditLease = default(StreamFlowController.ResolvedSendCreditLease);
        try
        {
            sendCreditLease = await Session.OpenWriterOwnedStreamAsync(requestId, streamId,
                cancellationToken, deadline, _timeProvider).ConfigureAwait(false);""")
    method = once(method, "            var sendCreditLease = default(StreamFlowController.ResolvedSendCreditLease);\n", "")
    method = once(method, "            cancellationToken.ThrowIfCancellationRequested();\n            if (!PendingCalls.TryAcceptProducerProgress(requestId))", """            await Session.DrainWriterOwnedStreamAsync(sendCreditLease, cancellationToken,
                deadline, _timeProvider).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!PendingCalls.TryAcceptProducerProgress(requestId))""")
    method = once(method, "        catch (Exception exception)\n        {", """        catch (Exception exception)
        {
            Session.AbortWriterOwnedStream(sendCreditLease, exception);""")
    method = once(method, "            throw;\n        }\n    }\n", """            throw;
        }
        finally
        {
            Session.ReleaseWriterOwnedStream(sendCreditLease);
        }
    }
""")
    client.write_text(text[:start] + method + text[end:])

    output = ROOT / "artifacts/742-integration"
    subprocess.run(["git", "add", "-N", "src/SharpLink.Runtime"], cwd=ROOT, check=True)
    (output / "b3.patch").write_bytes(subprocess.check_output(["git", "diff", "--binary", "--", "src"], cwd=ROOT))
    changed = subprocess.check_output(["git", "diff", "--name-only", "--", "src"], cwd=ROOT, text=True).splitlines()
    manifest = {
        "checkout": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
        "base": "e834d3c28c87ad496989af925515cf21babd308d",
        "activation": "SHARPLINK_WRITER_OWNED_STREAMS=1; not a production-acceptance declaration",
        "sha256": {name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest() for name in changed},
    }
    (output / "b3-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(json.dumps(manifest, indent=2))

if __name__ == "__main__":
    main()
