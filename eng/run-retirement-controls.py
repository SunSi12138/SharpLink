#!/usr/bin/env python3
"""One ordinary Unit graph; 47 selected actual UIDs, each in its own process.

No SDK invocation is authorized by importing this file. Raw reports/receipts stay
in a private disposable runner directory; console contains bounded summaries only.
"""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import sys
import tempfile
import time
import xml.etree.ElementTree as ET

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


owner = module('retirement_process', ROOT / 'eng/package-smoke-process.py')
results = module('retirement_results', ROOT / 'eng/retirement-results.py')
require = results.require


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def environment(work):
    keep = ('PATH', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'LANG', 'LC_ALL',
            'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY', 'http_proxy',
            'https_proxy', 'all_proxy', 'no_proxy', 'SSL_CERT_FILE', 'SSL_CERT_DIR')
    env = {key: os.environ[key] for key in keep if key in os.environ}
    for directory in ('home', 'cli', 'packages', 'http', 'plugins', 'tmp'):
        (work / directory).mkdir()
    env.update(HOME=str(work / 'home'), DOTNET_CLI_HOME=str(work / 'cli'),
               NUGET_PACKAGES=str(work / 'packages'), NUGET_HTTP_CACHE_PATH=str(work / 'http'),
               NUGET_PLUGINS_CACHE_PATH=str(work / 'plugins'), TMPDIR=str(work / 'tmp'),
               DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1',
               DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', DOTNET_MULTILEVEL_LOOKUP='0',
               MSBUILDDISABLENODEREUSE='1', DOTNET_CLI_USE_MSBUILD_SERVER='0', CI='true',
               GIT_CONFIG_GLOBAL='/dev/null', GIT_CONFIG_SYSTEM='/dev/null',
               GIT_ALLOW_PROTOCOL='file', GIT_NO_LAZY_FETCH='1', GIT_TERMINAL_PROMPT='0',
               TUNIT_ENABLE_JUNIT_REPORTER='1', TUNIT_DISABLE_ARTIFACT_UPLOAD='1',
               TUNIT_DISABLE_GITHUB_REPORTER='1', TUNIT_AGGREGATE_REPORTS='off', TUNIT_DISABLE_LOGO='1')
    return env


def verify_graph(source, catalog):
    found = set()
    todo = [catalog['project']]
    while todo:
        relative = todo.pop()
        if relative in found:
            continue
        found.add(relative)
        path = source / relative
        for ref in ET.parse(path).findall('.//ProjectReference'):
            target = (path.parent / ref.attrib['Include'].replace('\\', '/')).resolve()
            todo.append(target.relative_to(source).as_posix())
    require(sorted(found) == catalog['projects'] and len(found) == 14, 'Ordinary Unit project closure changed')
    counts = {group: 0 for group in catalog['expectedGroups']}
    identities = set()
    for scope in catalog['scopes']:
        for item in scope['methods']:
            identity = (scope['namespace'], scope['class'], item['name'])
            require(identity not in identities, 'Duplicate selected method')
            identities.add(identity)
            results.source_span(source / scope['source'], item['name'])
            counts[scope['group']] += 1
    require(counts == catalog['expectedGroups'] and sum(counts.values()) == 47, 'Candidate catalog mismatch')


def asset_identity(source, catalog, packages, expected_package_identity):
    assets = sorted(p.relative_to(source).as_posix() for p in source.rglob('project.assets.json'))
    expected = sorted(str(Path(p).parent / 'obj/project.assets.json') for p in catalog['projects'])
    require(assets == expected, 'Actual restored project assets differ from Unit14')
    for name in assets:
        value = results.read_json(source / name)
        restore = value['project']['restore']
        require(set(restore['sources']) == {'https://api.nuget.org/v3/index.json'}, 'Nonofficial restore source')
        results.package_directories(value.get('packageFolders'), restore.get('packagesPath'),
                                    packages, expected_package_identity, source, name)
        for key in value['libraries']:
            package, version = key.rsplit('/', 1)
            if package == 'TUnit' or package.startswith('TUnit.'):
                require(version == catalog['tunitPackageVersion'], 'TUnit package version mismatch')
    unit = results.read_json(source / Path(catalog['project']).parent / 'obj/project.assets.json')
    require('TUnit/' + catalog['tunitPackageVersion'] in unit['libraries'], 'Missing actual TUnit package')
    return {name: sha(source / name) for name in assets}


