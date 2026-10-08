#!/usr/bin/env python3
"""Reconstruct H2 with only three formatter-required test initializer changes."""
import hashlib
import json
import pathlib
import subprocess
import sys

kit = pathlib.Path(__file__).resolve().parent
source = kit.parents[1]
destination = pathlib.Path(sys.argv[1]).resolve()
original_revision = '07593f20b70f0fa403e0a483916467b26f366f4b'
h_tree = '522585079b97c99f7986eb50f3ed6c69d4086bfb'
h2_tree = 'dd15220add5703f5ecebe7367644d13775b60c0f'
relative = 'test/SharpLink.UnitTests/Runtime/StreamFlowControllerDirectionalGateTests.cs'
patch = kit / 'format-only.patch'
assert hashlib.sha256(patch.read_bytes()).hexdigest() == 'b9139359ea9c7256e87ec56225ead690a06973e479d3e6155a9fc4a332fc9b1c'
for name in ('prepare.py', 'gates.patch'):
    path = f'eng/preflight-742-gates/{name}'
    expected = subprocess.check_output(['git', 'show', f'{original_revision}:{path}'], cwd=source)
    assert (source / path).read_bytes() == expected, f'Original H reconstruction changed: {path}'
subprocess.run(['python3', 'eng/preflight-742-gates/prepare.py', str(destination)], cwd=source, check=True)
root = destination / 'candidate'
def git(*args):
    return subprocess.check_output(['git', *args], cwd=root, text=True).strip()
assert git('write-tree') == h_tree
old = (root / relative).read_text()
old_src_tree = git('rev-parse', f'{h_tree}:src')
subprocess.run(['git', 'apply', '--check', '--index', str(patch)], cwd=root, check=True)
subprocess.run(['git', 'apply', '--index', str(patch)], cwd=root, check=True)
assert git('write-tree') == h2_tree
assert git('diff', '--name-only', h_tree, h2_tree).splitlines() == [relative]
new = (root / relative).read_text()
marker = '        }) { IsBackground = true };'
assert old.count(marker) == 3
assert new == old.replace(marker, '        })\n        {\n            IsBackground = true\n        };')
assert ''.join(old.split()) == ''.join(new.split())
assert git('rev-parse', f'{h2_tree}:src') == old_src_tree
out = root / 'artifacts/first-receive-integration'
proof = json.loads((out / 'provenance.json').read_text())
(out / 'original-h-provenance.json').write_text(json.dumps(proof, indent=2) + '\n')
proof.update(disposable_tree=h2_tree, original_h_tree=h_tree, source_runtime_tree=old_src_tree,
    formatting_patch_sha256=hashlib.sha256(patch.read_bytes()).hexdigest(),
    formatting_only_changed_paths=[relative], production_runtime_identical_to_h=True,
    change='H2: formatter-only whitespace for three Thread initializers in one test file',
    acceptance='Correctness recovery only. Running original H measurements are preserved, not rerun or relabeled.')
full_patch = subprocess.check_output(['git', 'diff', '--cached', '--binary'], cwd=root)
proof['patch_sha256'] = hashlib.sha256(full_patch).hexdigest()
proof['source_blobs'][relative] = git('hash-object', relative)
(out / 'candidate.patch').write_bytes(full_patch)
(out / 'sources' / relative).write_text(new)
(out / 'provenance.json').write_text(json.dumps(proof, indent=2) + '\n')
identities = json.loads((destination / 'identities.json').read_text())
identities['candidate'] = proof
(destination / 'identities.json').write_text(json.dumps(identities, indent=2) + '\n')
print(json.dumps(proof, indent=2))
