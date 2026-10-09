#!/usr/bin/env python3
"""Fail-closed machine checks; no workload execution or source modification."""
import ast
import hashlib
import itertools
import pathlib
import statistics
import tempfile
from unittest.mock import patch

import prepare

kit = pathlib.Path(__file__).resolve().parent
assert prepare.sha(kit / 'common.py') == '474b4d628cc3c6529581a04f4404719530f15335893dc891a1dd505cbb1ef564'
source = (kit / 'screen.py').read_text()
tree = ast.parse(source)

def assignment(name):
    return next(n.value for n in tree.body if isinstance(n, ast.Assign)
                and any(isinstance(t, ast.Name) and t.id == name for t in n.targets))

workloads = ast.literal_eval(assignment('workloads'))
assert workloads == [('rpc', 'tcp', 'Client100x16'), ('rpc', 'sharedmemory', 'Client100x4096'),
                     ('rpc', 'sharedmemory', 'Server1x16'), ('c8', 'sharedmemory', '1'),
                     ('c8', 'sharedmemory', '10000')]
namespace = {'itertools': itertools, 'workloads': workloads}
for name in ('orders', 'plan'):
    namespace[name] = eval(compile(ast.Expression(assignment(name)), '<plan>', 'eval'), namespace)
orders, plan = namespace['orders'], namespace['plan']
assert orders == list(itertools.permutations(('D', 'P', 'R')))
assert len(plan) == 90 and len(set(plan)) == 90
assert plan == [(block, kind, transport, case, label) for block, order in enumerate(orders)
                for kind, transport, case in workloads for label in order]
assert sum(1 if launch[1] == 'rpc' else 4 for launch in plan) == 198
assert all(sum(order[position] == arm for order in orders) == 2
           for arm in ('D', 'P', 'R') for position in range(3))
# The entire inherited launch/final-integrity block is structurally unchanged:
# command arguments, environment, tracing removals, timeout, no retries and byte checks.
launch = next(n for n in tree.body if isinstance(n, ast.Try))
assert hashlib.sha256(ast.dump(launch, include_attributes=False).encode()).hexdigest() == \
    '670b061d59b8dd1e61839e010d405d9ef52ad07d877ba9c63ee66572da73f9dc'
assert "assert len(exits) == 90 and not failures and len(rows) == 198" in source
assert "(('D', 'P'), ('D', 'R'), ('P', 'R'))" in source
# Exercise postprocessing independently of a timer or .NET runtime.
post = next(n for n in tree.body if isinstance(n, ast.For) and isinstance(n.target, ast.Tuple)
            and [v.id for v in n.target.elts] == ['kind', 'transport', 'case'])
rows = []
for block, kind, transport, case, arm in plan:
    for shape in ([case] if kind == 'rpc' else ['unary', 'c2s', 's2c', 'duplex']):
        value = {'D': 100, 'P': 200, 'R': 300}[arm]
        rows.append(dict(block=block, kind=kind, transport=transport, case=case,
                         label=arm, shape=shape, rate=value, cpu_us_op=value, bytes_op=value))
ns = dict(rows=rows, roots=dict.fromkeys(('D', 'P', 'R')), workloads=workloads,
          statistics=statistics, comparisons=[], normalized=[])
exec(compile(ast.Module(body=[post], type_ignores=[]), '<postprocessing>', 'exec'), ns)
assert len(ns['comparisons']) == 33 and len(ns['normalized']) == 11
for row in ns['comparisons']:
    ratio = {'D': 100, 'P': 200, 'R': 300}[row['candidate']] / {'D': 100, 'P': 200, 'R': 300}[row['baseline']]
    for values in row['metrics'].values():
        assert values['paired_percent'] == [(ratio - 1) * 100] * 6
        assert values['ratio_of_medians_percent'] == (ratio - 1) * 100
for row in ns['normalized']:
    for arm, factor in (('D', 1), ('P', 2), ('R', 3)):
        assert set(row['dev_normalized'][arm].values()) == {factor}
    divisor = (100 if row['case'].startswith('Client100') else 1) if row['kind'] == 'rpc' else (1 if row['shape'] == 'unary' else int(row['case']))
    assert row['absolute_medians']['D']['bytes_item'] == 100 / divisor
