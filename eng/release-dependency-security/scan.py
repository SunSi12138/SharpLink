#!/usr/bin/env python3
"""Source-bound hosted dependency scan; pure validation functions are locally testable."""
import argparse
import hashlib
import json
import os
import signal
import shlex
from urllib.parse import urlparse
from pathlib import Path
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

SOURCE = 'e8fcef3ddb93d76457b690dadc279e7a8f91b47d'
TREE = '23074b603a369fe339b9958dec2da53a04b4bfa8'
REQUEST = 'eng/release-dependency-security/request.json'
TOOLS = {'.github/workflows/release-dependency-security.yml',
         'eng/release-dependency-security/scan.py',
         'eng/release-dependency-security/test_scan.py',
         'eng/release-dependency-security/README.md'}

def git(root, *args):
    return subprocess.check_output(['git', '-C', str(root), *args], text=True).strip()

def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + '\n')

def validate(root, source, event):
    r = json.loads((root / REQUEST).read_text())
    if set(r) != {'source_sha', 'source_tree', 'tools_sha'} or r['source_sha'] != SOURCE or r['source_tree'] != TREE:
        raise ValueError('wrong request schema/source')
    if git(root, 'rev-parse', 'HEAD') != event or git(root, 'show', '-s', '--format=%P', 'HEAD') != r['tools_sha']:
        raise ValueError('wrong event/parent')
    if git(root, 'diff', '--name-only', 'HEAD^', 'HEAD').splitlines() != [REQUEST]:
        raise ValueError('request must change only request.json')
    if git(root, 'ls-tree', '-r', '--name-only', 'HEAD^', '--', REQUEST):
        raise ValueError('only first request accepted')
    if set(git(root, 'diff', '--name-only', SOURCE, 'HEAD^').splitlines()) != TOOLS:
        raise ValueError('tools change outside reviewed allowlist')
    if git(source, 'rev-parse', 'HEAD') != SOURCE or git(source, 'rev-parse', 'HEAD^{tree}') != TREE:
        raise ValueError('wrong actual source')
    if git(source, 'status', '--porcelain', '--untracked-files=no'):
        raise ValueError('dirty tracked source')
    return {**r, 'request_sha': event, 'run_id': os.getenv('GITHUB_RUN_ID'),
            'run_attempt': os.getenv('GITHUB_RUN_ATTEMPT')}

def census(source):
    tracked = set(git(source, 'ls-files', '*.csproj').splitlines())
    projects = sorted(p.attrib['Path'].replace('\\', '/') for p in ET.parse(source / 'Sharplink.slnx').iter('Project'))
    if len(projects) != len(set(projects)) or not set(projects) <= tracked:
        raise ValueError('duplicate or untracked solution projects')
    production = sorted(p for p in tracked if p.startswith('src/'))
    if len(production) != 10 or not set(production) <= set(projects) or len(projects) != 64:
        raise ValueError('unexpected production/solution census')
    return {'solution_projects': projects, 'production_projects': production,
            'development_projects': [p for p in projects if p not in production],
            'outside_solution_not_scanned': sorted(tracked - set(projects)),
            'scope': 'all 10 src projects and 54 development solution projects; not a whole-repository scan'}

