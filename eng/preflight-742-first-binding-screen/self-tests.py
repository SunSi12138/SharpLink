#!/usr/bin/env python3
"""Verify the unchanged screen population and fail-closed source/review gates."""
import ast
import itertools
import json
import pathlib
import shutil
import tempfile
from unittest.mock import patch

import prepare

kit = pathlib.Path(__file__).resolve().parent
original = (prepare.SOURCE / 'eng/preflight-742-send-validator-screen/screen.py').read_text()
current = (kit / 'screen.py').read_text()
assert current == original.replace('D/G2/V', 'D/G2/A').replace("'V'", "'A'"), 'Only the candidate label may change'
tree = ast.parse(current)
workloads = ast.literal_eval(next(node.value for node in tree.body if isinstance(node, ast.Assign)
    and any(isinstance(target, ast.Name) and target.id == 'workloads' for target in node.targets)))
orders = list(itertools.permutations(('D', 'G2', 'A')))
assert len(orders) * len(workloads) * 3 == 90
assert len(orders) * 3 * sum(1 if row[0] == 'rpc' else 4 for row in workloads) == 198
assert all(sum(order[position] == arm for order in orders) == 2 for arm in ('D', 'G2', 'A') for position in range(3))


def rejected(action):
    try:
        action()
    except (AssertionError, KeyError, FileNotFoundError):
        return
    raise AssertionError('A pending review or test overlay was accepted')


with tempfile.TemporaryDirectory() as temporary:
    root = pathlib.Path(temporary)
    (root / 'codegen-review.json').write_text(json.dumps({'status': 'pending-independent-review'}))
    with patch.object(prepare, 'KIT', root):
        rejected(prepare.require_review)
    review = json.loads((kit / 'codegen-review.json').read_text())
    for name in review['proof_files']:
        shutil.copyfile(kit / name, root / name)
    (root / 'codegen-review.json').write_text(json.dumps(review))
    with patch.object(prepare, 'KIT', root):
        prepare.require_review()
        report = root / 'independent-compiled-review.md'
        contents = report.read_bytes()
        report.write_bytes(contents + b'changed')
        rejected(prepare.require_review)
        report.write_bytes(contents)
        wrong = dict(review, integrity_file='../unhashed.json')
        (root / 'codegen-review.json').write_text(json.dumps(wrong))
        rejected(prepare.require_review)
        wrong = dict(review, production_trees={**review['production_trees'], 'A': '0' * 40})
        (root / 'codegen-review.json').write_text(json.dumps(wrong))
        rejected(prepare.require_review)
    author = json.loads((prepare.SOURCE / 'eng/preflight-742-first-binding/author-manifest.json').read_text())
    roots = {arm: pathlib.Path(arm) for arm in ('D', 'G2', 'A')}
    for arm, wrong in (('G2', author['baseline_tests_tree']), ('A', author['candidate_tree'])):
        def git(source, *args):
            if args == ('write-tree',):
                return wrong if str(source) == arm else prepare.IDENTITIES[str(source)]
            return ''
        with patch.object(prepare, 'git', git):
            rejected(lambda: prepare.check_roots(roots))
print('Unchanged runner,90 launches/198 rows and balanced permutations verified; final proof passes;6 pending/hash/pointer/source/overlay cases rejected.')
