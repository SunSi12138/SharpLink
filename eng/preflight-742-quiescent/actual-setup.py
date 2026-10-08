#!/usr/bin/env python3
"""Use the author's actual RpcSession constructor probe against isolated F/G DLLs."""
import csv
import hashlib
import json
import math
import pathlib
import shutil
import statistics
import subprocess
import sys

roots = {label: pathlib.Path(arg).resolve() for label, arg in zip(('F', 'G'), sys.argv[1:3])}
out = pathlib.Path(sys.argv[3]).resolve()
kit = pathlib.Path(__file__).resolve().parent
config = json.loads((kit / 'production-config.json').read_text())
assert config['status'] == 'final' and config['setup_probe'], 'Final actual-session setup probe is not pinned'
probe = config['setup_probe']
out.mkdir(parents=True, exist_ok=True)
paths = {}
proof = {}
for label, root in roots.items():
    subprocess.run(['git', 'diff', '--exit-code', '--', 'src', 'test'], cwd=root, check=True)
    project = out / f'project-{label}'
    project.mkdir(parents=True, exist_ok=False)
    for name, expected in probe['sha256'].items():
        path = kit / 'actual-setup' / name
        assert hashlib.sha256(path.read_bytes()).hexdigest() == expected, path
        shutil.copyfile(path, project / name)
    runtime = root / 'test/SharpLink.UnitTests/bin/Release/net10.0'
    target = out / f'bin-{label}'
    with (out / f'build-{label}.log').open('w') as log:
        subprocess.run(['dotnet', 'build', str(project / 'Setup.csproj'), '-c', 'Release',
            '-p:RuntimeDirectory=' + str(runtime), '-o', str(target), '-m:1', '/nodeReuse:false',
            '-p:UseSharedCompilation=false'], stdout=log, stderr=subprocess.STDOUT, check=True)
    hashes = {}
    for name in ('SharpLink.Runtime.dll', 'SharpLink.Abstractions.dll', 'SharpLink.Sdk.dll'):
        assert (runtime / name).read_bytes() == (target / name).read_bytes(), name
        hashes[name] = hashlib.sha256((target / name).read_bytes()).hexdigest()
    proof[label] = dict(source=json.loads((root / 'artifacts/first-receive-integration/provenance.json').read_text()), binaries=hashes,
        probe_sha256=probe['sha256'], boundary='Real RpcSession constructor and fresh route. Transport/options are outside the interval; every instance is disposed. No imitation closure or extra candidate callback.')
    paths[label] = target / 'SharpLink.UnitTests.dll'
(out / 'provenance.json').write_text(json.dumps(proof, indent=2) + '\n')
rows = []
plan = ['F', 'G', 'G', 'F']
for process, label in enumerate(plan):
    path = out / f'{process}-{label}.csv'
    with path.open('w') as output:
        subprocess.run(['dotnet', str(paths[label])], stdout=output, check=True)
    current = list(csv.DictReader(path.open()))
    for scenario in ('actual-session-constructor', 'fresh-manager-empty-route'):
        selected = [row for row in current if row['scenario'] == scenario]
        assert len(selected) == 3 and {int(row['repeat']) for row in selected} == {0, 1, 2}
        assert all(int(row['count']) == 4096 for row in selected)
    assert {row['scenario'] for row in current} == {'actual-session-constructor', 'fresh-manager-empty-route',
        'layout-StreamManager', 'layout-DispatcherEntry', 'layout-DispatcherEntryCompletions'}
    for row in current:
        value = float(row['bytes_per_operation'])
        assert math.isfinite(value) and value >= 0
        rows.append(dict(label=label, process=process, scenario=row['scenario'], repeat=int(row['repeat']),
            count=int(row['count']), bytes_per_operation=value))
    for name, expected in proof[label]['binaries'].items():
        assert hashlib.sha256((paths[label].parent / name).read_bytes()).hexdigest() == expected
summary = []
for scenario in sorted({row['scenario'] for row in rows}):
    arms = {label: [r['bytes_per_operation'] for r in rows if r['label'] == label and r['scenario'] == scenario] for label in roots}
    summary.append(dict(scenario=scenario, F=arms['F'], G=arms['G'], median_delta=statistics.median(arms['G']) - statistics.median(arms['F'])))
(out / 'rows.json').write_text(json.dumps(rows, indent=2) + '\n')
(out / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(json.dumps(summary, indent=2))
print('Actual setup and object-layout bytes retained separately; no setup cost is subtracted from production results.')
