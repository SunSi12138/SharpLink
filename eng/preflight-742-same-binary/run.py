#!/usr/bin/env python3
"""Predeclared72 primary +24 non-tiered same-binary diagnostic launches."""
import argparse
import hashlib
import itertools
import json
import os
import pathlib
import statistics
import subprocess

from common import c8, rpc, sha, snapshot

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('shipping', type=pathlib.Path)
parser.add_argument('diagnostic', type=pathlib.Path)
parser.add_argument('output', type=pathlib.Path)
parser.add_argument('--affinity', required=True)
args = parser.parse_args()
kit = pathlib.Path(__file__).resolve().parent
spec = json.loads((kit / 'adapter.json').read_text())
assert spec['audit_approved'] and spec['diagnostic_tree'] and spec['flag_name']
roots = {'G2': args.shipping.resolve(), 'ON': args.diagnostic.resolve(), 'OFF': args.diagnostic.resolve()}
identities = {'G2': spec['shipping_tree'], 'ON': spec['diagnostic_tree'], 'OFF': spec['diagnostic_tree']}
flag_values = {'G2': spec['on_value'], 'ON': spec['on_value'], 'OFF': spec['off_value']}
assert flag_values['ON'] != flag_values['OFF']
for label in ('G2', 'ON'):
    proof = json.loads((roots[label] / 'artifacts/first-receive-integration/provenance.json').read_text())
    assert proof['disposable_tree'] == identities[label]
    subprocess.run(['git', 'diff', '--exit-code', '--', 'src', 'test'], cwd=roots[label], check=True)
out = args.output.resolve()
out.mkdir(parents=True, exist_ok=True)
relative = {'rpc': 'test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll',
            'c8': 'test/SharpLink.StreamLoadTest/bin/Release/net10.0/SharpLink.StreamLoadTest.dll'}
binaries = {label: {kind: root / path for kind, path in relative.items()} for label, root in roots.items()}
assert roots['ON'] == roots['OFF'] and binaries['ON'] == binaries['OFF']
shared_environment = dict(os.environ)
for key in ('DOTNET_JitDisasm', 'DOTNET_JitStdOutFile', 'COMPlus_JitDisasm', 'COMPlus_JitStdOutFile',
            'SHARPLINK_READY_TCP_RECEIVE_BUFFER', spec['flag_name']):
    shared_environment.pop(key, None)
workloads = [('rpc', 'sharedmemory', 'Client100x4096'), ('rpc', 'sharedmemory', 'Server1x16'),
             ('c8', 'sharedmemory', '1'), ('c8', 'sharedmemory', '10000')]
modes = {
    'primary-tiered': dict(arms=['G2', 'ON', 'OFF'], orders=list(itertools.permutations(('G2', 'ON', 'OFF'))),
        workloads=workloads, tiering='1', pgo='1', launches=72, rows=180,
        boundary='Original production runtime flags. ON/OFF estimates conditional mechanism plus possible tiered-PGO response. G2/ON measures adapter/build influence only.'),
    'secondary-nontiered': dict(arms=['ON', 'OFF'], orders=[('ON', 'OFF') if block % 2 == 0 else ('OFF', 'ON') for block in range(6)],
        workloads=[workloads[1], workloads[2]], tiering='0', pgo='0', launches=24, rows=60,
        boundary='Separate non-tiered ON/OFF diagnostic only, never a replacement for primary or original D acceptance.')}
initial = {label: snapshot(roots[label]) for label in ('G2', 'ON')}
plans = {mode: [(block, kind, transport, case, label) for block, order in enumerate(config['orders'])
          for kind, transport, case in config['workloads'] for label in order] for mode, config in modes.items()}
assert sum(len(plan) for plan in plans.values()) == 96
proof = dict(identities=identities, flag_name=spec['flag_name'], flag_values=flag_values, plans=plans, modes=modes,
    affinity=args.affinity, processor_count=4, same_diagnostic_app_paths={kind: str(path) for kind, path in binaries['ON'].items()},
    on_off_same_working_directory=str(roots['ON']),
    output_contract='All child invocations use one constant output path. Raw files are renamed only after process exit; ON/OFF argv is literally identical for each workload/runtime mode.', source_and_binaries_before=initial,
    boundary='No shipping candidate. Same DLL/IL does not prove identical tiered-PGO native code. No D baseline or performance acceptance substitution.')
