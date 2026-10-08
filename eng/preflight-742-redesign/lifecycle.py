#!/usr/bin/env python3
"""Same full warm/cold lifecycle probe against pristine, hash-verified build outputs."""
import csv
import hashlib
import json
import pathlib
import shutil
import subprocess
import sys

label, source_arg, output_arg = sys.argv[1:4]
assert label in ('control', 'candidate')
source, out = pathlib.Path(source_arg).resolve(), pathlib.Path(output_arg).resolve()
kit = pathlib.Path(__file__).resolve().parent / 'lifecycle'
project = out / f'lifecycle-project-{label}'
bin_dir = out / f'lifecycle-bin-{label}'
project.mkdir(parents=True, exist_ok=False)
for name in ('Program.cs', 'Probe.csproj'):
    shutil.copyfile(kit / name, project / name)
subprocess.run(['git', 'diff', '--exit-code', '--', 'src', 'test'], cwd=source, check=True)
runtime = source / 'test/SharpLink.UnitTests/bin/Release/net10.0'
with (out / f'lifecycle-build-{label}.log').open('w') as log:
    subprocess.run(['dotnet', 'build', str(project / 'Probe.csproj'), '-c', 'Release',
                    '-p:RuntimeDirectory=' + str(runtime), '-o', str(bin_dir), '-m:1',
                    '/nodeReuse:false', '-p:UseSharedCompilation=false'], stdout=log, stderr=subprocess.STDOUT, check=True)
sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
proof = {'label': label, 'source': json.loads((source / 'artifacts/first-receive-integration/provenance.json').read_text()),
         'probe': {name: sha(project / name) for name in ('Program.cs', 'Probe.csproj')}, 'binaries': {}}
for name in ('SharpLink.Runtime.dll', 'SharpLink.Abstractions.dll'):
    assert sha(runtime / name) == sha(bin_dir / name), name
    proof['binaries'][name] = sha(bin_dir / name)
(out / f'lifecycle-provenance-{label}.json').write_text(json.dumps(proof, indent=2) + '\n')
with (out / f'lifecycle-{label}.csv').open('w') as csv_file:
    subprocess.run(['dotnet', str(bin_dir / 'SharpLink.UnitTests.dll')], stdout=csv_file, check=True)
rows = list(csv.DictReader((out / f'lifecycle-{label}.csv').open()))
assert len(rows) == 120
keys = lambda row: (row['scenario'], row['cleanup'], row['cold'], row['repeat'])
assert len({keys(row) for row in rows}) == 120
if label == 'candidate':
    control = {keys(row): row for row in csv.DictReader((out / 'lifecycle-control.csv').open())}
    assert set(control) == {keys(row) for row in rows}
    comparisons = []
    for row in rows:
        before = float(control[keys(row)]['bytes_per_request'])
        after = float(row['bytes_per_request'])
        comparisons.append(dict(scenario=row['scenario'], cleanup=row['cleanup'], cold=row['cold'],
                                repeat=int(row['repeat']), control=before, candidate=after, delta=after-before))
    (out / 'lifecycle-comparison.json').write_text(json.dumps(comparisons, indent=2) + '\n')
    # The claimed mechanism removes common-path allocations. Keep every raw byte result.
    assert all(row['delta'] <= 0.01 for row in comparisons), 'New lifecycle allocation increase; do not promote'
print(f'{label}:120 complete lifecycle rows, source and loaded DLL hashes verified')
