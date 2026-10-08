#!/usr/bin/env python3
"""Untimed selected-method NoPGO shape proof, linked to the actual timing DLLs."""
import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile

root, out = (pathlib.Path(arg).resolve() for arg in sys.argv[1:3])
kit = pathlib.Path(__file__).resolve().parent
spec = json.loads((kit / 'adapter.json').read_text())
out.mkdir(parents=True, exist_ok=True)
work = pathlib.Path(tempfile.mkdtemp(prefix='742-shape-', dir=os.environ.get('RUNNER_TEMP')))
assert not work.is_relative_to(out)
for name in ('Program.cs', 'Shape.csproj'):
    expected = spec['disassembly_source_sha256'][name]
    assert hashlib.sha256((kit / 'disasm' / name).read_bytes()).hexdigest() == expected
    shutil.copyfile(kit / 'disasm' / name, work / name)
assert hashlib.sha256((kit / 'disasm/normalize-disassembly.py').read_bytes()).hexdigest() == spec['disassembly_source_sha256']['normalize-disassembly.py']
runtime = root / 'test/SharpLink.Benchmarks/bin/Release/net10.0'
files = ('SharpLink.Runtime.dll', 'SharpLink.Abstractions.dll', 'SharpLink.Sdk.dll')
original = {name: hashlib.sha256((runtime / name).read_bytes()).hexdigest() for name in files}
target = work / 'bin'
with (out / 'build.log').open('w') as log:
    subprocess.run(['dotnet', 'build', str(work / 'Shape.csproj'), '-c', 'Release', '-m:1', '/nodeReuse:false',
        '-p:UseSharedCompilation=false', '-p:IsPackable=false', '-p:RuntimeDirectory=' + str(runtime), '-o', str(target)],
        stdout=log, stderr=subprocess.STDOUT, check=True)
for name in files:
    assert (runtime / name).read_bytes() == (target / name).read_bytes(), name
hashes = {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in target.iterdir() if path.is_file()}
proof = dict(source=spec['diagnostic_tree'], timed_host_dependencies=original, probe_binaries=hashes,
    flags=dict(DOTNET_ReadyToRun='0', DOTNET_TieredCompilation='0', DOTNET_TieredPGO='0'),
    boundary='Untimed selected-method non-tiered probe. Same timing Runtime bytes; not proof of all timed native code or primary tiered-PGO code identity.')
(out / 'provenance.json').write_text(json.dumps(proof, indent=2) + '\n')
for mode in ('0', '1'):
    env = dict(os.environ, DOTNET_ReadyToRun='0', DOTNET_TieredCompilation='0', DOTNET_TieredPGO='0',
        DOTNET_JitDisasm='SharpLink.Runtime.StreamManager:CompleteStream SharpLink.Runtime.QuiescentCompletionDiagnostic:IsEnabled',
        DOTNET_JitStdOutFile=str(out / f'jit-mode-{mode}.txt'))
    env[spec['flag_name']] = mode
    with (out / f'mode-{mode}.log').open('w') as log:
        subprocess.run(['dotnet', str(target / 'SharpLink.UnitTests.dll')], env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
    text = (out / f'mode-{mode}.log').read_text()
    assert ('MODE=' + ('True' if mode == '1' else 'False')) in text
    assert text.count('CASE_OK ') == 4
subprocess.run(['python3', str(kit / 'disasm/normalize-disassembly.py'), str(out / 'jit-mode-0.txt'),
    str(out / 'jit-mode-1.txt'), str(out / 'jit-normalized')], check=True)
assert original == {name: hashlib.sha256((runtime / name).read_bytes()).hexdigest() for name in files}
assert hashes == {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in target.iterdir() if path.is_file()}
