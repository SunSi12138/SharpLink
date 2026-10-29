"""Exact candidate source and frozen, actually discovered test corpus."""
from pathlib import Path
import hashlib
import subprocess

from .scope import explicit_workers, ordinary_uids
from .validate import require, strict_json, unique_uids, uid_digest

SOURCE = 'fbfd5590372ca0cad9766c3cf7e509055c3874f1'
TREE = '2a833a52cc04346c41dcdcaf627766dc2beba280'
NEW_CLASS = 'SharpLink.UnitTests.Server.OutboundGeneratedCallLeaseTests.'


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
    require(len(new) == 18 and all(uid.startswith(NEW_CLASS) for uid in new), 'wrong new-18 identity/count')
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