def report(data, mode, expected, source):
    parameters = shlex.split(data.get('parameters', ''))
    wanted = ['--include-transitive'] + ([] if mode == 'inventory' else ['--' + mode])
    if data.get('version') != 1 or sorted(parameters) != sorted(wanted):
        raise ValueError('wrong schema or unexpected scan parameters/filters')
    if mode != 'inventory':
        sources = data.get('sources')
        if not isinstance(sources, list) or not sources or any(not isinstance(item, str) or not item.strip() for item in sources):
            raise ValueError('missing or malformed data sources')
    if data.get('problems'):
        raise ValueError('report contains problems')
    found, findings = {}, []
    for project in data.get('projects', []):
        path = Path(project['path'])
        path = path.relative_to(source).as_posix() if path.is_absolute() else path.as_posix()
        if path in found or path not in expected or project.get('problems'):
            raise ValueError('duplicate/unexpected/problem project: ' + path)
        frameworks = project.get('frameworks', [])
        if not isinstance(frameworks, list):
            raise ValueError('bad frameworks')
        found[path] = {}
        for framework in frameworks:
            name = framework['framework']
            if name in found[path] or name not in expected[path]:
                raise ValueError('unexpected/duplicate framework: ' + name)
            packages = [(kind, p) for kind in ('topLevelPackages', 'transitivePackages') for p in framework.get(kind, [])]
            found[path][name] = [(p['id'], p['resolvedVersion']) for _, p in packages]
            for kind, package in packages:
                if mode == 'vulnerable':
                    advisories = package.get('vulnerabilities')
                    if not isinstance(advisories, list) or not advisories:
                        raise ValueError('vulnerability entry lacks advisory details')
                    for advisory in advisories:
                        url = urlparse(advisory.get('advisoryurl', ''))
                        if advisory.get('severity') not in ('Low', 'Moderate', 'High', 'Critical') or url.scheme != 'https' or not url.netloc:
                            raise ValueError('invalid vulnerability severity/advisory URL')
                if mode == 'deprecated' and not package.get('deprecationReasons'):
                    raise ValueError('deprecation entry lacks reasons')
                if mode != 'inventory':
                    findings.append({'project': path, 'scope': 'production' if path.startswith('src/') else 'development',
                                     'framework': name, 'dependency_kind': kind, **package})
        # Filtered reports legitimately omit frameworks with no matching packages.
        # Unfiltered inventory plus fresh assets independently establish those TFMs.
        if mode == 'inventory' and set(found[path]) != set(expected[path]):
            raise ValueError('inventory missing frameworks: ' + path)
    if set(found) != set(expected):
        raise ValueError('report project coverage differs: ' + repr(sorted(set(expected) - set(found))))
    if mode == 'inventory' and not any(pkgs for fs in found.values() for pkgs in fs.values()):
        raise ValueError('empty package inventory')
    return {'projects': found, 'findings': findings}

def execute(name, args, source, out):
    started = time.time()
    record = {'name': name, 'argv': args, 'cwd': str(source), 'exit_code': None,
              'timed_out': False, 'started_unix': started}
    with (out / (name + '.stdout')).open('wb') as stdout, (out / (name + '.stderr')).open('wb') as stderr:
        process = subprocess.Popen(args, cwd=source, stdout=stdout, stderr=stderr, start_new_session=True)
        try:
            record['exit_code'] = process.wait(timeout=600)
        except subprocess.TimeoutExpired:
            record['timed_out'] = True
            try:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=10)
                try:
                    os.killpg(process.pid, 0)
                    record['process_group_cleanup'] = 'uncertain_group_still_exists'
                except ProcessLookupError:
                    record['process_group_cleanup'] = 'confirmed_absent'
            except Exception as exc:
                record['process_group_cleanup'] = 'uncertain: ' + type(exc).__name__
            # Do not turn a timeout into a success using the subsequent SIGKILL exit.
            record['process_exit_after_cleanup'] = process.returncode
    record['elapsed_seconds'] = time.time() - started
    return record