(out / 'provenance.json').write_text(json.dumps(proof, indent=2) + '\n')
all_status = {}
for mode, config in modes.items():
    destination = out / mode
    destination.mkdir()
    controlled = dict(DOTNET_PROCESSOR_COUNT='4', DOTNET_ReadyToRun='0', DOTNET_TieredCompilation=config['tiering'],
        DOTNET_TieredPGO=config['pgo'], DOTNET_TC_QuickJitForLoops='1')
    plan = plans[mode]
    assert len(plan) == config['launches']
    exits, normalized_commands = [], {}
    launch_output = out / 'current-launch-output.json'
    try:
        for block, kind, transport, case, label in plan:
            name = f'b{block}-{kind}-{transport}-{case}-{label}'
            target = destination / f'{name}.json'
            launch_output.unlink(missing_ok=True)
            command = ['taskset', '-c', args.affinity, 'dotnet', str(binaries[label][kind])]
            if kind == 'rpc':
                command += ['--generated-abi-streaming-evidence', case, '30', '5', '200000', str(launch_output), transport]
            else:
                command += ['--mode', 'local', '--transport', transport, '--operation', 'all', '--stream-size', case,
                    '--concurrency', '8', '--stream-receive-window-bytes', '8192', '--connection-receive-window-bytes', '65536',
                    '--warmup', '1', '--duration', '2', '--recording', 'off', '--json-output', str(launch_output)]
            env = dict(shared_environment, **controlled, SHARPLINK_BENCHMARK_SHA=identities[label], SHARPLINK_COMMIT=identities[label])
            env[spec['flag_name']] = flag_values[label]
            if label in ('ON', 'OFF'):
                normalized = list(command)
                key = (kind, transport, case)
                equivalent = dict(command=normalized, root=str(roots[label]), identity=identities[label],
                    environment_without_flag={key: value for key, value in env.items() if key != spec['flag_name']})
                if key in normalized_commands:
                    assert equivalent == normalized_commands[key], 'ON/OFF differs beyond the single process-start flag'
                normalized_commands[key] = equivalent
            with (destination / f'{name}.log').open('w') as log:
                try:
                    code = subprocess.run(command, cwd=roots[label], env=env, stdout=log,
                        stderr=subprocess.STDOUT, timeout=180).returncode
                except subprocess.TimeoutExpired:
                    code = 124
            if launch_output.exists():
                launch_output.replace(target)  # Byte-preserving archival after the process exits.
            exits.append(dict(name=name, exit_code=code, command=command, identity=identities[label],
                binary_sha256=sha(binaries[label][kind]), archived_output=str(target), block=block, kind=kind, transport=transport, case=case, label=label,
                controlled_environment={**controlled, spec['flag_name']: flag_values[label]}))
            (destination / 'exits.json').write_text(json.dumps(exits, indent=2) + '\n')
            print(mode, name, code, flush=True)
    finally:
        after = {label: snapshot(roots[label]) for label in ('G2', 'ON')}
        (destination / 'source-and-binaries-after.json').write_text(json.dumps(after, indent=2) + '\n')
        assert initial == after, 'Source or actual binary bytes changed; later observations are not safe'
    rows, failures, hashes = [], [], {}
    for launch in exits:
        path = destination / f"{launch['name']}.json"
        if path.exists():
            hashes[path.name] = sha(path)
        try:
            assert launch['exit_code'] == 0
            report = json.loads(path.read_text())
            assert report['processorCount' if launch['kind'] == 'rpc' else 'ProcessorCount'] == 4
            if launch['kind'] == 'rpc':
                selected = rpc(report, launch['identity'], launch['transport'], launch['case'])
                assert report['tieredCompilation'] == config['tiering'] and report['tieredPgo'] == config['pgo']
            else:
                selected = c8(report, launch['identity'], launch['transport'], int(launch['case']))
            rows += [dict(mode=mode, block=launch['block'], label=launch['label'], kind=launch['kind'],
                transport=launch['transport'], case=launch['case'], **row) for row in selected]
        except (AssertionError, OSError, ValueError, KeyError, TypeError) as error:
            failures.append(dict(name=launch['name'], exit_code=launch['exit_code'], validation_error=repr(error)))
    (destination / 'validated-rows.json').write_text(json.dumps(rows, indent=2) + '\n')
    (destination / 'validation-failures.json').write_text(json.dumps(failures, indent=2) + '\n')
    (destination / 'raw-sha256.json').write_text(json.dumps(hashes, indent=2) + '\n')
    complete = len(exits) == config['launches'] and not failures and len(rows) == config['rows']
    all_status[mode] = dict(complete=complete, launches=len(exits), rows=len(rows), failures=failures)
    (out / 'status.json').write_text(json.dumps(all_status, indent=2) + '\n')
    if not complete:
        continue  # Preserve every failure; collect the separate predeclared mode too.
    comparisons = []
    contrasts = [('OFF', 'ON', 'same-binary ON versus OFF')]
    if mode == 'primary-tiered':
        contrasts.append(('G2', 'ON', 'diagnostic ON versus untouched G2: adapter/build influence only'))
    for kind, transport, case in config['workloads']:
        for shape in ([case] if kind == 'rpc' else ['unary', 'c2s', 's2c', 'duplex']):
            selected = [row for row in rows if (row['kind'], row['transport'], row['case'], row['shape']) == (kind, transport, case, shape)]
            grouped = {label: sorted((row for row in selected if row['label'] == label), key=lambda row: row['block']) for label in config['arms']}
            assert all([row['block'] for row in group] == list(range(6)) for group in grouped.values())
            for baseline, candidate, meaning in contrasts:
                metrics = {}
                for key in ('rate', 'cpu_us_op', 'bytes_op'):
                    left, right = [row[key] for row in grouped[baseline]], [row[key] for row in grouped[candidate]]
                    metrics[key] = dict(baseline=left, candidate=right,
                        paired_percent=[(c / b - 1) * 100 if b else None for b, c in zip(left, right)],
                        paired_absolute_delta=[c - b for b, c in zip(left, right)],
                        ratio_of_medians_percent=(statistics.median(right) / statistics.median(left) - 1) * 100 if statistics.median(left) else None,
                        median_absolute_delta=statistics.median(right) - statistics.median(left))
                comparisons.append(dict(mode=mode, kind=kind, transport=transport, case=case, shape=shape,
                    baseline=baseline, candidate=candidate, meaning=meaning, metrics=metrics))
    (destination / 'comparisons.json').write_text(json.dumps(comparisons, indent=2) + '\n')
assert set(all_status) == set(modes) and all(value['complete'] for value in all_status.values()), 'Incomplete diagnostic population; all raw outputs retained'
print('96/96 launches and240 validated rows retained in separate modes. No shipping or acceptance claim.')
