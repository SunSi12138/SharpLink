#!/usr/bin/env python3
"""Offline positive/negative controls for only the new provenance wrapper."""
import copy
import json
import os
import shutil
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import gate


class GateTests(unittest.TestCase):
    def setUp(self):
        self.value = dict(schema_version=1, source_sha=gate.SOURCE, source_tree=gate.TREE, tools_sha='a' * 40)

    def test_exact_request(self):
        self.assertEqual(gate.request(json.dumps(self.value)), self.value)

    def test_request_rejects_identity_schema_and_extra_fields(self):
        for key, value in [('source_sha', 'b' * 40), ('source_tree', 'b' * 40),
                           ('schema_version', True), ('schema_version', 2),
                           ('tools_sha', 'main'), ('tools_sha', None), ('unexpected', 'value')]:
            altered = {**self.value, key: value}
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                gate.request(json.dumps(altered))

    def test_missing_fields_rejected(self):
        for key in self.value:
            altered = copy.copy(self.value)
            del altered[key]
            with self.subTest(key=key), self.assertRaises(ValueError):
                gate.request(json.dumps(altered))

    def test_duplicate_keys_rejected(self):
        text = json.dumps(self.value)[:-1] + ', "source_sha": "' + gate.SOURCE + '"}'
        with self.assertRaisesRegex(ValueError, 'duplicate'):
            gate.request(text)

    def test_source_exact_clean_identity(self):
        with patch.object(gate, 'git', side_effect=[gate.SOURCE, gate.TREE, '']):
            gate.verify_source(Path('.'))

    def test_source_sha_tree_dirty_rejected(self):
        for answers in [('bad',), (gate.SOURCE, 'bad'), (gate.SOURCE, gate.TREE, ' M src/foo.cs'),
                        (gate.SOURCE, gate.TREE, '?? untracked.py')]:
            with self.subTest(answers=answers), patch.object(gate, 'git', side_effect=answers), self.assertRaises(ValueError):
                gate.verify_source(Path('.'))

    def fixture(self, root):
        def run(*args):
            return subprocess.check_output(['git', '-C', str(root), *args], text=True, stderr=subprocess.DEVNULL).strip()
        run('init', '-q')
        run('config', 'user.name', 'Offline Fixture')
        run('config', 'user.email', 'fixture@example.invalid')
        (root / 'tool.py').write_text('# reviewed tool\n')
        run('add', '.')
        run('commit', '-qm', 'tools')
        tools_sha = run('rev-parse', 'HEAD')
        self.value['tools_sha'] = tools_sha
        path = root / gate.REQUEST
        path.parent.mkdir(parents=True)
        path.write_text(json.dumps(self.value))
        run('add', '.')
        run('commit', '-qm', 'request only')
        return run

    def test_real_request_only_commit(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run = self.fixture(root)
            self.assertEqual(gate.verify_tools(root, run('rev-parse', 'HEAD'), gate.BRANCH), self.value)

    def test_wrong_branch_and_workflow_checkout_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run = self.fixture(root)
            for sha, ref in [(run('rev-parse', 'HEAD'), 'refs/heads/main'), ('c' * 40, gate.BRANCH)]:
                with self.subTest(sha=sha, ref=ref), self.assertRaises(ValueError):
                    gate.verify_tools(root, sha, ref)

    def test_request_cannot_smuggle_product_or_tool_edit(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run = self.fixture(root)
            (root / 'tool.py').write_text('# replaced after review\n')
            run('add', '.')
            run('commit', '--amend', '--no-edit', '-q')
            with self.assertRaisesRegex(ValueError, 'only add request'):
                gate.verify_tools(root, run('rev-parse', 'HEAD'), gate.BRANCH)

    def test_dirty_tools_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run = self.fixture(root)
            (root / 'untracked').write_text('unexpected')
            with self.assertRaisesRegex(ValueError, 'dirty'):
                gate.verify_tools(root, run('rev-parse', 'HEAD'), gate.BRANCH)

    def test_wrong_tools_parent_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run = self.fixture(root)
            self.value['tools_sha'] = 'a' * 40
            (root / gate.REQUEST).write_text(json.dumps(self.value))
            run('add', '.')
            run('commit', '--amend', '--no-edit', '-q')
            with self.assertRaisesRegex(ValueError, 'reviewed tools parent'):
                gate.verify_tools(root, run('rev-parse', 'HEAD'), gate.BRANCH)



if __name__ == '__main__':
    unittest.main()