def run_scan(root, source, out):
    if os.getenv('GITHUB_ACTIONS') != 'true':
        raise ValueError('restore/scan is hosted-only; local tests must use pure fixtures')
    out.mkdir(parents=True, exist_ok=True)
    ledger, errors = [], []
    def call(name, args):
        record = execute(name, args, source, out)
        ledger.append(record)
        write(out / 'commands.json', ledger)
        if record['timed_out']:
            raise RuntimeError(name + ' timed out; no further restore/scan; cleanup=' + record.get('process_group_cleanup', 'uncertain'))
        if record['exit_code'] != 0:
            errors.append(name + ' exit ' + str(record['exit_code']))
        return record['exit_code']
    result = {'status': 'INCOMPLETE', 'errors': errors}
    try:
        result['provenance'] = validate(root, source, os.environ['GITHUB_SHA'])
        result['coverage'] = census(source)
        for key in ('NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH'):
            directory = Path(os.environ[key])
            if directory.exists() and any(directory.iterdir()):
                raise ValueError(key + ' must start empty')
            directory.mkdir(parents=True, exist_ok=True)
        result['fresh_cache_paths'] = {k: os.environ[k] for k in ('NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH')}
        result['policy_sha256'] = {p: hashlib.sha256((source / p).read_bytes()).hexdigest()
                                  for p in ('global.json', 'Directory.Build.props', 'Sharplink.slnx')}
        call('dotnet-info', ['dotnet', '--info'])
        call('dotnet-version', ['dotnet', '--version'])
        call('package-list-help', ['dotnet', 'package', 'list', '--help'])
        help_text = (out / 'package-list-help.stdout').read_text()
        for flag in ('--project', '--no-restore', '--include-transitive', '--vulnerable', '--deprecated', '--format', '--output-version'):
            if flag not in help_text:
                raise ValueError('fixed SDK help missing ' + flag)
        if any((source / Path(p).parent / 'obj/project.assets.json').exists() for p in result['coverage']['solution_projects']):
            raise ValueError('assets already exist before fresh restore')
        call('restore', ['dotnet', 'restore', 'Sharplink.slnx', '--verbosity', 'normal'])
        expected, assets = {}, {}
        for project in result['coverage']['solution_projects']:
            path = source / Path(project).parent / 'obj/project.assets.json'
            if not path.is_file():
                errors.append('missing fresh assets: ' + project)
                continue
            data = json.loads(path.read_text())
            expected[project] = data['project']['restore']['originalTargetFrameworks']
            assets[project] = {'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
                               'frameworks': expected[project], 'restore': data['project']['restore'],
                               'packages': {k: v for k, v in data['libraries'].items() if v.get('type') == 'package'},
                               'logs': data.get('logs', [])}
        write(out / 'assets-inventory.json', assets)
        if set(expected) != set(result['coverage']['solution_projects']):
            errors.append('fresh assets census incomplete')
        reports = {}
        for mode in ('inventory', 'vulnerable', 'deprecated'):
            args = ['dotnet', 'package', 'list', '--project', 'Sharplink.slnx', '--no-restore', '--include-transitive', '--format', 'json', '--output-version', '1']
            if mode != 'inventory':
                args.append('--' + mode)
            call(mode, args)
            try:
                raw = (out / (mode + '.stdout')).read_bytes()
                (out / (mode + '.json')).write_bytes(raw)
                reports[mode] = report(json.loads(raw), mode, expected, source)
            except (ValueError, KeyError, TypeError) as exc:
                errors.append(mode + ': ' + str(exc))
        result['reports'] = reports
        if 'inventory' in reports:
            inventory = reports['inventory']['projects']
            for project, frameworks in inventory.items():
                listed = {(i.lower(), v.lower()) for packages in frameworks.values() for i, v in packages}
                restored = {tuple(k.lower().rsplit('/', 1)) for k in assets[project]['packages']}
                if listed != restored:
                    errors.append('package inventory differs from fresh assets: ' + project)
            for mode in ('vulnerable', 'deprecated'):
                for item in reports.get(mode, {}).get('findings', []):
                    listed = inventory[item['project']][item['framework']]
                    if (item['id'], item['resolvedVersion']) not in listed:
                        errors.append(mode + ' finding absent from inventory: ' + item['id'])
        if git(source, 'status', '--porcelain', '--untracked-files=no'):
            errors.append('tracked source changed during scan')
        if not errors:
            result['status'] = 'ADVISORY_TRIAGE_REQUIRED' if reports['vulnerable']['findings'] else 'NO_KNOWN_VULNERABILITIES_IN_SCANNED_SCOPE'
            result['deprecation_status'] = 'REVIEW_REQUIRED' if reports['deprecated']['findings'] else 'NONE_REPORTED'
    except Exception as exc:
        errors.append(type(exc).__name__ + ': ' + str(exc))
    finally:
        result['completed_unix'] = time.time()
        write(out / 'summary.json', result)
        print(json.dumps({'status': result['status'], 'errors': errors}))
    return 1 if errors else (2 if result['status'] == 'ADVISORY_TRIAGE_REQUIRED' else 0)

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('action', choices=['validate', 'scan'])
    parser.add_argument('--source', default='source')
    parser.add_argument('--output', default='dependency-security-report')
    args = parser.parse_args()
    root, source, out = Path.cwd().resolve(), Path(args.source).resolve(), Path(args.output).resolve()
    if args.action == 'validate':
        try:
            write(out / 'preflight.json', validate(root, source, os.environ['GITHUB_SHA']))
        except Exception as exc:
            write(out / 'preflight.json', {'status': 'INCOMPLETE', 'error': str(exc)})
            raise
    else:
        sys.exit(run_scan(root, source, out))
