#!/usr/bin/env python3
"""Apply exact, reversible SendPump hooks ONLY to a disposable research checkout."""
from pathlib import Path
import argparse, hashlib, json
ROOT = Path(__file__).resolve().parents[1]
HASHES = {
    "RpcSession.SendPump.cs": "7672100a04a32ec96acd46a99895e0d2f3fbed2d6779c6db3698888c0e65d049",
    "OwnedFrame.cs": "becf57653e6aa89b00a7e28d3f3a64ec0b0814690d00495457bb1d5fbb1eb3e5",
}

def once(text, old, new):
    if text.count(old) != 1: raise ValueError("hook anchor missing or duplicated: " + old[:90])
    return text.replace(old,new)

def transform(name, text):
    if hashlib.sha256(text.encode()).hexdigest() != HASHES[name]: raise ValueError("runtime source drift: " + name)
    if name == "OwnedFrame.cs":
        # Reuse the existing object slot. The measured queue record must not grow,
        # nor should a per-frame delegate/marker be allocated for the experiment.
        return once(text, "    private readonly object? _completionState;", """    private readonly object? _completionState;
    internal IReadyFrameCompletion? ReadyCompletion => _completionState as IReadyFrameCompletion;
    internal OwnedFrame(ReadyStreamFrame ready)
        : this(ready.Packet, false, null, false)
    { _completionState = ready.Completion; }
""")
    text=once(text,"private sealed class SendPump","private sealed partial class SendPump")
    text=once(text,"private bool HasNormalFrames() => _normalQueue.Reader.TryPeek(out _);", """private bool HasNormalFrames() => _normalQueue.Reader.TryPeek(out _) ||
            (Volatile.Read(ref _stopped) == 0 && (Volatile.Read(ref _readyWriterExperiment)?.HasWork ?? false));""")
    text=once(text,"while (_normalQueue.Reader.TryRead(out var frame))\n                    {", "while (TryReadNormalOrReadyFrame(out var frame))\n                    {")
    text=once(text,"                    if (_flushPolicyState.Capture().ExplicitBatchWindowEnabled &&", """                    // A ready-stream source can be waiting for credit carried by this
                    // unfinished batch. Do not wait for more data while holding that credit.
                    if (Volatile.Read(ref _readyWriterExperiment) is null &&
                        _flushPolicyState.Capture().ExplicitBatchWindowEnabled &&""")
    text=once(text,"                DrainQueuedFrames(terminalException);", """                DrainQueuedFrames(terminalException);
                Volatile.Read(ref _readyWriterExperiment)?.Stopped(terminalException);""")
    text=once(text,"                PulseCapacityWaiters();\n                // This belongs", """                frame.ReadyCompletion?.Complete(exception);
                PulseCapacityWaiters();
                // This belongs""")
    return text

def main():
    a=argparse.ArgumentParser(); a.add_argument("--apply", action="store_true"); args=a.parse_args()
    dest=ROOT/"src/SharpLink.Runtime"; backup=ROOT/"artifacts/ready-writer-hook"
    transformed={}
    for name in HASHES: transformed[name]=transform(name,(dest/name).read_text())
    extra=ROOT/"test/SharpLink.ReadyWriterExperiment/RpcSession.ReadyWriter.cs"
    if (dest/extra.name).exists(): raise ValueError("unexpected pre-existing runtime hook")
    if args.apply:
        backup.mkdir(parents=True,exist_ok=True)
        for name,text in transformed.items():
            (backup/name).write_bytes((dest/name).read_bytes()); (dest/name).write_text(text)
        (dest/extra.name).write_bytes(extra.read_bytes())
        (backup/"transformed-sha256.json").write_text(json.dumps({p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in [*(dest/n for n in HASHES),dest/extra.name]},indent=2))
    print("PASS exact runtime anchors; " + ("applied disposable hook" if args.apply else "no source mutated"))
if __name__=="__main__": main()
