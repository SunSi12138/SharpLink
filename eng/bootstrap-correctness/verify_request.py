#!/usr/bin/env python3
"""Accept only a single REQUEST-only child of the independently reviewed tool."""
import json
import os
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
REQUEST = 'eng/bootstrap-correctness/REQUEST.json'
BRANCH = 'refs/heads/codex/bootstrap-correctness-validation'
MESSAGE = 'run: bootstrap supervision full correctness C1'
CANDIDATE = '13e75f15f16cb98960c97f9500e17d26ba81f43b'
CANDIDATE_TREE = '025dba3a4756227e062eab005b2693fc24a62f6a'


def validate_request(data, environment, head, parent, parent_tree, changed, message, clean):
    keys = {'state', 'campaign', 'reviewedToolCommit', 'reviewedToolTree', 'candidate', 'candidateTree'}
    if set(data) != keys or data['state'] != 'authorized' or data['campaign'] != 'bootstrap-supervision-full-unit-v1':
        raise ValueError('Wrong or dormant request schema/campaign')
    if (environment.get('GITHUB_EVENT_NAME'), environment.get('GITHUB_REF'), environment.get('GITHUB_RUN_ATTEMPT')) != ('push', BRANCH, '1'):
        raise ValueError('Only first-attempt bounded branch push permitted')
    if environment.get('GITHUB_REPOSITORY') != 'SunSi12138/SharpLink' or environment.get('GITHUB_SHA') != head:
        raise ValueError('Wrong repository/request checkout')
    if message != MESSAGE or not clean or changed != [REQUEST]:
        raise ValueError('Clean request commit may change only REQUEST with fixed message')
    if (data['reviewedToolCommit'], data['reviewedToolTree']) != (parent, parent_tree):
        raise ValueError('Request parent must be independently reviewed tool identity')
    if any(not re.fullmatch('[0-9a-f]{40}', value) for value in (head, parent, parent_tree)):
        raise ValueError('Full SHA identities required')
    if (data['candidate'], data['candidateTree']) != (CANDIDATE, CANDIDATE_TREE):
        raise ValueError('Frozen product source/tree changed')


def main():
    def git(*args):
        return subprocess.check_output(['git', *args], cwd=ROOT, text=True).strip()
    parents = git('rev-list', '--parents', '-n', '1', 'HEAD').split()
    if len(parents) != 2:
        raise ValueError('Exactly one request parent required')
    validate_request(json.loads((ROOT / REQUEST).read_text()), os.environ, git('rev-parse', 'HEAD'), parents[1],
                     git('rev-parse', parents[1] + '^{tree}'), git('diff', '--name-only', 'HEAD^', 'HEAD').splitlines(),
                     git('show', '-s', '--format=%B', 'HEAD'), not git('status', '--porcelain'))
    print('REQUEST_IDENTITY_PASSED')


if __name__ == '__main__':
    main()
