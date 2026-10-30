#!/usr/bin/env python3
"""Fail-closed, request-only first-attempt push gate; never launches work itself."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
REQUEST = 'eng/codeql-sarif-evidence/REQUEST.json'
BRANCH = 'refs/heads/validation/codeql-sarif-c22-20261030-r5'
MESSAGE = 'run: isolated CodeQL SARIF CQL5'
CAMPAIGN = 'codeql-d39aa-manual-sarif-CQL5'
REPOSITORY = 'SunSi12138/SharpLink'
DEV = 'd39aa31948461e16264be54a69be231c8fb5882d'
DEV_TREE = '2c1fe56b793ce06cff6fb63c9e633c67d4fd1ac3'
DORMANT = {'state': 'review-required', 'campaign': CAMPAIGN}


def strict_json(text):
    def pairs(entries):
        result = {}
        for key, value in entries:
            if key in result:
                raise ValueError(f'Duplicate JSON key: {key}')
            result[key] = value
        return result
    def constant(value):
        raise ValueError(f'Nonfinite JSON value: {value}')
    return json.loads(text, object_pairs_hook=pairs, parse_constant=constant)


def validate_request(data, environment, head, parent, parent_tree, changed, message, clean,
                     parent_request, event):
    expected = {'state', 'campaign', 'reviewedToolCommit', 'reviewedToolTree',
                'reviewStatus', 'rootRelease', 'devSource', 'devTree'}
    if not isinstance(data, dict) or set(data) != expected:
        raise ValueError('Wrong/unreviewed request schema')
    if (data['state'], data['campaign'], data['reviewStatus'], data['rootRelease']) != (
            'authorized', CAMPAIGN, 'PASS', 'authorized'):
        raise ValueError('Exact tool/workflow review PASS and root release required')
    if (environment.get('GITHUB_EVENT_NAME'), environment.get('GITHUB_REF'), environment.get('GITHUB_RUN_ATTEMPT')) != ('push', BRANCH, '1'):
        raise ValueError('Only first-attempt push on the bounded validation branch is permitted')
    if environment.get('GITHUB_REPOSITORY') != REPOSITORY or environment.get('GITHUB_SHA') != head:
        raise ValueError('Wrong repository/request checkout')
    if not all(isinstance(value, str) and re.fullmatch('[0-9a-f]{40}', value) for value in (head, parent, parent_tree)):
        raise ValueError('Full lowercase tool/request identities required')
    if message != MESSAGE or not clean or changed != [REQUEST]:
        raise ValueError('Clean request commit may change only REQUEST with the fixed message')
    if (data['reviewedToolCommit'], data['reviewedToolTree']) != (parent, parent_tree):
        raise ValueError('Parent is not the independently reviewed tool identity')
    if (data['devSource'], data['devTree']) != (DEV, DEV_TREE):
        raise ValueError('Pinned package/product identity changed')
    if parent_request != DORMANT:
        raise ValueError('Reviewed parent must retain the dormant request')
    if not isinstance(event, dict) or event.get('deleted') is not False or event.get('forced') is not False:
        raise ValueError('Deleted/forced/unknown push is forbidden')
    commits = event.get('commits')
    if (event.get('before'), event.get('after'), event.get('ref')) != (parent, head, BRANCH) or not isinstance(commits, list) or len(commits) != 1:
        raise ValueError('Push must contain exactly this single request child of reviewed parent')
    if commits[0].get('id') != head or event.get('head_commit', {}).get('id') != head or event['head_commit'].get('message') != MESSAGE:
        raise ValueError('Push commit identity/message differs')
    if event.get('repository', {}).get('full_name') != REPOSITORY:
        raise ValueError('Push repository differs')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--record', type=Path, required=True, help='External evidence path; never modify reviewed checkout')
    args = parser.parse_args()
    def git(*arguments):
        return subprocess.check_output(['git', *arguments], cwd=ROOT, text=True).strip()
    parents = git('rev-list', '--parents', '-n', '1', 'HEAD').split()
    if len(parents) != 2:
        raise ValueError('Exactly one request parent required')
    head, parent = parents
    tree = git('rev-parse', parent + '^{tree}')
    if git('ls-tree', 'HEAD', REQUEST).split()[0] != '100644':
        raise ValueError('Request must be a regular tracked non-executable JSON file')
    data = strict_json((ROOT / REQUEST).read_text())
    event = strict_json(Path(os.environ['GITHUB_EVENT_PATH']).read_text())
    validate_request(data, os.environ, head, parent, tree,
                     git('diff', '--name-only', '--no-renames', parent, head).splitlines(),
                     git('show', '-s', '--format=%B', 'HEAD'), not git('status', '--porcelain'),
                     strict_json(git('show', parent + ':' + REQUEST)), event)
    record = args.record.resolve()
    if record.is_relative_to(ROOT):
        raise ValueError('Request evidence must be outside the reviewed checkout')
    record.parent.mkdir(parents=True, exist_ok=True)
    with record.open('x') as stream:
        json.dump({'request': data, 'requestCommit': head, 'reviewedToolCommit': parent,
                   'reviewedToolTree': tree, 'runId': os.environ['GITHUB_RUN_ID'], 'attempt': 1,
                   'status': 'REQUEST_IDENTITY_PASSED'}, stream, indent=2)
        stream.write('\n')
    with Path(os.environ['GITHUB_OUTPUT']).open('a') as stream:
        stream.write(f'reviewed_tool_sha={parent}\nreviewed_tool_tree={tree}\n')
    print('REQUEST_IDENTITY_PASSED; execution still belongs to the bounded workflow')


if __name__ == '__main__':
    main()
