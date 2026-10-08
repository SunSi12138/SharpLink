#!/usr/bin/env python3
"""Paired original120-row lifecycle matrix with identical final production callbacks on F/G."""
import csv
import hashlib
import json
import pathlib
import shutil
import subprocess
import sys

roots = {label: pathlib.Path(arg).resolve() for label, arg in zip(('F', 'G'), sys.argv[1:3])}
out = pathlib.Path(sys.argv[3]).resolve()
out.mkdir(parents=True, exist_ok=True)
helper = pathlib.Path(__file__).resolve().parent
config = json.loads((helper / 'production-config.json').read_text())
assert config['status'] == 'final' and config['f_constructor_arguments'], 'Final production constructor adapter is not pinned'
source = helper.parents[1]
kit = source / 'eng/preflight-742-redesign/lifecycle'
program = (kit / 'Program.cs').read_text()
anchor = '        flow.ResolveReceiveCreditLease, Accept, Consume, Terminal);'
assert program.count(anchor) == 1
proof, all_rows = {}, {}
for label, root in roots.items():
    subprocess.run(['git', 'diff', '--exit-code', '--', 'src', 'test'], cwd=root, check=True)
    project = out / f'project-{label}'
    project.mkdir(parents=True, exist_ok=False)
    text = program.replace(anchor, config['f_constructor_arguments'])
    manager_anchor = '    var manager = new StreamManager('
    assert text.count(manager_anchor) == 1
    text = text.replace(manager_anchor, config['f_preamble'] + manager_anchor)
    (project / 'Program.cs').write_text(text)
    shutil.copyfile(kit / 'Probe.csproj', project / 'Probe.csproj')
    runtime = root / 'test/SharpLink.UnitTests/bin/Release/net10.0'
    binaries = out / f'bin-{label}'
    with (out / f'build-{label}.log').open('w') as log:
        subprocess.run(['dotnet', 'build', str(project / 'Probe.csproj'), '-c', 'Release',
            '-p:RuntimeDirectory=' + str(runtime), '-o', str(binaries), '-m:1', '/nodeReuse:false',
            '-p:UseSharedCompilation=false'], stdout=log, stderr=subprocess.STDOUT, check=True)
    hashes = {}
    for name in ('SharpLink.Runtime.dll', 'SharpLink.Abstractions.dll'):
        assert (runtime / name).read_bytes() == (binaries / name).read_bytes(), name
        hashes[name] = hashlib.sha256((binaries / name).read_bytes()).hexdigest()
    proof[label] = dict(source=json.loads((root / 'artifacts/first-receive-integration/provenance.json').read_text()),
        program_sha256=hashlib.sha256(text.encode()).hexdigest(), binaries=hashes,
        production_constructor_arguments=config['f_constructor_arguments'])
    (out / 'provenance.json').write_text(json.dumps(proof, indent=2) + '\n')
    with (out / f'{label}.csv').open('w') as output:
        subprocess.run(['dotnet', str(binaries / 'SharpLink.UnitTests.dll')], stdout=output, check=True)
    rows = list(csv.DictReader((out / f'{label}.csv').open()))
    key = lambda row: (row['scenario'], row['cleanup'], row['cold'], row['repeat'])
    assert len(rows) == 120 and len({key(row) for row in rows}) == 120
    all_rows[label] = {key(row): row for row in rows}
assert set(all_rows['F']) == set(all_rows['G'])
comparison = [dict(scenario=key[0], cleanup=key[1], cold=key[2], repeat=int(key[3]),
    baseline=float(all_rows['F'][key]['bytes_per_request']), candidate=float(all_rows['G'][key]['bytes_per_request']),
    delta=float(all_rows['G'][key]['bytes_per_request']) - float(all_rows['F'][key]['bytes_per_request'])) for key in all_rows['F']]
(out / 'comparison.json').write_text(json.dumps(comparison, indent=2) + '\n')
print(json.dumps(comparison, indent=2))
print('Supplemental production-path matrix. Original matrix and its allocation gate are retained separately; fixed setup costs are not subtracted.')
