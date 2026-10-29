#!/usr/bin/env python3
"""Two-commit release gate: reviewed source/tool commit, then request-only commit."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import subprocess

from scope import explicit_workers, ordinary_uids
from validate import GateError, require, strict_json, uid_digest, unique_uids

BRANCH = 'refs/heads/validation/static-stream-unit-no-evidence-20261029-r2'
REQUEST = 'eng/static-stream-unit-validation/request.json'
REQUEST_KEYS = {'schema_version', 'source_sha', 'source_tree', 'raw_discovered_count', 'raw_uid_sha256',
                'ordinary_count', 'ordinary_uid_sha256'}


def git(root: Path, *args: str) -> str:
    return subprocess.check_output(['git', '-C', str(root), *args], text=True).strip()


def read_request(path: Path) -> dict:
    request = strict_json(path.read_text(encoding='utf-8'))
    require(isinstance(request, dict) and set(request) == REQUEST_KEYS, 'invalid request fields')
    require(type(request['schema_version']) is int and request['schema_version'] == 1,
            'unsupported request schema')
    for key in ('source_sha', 'source_tree'):
        require(isinstance(request[key], str) and re.fullmatch('[0-9a-f]{40}', request[key]) is not None,
                f'invalid {key}')
    for key in ('raw_discovered_count', 'ordinary_count'):
        require(type(request[key]) is int and request[key] > 0, f'invalid request {key}')
    for key in ('raw_uid_sha256', 'ordinary_uid_sha256'):
        require(isinstance(request[key], str) and re.fullmatch('[0-9a-f]{64}', request[key]) is not None,
                f'invalid request {key}')
    return request


def verify_release(root: Path, expected_release: str, expected_ref: str) -> dict:
    require(expected_ref == BRANCH, 'wrong validation branch')
    require(re.fullmatch('[0-9a-f]{40}', expected_release) is not None, 'invalid release SHA')
    require(git(root, 'rev-parse', 'HEAD') == expected_release, 'release checkout mismatch')
    require(not git(root, 'status', '--porcelain', '--untracked-files=all'), 'release checkout not clean')
    request = read_request(root / REQUEST)
    parents = git(root, 'show', '-s', '--format=%P', 'HEAD').split()
    require(parents == [request['source_sha']], 'release must have exactly the reviewed source parent')
    require(git(root, 'rev-parse', request['source_sha'] + '^{tree}') == request['source_tree'],
            'reviewed source tree mismatch')
    delta = git(root, 'diff', '--name-status', '--no-renames', request['source_sha'], 'HEAD').splitlines()
    require(delta == ['A\t' + REQUEST], 'release must only add the request file')
    read_corpus(root, request)
    return request


def read_corpus(root: Path, request: dict) -> list[str]:
    corpus = strict_json((root / 'eng/static-stream-unit-validation/expected-uids.json').read_text(encoding='utf-8'))
    uids = unique_uids(corpus, 'reviewed corpus')
    require(corpus == uids, 'reviewed UID corpus must be sorted')
    require(len(uids) == request['raw_discovered_count'], 'request census count mismatch')
    require(uid_digest(uids) == request['raw_uid_sha256'], 'request UID corpus digest mismatch')
    ordinary = ordinary_uids(uids, explicit_workers(root / 'eng/static-stream-unit-validation'))
    require(len(ordinary) == request['ordinary_count'], 'request ordinary census count mismatch')
    require(uid_digest(ordinary) == request['ordinary_uid_sha256'], 'request ordinary UID digest mismatch')
    return uids


def verify_source(root: Path, request: dict) -> None:
    require(git(root, 'rev-parse', 'HEAD') == request['source_sha'], 'candidate source SHA mismatch')
    require(git(root, 'rev-parse', 'HEAD^{tree}') == request['source_tree'], 'candidate source tree mismatch')
    require(not git(root, 'status', '--porcelain', '--untracked-files=all'), 'candidate checkout not clean')
    require(not (root / REQUEST).exists(), 'source tree already contains a request')
    read_corpus(root, request)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument('--release-root', required=True, type=Path)
    parser.add_argument('--source-root', type=Path)
    args = parser.parse_args()
    request = verify_release(args.release_root, os.environ.get('GITHUB_SHA', ''),
                             os.environ.get('GITHUB_REF', ''))
    if args.source_root is not None:
        verify_source(args.source_root, request)
    print(json.dumps({key: request[key] for key in ('source_sha', 'source_tree', 'raw_uid_sha256', 'ordinary_uid_sha256')}, sort_keys=True))
    with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as output:
        output.write(f"source_sha={request['source_sha']}\n")


if __name__ == '__main__':
    try:
        main()
    except (GateError, OSError, ValueError, subprocess.SubprocessError) as error:
        raise SystemExit(f'PROVENANCE FAIL: {error}') from None
