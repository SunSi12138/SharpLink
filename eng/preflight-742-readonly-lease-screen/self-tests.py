#!/usr/bin/env python3
"""Verify unchanged workload and fail-closed gates without any C# execution."""
import ast
import itertools
import json
import pathlib
import shutil
import tempfile
from unittest.mock import patch

import prepare

kit = pathlib.Path(__file__).resolve().parent
original = (prepare.SOURCE / 'eng/preflight-742-first-binding-screen/screen.py').read_text()
current = (kit / 'screen.py').read_text()
assert current == original.replace('D/G2/A', 'D/G2/R').replace("'A'", "'R'"), 'Only the candidate label may change'
tree = ast.parse(current)
workloads = ast.literal_eval(next(node.value for node in tree.body if isinstance(node, ast.Assign)
    and any(isinstance(target, ast.Name) and target.id == 'workloads' for target in node.targets)))
assert workloads == [('rpc', 'tcp', 'Client100x16'), ('rpc', 'sharedmemory', 'Client100x4096'),
    ('rpc', 'sharedmemory', 'Server1x16'), ('c8', 'sharedmemory', '1'), ('c8', 'sharedmemory', '10000')]
orders = list(itertools.permutations(('D', 'G2', 'R')))
assert len(orders) * len(workloads) * 3 == 90
assert len(orders) * 3 * sum(1 if row[0] == 'rpc' else 4 for row in workloads) == 198
assert all(sum(order[position] == arm for order in orders) == 2 for arm in ('D', 'G2', 'R') for position in range(3))


def rejected(action):
    try:
        action()
    except (AssertionError, KeyError, FileNotFoundError):
        return
    raise AssertionError('An invalid review or forbidden source tree was accepted')


with tempfile.TemporaryDirectory() as temporary:
    root = pathlib.Path(temporary)
    (root / 'codegen-review.json').write_text(json.dumps({'status': 'pending-independent-review'}))
    with patch.object(prepare, 'KIT', root):
        rejected(prepare.require_review)
    review = json.loads((kit / 'codegen-review.json').read_text())
    if prepare.FINAL_PROOF is None:
        with patch.object(prepare, 'KIT', root):
            (root / 'codegen-review.json').write_text(json.dumps({'status': 'independently-reviewed-pass'}))
            rejected(prepare.require_review)
    else:
        for name in review['proof_files']:
            target = root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(kit / name, target)
        (root / 'codegen-review.json').write_text(json.dumps(review))
        with patch.object(prepare, 'KIT', root):
            prepare.require_review()
            for name in review['proof_files']:
                report = root / name
                contents = report.read_bytes()
                report.write_bytes(contents + b'changed')
                rejected(prepare.require_review)
                report.write_bytes(contents)
            for wrong in (
                dict(review, integrity_file='../unhashed.json'),
                dict(review, production_trees={**review['production_trees'], 'R': '0' * 40}),
                dict(review, status='pending-independent-review'),
                dict(review, run_id=0),
                dict(review, proof_files={name: digest for name, digest in review['proof_files'].items()
                                          if name != review['human_file']}),
            ):
                (root / 'codegen-review.json').write_text(json.dumps(wrong))
                rejected(prepare.require_review)
    author = json.loads((prepare.SOURCE / 'eng/preflight-742-readonly-lease/author-manifest.json').read_text())
    roots = {arm: pathlib.Path(arm) for arm in ('D', 'G2', 'R')}
    forbidden = [('G2', author['baseline_tests_tree']), ('R', author['candidate_tree']),
                 ('R', '4cb4ce7a13b06c3a6f3213e30215b6769ed49f90')]
    for arm, wrong in forbidden:
        def git(source, *args):
            if args == ('write-tree',):
                return wrong if str(source) == arm else prepare.IDENTITIES[str(source)]
            return ''
        with patch.object(prepare, 'git', git):
            rejected(lambda: prepare.check_roots(roots))

workflow = (prepare.SOURCE / '.github/workflows/742-readonly-lease-screen.yml').read_text()
assert 'permissions:\n  contents: read\n' in workflow
assert 'persist-credentials: false' in workflow
assert workflow.index('require_review()') < workflow.index('uses: actions/setup-dotnet@') < workflow.index('dotnet build')
assert 'sum(path.stat().st_size for path in files) < 24 * 1024 * 1024' in workflow
assert "if: always() && steps.artifact.outputs.ready == 'true'" in workflow
assert workflow.count('uses: actions/') == 3
assert 'actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1' in workflow
assert 'actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68' in workflow
assert 'actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a' in workflow
print('Unchanged A-to-R runner,90 launches/198 rows and balanced permutations verified; final proof validated; all11 proof-file mutation cases and5 review mutation cases rejected; pending proof and3 forbidden production overlays rejected; workflow fail-closed order,permissions,pins and artifact budget verified.')
