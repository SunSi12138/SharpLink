#!/usr/bin/env python3
"""One fixed push request: only REQUEST may differ from independently reviewed tool parent."""
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[3]
REQUEST = 'eng/physical-ownership-probes/wakeup-performance/REQUEST.json'
BRANCH = 'refs/heads/codex/wakeup-performance-validation'
MESSAGE = 'run: fenced-wakeup paired diagnostic W1'
BASELINE = 'f724c7aaabdeb5e02bec23f32f6b3c709860e97c'
CANDIDATE = 'e5a35b9bf63d162c3077aec389d402e28bd2fb53'
CANDIDATE_TREE = 'ea401a81c4b5a52966f4281acc348f838b9fc935'


def validate_request(data, environment, head, parent, parent_tree, changed, message, clean):
    expected_keys = {'state', 'campaign', 'reviewedToolCommit', 'reviewedToolTree', 'baseline', 'candidate', 'candidateTree'}
    if set(data) != expected_keys or data['state'] != 'authorized' or data['campaign'] != 'fenced-wakeup-48tcp-v1':
        raise ValueError('Wrong/unreviewed request schema or campaign')
    if (environment.get('GITHUB_EVENT_NAME'), environment.get('GITHUB_REF'), environment.get('GITHUB_RUN_ATTEMPT')) != ('push', BRANCH, '1'):
        raise ValueError('Only first-attempt push on the bounded validation branch is permitted')
    if environment.get('GITHUB_REPOSITORY') != 'SunSi12138/SharpLink' or environment.get('GITHUB_SHA') != head:
        raise ValueError('Wrong repository/request checkout')
    if message != MESSAGE or not clean or changed != [REQUEST]:
        raise ValueError('Request commit may change only REQUEST with the fixed message')
    if (data['reviewedToolCommit'], data['reviewedToolTree']) != (parent, parent_tree):
        raise ValueError('Parent is not the independently reviewed tool identity')
    if (data['baseline'], data['candidate'], data['candidateTree']) != (BASELINE, CANDIDATE, CANDIDATE_TREE):
        raise ValueError('Product source/tree pins changed')
    if len(parent) != 40 or len(parent_tree) != 40:
        raise ValueError('Full tool identity required')


def main():
    def git(*args):
        return subprocess.check_output(['git', *args], cwd=ROOT, text=True).strip()
    parents = git('rev-list', '--parents', '-n', '1', 'HEAD').split()
    if len(parents) != 2:
        raise ValueError('Single request parent required')
    validate_request(json.loads((ROOT / REQUEST).read_text()), os.environ, git('rev-parse', 'HEAD'), parents[1],
                     git('rev-parse', parents[1] + '^{tree}'), git('diff', '--name-only', 'HEAD^', 'HEAD').splitlines(),
                     git('show', '-s', '--format=%B', 'HEAD'), not git('status', '--porcelain'))
    print('REQUEST_IDENTITY_PASSED')


if __name__ == '__main__':
    main()
