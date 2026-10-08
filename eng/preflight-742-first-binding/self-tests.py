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

from codegen import OLD, module, selected_roots
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
print('8 first-binding tier/instantiation fixtures passed; no code path or size claim is proved.')

with tempfile.TemporaryDirectory() as temporary:
    root = pathlib.Path(temporary)
    for failure in ('oversized-arm', 'hash-failure', 'copy-failure', 'oversized-safety'):
        source, output = root / failure / 'source', root / failure / 'upload'
        (source / 'jit/A').mkdir(parents=True)
        (source / 'jit/A/good.json').write_text('{}')
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
        assert 'ready_jit_A=true' in ready and 'ready_safety=true' in ready
        assert (output / 'jit-A/good.json').read_text() == '{}'
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
