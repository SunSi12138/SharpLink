#!/usr/bin/env python3
"""Eight per-arm counter controls in separate, restored, never-timed worktrees."""
import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile

roots = {label: pathlib.Path(arg).resolve() for label, arg in zip(('F', 'G'), sys.argv[1:3])}
out = pathlib.Path(sys.argv[3]).resolve()
kit = pathlib.Path(__file__).resolve().parent / 'counter'
manifest = json.loads((kit / 'manifest.json').read_text())
for name, expected in manifest['files'].items():
    assert hashlib.sha256((kit / name).read_bytes()).hexdigest() == expected, name
out.mkdir(parents=True, exist_ok=True)
work = pathlib.Path(tempfile.mkdtemp(prefix='742-quiescent-counter-', dir=os.environ.get('RUNNER_TEMP')))
assert not work.is_relative_to(out), 'Instrumented checkouts must remain outside uploaded artifacts'
(out / 'counter-tool-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
proof = {}
for label, source in roots.items():
    source_proof = json.loads((source / 'artifacts/first-receive-integration/provenance.json').read_text())
    root = work / f'instrumented-{label}'
    subprocess.run(['git', 'worktree', 'add', '--detach', str(root), '06b737df0d9aab66caed9c446a572b3a8ce4efae'], cwd=source, check=True)
    subprocess.run(['git', 'apply', '--index', str(source / 'artifacts/first-receive-integration/candidate.patch')], cwd=root, check=True)
    tree = subprocess.check_output(['git', 'write-tree'], cwd=root, text=True).strip()
    assert tree == source_proof['disposable_tree']
    try:
        patch = kit / f'{label}-counter.patch'
        subprocess.run(['git', 'apply', '--check', str(patch)], cwd=root, check=True)
        subprocess.run(['git', 'apply', str(patch)], cwd=root, check=True)
        subprocess.run(['python3', str(kit / 'verify.py'), str(root), str(out / f'{label}-instrumentation-verification.json')], check=True)
        with (out / f'{label}-runtime-build.log').open('w') as log:
            subprocess.run(['dotnet', 'build', 'test/SharpLink.UnitTests', '-c', 'Release', '-m:1', '/nodeReuse:false',
                '-p:UseSharedCompilation=false', '-p:TreatWarningsAsErrors=true'], cwd=root, stdout=log, stderr=subprocess.STDOUT, check=True)
        runtime = root / 'test/SharpLink.UnitTests/bin/Release/net10.0'
        counts_paths, binaries = [], {}
        for kind, program in (('primary', 'Program.cs'), ('ineligible', 'Program-ineligible.cs')):
            project = work / f'probe-{label}-{kind}'
            project.mkdir()
            shutil.copyfile(kit / program, project / 'Program.cs')
            shutil.copyfile(kit / 'Counter.csproj', project / 'Counter.csproj')
            target = work / f'bin-{label}-{kind}'
            with (out / f'{label}-{kind}-probe-build.log').open('w') as log:
                subprocess.run(['dotnet', 'build', str(project / 'Counter.csproj'), '-c', 'Release', '-m:1', '/nodeReuse:false',
                    '-p:UseSharedCompilation=false', '-p:IsPackable=false', '-p:RuntimeDirectory=' + str(runtime), '-o', str(target)],
                    stdout=log, stderr=subprocess.STDOUT, check=True)
            for name in ('SharpLink.Runtime.dll', 'SharpLink.Abstractions.dll', 'SharpLink.Sdk.dll'):
                assert (runtime / name).read_bytes() == (target / name).read_bytes(), name
            counts = out / f'{label}-{kind}-counts.psv'
            with counts.open('w') as rows, (out / f'{label}-{kind}-probe-stderr.log').open('w') as errors:
                subprocess.run(['dotnet', str(target / 'SharpLink.UnitTests.dll')], stdout=rows, stderr=errors, check=True)
            counts_paths.append(counts)
            binaries[kind] = dict(runtime_sha256=hashlib.sha256((target / 'SharpLink.Runtime.dll').read_bytes()).hexdigest(),
                probe_sha256=hashlib.sha256((target / 'SharpLink.UnitTests.dll').read_bytes()).hexdigest(),
                dependencies_sha256={name: hashlib.sha256((target / name).read_bytes()).hexdigest() for name in
                    ('SharpLink.Runtime.dll', 'SharpLink.Abstractions.dll', 'SharpLink.Sdk.dll',
                     'SharpLink.UnitTests.deps.json', 'SharpLink.UnitTests.runtimeconfig.json')},
                raw_sha256=hashlib.sha256(counts.read_bytes()).hexdigest())
        subprocess.run(['python3', str(kit / 'validate.py'), '--arm', label, '--output', str(out / f'{label}-matrix-proof.json'),
            *(str(path) for path in counts_paths)], check=True)
        # The negative requires real F business/credit/pool validation first, then
        # fails the same selected default7-RMW criterion that G2 satisfies.
        with (out / f'{label}-default-negative-control.log').open('w') as log:
            checked = subprocess.run(['python3', str(kit / 'check_count.py'), str(counts_paths[0]),
                '--expected', '7', '--scenario', 'default-buffered'], stdout=log, stderr=subprocess.STDOUT)
        assert checked.returncode == (2 if label == 'F' else 0)
        proof[label] = dict(source=source_proof, counter_binaries=binaries,
            boundary='Counter-only business/mechanism correctness. No timing is collected or used as acceptance.')
        (out / 'provenance.json').write_text(json.dumps(proof, indent=2) + '\n')
    finally:
        subprocess.run(['git', 'restore', '--worktree', '--', 'src', 'test'], cwd=root, check=True)
        (root / 'src/SharpLink.Runtime/LifecycleDiagnosticCounters.cs').unlink(missing_ok=True)
        subprocess.run(['git', 'diff', '--exit-code', '--', 'src', 'test'], cwd=root, check=True)
        assert subprocess.check_output(['git', 'write-tree'], cwd=root, text=True).strip() == tree
print('All16 per-arm scenario RMW/request-monitor checks passed, plus exact F-negative/G-positive control. No timing claim.')
