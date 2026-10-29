"""Exact candidate source and frozen, actually discovered test corpus."""
from pathlib import Path
from collections import Counter
import hashlib
import subprocess

from .scope import explicit_workers, ordinary_uids
from .validate import require, strict_json, unique_uids, uid_digest

SOURCE = '279fdb2a93736eb25bec948c3acd65f2ed4830d9'
TREE = '9b10700c8626f6f51953273df0187680e8f766d6'
NEW_CLASSES = {'SharpLink.UnitTests.Server.ConnectionCancellationFanoutTests.': 31}


def verify_source(root: Path, git_output=None):
    def git(*args):
        result = subprocess.run(['git', '-C', str(root), *args], capture_output=True, text=True)
        if git_output is not None:
            with git_output.open('a') as log:
                log.write(repr(args) + '\n' + result.stdout + result.stderr)
        require(result.returncode == 0, 'source git command failed')
        return result.stdout.strip()
    require(git('rev-parse', 'HEAD') == SOURCE, 'candidate source SHA mismatch')
    require(git('rev-parse', 'HEAD^{tree}') == TREE, 'candidate source tree mismatch')
    require(not git('status', '--porcelain', '--untracked-files=all'), 'candidate source is dirty')


def read_corpus(tool: Path):
    manifest = strict_json((tool / 'corpus.json').read_text())
    require(manifest.get('source_sha') == SOURCE and manifest.get('source_tree') == TREE,
            'frozen corpus source mismatch')
    base = unique_uids(strict_json((tool / 'expected-base-uids.json').read_text()), 'baseline corpus')
    new = unique_uids(strict_json((tool / 'new-uids.json').read_text()), 'new discovered corpus')
    classes = Counter(next((name for name in NEW_CLASSES if uid.startswith(name)), '<unknown>') for uid in new)
    require(len(new) == 31 and classes == NEW_CLASSES, 'wrong new-31 class identities/counts')
    require(not set(base).intersection(new), 'new corpus overlaps baseline')
    require(uid_digest(base) == manifest.get('base_uid_sha256'), 'baseline corpus digest mismatch')
    require(uid_digest(new) == manifest.get('new_uid_sha256'), 'new corpus digest mismatch')
    raw = sorted(base + new)
    require(len(raw) == manifest.get('raw_count') and uid_digest(raw) == manifest.get('raw_uid_sha256'),
            'frozen raw corpus mismatch')
    workers = explicit_workers(tool)
    require(len(workers) == 5 and len({w['type_name'] for w in workers}) == 4,
            'exact five explicit UIDs/four worker classes required')
    require(hashlib.sha256((tool / 'explicit-workers.json').read_bytes()).hexdigest() == manifest.get('explicit_file_sha256'),
            'explicit-worker manifest changed')
    ordinary = ordinary_uids(raw, workers)
    require(len(ordinary) == manifest.get('ordinary_count') and uid_digest(ordinary) == manifest.get('ordinary_uid_sha256'),
            'frozen ordinary corpus mismatch')
    return raw, ordinary, workers