def summarize_failure(error):
    if isinstance(error, BaseExceptionGroup):
        for member in error.exceptions:
            summarize_failure(member)
    else:
        print('failure: ' + type(error).__name__ + ': ' + json.dumps(str(error)[:1800]), flush=True)


def main():
    catalog = results.read_json(ROOT / 'eng/retirement-controls.json')
    limits = catalog['limits']
    owner.enable_subreaper()
    work = Path(tempfile.mkdtemp(prefix='slret-', dir='/tmp'))
    logs = work / 'logs'; logs.mkdir()
    env = environment(work)
    package_identity = results.directory_identity(str(work / 'packages'))
    stop_at = time.monotonic() + limits['commandBudgetSeconds']
    sequence = 0
    source = work / 'src'
    records = [dict(group=s['group'], namespace=s['namespace'], className=s['class'], method=m['name'],
                    qualification=m['qualification'], status='UNRUN', uid=None)
               for s in catalog['scopes'] for m in s['methods']]
    ledger = {}
    original = {}
    built = {}
    errors = []
    current = None

    def run(label, argv, cwd=ROOT, seconds=120):
        nonlocal sequence
        remaining = stop_at - time.monotonic()
        require(remaining > 0, 'Remaining command budget exhausted before ' + label)
        sequence += 1
        pipe = work / ('p' + str(sequence)); pipe.mkdir()
        require(len(os.fsencode(pipe)) + 1 + 32 <= 103, 'Testing platform Unix socket path too long')
        identity = (pipe.stat().st_dev, pipe.stat().st_ino)
        child = dict(env, TESTINGPLATFORM_PIPE_DIRECTORY=str(pipe))
        failure = None
        output = None
        try:
            output = owner.run_owned(label, [str(x) for x in argv], cwd, child, logs, min(seconds, remaining))
        except BaseException as error:
            failure = error
        checks = []
        try:
            require((pipe.stat().st_dev, pipe.stat().st_ino) == identity and not any(pipe.iterdir()),
                    'Pipe directory changed or retained entries: ' + label)
        except BaseException as error:
            checks.append(error)
        if failure is not None or checks:
            if failure is not None:
                checks.insert(0, failure)
            try:
                for suffix in ('stdout', 'stderr'):
                    path = logs / (label + '.' + suffix)
                    if path.exists():
                        lines = path.read_text(errors='replace').splitlines()
                        important = [line for line in lines if re.search(r'error|exception|failed|timeout', line, re.I)]
                        for line in (important or lines[-3:])[:8]:
                            print('diagnostic: ' + line[:500], flush=True)
            except BaseException as error:
                checks.append(error)
            raise BaseExceptionGroup('Command and actual containment checks: ' + label, checks)
        print('stage: ' + label + ' exit=0 owned=joined', flush=True)
        return output[0]

    def git(label, *args, cwd=ROOT):
        return run(label, ['git', '-c', 'core.hooksPath=/dev/null', *args], cwd, 60).read_text().strip()

    def check_files():
        for path, identity in original.items():
            require(path.is_file() and not path.is_symlink() and
                    (sha(path), path.stat().st_mode & 0o777) == identity, 'Tracked source changed: ' + str(path))
        for path, expected in built.items():
            require(path.is_file() and sha(path) == expected, 'Runtime/build input changed: ' + str(path))

    try:
        head = git('head', 'rev-parse', 'HEAD')
        tree = git('tree', 'rev-parse', 'HEAD^{tree}')
        require(re.fullmatch('[0-9a-f]{40}', head) and re.fullmatch('[0-9a-f]{40}', tree), 'Invalid Git identity')
        require(git('clean', 'status', '--porcelain', '--untracked-files=all') == '', 'Checkout must be clean')
        names = run('tracked', ['git', 'ls-files', '-z'], seconds=60).read_bytes().decode().split('\0')[:-1]
        require(names and len(names) == len(set(names)), 'Invalid tracked inventory')
        for name in names:
            path = ROOT / name
            require(path.is_file() and not path.is_symlink(), 'Unexpected tracked nonregular file')
            original[path] = (sha(path), path.stat().st_mode & 0o777)
        run('clone', ['git', '-c', 'core.hooksPath=/dev/null', 'clone', '--no-local', '--depth=1', '--no-checkout', ROOT.as_uri(), source], seconds=120)
        git('checkout', 'checkout', '--detach', head, cwd=source)
        require(git('cloned-head', 'rev-parse', 'HEAD', cwd=source) == head, 'Clone identity mismatch')
        for name in names:
            path = source / name
            require((sha(path), path.stat().st_mode & 0o777) == original[ROOT / name], 'Clone source mismatch')
            original[path] = original[ROOT / name]
        for ancestor in source.parents:
            for name in ('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'NuGet.Config', 'nuget.config', 'global.json'):
                require(not (ancestor / name).exists(), 'Unselected ancestor build/restore input: ' + str(ancestor / name))
        verify_graph(source, catalog)
        run('classifier-checks', [sys.executable, '-B', source / 'eng/tests/test_retirement_results.py'], source, 30)
        dotnet = Path(shutil.which('dotnet', path=env.get('PATH')) or '').resolve()
        require(dotnet.is_file(), 'Official setup-dotnet executable unavailable')
        built[dotnet] = sha(dotnet)
        sdk = run('sdk-version', [dotnet, '--version'], source).read_text().strip()
        require(re.fullmatch(r'10\.0\.1\d\d', sdk) is not None, 'global.json selected an unexpected SDK feature band')
        run('sdk-info', [dotnet, '--info'], source)
        runtimes = run('runtimes', [dotnet, '--list-runtimes'], source).read_text()
        print('identity: ' + json.dumps(dict(head=head, tree=tree, sdk=sdk, dotnet=str(dotnet), dotnetSha256=built[dotnet], projects=14, selected=47)), flush=True)
        config = work / 'NuGet.Config'
        config.write_text('<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3"/></packageSources></configuration>\n')
        project = source / catalog['project']
        run('restore', [dotnet, 'restore', project, '--configfile', config, '--disable-parallel', '-p:Configuration=Release', '-p:RestoreIgnoreFailedSources=false', '-m:1', '-nr:false'], source, limits['restoreSeconds'])
        assets = asset_identity(source, catalog, work / 'packages', package_identity)
        built.update({source / name: value for name, value in assets.items()})
        check_files()
        run('build', [dotnet, 'build', project, '-c', 'Release', '--no-restore', '-v:minimal', '-p:UseSharedCompilation=false', '-p:PathMap=' + str(source) + '=/_/retirement-unit', '-m:1', '-nr:false'], source, limits['buildSeconds'])
        output = project.parent / 'bin/Release/net10.0'
        dll = output / 'SharpLink.UnitTests.dll'
        require(dll.is_file(), 'Unit binary missing')
        for plugin in ('SharpLink.RollbackPlugin', 'SharpLink.RetirementEntryPlugin'):
            expected = source / 'test' / plugin / 'bin/Release/net10.0' / (plugin + '.dll')
            require(sha(output / (plugin + '.dll')) == sha(expected), 'Plugin runtime copy mismatch')
        built.update({p: sha(p) for p in output.rglob('*') if p.is_file()})
        print('binary: SharpLink.UnitTests.dll sha256=' + sha(dll), flush=True)
        for number, scope in enumerate(catalog['scopes']):
            batches = [scope['methods']] if scope['discovery'] == 'class' else [[m] for m in scope['methods']]
            for part, methods in enumerate(batches):
                selected = '*' if scope['discovery'] == 'class' else methods[0]['name']
                filter_value = '/*/' + scope['namespace'] + '/' + scope['class'] + '/' + selected
                path = run('list-' + str(number) + '-' + str(part), [dotnet, dll, '--list-tests', 'json', '--treenode-filter', filter_value], source, limits['discoverySeconds'])
                rows = results.discovery(results.read_json(path), scope, methods, source / scope['source'], catalog['assemblyFullName'], Path('/_/retirement-unit') / scope['source'])
                require(not set(rows).intersection(ledger), 'UID appears in multiple selections')
                for uid, row in rows.items():
                    row['selection'] = scope
                    ledger[uid] = row
        require(len(ledger) == catalog['expectedCaseCount'], 'Actual discovered UID count mismatch')
        for record in records:
            matches = [r for r in ledger.values() if r['type']['namespace'] == record['namespace'] and
                       r['selection']['class'] == record['className'] and r['type']['methodName'] == record['method']]
            require(len(matches) == 1, 'Actual UID binding is not singleton')
            record['uid'] = matches[0]['uid']
        (work / 'actual-ledger.json').write_text(json.dumps(ledger, indent=2) + '\n')
        check_files()
        runtime = None
        generations = set()
        for index, record in enumerate(records):
            current = record
            row = ledger[record['uid']]
            report = work / ('r' + str(index)); report.mkdir()
            filter_value = '/*/' + record['namespace'] + '/' + record['className'] + '/' + record['method']
            # The exact filter is bound above to ONE real UID. No guessed UID/filter CLI.
            record['status'] = 'INCOMPLETE'
            run('case-' + str(index), [dotnet, dll, '--treenode-filter', filter_value, '--maximum-parallel-tests', '1', '--timeout', limits['engineTimeout'], '--results-directory', report, '--report-html', '--report-html-filename', report / 'session.html', '--junit-output-path', report / 'junit.xml'], source, limits['caseSeconds'])
            verdict = results.passed(results.read_json(report / 'session.tunit-report.json'), (report / 'junit.xml').read_text(), row, Path('/_/retirement-unit') / row['selection']['source'], catalog['tunitRuntimeVersion'], runtime, filter_value)
            if runtime is None:
                print('runtime: ' + verdict['runtime'], flush=True)
            runtime = verdict['runtime']
            require(any(line.startswith('Microsoft.NETCore.App ' + runtime.removeprefix('.NET ') + ' [') for line in runtimes.splitlines()), 'Loaded runtime is not in actual installed runtime inventory')
            require(verdict['publicationGeneration'] not in generations, 'Reused native publication')
            generations.add(verdict['publicationGeneration'])
            check_files()
            record['status'] = 'PASS'
            print('case: ' + json.dumps(record, sort_keys=True), flush=True)
            current = None
    except BaseException as error:
        errors.append(error)
        if current is not None:
            current['status'] = 'FAILED_OR_INCOMPLETE'
    finally:
        try:
            check_files()
        except BaseException as error:
            errors.append(error)
        try:
            summary = {group: {status: sum(r['group'] == group and r['status'] == status for r in records)
                              for status in ('PASS', 'FAILED_OR_INCOMPLETE', 'INCOMPLETE', 'UNRUN')}
                       for group in catalog['expectedGroups']}
            (work / 'summary.json').write_text(json.dumps(dict(groups=summary, cases=records, errors=[type(e).__name__ + ': ' + str(e) for e in errors]), indent=2) + '\n')
            print('summary: ' + json.dumps(summary, sort_keys=True), flush=True)
            for record in records:
                if record['status'] != 'PASS':
                    print('not-passed: ' + json.dumps(record, sort_keys=True), flush=True)
            print('qualification: selected controls only; timer/cleanup retained-safety cases are not quota-zero; D2 and AB setup failure gap remain outside this positive lane.', flush=True)
        except BaseException as error:
            errors.append(error)
    if errors:
        for error in errors:
            summarize_failure(error)
        return 1
    require(all(r['status'] == 'PASS' for r in records), 'Incomplete finite lane')
    return 0


if __name__ == '__main__':
    sys.exit(main())
