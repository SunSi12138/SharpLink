#!/usr/bin/env python3
"""Expose the first captured pre-cancellation stall, without executing the experiment."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import unittest
import uuid

HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location('retained_audit', HERE / 'audit-flow-state-retained.py')
AUDIT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AUDIT)
ARTIFACT = 10814223986
DIGEST = '54f35129757a437bd25eedc056c14581a8656ec15d80b22be3e78f09aae7d4e3'
HEAD = '0991a079c9aff2423afc8299737260525fde01e5'


def extract(text):
    marker = 'STALL-SNAPSHOT mode='
    if 'FAILED ' not in text or marker not in text:
        raise ValueError('Expected a failed case with a pre-cancellation snapshot')
    pieces = text.rsplit(marker, 2)
    tail = marker + marker.join(pieces[1:])
    if len(tail) > 150000:
        raise ValueError('Snapshot exceeds log review bound; raw evidence is retained')
    return tail


def inspect(output):
    output.mkdir(parents=True, exist_ok=True)
    root = output / 'original'
    if root.exists():
        raise FileExistsError('Do not overwrite the retained snapshot')
    root.mkdir()
    data = AUDIT.download(ARTIFACT, os.environ['GH_TOKEN'])
    (output / 'original.zip').write_bytes(data)
    AUDIT.unpack(data, DIGEST, root)
    captured = []
    for path in sorted(root.glob('*.log')):
        text = path.read_text(errors='replace')
        if 'FAILED ' in text and 'STALL-SNAPSHOT mode=' in text:
            captured.append((path.name, extract(text)))
    if not captured:
        raise ValueError('The recorded diagnostic failure is missing')
    report = [f'# Captured ready-writer stall\n\nDiagnostic source `{HEAD}`, artifact `{ARTIFACT}`, SHA256 `{DIGEST}` verified. These observations are not timing samples or an atomic state snapshot.\n']
    for name, text in captured:
        report.append(f'## {name}\n\nLast two live snapshots and the unchanged case failure:\n\n```text\n{text}\n```\n')
    report.append('Original files, earlier snapshots and nonzero process exits remain in original/. No runtime was rerun or modified by this inspection.\n')
    text = '\n'.join(report)
    (output / 'snapshot.md').write_text(text)
    marker = uuid.uuid4().hex
    print(f'::stop-commands::{marker}', flush=True)
    print(text, flush=True)
    print(f'::{marker}::', flush=True)
    summary = os.environ.get('GITHUB_STEP_SUMMARY')
    if summary:
        with open(summary, 'a') as stream:
            stream.write(text)


class Guards(unittest.TestCase):
    def test_live_snapshots_and_failure_are_preserved(self):
        text = 'header\nSTALL-SNAPSHOT mode=A first\nSTALL-SNAPSHOT mode=A second\nSTALL-SNAPSHOT mode=A third\nFAILED A timeout'
        result = extract(text)
        self.assertNotIn('first', result)
        self.assertIn('second', result)
        self.assertIn('third', result)
        self.assertTrue(result.endswith('FAILED A timeout'))

    def test_success_and_post_cancellation_only_are_rejected(self):
        for text in ('STALL-SNAPSHOT mode=A\nPASS', 'FAILED A timeout', ''):
            with self.assertRaises(ValueError):
                extract(text)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--self-test', action='store_true')
    parser.add_argument('--output', type=Path, default=Path('artifacts/retained-flow-state-audit/captured-stall'))
    args = parser.parse_args()
    if args.self_test:
        result = unittest.TextTestRunner().run(unittest.defaultTestLoader.loadTestsFromTestCase(Guards))
        raise SystemExit(0 if result.wasSuccessful() else 1)
    inspect(args.output)
