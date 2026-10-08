#!/usr/bin/env python3
"""Parser/source fixtures only; no fixture is runtime or mechanism proof."""
import pathlib
import subprocess
import tempfile
import contextlib
import io
import json
import os
from unittest.mock import patch

from codegen import OLD, KIT, SOURCE, CONFIG, CASES, module, selected_roots, match_pair, validate_config
import package as packager

subprocess.run(['python3', str(OLD / 'self-tests.py')], check=True)
inventory = module(OLD / 'inventory.py', 'frozen_inventory').inventory
method = 'SharpLink.Client.ClientConnection+<SendClientStreamAsync>d__62`1[System.__Canon]:MoveNext():this'


def block(tier='Tier1', pgo='Synthesized PGO', complete=True):
    return (f'; Assembly listing for method {method} ({tier})\n; optimized code\n'
            f'; optimized using {pgo}\n; with {pgo}: fgCalledCount is 1\n'
            '       55                   push rbp\n' + ('; Total bytes of code 1\n' if complete else ''))


with tempfile.TemporaryDirectory() as temporary:
    root = pathlib.Path(temporary)
    for index, (tier, pgo, complete, expected) in enumerate([
        ('Tier1', 'Synthesized PGO', True, 1),
        ('Tier1', 'Dynamic PGO', True, 1),
        ('Tier1', 'Static PGO', True, 1),
        ('Tier1', 'Blended PGO', True, 1),
        ('Instrumented Tier0', 'Synthesized PGO', True, 0),
        ('Instrumented Tier1', 'Dynamic PGO', True, 0),
        ('Tier1', 'Synthesized PGO', False, 0),
    ]):
        raw = root / f'{index}.txt'
        raw.write_text(block(tier, pgo, complete))
        captured = inventory(raw, root / str(index), 'Client100x16')
        selected = selected_roots(captured, 'SendClientStreamAsync')
        assert len(selected) == expected
        assert all(row['tier'] == tier and row['pgo_source'] == pgo for row in selected)
        if pgo != 'Dynamic PGO':
            assert captured['status'] == 'inconclusive', 'Original strict verdict must not be relabeled'
    raw = root / 'int32.txt'
    raw.write_text(block().replace('[System.__Canon]', '[int]'))
    captured = inventory(raw, root / 'int32', 'Client100x16')
    assert not selected_roots(captured, 'SendClientStreamAsync'), 'Int32 cannot prove the byte[] shared root'
print('8 readonly-lease tier/instantiation fixtures passed; no code path or size claim is proved.')

with tempfile.TemporaryDirectory() as temporary:
    root = pathlib.Path(temporary)
    for failure in ('oversized-arm', 'hash-failure', 'copy-failure', 'oversized-safety'):
        source, output = root / failure / 'source', root / failure / 'upload'
        (source / 'jit/R').mkdir(parents=True)
        (source / 'jit/R/good.json').write_text('{}')
        (source / 'jit/G2').mkdir()
        (source / 'jit/G2/bad.json').write_text('{}')
        (source / 'default').mkdir()
        (source / 'default/safety.log').write_text('Retained safety result')
        if failure.startswith('oversized'):
            target = source / ('default/oversized.log' if failure == 'oversized-safety' else 'jit/G2/oversized.log')
            with target.open('wb') as stream:
                stream.truncate(packager.BUDGET)
        environment_output = root / (failure + '-outputs.txt')
        original_sha, original_copy = packager.sha, packager.shutil.copytree

        def checked_sha(path):
            if failure == 'hash-failure' and path.name == 'bad.json':
                raise OSError('fixture hash error')
            return original_sha(path)

        def checked_copy(source_path, *args, **kwargs):
            if failure == 'copy-failure' and source_path.name == 'G2':
                raise OSError('fixture copy error')
            return original_copy(source_path, *args, **kwargs)

        with patch.dict(os.environ, GITHUB_OUTPUT=str(environment_output)), \
                patch.object(packager, 'sha', checked_sha), \
                patch.object(packager.shutil, 'copytree', checked_copy), \
                contextlib.redirect_stdout(io.StringIO()):
            try:
                packager.package(source, output)
                raise AssertionError('A packaging error must fail the step')
            except SystemExit as error:
                assert error.code == 1
        ready = environment_output.read_text()
        assert 'ready_jit_R=true' in ready and 'ready_safety=true' in ready
        assert (output / 'jit-R/good.json').read_text() == '{}'
        failures = json.loads((output / 'safety/packaging-failures.json').read_text())
        assert len(failures) == 1
        if failure == 'oversized-safety':
            assert 'ready_jit_G2=true' in ready and failures[0]['artifact'] == 'safety'
            assert not (output / 'safety/default/oversized.log').exists()
        else:
            assert 'ready_jit_G2=true' not in ready and not (output / 'jit-G2').exists()
            assert (output / 'safety/default/safety.log').is_file()
        assert all(packager.budget(path) < packager.BUDGET for path in output.iterdir())
print('4 packaging failure fixtures passed; valid evidence remains independently uploadable.')