for name, digest in prepare.PATCHES.items():
    assert prepare.sha(kit / name) == digest

def rejected(action):
    try:
        action()
    except (AssertionError, FileNotFoundError, KeyError):
        return
    raise AssertionError('Forbidden input was accepted')

with tempfile.TemporaryDirectory() as temporary:
    root = pathlib.Path(temporary)
    roots = {arm: root / arm for arm in ('D', 'P', 'R')}
    for arm_root in roots.values():
        for name in prepare.HARNESS:
            path = arm_root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b'common harness')
    def valid_git(where, *args):
        arm = where.name
        if args == ('write-tree',):
            return 'dev-tree' if arm == 'D' else prepare.IDENTITIES[arm]
        if args == ('rev-parse', 'HEAD'):
            return prepare.DEV
        if args == ('rev-parse', prepare.DEV + '^{tree}'):
            return 'dev-tree'
        return ''
    with patch.object(prepare, 'git', valid_git):
        assert prepare.check_roots(roots) == prepare.IDENTITIES
    mutations = [
        ('P', ('write-tree',), 'test-overlay'),
        ('R', ('write-tree',), 'test-overlay'),
        ('D', ('write-tree',), 'wrong-dev-index'),
        ('D', ('rev-parse', 'HEAD'), 'wrong-dev-head'),
    ]
    for arm in roots:
        mutations += [(arm, ('diff', '--name-only'), 'src/forbidden.cs'),
                      (arm, ('ls-files', '--others', '--exclude-standard', '--', 'src', 'test'),
                       'test/untracked.cs')]
    for arm, command, result in mutations:
        def mutated_git(where, *args):
            return result if where.name == arm and args == command else valid_git(where, *args)
        with patch.object(prepare, 'git', mutated_git):
            rejected(lambda: prepare.check_roots(roots))
    with patch.object(prepare, 'git', valid_git):
        for arm in ('D', 'R'):
            path = roots[arm] / prepare.HARNESS[0]
            path.write_bytes(b'drift')
            rejected(lambda: prepare.check_roots(roots))
            path.write_bytes(b'common harness')
    patch_root = root / 'patches'
    patch_root.mkdir()
    for name in prepare.PATCHES:
        (patch_root / name).write_bytes((kit / name).read_bytes())
    for name in prepare.PATCHES:
        path = patch_root / name
        original = path.read_bytes()
        path.write_bytes(original + b'changed')
        with patch.object(prepare, 'KIT', patch_root), patch.object(prepare.subprocess, 'run') as run:
            rejected(lambda: prepare.prepare(root / 'destination', root / 'output'))
            run.assert_not_called()
            assert not (root / 'destination').exists()
        path.write_bytes(original)

workflow = (prepare.SOURCE / '.github/workflows/742-three-arm.yml').read_text()
for text in ('permissions:\n  contents: read\n', 'persist-credentials: false', 'fetch-depth: 0',
             'dotnet-version: 10.0.112', 'assert max(versions) == (10, 0, 12)',
             'available[:4]', 'for label in D P R;',
             '-c Release -p:PublishAot=false -m:1 /nodeReuse:false -p:UseSharedCompilation=false',
             'sum(path.stat().st_size for path in files) < 24 * 1024 * 1024',
             "if: always() && steps.artifact.outputs.ready == 'true'"):
    assert text in workflow, text
assert workflow.index('/self-tests.py') < workflow.index('/prepare.py') < workflow.index('dotnet build') < workflow.index('/screen.py')
assert workflow.count('uses: actions/') == 3
for action in ('checkout@3d3c42e5aac5ba805825da76410c181273ba90b1',
               'setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68',
               'upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a'):
    assert 'actions/' + action in workflow
print('PASS: unchanged validators and launch block; balanced 90 launches/198 rows; source, harness and patch mutation rejection; pinned workflow runtime/build/artifact constraints.')
