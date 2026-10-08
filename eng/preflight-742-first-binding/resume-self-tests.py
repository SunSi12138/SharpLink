#!/usr/bin/env python3
"""Reject altered evidence and source before reusing the pinned successful stages."""
import copy
import importlib.util
import json
import pathlib
import sys
import tempfile

path = pathlib.Path(__file__).with_name('reuse-safety.py')
loader = importlib.util.spec_from_file_location('reuse_safety', path)
reuse = importlib.util.module_from_spec(loader)
loader.loader.exec_module(reuse)
download, work = map(pathlib.Path, sys.argv[1:])
archives = [download] if download.is_file() else list(download.rglob('*.zip'))
assert len(archives) == 1
files = reuse.load_archive(archives[0])
reuse.validate_content(files)
reuse.validate_sources(files, work)
pin = reuse.PIN
run = dict(id=pin['run_id'], run_attempt=pin['run_attempt'], head_sha=pin['head'],
           path=pin['workflow_path'], repository={'full_name': pin['repository']},
           status='completed', conclusion='failure')
artifact = dict(pin['artifact'], expired=False, workflow_run={'id': pin['run_id'], 'head_sha': pin['head']})
job = dict(id=pin['job_id'], name=pin['job_name'], run_id=pin['run_id'], run_attempt=pin['run_attempt'],
           head_sha=pin['head'], status='completed', conclusion='failure',
           steps=[dict(row, status='completed') for row in pin['required_steps']])
jobs = dict(total_count=1, jobs=[job])
reuse.validate_metadata(run, jobs, artifact)


def rejected(action):
    try:
        action()
    except AssertionError:
        return
    raise AssertionError('Altered evidence was accepted')


bad = dict(files)
bad['default/format.log'] = b'changed'
rejected(lambda: reuse.validate_hashes(bad))
bad = dict(files)
bad.pop('default/format.log')
rejected(lambda: reuse.validate_hashes(bad))
bad = dict(files)
name = 'default/A-tests-default-unit.log'
bad[name] = bad[name].replace(b'2186', b'2185')
rejected(lambda: reuse.validate_content(bad))
bad = dict(files)
source = json.loads(bad['source/identities.json'])
source['trees']['A'] = '0' * 40
bad['source/identities.json'] = json.dumps(source).encode()
rejected(lambda: reuse.validate_content(bad))
bad_jobs = copy.deepcopy(jobs)
bad_jobs['jobs'][0]['steps'][1]['conclusion'] = 'failure'
rejected(lambda: reuse.validate_metadata(run, bad_jobs, artifact))
bad_artifact = dict(artifact, digest='sha256:' + '0' * 64)
rejected(lambda: reuse.validate_metadata(run, jobs, bad_artifact))
with tempfile.TemporaryDirectory() as temporary:
    destination = pathlib.Path(temporary)
    source = json.loads((work / 'identities.json').read_text())
    source['trees']['A'] = '0' * 40
    (destination / 'identities.json').write_text(json.dumps(source))
    rejected(lambda: reuse.validate_sources(files, destination))
print('Exact83-file safety evidence validated;7 altered-evidence/source fixtures rejected before codegen.')
