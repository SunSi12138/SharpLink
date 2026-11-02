#!/usr/bin/env python3
"""One three-project Generator graph; eight selected actual UIDs, each in its own process.

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
import traceback
import xml.etree.ElementTree as ET

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


owner = module('generator_process', ROOT / 'eng/package-smoke-process.py')
results = module('generator_results', ROOT / 'eng/generator-bootstrap-results.py')
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
    require(sorted(found) == catalog['projects'] and len(found) == 3, 'Generator project closure changed')
    require(len(catalog['cases']) == catalog['caseCount'] == 8, 'Expected eight selected cases')
    require(len({item['method'] for item in catalog['cases']}) == catalog['methods'] == 5, 'Expected five selected methods')
    require(len({item['key'] for item in catalog['cases']}) == 8, 'Duplicate selected case key')
    require(len({item['expectedDisplayName'] for item in catalog['cases']}) == 8, 'Duplicate selected argument display')
    for item in catalog['cases']:
        span = catalog['sourceSpans'][item['spanKey']]
        require(span['source'] == item['source'], 'Catalog source/span mismatch')
        results.verify_span(source / item['source'], span)


def asset_identity(source, catalog, packages, expected_package_identity):
    assets = sorted(p.relative_to(source).as_posix() for p in source.rglob('project.assets.json'))
    expected = sorted(str(Path(p).parent / 'obj/project.assets.json') for p in catalog['projects'])
    require(assets == expected, 'Actual restored project assets differ from Generator3')
    for name in assets:
        value = results.read_json(source / name)
        restore = value['project']['restore']
        expected_project = source / next(p for p in catalog['projects'] if str(Path(p).parent / 'obj/project.assets.json') == name)
        require(Path(restore['projectPath']).resolve() == expected_project.resolve(), 'Actual asset project identity mismatch')
        for library in value['libraries'].values():
            if library.get('type') == 'project':
                linked = (expected_project.parent / library['msbuildProject'].replace('\\', '/')).resolve()
                require(linked in {source / p for p in catalog['projects']}, 'Actual restored dependency escaped Generator3')
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
    # Never publish arbitrary exception names/messages, including parser source text.
    if isinstance(error, BaseExceptionGroup):
        print('failure-group: members=' + str(len(error.exceptions)), flush=True)
        for member in error.exceptions:
            summarize_failure(member)
    else:
        allowed = (ValueError, RuntimeError, OSError, FileNotFoundError, PermissionError,
                   TimeoutError, InterruptedError, KeyboardInterrupt, SystemExit)
        kind = next((t.__name__ for t in allowed if type(error) is t), 'OtherError')
        if type(error) is owner.CommandFailure:
            kind = 'CommandFailure'
        if type(error) is owner.CommandFailure:
            row = error.outcome
            phases = {'head', 'tree', 'clean', 'tracked', 'clone', 'checkout', 'cloned-head',
                      'classifier-checks', 'sdk-version', 'sdk-info', 'runtimes', 'restore', 'build'}
            phases.update('list-' + str(i) for i in range(5))
            phases.update('case-' + str(i) for i in range(8))
            phase = row.get('stage')
            print('command-failure: ' + json.dumps(dict(
                phase=phase if type(phase) is str and phase in phases else 'unknown',
                exitCode=row.get('exitCode') if type(row.get('exitCode')) is int else None,
                timedOut=row.get('timedOut') if type(row.get('timedOut')) is bool else None,
                executeErrors=len(row['errors']), cleanupErrors=len(row['cleanupErrors']),
                reportingErrors=len(row['reportingErrors']), remainingOwned=len(row.get('remainingOwned', {}))),
                sort_keys=True), flush=True)
        message = str(error).encode('utf-8', errors='replace')
        print('failure: ' + json.dumps(dict(type=kind, messageBytes=len(message),
              messageSha256=hashlib.sha256(message).hexdigest()), sort_keys=True), flush=True)


def summarize_logs(logs, label):
    # Only byte identities leave the private runner, never raw output excerpts.
    for suffix in ('stdout', 'stderr'):
        path = logs / (label + '.' + suffix)
        if path.exists():
            raw = path.read_bytes()
            print('diagnostic-identity: ' + json.dumps(dict(stream=suffix, bytes=len(raw),
                  sha256=hashlib.sha256(raw).hexdigest()), sort_keys=True), flush=True)


def private_errors(errors):
    # Preserve edges and repeated occurrences (including CommandFailure.causes).
    # IDs avoid cycles; this graph is written locally, never printed/uploaded.
    nodes = []
    seen = {}
    def visit(error):
        key = id(error)
        if key in seen:
            return seen[key]
        index = len(nodes)
        seen[key] = index
        node = dict(type=type(error).__name__, message=str(error),
                    traceback=''.join(traceback.format_exception(error)))
        nodes.append(node)
        node['cause'] = visit(error.__cause__) if error.__cause__ is not None else None
        node['context'] = visit(error.__context__) if error.__context__ is not None else None
        node['suppressContext'] = error.__suppress_context__
        node['members'] = [visit(e) for e in error.exceptions] if isinstance(error, BaseExceptionGroup) else []
        node['commandCauses'] = [visit(e) for e in error.causes] if isinstance(error, owner.CommandFailure) else []
        return index
    roots = [visit(error) for error in errors]
    return dict(roots=roots, nodes=nodes)


def guarded_main():
    # Do not let a reporter failure turn into Python's raw public traceback.
    try:
        return main()
    except BaseException as error:
        try:
            path = Path(tempfile.mkdtemp(prefix='slgen-failure-', dir='/tmp')) / 'failure.json'
            path.write_text(json.dumps(private_errors([error]), indent=2) + '\n')
        except BaseException as preservation_error:
            error = BaseExceptionGroup('Lane and private preservation failures', [error, preservation_error])
        try:
            summarize_failure(error)
        except BaseException:
            try:
                os.write(2, b'GENERATOR_BOOTSTRAP_FAIL safe-reporter-unavailable\n')
            except BaseException:
                pass  # No functioning output channel; nonzero remains mandatory.
        return 1


def command_seconds(stop_at, requested, now=None):
    remaining = stop_at - (time.monotonic() if now is None else now)
    require(remaining > 0 and requested > 0, 'Remaining command budget exhausted')
    return min(requested, remaining)


def main():
    catalog = results.read_json(ROOT / 'eng/generator-bootstrap-controls.json')
    limits = catalog['limits']
    owner.enable_subreaper()
    work = Path(tempfile.mkdtemp(prefix='slgen-', dir='/tmp'))
    logs = work / 'logs'; logs.mkdir()
    env = environment(work)
    package_identity = results.directory_identity(str(work / 'packages'))
    stop_at = time.monotonic() + limits['commandBudgetSeconds']
    sequence = 0
    source = work / 'src'
    records = [dict(key=item['key'], method=item['method'], arguments=item['arguments'], status='UNRUN', uid=None)
               for item in catalog['cases']]
    ledger = {}
    original = {}
    built = {}
    cache_files = None
    errors = []
    current = None

    def run(label, argv, cwd=ROOT, seconds=120):
        nonlocal sequence
        seconds = command_seconds(stop_at, seconds)
        sequence += 1
        pipe = work / ('p' + str(sequence)); pipe.mkdir()
        require(len(os.fsencode(pipe)) + 1 + 32 <= 103, 'Testing platform Unix socket path too long')
        identity = (pipe.stat().st_dev, pipe.stat().st_ino)
        child = dict(env, TESTINGPLATFORM_PIPE_DIRECTORY=str(pipe))
        failure = None
        output = None
        try:
            output = owner.run_owned(label, [str(x) for x in argv], cwd, child, logs, seconds)
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
                summarize_logs(logs, label)
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
        if cache_files is not None:
            require({p.relative_to(work / 'packages').as_posix() for p in (work / 'packages').rglob('*') if p.is_file()} == cache_files,
                    'Installed package cache inventory changed')

    try:
        head = git('head', 'rev-parse', 'HEAD')
        tree = git('tree', 'rev-parse', 'HEAD^{tree}')
        require(re.fullmatch('[0-9a-f]{40}', head) and re.fullmatch('[0-9a-f]{40}', tree), 'Invalid Git identity')
        require(os.environ.get('GITHUB_SHA', head) == head, 'Checkout differs from triggering source')
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
        run('classifier-checks', [sys.executable, '-B', source / 'eng/tests/test_generator_bootstrap_results.py'], source, 30)
        dotnet = Path(shutil.which('dotnet', path=env.get('PATH')) or '').resolve()
        require(dotnet.is_file(), 'Official setup-dotnet executable unavailable')
        built[dotnet] = sha(dotnet)
        sdk = run('sdk-version', [dotnet, '--version'], source).read_text().strip()
        require(re.fullmatch(r'10\.0\.1\d\d', sdk) is not None, 'global.json selected an unexpected SDK feature band')
        run('sdk-info', [dotnet, '--info'], source)
        runtimes = run('runtimes', [dotnet, '--list-runtimes'], source).read_text()
        print('identity: ' + json.dumps(dict(head=head, tree=tree, sdk=sdk, dotnet=str(dotnet), dotnetSha256=built[dotnet], projects=3, selected=8)), flush=True)
        config = work / 'NuGet.Config'
        config.write_text('<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3"/></packageSources></configuration>\n')
        project = source / catalog['project']
        run('restore', [dotnet, 'restore', project, '--configfile', config, '--disable-parallel', '-p:Configuration=Release', '-p:RestoreIgnoreFailedSources=false', '-m:1', '-nr:false'], source, limits['restoreSeconds'])
        assets = asset_identity(source, catalog, work / 'packages', package_identity)
        built.update({source / name: value for name, value in assets.items()})
        # Pin all installed bytes, including ancillary/download-only packages, not just resolved libraries.
        cache_files = {p.relative_to(work / 'packages').as_posix() for p in (work / 'packages').rglob('*') if p.is_file()}
        require(cache_files, 'Empty actual package cache')
        built.update({work / 'packages' / name: sha(work / 'packages' / name) for name in cache_files})
        for name in catalog['projects']:
            obj = (source / name).parent / 'obj'
            for generated in obj.glob('*.nuget.g.*'):
                built[generated] = sha(generated)
        (work / 'restored-inputs.json').write_text(json.dumps({str(p.relative_to(work)): value for p, value in built.items() if p.is_relative_to(work)}, indent=2) + '\n')
        print('restore-identity: projects=3 cacheFiles=' + str(len(cache_files)) + ' official-only=true TUnit=' + catalog['tunitPackageVersion'], flush=True)
        check_files()
        print('source-path-policy: requested <selected-checkout>/=/_/; actual discovery/native paths must independently match /_/ plus exact catalog source', flush=True)
        run('build', [dotnet, 'build', project, '-c', 'Release', '--no-restore', '-v:minimal', '-p:UseSharedCompilation=false', '-p:PathMap=' + results.compile_path_map(source), '-m:1', '-nr:false'], source, limits['buildSeconds'])
        check_files()  # Build may not silently replace the approved restore/import/cache inputs.
        output = project.parent / 'bin/Release/net10.0'
        dll = output / 'SharpLink.Generator.Tests.dll'
        require(dll.is_file(), 'GeneratorTests binary missing')
        producer = source / 'src/SharpLink.Generator/bin/Release/netstandard2.0/SharpLink.Generator.dll'
        require(sha(output / 'SharpLink.Generator.dll') == sha(producer), 'Generator runtime copy mismatch')
        for name in catalog['projects']:
            directory = (source / name).parent
            for area in ('bin', 'obj'):
                built.update({p: sha(p) for p in (directory / area).rglob('*') if p.is_file()})
        for name in ('referenced-manifest-bootstrap.input.txt', 'referenced-manifest-bootstrap.expected.txt'):
            require(sha(output / 'Fixtures/GeneratedSources' / name) ==
                    sha(project.parent / 'Fixtures/GeneratedSources' / name), 'Generator fixture output mismatch')
        for name in ('SharpLink.Generator.Tests.deps.json', 'SharpLink.Generator.Tests.runtimeconfig.json'):
            require((output / name).is_file(), 'Missing executable metadata: ' + name)
        print('binary: SharpLink.Generator.Tests.dll sha256=' + sha(dll), flush=True)
        methods = list(dict.fromkeys(item['method'] for item in catalog['cases']))
        for number, method in enumerate(methods):
            selected = [item for item in catalog['cases'] if item['method'] == method]
            filter_value = '/*/' + catalog['namespace'] + '/' + catalog['class'] + '/' + method
            path = run('list-' + str(number), [dotnet, dll, '--list-tests', 'json', '--treenode-filter', filter_value], source, limits['discoverySeconds'])
            rows = results.discovery(results.read_json(path), catalog, selected, source)
            require(not set(rows).intersection(ledger), 'UID appears in multiple selections')
            ledger.update(rows)
        require(len(ledger) == catalog['caseCount'], 'Actual discovered UID count mismatch')
        for record in records:
            matches = [row for row in ledger.values() if row['selectedCase']['key'] == record['key']]
            require(len(matches) == 1, 'Actual source-case UID binding is not singleton')
            record['uid'] = matches[0]['uid']
        (work / 'actual-ledger.json').write_text(json.dumps(ledger, indent=2) + '\n')
        check_files()
        runtime = None
        generations = set()
        for index, record in enumerate(records):
            current = record
            row = ledger[record['uid']]
            report = work / ('r' + str(index)); report.mkdir()
            argv = [str(dotnet), str(dll), '--filter-uid', record['uid'], '--maximum-parallel-tests', '1',
                    '--timeout', limits['engineTimeout'], '--results-directory', str(report), '--report-html',
                    '--report-html-filename', str(report / 'session.html'), '--junit-output-path', str(report / 'junit.xml')]
            results.verify_uid_command(argv, dll, record['uid'])
            record['status'] = 'INCOMPLETE'
            run('case-' + str(index), argv, source, limits['caseSeconds'])
            verdict = results.passed(results.read_json(report / 'session.tunit-report.json'), (report / 'junit.xml').read_text(), row,
                results.reported_source_path(row['selectedCase']['source']), catalog['tunitRuntimeVersion'], runtime)
            record['nativeFilterDiagnostic'] = verdict['filterDiagnostic']
            if runtime is None:
                print('runtime: ' + verdict['runtime'], flush=True)
            runtime = verdict['runtime']
            require(any(line.startswith('Microsoft.NETCore.App ' + runtime.removeprefix('.NET ') + ' [') for line in runtimes.splitlines()), 'Loaded runtime is not in actual installed runtime inventory')
            results.unique_generation(verdict, generations)
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
            summary = {status: sum(r['status'] == status for r in records)
                       for status in ('PASS', 'FAILED_OR_INCOMPLETE', 'INCOMPLETE', 'UNRUN')}
            (work / 'summary.json').write_text(json.dumps(dict(cases=records, counts=summary, errors=private_errors(errors)), indent=2) + '\n')
            print('summary: ' + json.dumps(summary, sort_keys=True), flush=True)
            for record in records:
                if record['status'] != 'PASS':
                    print('not-passed: ' + json.dumps(record, sort_keys=True), flush=True)
            print('qualification: exactly eight Generator bootstrap controls; not full Generator, Unit47, route59, or performance acceptance.', flush=True)
        except BaseException as error:
            errors.append(error)
    if errors:
        reporting_errors = []
        for error in tuple(errors):
            try:
                summarize_failure(error)
            except BaseException as reporting_error:
                reporting_errors.append(reporting_error)
        if reporting_errors:
            retained = BaseExceptionGroup('Original lane and reporting failures', [*errors, *reporting_errors])
            try:
                os.write(2, ('GENERATOR_BOOTSTRAP_FAIL originalErrors=' + str(len(errors)) +
                             ' reportingErrors=' + str(len(reporting_errors)) + '\n').encode())
            except BaseException as fallback_error:
                raise BaseExceptionGroup('Original and final reporter failures', [retained, fallback_error])
            raise retained  # guarded_main privately records the complete reporting graph.
        return 1
    require(all(r['status'] == 'PASS' for r in records), 'Incomplete finite lane')
    return 0


if __name__ == '__main__':
    sys.exit(guarded_main())
