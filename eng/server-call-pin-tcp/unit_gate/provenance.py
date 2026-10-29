"""Exact per-arm sources and frozen full ordinary corpora; protocol is identity authority."""
from pathlib import Path
import hashlib
import subprocess

from common import ARMS, CONTRACT
from .scope import explicit_workers, ordinary_uids
from .validate import require, strict_json, unique_uids, uid_digest

SOURCE = CONTRACT['candidateSource']
TREE = CONTRACT['candidateTree']
PIN_CLASS = 'SharpLink.UnitTests.Server.ServerGeneratedOutboundCallPinTests.'
NEW_CLASSES = {PIN_CLASS: 15}


def verify_source(root: Path, git_output=None, arm='candidate'):
    require(arm in ARMS, 'unknown source arm')
    def git(*args):
        result = subprocess.run(['git', '-C', str(root), *args], capture_output=True, text=True)
        if git_output is not None:
            with git_output.open('a') as log:
                log.write(repr(args) + '\n' + result.stdout + result.stderr)
        require(result.returncode == 0, 'source git command failed')
        return result.stdout.strip()
    require(git('rev-parse', 'HEAD') == CONTRACT[arm + 'Source'], 'arm source SHA mismatch')
    require(git('rev-parse', 'HEAD^{tree}') == CONTRACT[arm + 'Tree'], 'arm source tree mismatch')
    require(not git('status', '--porcelain', '--untracked-files=all'), 'arm source is dirty')


def read_corpus(tool: Path, arm='candidate'):
    require(arm in ARMS, 'unknown corpus arm')
    manifest = strict_json((tool / 'corpus.json').read_text())
    require(manifest.get('schema') == 2, 'frozen corpus schema mismatch')
    base = unique_uids(strict_json((tool / 'expected-base-uids.json').read_text()), 'baseline corpus')
    new = unique_uids(strict_json((tool / 'new-uids.json').read_text()), 'new pin corpus')
    require(len(new) == 15 and all(uid.startswith(PIN_CLASS) for uid in new), 'wrong new-15 pin identities/count')
    require(not set(base).intersection(new), 'pin corpus overlaps baseline')
    require(uid_digest(base) == manifest.get('base_uid_sha256'), 'baseline corpus digest mismatch')
    require(uid_digest(new) == manifest.get('new_uid_sha256'), 'pin corpus digest mismatch')
    raw = sorted(base + (new if arm == 'candidate' else []))
    frozen = manifest['arms'][arm]
    require(len(raw) == frozen.get('raw_count') and uid_digest(raw) == frozen.get('raw_uid_sha256'),
            'frozen raw corpus mismatch')
    require(len(raw) == (2331 if arm == 'candidate' else 2316), 'wrong arm corpus count')
    workers = explicit_workers(tool)
    require(len(workers) == 5 and len({w['type_name'] for w in workers}) == 4,
            'exact five explicit UIDs/four worker classes required')
    require(hashlib.sha256((tool / 'explicit-workers.json').read_bytes()).hexdigest() == manifest.get('explicit_file_sha256'),
            'explicit-worker manifest changed')
    ordinary = ordinary_uids(raw, workers)
    require(len(ordinary) == frozen.get('ordinary_count') and uid_digest(ordinary) == frozen.get('ordinary_uid_sha256'),
            'frozen ordinary corpus mismatch')
    return raw, ordinary, workers
