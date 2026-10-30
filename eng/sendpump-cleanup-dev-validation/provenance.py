"""Bind dev-only product ancestry, fixture-only baseline and one request commit."""
from pathlib import Path
import re
import subprocess
from validate import require, strict_json

BASE_SHA = '57f06f172d29fa5cfd4ea8e9b86a23f122df24ab'
BASE_TREE = 'e5b72625377ffdcf5f5470e2ddd69be12a449d8e'
SOURCE_SHA = '402ae37f26d1221159a6f577d985b210655e0e47'
SOURCE_TREE = '33bc4aff4f6d19372fd5308ed027473399e61523'
IMPLEMENTATION_SHA = '21ea65ec14d38de6881fe2f7fc4a0d99a8d4ad2a'
HOSTING_CENSUS_SHA = '981a5d5816e5869859ecfea4e952e35fe519deed'
TEST_PATH = 'test/SharpLink.UnitTests/Runtime/SendPumpCleanupProgressTests.cs'
PRODUCT_PATH = 'src/SharpLink.Runtime/RpcSession.SendPump.cs'
TEST_BLOB = '0573677c1d83b95c2ed75d5e8f0d395babc7d49e'
BRANCH = 'refs/heads/validation/sendpump-cleanup-dev-ordinary-diagnostic-20261030'
REQUEST = 'eng/sendpump-cleanup-dev-validation/request.json'
TOOL_DIR = 'eng/sendpump-cleanup-dev-validation/'
TOOL_FILES = sorted(['.github/workflows/sendpump-cleanup-dev-ordinary-diagnostic.yml'] + [TOOL_DIR + name for name in (
    'run.py', 'provenance.py', 'host.py', 'allocation.py', 'diagnostics.py', 'validate.py', 'scope.py',
    'explicit-workers.json', 'new-uids.json', 'test_validation.py', 'SCOPE.md', 'REQUEST.template.json')])
IDENTITIES = {'candidate': (SOURCE_SHA, SOURCE_TREE)}


def git(root, *args):
    return subprocess.check_output(['git', '-C', str(root), *args], text=True).strip()


def verify_candidate_chain(root):
    for child, parent in ((SOURCE_SHA, HOSTING_CENSUS_SHA), (HOSTING_CENSUS_SHA, IMPLEMENTATION_SHA),
                          (IMPLEMENTATION_SHA, BASE_SHA)):
        require(git(root, 'show', '-s', '--format=%P', child) == parent, 'candidate ancestry mismatch')
    require(git(root, 'rev-parse', IMPLEMENTATION_SHA + '^{tree}') == '1140b76553521b86cd5ae431bf6344313225bc08',
            'implementation tree mismatch')
    require(git(root, 'diff', '--name-status', '--no-renames', BASE_SHA, IMPLEMENTATION_SHA).splitlines()
            == ['M\t' + PRODUCT_PATH, 'A\t' + TEST_PATH], 'implementation scope differs')
    require(not git(root, 'diff', IMPLEMENTATION_SHA, SOURCE_SHA, '--', 'src', 'test'), 'census commits changed product/test')


def verify_source(root, phase):
    sha, tree = IDENTITIES[phase]
    require(root.is_absolute() and root.resolve(strict=True) == root, 'noncanonical source')
    require(git(root, 'rev-parse', 'HEAD') == sha and git(root, 'rev-parse', 'HEAD^{tree}') == tree,
            'execution source identity mismatch')
    require(not git(root, 'status', '--porcelain', '--untracked-files=all'), 'execution source not clean')
    if phase == 'candidate': verify_candidate_chain(root)
    require(git(root, 'rev-parse', 'HEAD:' + TEST_PATH) == TEST_BLOB, 'fixture bytes mismatch')


def verify_release(root, sha, ref):
    require(ref == BRANCH and re.fullmatch('[0-9a-f]{40}', sha) is not None, 'wrong request ref/SHA')
    require(git(root, 'rev-parse', 'HEAD') == sha and not git(root, 'status', '--porcelain', '--untracked-files=all'),
            'request identity/cleanliness mismatch')
    request = strict_json((root / REQUEST).read_text())
    require(isinstance(request, dict) and set(request) == {'schema_version', 'source_sha', 'tools_sha', 'tools_tree'},
            'invalid request shape')
    require(type(request['schema_version']) is int and request['schema_version'] == 2, 'invalid request schema')
    for key in ('source_sha', 'tools_sha', 'tools_tree'):
        require(isinstance(request[key], str) and re.fullmatch('[0-9a-f]{40}', request[key]) is not None,
                'invalid request identity')
    require(request['source_sha'] == SOURCE_SHA, 'unreviewed product')
    tools = request['tools_sha']
    require(git(root, 'show', '-s', '--format=%P', 'HEAD') == tools, 'request parent mismatch')
    require(git(root, 'rev-parse', tools + '^{tree}') == request['tools_tree'], 'tools tree mismatch')
    require(git(root, 'diff', '--name-status', '--no-renames', tools, 'HEAD') == 'A\t' + REQUEST, 'not request-only')
    require(git(root, 'show', '-s', '--format=%P', tools) == SOURCE_SHA, 'tool parent mismatch')
    require(git(root, 'diff', '--name-status', '--no-renames', SOURCE_SHA, tools).splitlines()
            == sorted('A\t' + path for path in TOOL_FILES), 'tool scope mismatch')
    require(git(root, 'rev-parse', SOURCE_SHA + '^{tree}') == SOURCE_TREE
            and git(root, 'rev-parse', BASE_SHA + '^{tree}') == BASE_TREE, 'product tree mismatch')
    verify_candidate_chain(root)
    return request
