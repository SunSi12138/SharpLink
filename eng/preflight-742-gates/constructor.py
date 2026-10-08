#!/usr/bin/env python3
"""Paired N/H fixed constructor costs; isolated builds prevent cached arm references."""
import hashlib
import json
import os
import pathlib
import shutil
import statistics
import subprocess
import sys

roots = {label: pathlib.Path(value).resolve() for label, value in zip(('N', 'H'), sys.argv[1:3])}
out = pathlib.Path(sys.argv[3]).resolve()
kit = pathlib.Path(__file__).resolve().parent
out.mkdir(parents=True, exist_ok=True)
expected = {'N': '4c5943fec3a85089cefa8a1b6dc8c0f6502567ce', 'H': '522585079b97c99f7986eb50f3ed6c69d4086bfb'}
plan = [(repeat, label) for repeat in range(4) for label in (('N', 'H') if repeat % 2 == 0 else ('H', 'N'))]
identities, binaries, hashes = {}, {}, {}
for label, root in roots.items():
    identities[label] = json.loads((root / 'artifacts/first-receive-integration/provenance.json').read_text())['disposable_tree']
    assert identities[label] == expected[label]
    arm = out / 'arms' / label
    arm.mkdir(parents=True, exist_ok=True)
    for name in ('Program.cs', 'DirectionalDiagnostic.csproj'):
        shutil.copyfile(kit / 'constructor' / name, arm / name)
    for name, command in (
        ('runtime', ['dotnet', 'build', str(root / 'src/SharpLink.Runtime'), '-c', 'Release', '-p:Platform=AnyCPU', '-p:TreatWarningsAsErrors=true', '-m:1', '/nodeReuse:false', '-p:UseSharedCompilation=false']),
        ('probe', ['dotnet', 'build', str(arm / 'DirectionalDiagnostic.csproj'), '-c', 'Release', f'-p:ArmPath={root}', '-m:1', '/nodeReuse:false', '-p:UseSharedCompilation=false']),
    ):
        with (out / f'{label}-{name}-build.log').open('w') as log:
            result = subprocess.run(command, cwd=root, stdout=log, stderr=subprocess.STDOUT)
        assert result.returncode == 0, (out / f'{label}-{name}-build.log').read_text()
    binary = arm / 'bin/Release/net10.0/SharpLink.Benchmarks.dll'
    runtime = binary.parent / 'SharpLink.Runtime.dll'
    actual = root / 'src/SharpLink.Runtime/bin/Release/net10.0/SharpLink.Runtime.dll'
    assert runtime.read_bytes() == actual.read_bytes(), 'Probe references the wrong runtime arm'
    binaries[label] = binary
    hashes[label] = dict(probe=hashlib.sha256(binary.read_bytes()).hexdigest(), runtime=hashlib.sha256(runtime.read_bytes()).hexdigest())
assert hashes['N']['runtime'] != hashes['H']['runtime']
(out / 'provenance.json').write_text(json.dumps(dict(identities=identities, plan=plan, binary_sha256=hashes,
    source_sha256={name: hashlib.sha256((kit / 'constructor' / name).read_bytes()).hexdigest() for name in ('Program.cs', 'DirectionalDiagnostic.csproj')},
    boundary='Mechanism and fixed constructor cost only. Startup is outside operation allocation measurements. No cost is removed from production acceptance. Each arm uses an isolated project and output directory.'), indent=2) + '\n')
rows, exits = [], []
for repeat, label in plan:
    name = f'{label}-r{repeat}'
    env = dict(os.environ, DOTNET_PROCESSOR_COUNT='4', DOTNET_ReadyToRun='0', DOTNET_TieredPGO='1')
    with (out / f'{name}.json').open('w') as output, (out / f'{name}.stderr.log').open('w') as errors:
        result = subprocess.run(['dotnet', str(binaries[label]), label, '200000'], env=env,
            stdout=output, stderr=errors, timeout=180)
    exits.append(dict(name=name, exit_code=result.returncode))
    (out / 'exits.json').write_text(json.dumps(exits, indent=2) + '\n')
    assert result.returncode == 0, name
    data = json.loads((out / f'{name}.json').read_text())
    assert data['Arm'] == label and data['RuntimeSha256'] == hashes[label]['runtime'], name
    assert len(data['Rows']) == 12
    assert {row['Mode'] for row in data['Rows']} == {'sequential-mixed', 'parallel-mixed', 'parallel-send', 'parallel-receive'}
    assert all(row['Checksum'] == row['Operations'] == 400000 and row['LoopAllocatedBytes'] >= 0 for row in data['Rows'])
    rows.append(dict(label=label, repeat=repeat, controller_bytes=data['ControllerConstructionBytes'], lock_bytes=data['LockConstructionBytes'],
        mechanism_loop_allocated_bytes=[row['LoopAllocatedBytes'] for row in data['Rows']]))
assert len(rows) == 8
values = {label: [r['controller_bytes'] for r in rows if r['label'] == label] for label in roots}
summary = dict(rows=rows, controller_bytes=values,
    candidate_fixed_controller_delta_bytes=statistics.median(values['H']) - statistics.median(values['N']),
    note='Report fixed per-controller cost honestly. A lower operation allocation is not zero setup cost. Local source-author evidence was 408 to 456 bytes (+48) on x64 .NET 10.0.2; this run reports its own observed values.')
(out / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(json.dumps(summary, indent=2))
