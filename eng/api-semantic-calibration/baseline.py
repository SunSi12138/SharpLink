#!/usr/bin/env python3
"""Fixed, read-only historical artifact input. No upload, cache, or baseline update."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import stat
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
import zipfile
import compare

REPO = 'SunSi12138/SharpLink'
REPO_ID, RUN_ID, ARTIFACT_ID = 1155708874, 39986953281, 12515194592
RUN_HEAD = '87aeaf66cba5f804b8a33d312b9878143adbc9dd'
ZIP_SHA = 'ae34d7099e7a5549dfcafafc2b13b94daddb333b02d948937c3b95186957f03a'
ZIP_SIZE = 7477905
FIXTURE_SHA = '856f10f7a53e4bb0d9689b6383cb8ce691a24d0efa09714363deefd5fe137634'
REQUIRED_SHA = '7800d0523c8199cfe5772125240a085c988df11f9f9f62f4e06dfbf9af089017'
BASE_SOURCE = '3d76a3a8b55a37e24c62a073fe588cff3d3867be'
BASE_TREE = '1c24da3af090acd4d6cdbfb005a9e3bd95304e3a'
MAX_DOWNLOAD, MAX_ENTRIES, MAX_ENTRY, MAX_UNPACKED = 8*1024*1024, 1024, 64*1024*1024, 128*1024*1024
PREFIX = 'api-inventory/pr/'
require = compare.require


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def read_response(response, limit):
    chunks, size = [], 0
    while True:
        chunk = response.read(min(65536, limit-size+1))
        if not chunk:
            break
        size += len(chunk)
        require(size <= limit, 'download limit exceeded')
        chunks.append(chunk)
    return b''.join(chunks)


def validate_metadata(value):
    require(type(value['id']) is int and value['id'] == ARTIFACT_ID, 'wrong artifact identity')
    require(value['name'] == 'api-inventory-39986953281-1', 'wrong artifact name')
    require(value['expired'] is False, 'artifact expired')
    require(type(value['size_in_bytes']) is int and value['size_in_bytes'] == ZIP_SIZE, 'wrong artifact size')
    require(value['digest'] == 'sha256:' + ZIP_SHA, 'wrong artifact digest')
    run = value['workflow_run']
    require(type(run['id']) is int and run['id'] == RUN_ID, 'wrong historical run')
    require(type(run['repository_id']) is int and type(run['head_repository_id']) is int and run['repository_id'] == REPO_ID and run['head_repository_id'] == REPO_ID and run['head_sha'] == RUN_HEAD, 'wrong historical run source')


def fetch_archive(destination, token):
    """Only GET the fixed API endpoint. Never forward authorization to storage."""
    require(isinstance(token, str) and token.strip() and '\n' not in token and '\r' not in token, 'missing or invalid read token')
    require(not destination.exists() and not destination.is_symlink(), 'archive destination exists')
    endpoint = f'https://api.github.com/repos/{REPO}/actions/artifacts/{ARTIFACT_ID}'
    headers = {'Accept':'application/vnd.github+json', 'Authorization':'Bearer '+token,
               'X-GitHub-Api-Version':'2022-11-28', 'User-Agent':'SharpLink-fixed-api-calibration'}
    opener = urllib.request.build_opener(NoRedirect())
    with opener.open(urllib.request.Request(endpoint, headers=headers), timeout=30) as response:
        require(response.status == 200, 'artifact metadata unavailable')
        validate_metadata(compare.parse(read_response(response, 65536).decode('utf-8')))
    try:
        with opener.open(urllib.request.Request(endpoint+'/zip', headers=headers), timeout=30):
            raise ValueError('expected artifact redirect')
    except urllib.error.HTTPError as error:
        require(error.code == 302, 'artifact download unavailable')
        location = error.headers.get('Location', '')
        error.close()
    parsed = urllib.parse.urlsplit(location)
    require(parsed.scheme == 'https' and parsed.hostname is not None and parsed.port in (None,443)
            and parsed.username is None and parsed.password is None and not parsed.fragment
            and (parsed.hostname.endswith('.blob.core.windows.net') or parsed.hostname.endswith('.actions.githubusercontent.com')),
            'unexpected artifact storage endpoint')
    # Fresh request has no GitHub token, cookies, or reusable checkout credential.
    with opener.open(urllib.request.Request(location, headers={'User-Agent':'SharpLink-fixed-api-calibration'}), timeout=60) as response:
        require(response.status == 200, 'artifact storage unavailable')
        body = read_response(response, MAX_DOWNLOAD)
    require(len(body) == ZIP_SIZE and hashlib.sha256(body).hexdigest() == ZIP_SHA, 'artifact bytes mismatch')
    with destination.open('xb') as stream:
        stream.write(body)


def safe_extract(zip_path, destination):
    require(zip_path.is_file() and not zip_path.is_symlink() and zip_path.stat().st_size <= MAX_DOWNLOAD, 'invalid artifact file')
    body = zip_path.read_bytes()
    require(len(body) == ZIP_SIZE and hashlib.sha256(body).hexdigest() == ZIP_SHA, 'artifact bytes mismatch')
    require(not destination.exists() and not destination.is_symlink(), 'extraction destination exists')
    with zipfile.ZipFile(zip_path) as archive:
        entries = archive.infolist()
        require(0 < len(entries) <= MAX_ENTRIES, 'archive entry limit exceeded')
        names, selected, total = set(), [], 0
        for entry in entries:
            name = entry.filename
            path = PurePosixPath(name)
            require(entry.orig_filename == name and name and '\\' not in name and '\x00' not in name and ':' not in name and not path.is_absolute()
                    and all(part not in ('', '.', '..') for part in name.rstrip('/').split('/'))
                    and str(path) == name.rstrip('/'), 'unsafe archive path')
            normalized = name.rstrip('/')
            require(normalized not in names, 'duplicate archive path')
            names.add(normalized)
            mode = entry.external_attr >> 16
            require(stat.S_IFMT(mode) in (0, stat.S_IFREG, stat.S_IFDIR) and not stat.S_ISLNK(mode), 'special archive member')
            require(not entry.flag_bits & 1 and entry.compress_type in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED), 'unsupported archive member')
            require(0 <= entry.file_size <= MAX_ENTRY, 'archive member limit exceeded')
            total += entry.file_size
            require(total <= MAX_UNPACKED, 'archive expansion limit exceeded')
            require(len(name.encode('utf-8')) <= 1024, 'archive path limit exceeded')
            if name.startswith(PREFIX) and not entry.is_dir():
                selected.append(entry)
        # Reject file/directory collisions, including ancestors of selected members.
        files = {e.filename for e in entries if not e.is_dir()}
        require(all(not any(str(p) in files for p in PurePosixPath(n).parents if str(p) != '.') for n in names), 'archive path collision')
        require(len(selected) == 315, 'historical file count mismatch')
        destination.mkdir(mode=0o700)
        for entry in selected:
            target = destination / entry.filename[len(PREFIX):]
            target.parent.mkdir(parents=True, exist_ok=True)
            require(target.resolve().is_relative_to(destination.resolve()), 'archive path escapes destination')
            with archive.open(entry) as stream:
                contents = read_response(stream, min(MAX_ENTRY, entry.file_size))
            require(len(contents) == entry.file_size, 'archive member size mismatch')
            with target.open('xb') as stream:
                stream.write(contents)
    return destination


def verify_baseline_source(root):
    def git(*args):
        return subprocess.check_output(['git','-C',str(root),*args], text=True, stderr=subprocess.DEVNULL).strip()
    require(git('rev-parse','HEAD') == BASE_SOURCE and git('rev-parse','HEAD^{tree}') == BASE_TREE, 'baseline source identity mismatch')
    require(not git('status','--porcelain','--untracked-files=all'), 'baseline source dirty')


def rebuild(root, source_root):
    verify_baseline_source(source_root)
    ledger = compare.read(source_root/'eng/api-inventory/3.0-decisions.json')
    identities = compare.read(source_root/'eng/api-inventory/3.0-decision-ids.json')
    provenance = compare.verify_reports(root)
    require(len(provenance['outputsSha256']) == 314, 'historical manifest count mismatch')
    for field in ('toolSourceSha256','sourceControlFilesSha256'):
        for name, expected in provenance[field].items():
            relative = Path(name)
            require(not relative.is_absolute() and '..' not in relative.parts, 'invalid baseline source path')
            require(hashlib.sha256((source_root/relative).read_bytes()).hexdigest() == expected, 'historical helper or control changed')
    targets = sorted(ledger['targets'].values(), key=lambda t:(t['project'],t['framework']))
    snapshot, symbols, relations, metadata = compare.validate_inventory(root,compare.HISTORICAL_SOURCE,compare.HISTORICAL_TREE,'10.0.113',targets,provenance['toolSourceSha256'])
    spec = importlib.util.spec_from_file_location('original_ledger',source_root/'eng/verify-api-decision-ledger.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    result = module.validate(ledger,identities,compare.read(root/'coverage.json'),provenance,compare.rows(root/'symbols.jsonl'),compare.read(root/'decisions.json'),compare.rows(root/'relations.jsonl'),source_root)
    require(result['status'] == 'proposal-not-approved' and result['declarations'] == 18713 and result['decisionIds'] == 3397 and result['types'] == 302 and result['memberExceptions'] == 5, 'historical ledger mismatch')
    fixture = {'schemaVersion':1,'projection':'all-symbol-fields-except-declarations-locations+incident-relations-v1',
               'historical':{**metadata,'runId':RUN_ID,'artifactId':ARTIFACT_ID},'targets':targets,
               'toolSourceSha256':provenance['toolSourceSha256'],'required':snapshot,'requiredSha256':compare.digest(snapshot),
               'assemblyRelationSha256':sorted(h for h,r in relations.items() if r['kind']=='internals-visible-to'),
               'sourceControlFilesSha256':provenance['sourceControlFilesSha256']}
    compare.validate_fixture(fixture)
    encoded = compare.canonical(fixture)+b'\n'
    require(fixture['requiredSha256'] == REQUIRED_SHA and hashlib.sha256(encoded).hexdigest() == FIXTURE_SHA, 'frozen baseline digest mismatch')
    return encoded


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--workspace',type=Path,required=True)
    parser.add_argument('--source-root',type=Path,required=True)
    args = parser.parse_args()
    try:
        require(not args.workspace.exists() and not args.workspace.is_symlink(), 'baseline workspace exists')
        args.workspace.mkdir(mode=0o700)
        archive = args.workspace/'historical.zip'
        fetch_archive(archive,os.environ.get('GH_READ_TOKEN'))
        root = safe_extract(archive,args.workspace/'inventory')
        encoded = rebuild(root,args.source_root)
        with (args.workspace/'expected.json').open('xb') as stream:
            stream.write(encoded)
        print('API_BASELINE '+json.dumps({'status':'VERIFIED','artifactId':ARTIFACT_ID,'fixtureSha256':FIXTURE_SHA,'requiredSha256':REQUIRED_SHA},sort_keys=True))
        return 0
    except Exception as error:
        # No remote responses, paths, file contents, credentials or traceback in CI logs.
        print('API_BASELINE '+json.dumps({'status':'INCOMPLETE','errorType':type(error).__name__},sort_keys=True))
        return 2


if __name__ == '__main__':
    sys.exit(main())
