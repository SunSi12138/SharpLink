#!/usr/bin/env python3
"""Reuse only exact immutable safety evidence before the unfinished codegen stages."""
import argparse
import hashlib
import json
import os
import pathlib
import re
import shutil
import urllib.request
import zipfile

from prepare import KIT, SOURCE, git, save, sha, spec, trees

PIN = json.loads((KIT / 'prior-safety.json').read_text())
ALLOWED = ('.github/workflows/742-first-binding-preflight.yml',
           'eng/preflight-742-first-binding/README.md',
           'eng/preflight-742-first-binding/layout/RootLayout.csproj',
           'eng/preflight-742-first-binding/prior-safety.json',
           'eng/preflight-742-first-binding/reuse-safety.py',
           'eng/preflight-742-first-binding/resume-self-tests.py')


def api(path):
    request = urllib.request.Request('https://api.github.com/repos/' + PIN['repository'] + path,
        headers={'Authorization': 'Bearer ' + os.environ['GITHUB_TOKEN'],
                 'Accept': 'application/vnd.github+json'})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def validate_metadata(run, jobs, artifact):
    assert run['id'] == PIN['run_id'] and run['run_attempt'] == PIN['run_attempt']
    assert run['head_sha'] == PIN['head'] and run['path'] == PIN['workflow_path']
    assert run['repository']['full_name'] == PIN['repository']
    assert run['status'] == 'completed' and run['conclusion'] == PIN['run_conclusion'] == 'failure'
    assert all(artifact[key] == value for key, value in PIN['artifact'].items())
    assert artifact['expired'] is False
    assert artifact['workflow_run']['id'] == PIN['run_id'] and artifact['workflow_run']['head_sha'] == PIN['head']
    assert jobs['total_count'] == len(jobs['jobs']) == 1
    job = jobs['jobs'][0]
    assert job['id'] == PIN['job_id'] and job['name'] == PIN['job_name']
    assert job['run_id'] == PIN['run_id'] and job['run_attempt'] == PIN['run_attempt']
    assert job['head_sha'] == PIN['head'] and job['status'] == 'completed' and job['conclusion'] == 'failure'
    for expected in PIN['required_steps']:
        matches = [step for step in job['steps'] if step['number'] == expected['number']]
        assert len(matches) == 1 and matches[0]['status'] == 'completed'
        assert all(matches[0][key] == value for key, value in expected.items()), expected


def file_hashes(files):
    return {name: dict(sha256=hashlib.sha256(data).hexdigest(), bytes=len(data)) for name, data in files.items()}


def validate_hashes(files):
    assert len(files) == PIN['file_count'] == 83
    assert file_hashes(files) == PIN['files'], 'The complete retained safety file population must match'


def load_archive(archive):
    assert archive.stat().st_size == PIN['artifact']['size_in_bytes']
    assert 'sha256:' + sha(archive) == PIN['artifact']['digest'], 'Immutable ZIP digest mismatch'
    with zipfile.ZipFile(archive) as bundle:
        entries = [entry for entry in bundle.infolist() if not entry.is_dir()]
        assert len(entries) == len({entry.filename for entry in entries}) == PIN['file_count']
        assert all(entry.filename in PIN['files'] for entry in entries)
        files = {entry.filename: bundle.read(entry) for entry in entries}
    validate_hashes(files)
    return files


