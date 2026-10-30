#!/usr/bin/env python3
"""Narrow request-only trigger and immutable candidate checkout guard."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess

SOURCE = 'a91ce4bf48523264282928231f35fce339fb1567'
TREE = '83fe67363af2c3c815a333e93806d8a5fc6136aa'
BRANCH = 'refs/heads/validation/api-semantic-readpath-20261029'
REQUEST = 'eng/api-semantic-calibration/request.json'
KEYS = {'schema_version', 'source_sha', 'source_tree', 'tools_sha'}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def git(root, *args):
    return subprocess.check_output(['git', '-C', str(root), *args], text=True).strip()


def pairs(items):
    result = {}
    for key, value in items:
        require(key not in result, 'duplicate request key')
        result[key] = value
    return result


def request(text):
    value = json.loads(text, object_pairs_hook=pairs)
    require(isinstance(value, dict) and set(value) == KEYS, 'invalid request fields')
    require(type(value['schema_version']) is int and value['schema_version'] == 1, 'invalid schema')
    require(value['source_sha'] == SOURCE and value['source_tree'] == TREE, 'candidate identity changed')
    require(isinstance(value['tools_sha'], str) and re.fullmatch('[0-9a-f]{40}', value['tools_sha']), 'invalid tools SHA')
    return value


def verify_tools(root, sha, ref):
    require(ref == BRANCH, 'wrong validation branch')
    require(re.fullmatch('[0-9a-f]{40}', sha or ''), 'invalid workflow SHA')
    require(git(root, 'rev-parse', 'HEAD') == sha, 'workflow checkout mismatch')
    require(not git(root, 'status', '--porcelain', '--untracked-files=all'), 'tools checkout dirty')
    value = request((root / REQUEST).read_text())
    require(git(root, 'show', '-s', '--format=%P', 'HEAD').split() == [value['tools_sha']], 'request must have one reviewed tools parent')
    require(git(root, 'diff', '--name-status', '--no-renames', value['tools_sha'], 'HEAD').splitlines() == ['A\t' + REQUEST], 'request commit must only add request.json')
    return value


def verify_source(root):
    require(git(root, 'rev-parse', 'HEAD') == SOURCE, 'candidate SHA mismatch')
    require(git(root, 'rev-parse', 'HEAD^{tree}') == TREE, 'candidate tree mismatch')
    require(not git(root, 'status', '--porcelain', '--untracked-files=all'), 'candidate checkout dirty')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tools-root', type=Path, required=True)
    parser.add_argument('--source-root', type=Path)
    args = parser.parse_args()
    value = verify_tools(args.tools_root, os.environ.get('GITHUB_SHA'), os.environ.get('GITHUB_REF'))
    if args.source_root:
        require(args.tools_root.resolve() != args.source_root.resolve(), 'source and tools must be separate')
        verify_source(args.source_root)
    print('PROVENANCE_PASS ' + json.dumps(value, sort_keys=True))
    with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
        output.write('source_sha=' + SOURCE + '\n')


if __name__ == '__main__':
    main()
