#!/usr/bin/env python3
"""Revalidate fixed historical evidence; never rerun or relabel failed measurements."""
import argparse
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path, PurePosixPath
import stat
import subprocess
import tempfile
import unittest
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile

REPO = 'SunSi12138/SharpLink'
HEAD = 'be54089d1dc5a0e97006879ef293b4640f8ec629'
# Immutable IDs and digests copied from each completed Actions run, including failure.
ARTIFACTS = (
    ('jit', 10813882257, 'b05e548bacd8302d499653cb220381a139fda46ee5c20aa772034de319dbe1c0'),
    ('native', 10813244017, '8e1d7aa3e386b7d5fcd58f03f4a92e46849a95f6d256ea2d82a8063de0fa3dcf'),
    ('failed-control', 10813696767, 'cb931c165885a84b44f77d29eba725331c6bb473e806eca6c8e1e1651c542b00'),
)
LIMIT = 16 * 1024 * 1024


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, newurl):
        return None


def download(artifact_id, token):
    # Never forward the GitHub Authorization header to signed blob storage.
    url = f'https://api.github.com/repos/{REPO}/actions/artifacts/{artifact_id}/zip'
    request = urllib.request.Request(url, headers={'Authorization': f'Bearer {token}',
        'Accept': 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28'})
    opener = urllib.request.build_opener(NoRedirect())
    try:
        response = opener.open(request, timeout=30)
    except urllib.error.HTTPError as error:
        if error.code not in (301, 302, 303, 307, 308):
            raise RuntimeError(f'GitHub artifact download failed: HTTP {error.code}') from None
        location = error.headers.get('Location', '')
        error.close()
        if urllib.parse.urlsplit(location).scheme != 'https':
            raise ValueError('Expected a signed HTTPS artifact location')
        response = urllib.request.urlopen(location, timeout=30)
    with response:
        data = response.read(LIMIT + 1)
    if len(data) > LIMIT:
        raise ValueError('Compressed artifact exceeds review budget')
    return data


def unpack(data, expected, root):
    if hashlib.sha256(data).hexdigest() != expected:
        raise ValueError('Historical artifact digest mismatch')
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        members = archive.infolist()
        if len(members) > 2048 or sum(item.file_size for item in members) > 64 * 1024 * 1024:
            raise ValueError('Expanded archive exceeds review budget')
        seen = set()
        for item in members:
            path = PurePosixPath(item.filename)
            if path.is_absolute() or '..' in path.parts or '\\' in item.filename or not path.parts:
                raise ValueError('Unsafe archive path')
            if str(path) in seen or stat.S_ISLNK(item.external_attr >> 16):
                raise ValueError('Duplicate path or archive link')
            seen.add(str(path))
            destination = root.joinpath(*path.parts)
            if item.is_dir():
                destination.mkdir(parents=True, exist_ok=True)
                continue
            if item.file_size > LIMIT:
                raise ValueError('Archive member exceeds review budget')
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(archive.read(item))


def load(path):
    spec = importlib.util.spec_from_file_location(path.stem, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def audit(output):
    output.mkdir(parents=True, exist_ok=True)
    if (output / 'audit.md').exists():
        raise FileExistsError('Do not overwrite an audit')
    token = os.environ['GH_TOKEN']
    report = [f'# Retained Phase B evidence audit\n\nMeasured commit: `{HEAD}`. This audit creates no new performance samples.\n']
    with tempfile.TemporaryDirectory() as temp:
        scripts = Path(temp)
        for name in ('verify-ready-writer.py', 'verify-ready-writer-budget.py',
                     'verify-ready-writer-native.py', 'verify-flow-state-transport.py'):
            data = subprocess.check_output(['git', 'show', f'{HEAD}:eng/{name}'])
            (scripts / name).write_bytes(data)
        for kind, artifact_id, digest in ARTIFACTS:
            root = output / kind
            root.mkdir()
            data = download(artifact_id, token)
            (output / f'{kind}.zip').write_bytes(data)
            unpack(data, digest, root)
            report.append(f'## {kind}: artifact {artifact_id}\n\nSHA256 `{digest}` verified.\n')
            if kind == 'failed-control':
                failed = []
                for path in sorted(root.glob('*.exit.json')):
                    entry = json.loads(path.read_text())
                    code = entry.get('returncode', entry.get('exit_code', entry.get('code')))
                    if code is None:
                        report.append(f'Exit record `{path.name}`: `{entry}`\n')
                        continue
                    if code != 0:
                        failed.append(path)
                        logfile = root / (path.name[:-len('.exit.json')] + '.log')
                        report.append(f'Failed process `{path.name}`, exit `{code}`. Original tail follows; not a passing performance population.\n')
                        if logfile.exists():
                            report.append('```text\n' + logfile.read_text(errors='replace')[-50000:] + '\n```\n')
                # Retain failures even when a historical runner used a different exit schema.
                if not failed:
                    for path in sorted(root.glob('*.log')):
                        text = path.read_text(errors='replace')
                        if any(word in text for word in ('FAILED ', 'CASE STATE', 'Unhandled exception')):
                            failed.append(path)
                            report.append(f'Failure log `{path.name}`:\n```text\n{text[-50000:]}\n```\n')
                if not failed:
                    raise ValueError('Expected historical failure was not found; do not relabel as green')
                report.append('Original incomplete/failed control remains a blocker. This audit only preserves its evidence.\n')
                continue
            verifier = load(scripts / ('verify-ready-writer-native.py' if kind == 'native' else 'verify-ready-writer-budget.py'))
            result = verifier.summarize(root)
            regenerated, rows = result[0], result[-1]
            original = (root / 'summary.md').read_bytes()
            if regenerated.encode() != original:
                raise ValueError('Regenerated summary differs from the retained CI summary')
            expected_rows = 128 if kind == 'native' else 768
            if len(rows) != expected_rows:
                raise ValueError('Wrong historical population')
            report.append(f'Original verifier passed all {len(rows)} rows; summary is byte-identical.\n\n' + regenerated)
    report.append('\nHistorical failures are not fixed by an audit or one later passing run. Dynamic lifecycle, FIFO, duplicate-credit and full production RPC acceptance remain open. Keep #735 open and #742 Draft.\n')
    text = '\n'.join(report)
    (output / 'audit.md').write_text(text)
    # Treat artifact log text as data, not GitHub workflow commands.
    stop = uuid.uuid4().hex
    print(f'::stop-commands::{stop}', flush=True)
    print(text, flush=True)
    print(f'::{stop}::', flush=True)
    summary = os.environ.get('GITHUB_STEP_SUMMARY')
    if summary:
        with open(summary, 'a') as stream:
            stream.write(text)


class Guards(unittest.TestCase):
    def archive(self, name='summary.md', payload=b'content'):
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, 'w') as archive:
            archive.writestr(name, payload)
        return buffer.getvalue()

    def test_digest_and_round_trip(self):
        data = self.archive()
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            unpack(data, hashlib.sha256(data).hexdigest(), root)
            self.assertEqual((root / 'summary.md').read_bytes(), b'content')

    def test_wrong_digest_is_rejected_before_extraction(self):
        with tempfile.TemporaryDirectory() as temp:
            with self.assertRaises(ValueError):
                unpack(self.archive(), '0' * 64, Path(temp))
            self.assertEqual(list(Path(temp).iterdir()), [])

    def test_unsafe_archive_names_rejected(self):
        for name in ('../outside', '/outside', 'dir\\outside'):
            data = self.archive(name)
            with tempfile.TemporaryDirectory() as temp:
                with self.assertRaises(ValueError):
                    unpack(data, hashlib.sha256(data).hexdigest(), Path(temp))

    def test_reviewed_population_is_fixed_and_distinct(self):
        self.assertEqual(len(ARTIFACTS), 3)
        self.assertEqual(len({item[1] for item in ARTIFACTS}), 3)
        self.assertEqual({item[0] for item in ARTIFACTS}, {'jit', 'native', 'failed-control'})
        self.assertTrue(all(len(item[2]) == 64 for item in ARTIFACTS))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--self-test', action='store_true')
    parser.add_argument('--output', type=Path, default=Path('artifacts/retained-flow-state-audit'))
    args = parser.parse_args()
    if args.self_test:
        suite = unittest.defaultTestLoader.loadTestsFromTestCase(Guards)
        raise SystemExit(0 if unittest.TextTestRunner().run(suite).wasSuccessful() else 1)
    audit(args.output)