# Exact matching may normalize compiler-generated ordinals, never tier/profile,
# canonical instantiation, or OSR identity. Collection success remains pending review.
base = dict(method=method, tier='Tier1', pgo_source='Synthesized PGO',
            profile_source='Synthesized PGO', osr_entry=None)
wrapper = dict(base, method='SharpLink.Client.ClientConnection:SendClientStreamAsync[System.__Canon]():this')
assert match_pair([base, wrapper], [dict(base, method=method.replace('d__62', 'd__91')), wrapper])['wrapper_and_movenext_matched']
for field, value in [('tier', 'Tier1-OSR'), ('pgo_source', 'Dynamic PGO'),
                     ('profile_source', 'Dynamic PGO'), ('osr_entry', '0x10')]:
    assert not match_pair([base, wrapper], [dict(base, **{field: value}), dict(wrapper, **{field: value})])['wrapper_and_movenext_matched']
assert not match_pair([base], [base])['wrapper_and_movenext_matched']
assert not match_pair([base, wrapper], [dict(base, method=method.replace('System.__Canon', 'int')), wrapper])['wrapper_and_movenext_matched']
print('7 exact tier/profile/OSR/canonical-pair fixtures passed; none proves byte[] path mapping.')

validate_config()
assert len(CASES) == 4 and CONFIG['launches'] == 8
workflow = (SOURCE / '.github/workflows/742-readonly-lease-preflight.yml').read_text()
assert 'permissions:\n  contents: read\n' in workflow
assert 'write' not in workflow.split('permissions:', 1)[1].split('concurrency:', 1)[0]
assert 'download-artifact' not in workflow and 'reuse-safety' not in workflow
assert 'safety.py default' in workflow and 'safety.py experimental' in workflow
expected = ['safety', 'jit-index', 'jit-G2-build', 'jit-R-build', 'native-index']
expected += ['jit-' + arm + '-' + transport + '-' + scenario for transport, scenario in CASES for arm in ('G2', 'R')]
expected += ['native-' + arm + '-' + kind for arm in ('G2', 'R') for kind in ('elf', 'code')]
for name in expected:
    assert 'steps.package.outputs.ready_' + name.replace('-', '_') + " == 'true'" in workflow
    assert 'path: artifacts/readonly-lease-upload/' + name + '\n' in workflow
import re
assert all(re.fullmatch(r'actions/(?:checkout|setup-dotnet|upload-artifact)@[0-9a-f]{40}', action)
           for action in re.findall(r'uses: ([^\s]+)', workflow))
assert workflow.count('uses: actions/upload-artifact@') == len(expected) == 17
assert "paths:\n      - '.github/workflows/742-readonly-lease-preflight.yml'\n      - 'eng/preflight-742-readonly-lease/**'" in workflow
print('Frozen runtime/workload and scoped read-only pinned workflow with all17 artifact destinations verified.')

contract = module(KIT / 'source-contract.py', 'readonly_source_contract')
with tempfile.TemporaryDirectory() as temporary:
    root = pathlib.Path(temporary)
    control, candidate = root / 'G2', root / 'R'
    for name in contract.FILES:
        original = ('internal ValueTask ' + contract.INNER + '<T>(\n    ' + contract.LEASE + ')\n    {\n        return ValueTask.CompletedTask;\n    }'
                    if name.endswith('PreCreditStreaming.cs') else
                    'return ' + contract.INNER + '(requestId, streamId, item, codec, token, creditLease, deadline);')
        for folder, text in ((control, original), (candidate, contract.candidate_text(name, original))):
            path = folder / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text)
    with contextlib.redirect_stdout(io.StringIO()):
        contract.verify(control, candidate)
    for name in contract.FILES:
        path = candidate / name
        text = path.read_text()
        path.write_text(text + '\n// Extra production change\n')
        try:
            contract.verify(control, candidate)
            raise RuntimeError('Out-of-scope edit escaped source contract')
        except AssertionError:
            pass
        path.write_text(text)
print('4 exact-production-edit fixtures passed; source preservation is distinct from codegen proof.')

with tempfile.TemporaryDirectory() as temporary:
    root = pathlib.Path(temporary)
    # Missing identities fail at the first Python-only source read, before any
    # dotnet/host execution. Exercise artifact retention for both entry points.
    for mode in ('build', 'run'):
        output = root / mode
        command = ['python3', str(KIT / 'codegen.py'), mode, str(root / 'missing-work'), str(output)]
        if mode == 'run':
            command += ['--affinity', '0,1,2,3']
        result = subprocess.run(command, capture_output=True, text=True)
        assert result.returncode != 0
        failure = json.loads((output / 'index' / (mode + '-collection-failure.json')).read_text())
        assert failure['phase'] == mode and failure['status'] == 'failed'
        assert 'FileNotFoundError' in failure['error'] and not failure['traceback_truncated']
        assert 'identities.json' in (output / 'index' / (mode + '-collection-failure.txt')).read_text()
print('2 Python-only failed-entry fixtures retain failure reasons without reaching any C# execution.')
