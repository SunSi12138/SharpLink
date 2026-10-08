#!/usr/bin/env python3
"""Identical boundary-only diagnostic instrumentation; never changes shipping runtime."""
import hashlib,json,pathlib,subprocess,sys
roots=[pathlib.Path(p).resolve() for p in sys.argv[1:4]]
source=pathlib.Path(sys.argv[4]).resolve();out=pathlib.Path(sys.argv[5]).resolve();out.mkdir(parents=True,exist_ok=True)
marker=(source/'ProfileWindow742.cs').read_bytes()
base='e834d3c28c87ad496989af925515cf21babd308d'
paths=['test/SharpLink.Benchmarks/GeneratedAbiStreamingEvidenceRunner.cs','test/SharpLink.StreamLoadTest/Program.cs']
proof={}
def once(s,a,b):
    assert s.count(a)==1,(a,s.count(a))
    return s.replace(a,b,1)
for label,root in zip(('dev','published','candidate'),roots):
    entry={}
    for path in paths:
        # The same original measurement harness is linked to each runtime revision.
        original=subprocess.check_output(['git','show',f'{base}:{path}'],cwd=root)
        text=original.decode()
        if 'Benchmarks' in path:
            a='        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);'
            text=once(text,a,'        SharpLink.Profiling742.ProfileWindow742.Log.WindowBegin("rpc-Server1x16");\n'+a)
            a='        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);'
            text=once(text,a,a+'\n        SharpLink.Profiling742.ProfileWindow742.Log.WindowEnd("rpc-Server1x16", completed, checked((long)completed * benchmark.ItemCount));')
        else:
            a='        var evidenceBefore = s_evidenceCollector!.Capture();'
            text=once(text,a,'        if (!isWarmup) SharpLink.Profiling742.ProfileWindow742.Log.WindowBegin("c8-s2c-10000");\n'+a)
            a='        var measurementStopped = lifecycle.StopStartingNewOperations();'
            text=once(text,a,a+'\n        if (!isWarmup) SharpLink.Profiling742.ProfileWindow742.Log.AdmissionsClosed("c8-s2c-10000");')
            a='        var formalStatistics = formalRecorder?.Complete();'
            text=once(text,a,'        if (!isWarmup) SharpLink.Profiling742.ProfileWindow742.Log.WindowEnd("c8-s2c-10000", success, checked(success * options.StreamSize));\n'+a)
        (root/path).write_text(text)
        (root/path).parent.joinpath('ProfileWindow742.cs').write_bytes(marker)
        entry[path]={'original_sha256':hashlib.sha256(original).hexdigest(),'diagnostic_sha256':hashlib.sha256(text.encode()).hexdigest()}
    proof[label]=entry
assert proof['dev']==proof['published']==proof['candidate']
(out/'harness-provenance.json').write_text(json.dumps(dict(arms=proof,marker_sha256=hashlib.sha256(marker).hexdigest(),meaning='Diagnostic markers only; include c8 drain; no per-item events; no acceptance timing claim'),indent=2)+'\n')
print(json.dumps(proof,indent=2))