def validate_content(files):
    def document(name):
        return json.loads(files[name])

    author = spec()
    assert files['source/author-manifest.json'] == (KIT / 'author-manifest.json').read_bytes()
    assert document('source/identities.json')['execution_commit'] == PIN['head']
    assert document('source/identities.json')['trees'] == PIN['trees'] == trees()
    assert document('packaging-failures.json') == []
    for name in ('production.patch', 'tests.patch'):
        assert files['source/' + name] == (KIT / name).read_bytes()
    populations = {f'default/{arm}-default-unit.log': 2186 for arm in ('G2-tests', 'A-tests')}
    populations['experimental/experimental-unit.log'] = 2186
    for arm in ('G2-tests', 'A-tests'):
        for row in author['focused_tests']:
            populations[f"default/{arm}-{row['class']}-{row['method'].rstrip('*')}.log"] = row['count']
    assert len(populations) == 15 and sum(row['count'] for row in author['focused_tests']) == 58
    for name, expected in populations.items():
        text = re.sub(r'\x1b\[[0-9;]*m', '', files[name].decode())
        for key, count in (('total', expected), ('succeeded', expected), ('failed', 0), ('skipped', 0)):
            assert re.search(rf'{key}:\s+{count}\b', text), (name, key, count)
    default = document('default/default-safety-result.json')
    assert default['full_default_units'] == {'G2': 2186, 'A': 2186}
    assert default['original_cases_per_arm'] == 2128 and default['new_cases_per_arm'] == 58
    assert default['original_allocation_gates'] and default['legacy_lifecycle_rows_per_arm'] == 120
    experimental = document('experimental/experimental-safety-result.json')
    assert experimental['full_experimental_units'] == 2186 and experimental['writer_checks'] == 171
    assert b'171/171 ready writer transport checks passed.' in files['experimental/writer-checks.log']
    for arm in ('G2-tests', 'A-tests'):
        allocation = document(f'default/{arm}-allocation-gate.json')
        assert allocation['passed'] and not allocation['errors'] and len(allocation['cases']) == 4
        assert all(case['passed'] and case['failure'] is None for case in allocation['cases'])
        assert allocation['runtimeVersion'] == '10.0.12' and allocation['configuration'] == 'Release'
    # These are checks of already-recorded exact evidence, not changed thresholds
    # for a newly executed gate. Both historical 120-row matrices observed zero.
    for name in ('default/lifecycle-comparison.json', 'default/production-lifecycle/comparison.json'):
        rows = document(name)
        assert len(rows) == 120 and all(row['delta'] == 0 for row in rows)
    setup = document('default/actual-setup/summary.json')
    assert {row['scenario'] for row in setup} == {'actual-session-constructor', 'fresh-manager-empty-route',
        'layout-DispatcherEntry', 'layout-DispatcherEntryCompletions', 'layout-StreamManager'}
    assert len(setup) == 5 and all(row['median_delta'] == 0 for row in setup)
    assert files['default/format.log'] == b''
    assert b'Production project-reference boundary guard passed' in files['default/references.log']
    assert b'Maintainability debt gate passed' in files['default/maintainability.log']
    return dict(full_suites=3, cases_per_full_suite=2186, focused_per_default_arm=58,
        allocation_reports=2, allocation_cases_per_report=4, lifecycle_rows=[120, 120], setup_scenarios=5,
        writer_checks=171, engineering_guards=['format', 'references', 'maintainability'])


def validate_sources(files, work):
    exclusions = [':(exclude)' + name for name in ALLOWED]
    for revision in ((PIN['head'], 'HEAD'), ()):
        assert not git(SOURCE, 'diff', '--name-only', *revision, '--', '.', *exclusions), 'Resume source/gate drift'
    current = json.loads((work / 'identities.json').read_text())
    archived = json.loads(files['source/identities.json'])
    for key in ('trees', 'production_src_subtrees', 'production_changes', 'patches',
                'full_g2_patch_sha256', 'common_harness_sha256', 'expected_unit_cases', 'focused_tests'):
        assert current[key] == archived[key], key
    for label in PIN['trees']:
        old = json.loads(files[f'source/{label}-source.json'])
        root = pathlib.Path(current['roots'][label])
        new = json.loads((root / 'artifacts/first-receive-integration/provenance.json').read_text())
        assert new == old, label


def main(download, work, output):
    archives = [download] if download.is_file() else list(download.rglob('*.zip'))
    assert len(archives) == 1, 'The single pinned original ZIP is required'
    files = load_archive(archives[0])
    run = api(f"/actions/runs/{PIN['run_id']}/attempts/{PIN['run_attempt']}")
    jobs = api(f"/actions/runs/{PIN['run_id']}/attempts/{PIN['run_attempt']}/jobs?per_page=100")
    artifacts = api(f"/actions/runs/{PIN['run_id']}/artifacts?per_page=100")
    selected = [item for item in artifacts['artifacts'] if item['id'] == PIN['artifact']['id']]
    assert len(selected) == 1
    validate_metadata(run, jobs, selected[0])
    summary = validate_content(files)
    validate_sources(files, work)
    output.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(archives[0], output / 'prior-safety.zip')
    save(output / 'reused-safety.json', dict(prior_run=PIN['run_id'], prior_attempt=PIN['run_attempt'],
        prior_helper=PIN['head'], prior_overall_conclusion='failure', artifact=PIN['artifact'],
        verified_steps=PIN['required_steps'], verified_files=file_hashes(files), trees=PIN['trees'],
        safety=summary, current_helper=git(SOURCE, 'rev-parse', 'HEAD'),
        resumed_only=['managed JIT layout', 'four original diagnostic RPC captures', 'NativeAOT code collection'],
        boundary='Immutable successful safety stages reused after exact source/test/hash verification. '
                 'The prior job remains failed. No shipping gate waiver, layout proof or throughput acceptance.'))
    print(json.dumps(summary, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('download', type=pathlib.Path)
    parser.add_argument('work', type=pathlib.Path)
    parser.add_argument('output', type=pathlib.Path)
    args = parser.parse_args()
    main(args.download.resolve(), args.work.resolve(), args.output.resolve())
