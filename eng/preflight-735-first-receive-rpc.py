#!/usr/bin/env python3
"""Fixed incremental RPC screen; retain negatives rather than inferring Go from exit zero."""
import argparse
import hashlib
import json
import math
import os
import pathlib
import statistics
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument('control', type=pathlib.Path)
parser.add_argument('candidate', type=pathlib.Path)
parser.add_argument('output', type=pathlib.Path)
parser.add_argument('--pgo', type=int, choices=(0, 1), required=True)
args = parser.parse_args()
control, candidate, output = args.control.resolve(), args.candidate.resolve(), args.output.resolve()
output.mkdir(parents=True, exist_ok=True)
control_sha = 'e834d3c28c87ad496989af925515cf21babd308d'
candidate_tree = (candidate/'artifacts/first-receive-integration/candidate-tree.txt').read_text().strip()
harness = 'test/SharpLink.Benchmarks/GeneratedAbiStreamingEvidenceRunner.cs'
if (control/harness).read_bytes() != (candidate/harness).read_bytes():
    raise RuntimeError('Control and candidate must use the identical measurement harness')
plan = [(repeat, transport, scenario, label)
        for repeat in range(3)
        for transport in ('tcp', 'sharedmemory')
        for scenario in ('Server1x16', 'Server100x16')
        for label in (('candidate', 'control') if repeat == 1 else ('control', 'candidate'))]
provenance = dict(control_commit=control_sha, candidate_tree=candidate_tree, pgo=args.pgo,
                  plan=plan, affinity=[0,1,2,3], processor_count=4,
                  warmup_operations=30, measurement_seconds=5, max_operations=200000,
                  attempts_per_cell=1, socket_profile='unchanged default',
                  harness_sha256=hashlib.sha256((control/harness).read_bytes()).hexdigest(),
                  interpretation='incremental real-RPC screen, not pinned-dev total acceptance')
(output/'provenance.json').write_text(json.dumps(provenance, indent=2)+'\n')
exits = []
for repeat, transport, scenario, label in plan:
    root = control if label == 'control' else candidate
    name = f'{label}-{transport}-{scenario}-r{repeat}'
    identity = control_sha if label == 'control' else candidate_tree
    env = dict(os.environ, SHARPLINK_BENCHMARK_SHA=identity, DOTNET_ReadyToRun='0',
               DOTNET_TieredCompilation='1', DOTNET_TieredPGO=str(args.pgo),
               DOTNET_TC_QuickJitForLoops='1', DOTNET_PROCESSOR_COUNT='4')
    # Do not inherit diagnostic code generation or benchmark socket overrides.
    for key in ('DOTNET_JitDisasm','DOTNET_JitStdOutFile','SHARPLINK_READY_TCP_RECEIVE_BUFFER'):
        env.pop(key, None)
    command = ['taskset','-c','0-3','dotnet',
               str(root/'test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll'),
               '--generated-abi-streaming-evidence',scenario,'30','5','200000',
               str(output/f'{name}.json'),transport]
    print('RUN', name, flush=True)
    with (output/f'{name}.log').open('w') as log:
        try:
            code = subprocess.run(command, cwd=root, env=env, stdout=log,
                                  stderr=subprocess.STDOUT, timeout=60).returncode
        except subprocess.TimeoutExpired:
            code = 124
    exits.append(dict(repeat=repeat, transport=transport, scenario=scenario, label=label, exit_code=code))
    (output/'exits.json').write_text(json.dumps(exits, indent=2)+'\n')
    print('EXIT', code, name, flush=True)
if len(exits) != 24 or any(row['exit_code'] for row in exits):
    raise SystemExit('Incomplete RPC population; every failed attempt is retained')
results = []
for transport in ('tcp', 'sharedmemory'):
    for scenario in ('Server1x16', 'Server100x16'):
        arms = {label:[json.loads((output/f'{label}-{transport}-{scenario}-r{repeat}.json').read_text())
                       for repeat in range(3)] for label in ('control','candidate')}
        for label, reports in arms.items():
            identity = control_sha if label == 'control' else candidate_tree
            for row in reports:
                if not (row['commit']==identity and row['validationFailures']==0 and
                        row['warmupOperations']==30 and row['requestedMeasurementSeconds']==5 and
                        row['throughputItemsPerSecond']>0 and
                        math.isfinite(row['allocatedBytesPerItem'])):
                    raise RuntimeError(f'Invalid RPC report: {row}')
        median = lambda label,key:statistics.median(row[key] for row in arms[label])
        results.append(dict(transport=transport, scenario=scenario,
            throughput_pct=(median('candidate','throughputItemsPerSecond')/median('control','throughputItemsPerSecond')-1)*100,
            cpu_time_pct=(median('candidate','cpuUsPerOperation')/median('control','cpuUsPerOperation')-1)*100,
            allocation_delta=median('candidate','allocatedBytesPerItem')-median('control','allocatedBytesPerItem'),
            throughput={label:[row['throughputItemsPerSecond'] for row in reports] for label,reports in arms.items()},
            allocations={label:[row['allocatedBytesPerItem'] for row in reports] for label,reports in arms.items()}))
text = json.dumps(results, indent=2)
(output/'comparison.json').write_text(text+'\n')
print(text)
print('24/24 complete processes; integrity passed, performance acceptance requires review of all deltas.')
