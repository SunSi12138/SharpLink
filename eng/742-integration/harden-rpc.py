#!/usr/bin/env python3
"""Apply reviewed integration corrections after prepare-b3, then seal its exact manifest."""
from pathlib import Path
import hashlib
import json
import subprocess

ROOT = Path(__file__).resolve().parents[2]
R = ROOT / "src/SharpLink.Runtime"

def once(text, old, new):
    if text.count(old) != 1:
        raise RuntimeError(f"Expected one anchor, got {text.count(old)}: {old[:120]}")
    return text.replace(old, new, 1)

def edit(name, old, new):
    path = R / name
    path.write_text(once(path.read_text(), old, new))

def main():
    edit("WriterOwnedStreamCoordinator.RpcAdapter.cs",
         "    public bool MustFlushPending => Volatile.Read(ref _rpcDrainPending) != 0 || _blocked;",
         "    public bool MustFlushPending => Volatile.Read(ref _rpcDrainPending) != 0 || (_blocked && _ready.Count != 0);")
    edit("WriterOwnedStreamCoordinator.RpcAdapter.cs",
         "    internal Task DrainRpcStreamAsync(StreamHandle handle)\n    {\n        var stream = _streams[handle.Slot];",
         """    internal Task DrainRpcStreamAsync(StreamHandle handle)
        => OnWriterAsync(() => BeginRpcDrainOnWriter(handle)).Unwrap();

    // Taken and Released are writer-owned. Installing the fence on the producer
    // could miss the last release between observing its count and publishing a TCS.
    // A cold owner command removes that window without a new per-DATA lock.
    private Task BeginRpcDrainOnWriter(StreamHandle handle)
    {
        if (!ReferenceEquals(handle.Owner, this) || (uint)handle.Slot >= (uint)_streams.Length)
            throw new InvalidOperationException("Foreign RPC drain handle.");
        var stream = _streams[handle.Slot];""")
    edit("WriterOwnedStreamCoordinator.cs", "                TryCompleteRpcDrain(stream);\n", "")
    edit("RpcSession.WriterOwnedStreams.cs", "            Interlocked.Increment(ref WriterOwnedStreamItemsSubmitted);\n", "")
    edit("WriterOwnedStreamCoordinator.Cancellation.cs",
         "        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;",
         """        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        if (_rpcMode) Interlocked.Add(ref RpcSession.WriterOwnedStreamItemsSubmitted, _creditDebits);""")
    edit("WriterOwnedStreamCoordinator.Cancellation.cs",
         "                // A blocked transport cannot service owner commands.",
         """                FailRpcDrainLocked(stream, cancellation);
                // A blocked transport cannot service owner commands.""")
    edit("RpcSession.ReadyWriter.cs",
         "    internal void SignalReadyWriterExperiment() => GetOrCreatePump().SignalReadyWriterExperiment();",
         "    internal void SignalReadyWriterExperiment() => Volatile.Read(ref _pump)?.SignalReadyWriterExperiment();")

    edit("WriterOwnedStreamCoordinator.cs", "        internal int RpcScopeUsers;",
         "        internal int RpcScopeUsers;\n        internal CancellationToken RpcCallToken;")
    edit("WriterOwnedStreamCoordinator.Lifecycle.cs", "            stream.RpcScopeUsers = _rpcMode ? 1 : 0;",
         "            stream.RpcScopeUsers = _rpcMode ? 1 : 0;\n            stream.RpcCallToken = default;")
    edit("WriterOwnedStreamCoordinator.RpcAdapter.cs", "    internal void ReleaseRpcScope(StreamHandle handle)", """    internal void BindRpcCancellation(StreamHandle handle, CancellationToken token)
    {
        if (!ReferenceEquals(handle.Owner, this) || (uint)handle.Slot >= (uint)_streams.Length)
            throw new InvalidOperationException("Foreign RPC cancellation handle.");
        var stream = _streams[handle.Slot];
        lock (stream.Gate)
        {
            ValidateHandle(handle, stream);
            stream.RpcCallToken = token;
        }
    }

    internal void ReleaseRpcScope(StreamHandle handle)""")
    edit("RpcSession.WriterOwnedStreams.cs", "            _callToken = callToken;",
         "            _callToken = callToken;\n            source.BindRpcCancellation(handle, callToken);")
    path = R / "WriterOwnedStreamCoordinator.cs"
    text = path.read_text()
    anchor = "                ValidateHandle(handle, stream);"
    if text.count(anchor) != 2:
        raise RuntimeError("Changed producer publication sites")
    text = text.replace(anchor, anchor + "\n                if (_rpcMode) stream.RpcCallToken.ThrowIfCancellationRequested();")
    text = once(text, "                _selectionChecks++;", """                if (_rpcMode && stream.RpcCallToken.IsCancellationRequested)
                {
                    stream.AbortRequested ??= new OperationCanceledException(stream.RpcCallToken);
                    Interlocked.Exchange(ref _streamAbortRequested, 1);
                    _session.SignalReadyWriterExperiment();
                    return false;
                }
                _selectionChecks++;""")
    path.write_text(text)

    # A diagnostic snapshot is not an input to any runtime decision. It runs only
    # in explicitly instrumented failure repros and never in timing populations.
    edit("WriterOwnedStreamCoordinator.RpcAdapter.cs", "    private void ReleaseRpcBudget(int bytes)", """    internal string CaptureRpcDiagnostic()
    {
        var text = new System.Text.StringBuilder();
        text.Append($"RPC-B3 role={_session.Role} connected={_session.IsConnected} hasWork={HasWork} blocked={_blocked} ready={_ready.Count} connCredit={_connectionCredit} debits={_creditDebits} releases={_releases} notifications={_notificationsPending} updates={_updatesPending} wire={_rpcWirePending} drains={_rpcDrainPending} queued={_session.QueuedSendBytes} prepared={_rpcBudget?.ReservedBytes}");
        foreach (var stream in _streams)
        {
            if (!stream.Gate.TryEnter()) { text.Append($"\\n slot={stream.Index} gate=busy"); continue; }
            try
            {
                if (stream.Retired) continue;
                text.Append($"\\n slot={stream.Index} key={stream.RequestId}/{stream.StreamId} generation={stream.Generation} closed={stream.Closed} frames={stream.Frames.Count} bytes={stream.QueuedBytes} taken={stream.Taken} released={stream.Released} outstanding={stream.Outstanding} credit={stream.Credit} capacityHeld={stream.HasHeldSpace} busy={stream.ProducerBusy} scope={stream.RpcScopeUsers} scheduled={stream.Scheduled} notified={stream.NotificationPending} wirePending={stream.RpcWirePending} drain={stream.RpcDrained is not null}");
            }
            finally { stream.Gate.Exit(); }
        }
        return text.ToString();
    }

    private void ReleaseRpcBudget(int bytes)""")
    edit("RpcSession.WriterOwnedStreams.cs", "        try { await source.Completion.ConfigureAwait(false); }", """        using var diagnostic = Environment.GetEnvironmentVariable("SHARPLINK_B3_DIAGNOSTICS") == "1"
            ? new Timer(static state =>
            {
                try { Console.Error.WriteLine(((WriterOwnedStreamCoordinator)state!).CaptureRpcDiagnostic()); }
                catch (Exception error) { Console.Error.WriteLine("B3 diagnostic failed: " + error); }
            }, source, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3))
            : null;
        try { await source.Completion.ConfigureAwait(false); }""")

    program = ROOT / "test/SharpLink.Benchmarks/Program.cs"
    program.write_text(once(program.read_text(),
        "            await GeneratedAbiStreamingEvidenceRunner.RunAsync(args[1..]);\n            return;",
        """            await GeneratedAbiStreamingEvidenceRunner.RunAsync(args[1..]);
            if (Environment.GetEnvironmentVariable("SHARPLINK_WRITER_OWNED_STREAMS") == "1")
            {
                var scopes = System.Threading.Interlocked.Read(ref SharpLink.Runtime.RpcSession.WriterOwnedStreamScopesOpened);
                var items = System.Threading.Interlocked.Read(ref SharpLink.Runtime.RpcSession.WriterOwnedStreamItemsSubmitted);
                Console.WriteLine($"B3_PATH_PROOF scopes={scopes} writer_debits={items}");
                if (scopes == 0 || items == 0) throw new InvalidOperationException("B3 RPC path was not exercised.");
            }
            return;"""))
    subprocess.run(["git", "add", "-N", "src/SharpLink.Runtime"], cwd=ROOT, check=True)
    output = ROOT / "artifacts/742-integration"
    changed = subprocess.check_output(["git", "diff", "--name-only", "--", "src", "test/SharpLink.Benchmarks/Program.cs"], cwd=ROOT, text=True).splitlines()
    (output / "b3-final-manifest.json").write_text(json.dumps({
        "checkout": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
        "status": "integration candidate; acceptance requires logs, not this manifest",
        "sha256": {name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest() for name in changed},
    }, indent=2) + "\n")
    (output / "b3-final.patch").write_bytes(subprocess.check_output(["git", "diff", "--binary", "--", "src"], cwd=ROOT))
    print("PASS writer-owned drain installation and exact integration hardening anchors")

if __name__ == "__main__":
    main()
