#!/usr/bin/env python3
"""One focused eight-project graph; 35 actual UIDs, each in its own process.

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
import stat
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


results = None
owner = None


def require(ok, message):
    if not ok:
        raise ValueError(message)


def initialize():
    global results, owner
    # Includes helper verification and execution inside entry's safety boundary.
    results = module('d2_results', ROOT / 'eng/d2-ownership-results.py')
    owner = module('d2_process', ROOT / 'eng/package-smoke-process.py')


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def safe_command_record(label, state):
    fixed = {'head', 'tree', 'clean', 'tracked', 'clone', 'checkout', 'cloned-head',
             'classifier-checks', 'sdk-version', 'sdk-info', 'runtimes', 'restore', 'build'}
    selected = isinstance(label, str) and (label in fixed or
        re.fullmatch(r'list-[0-5]', label) or re.fullmatch(r'case-(?:[0-9]|[12][0-9]|3[0-4])', label))
    phase = state.get('phase')
    receipt = state.get('receipt')
    receipt = receipt if isinstance(receipt, dict) else {}
    def count(key, kind):
        value = receipt.get(key)
        return len(value) if type(value) is kind else None
    code = receipt.get('exitCode')
    timeout = receipt.get('timedOut')
    return dict(stage=label if selected else None,
        phase=phase if isinstance(phase, str) and phase in {'preflight', 'owner-call', 'receipt-check', 'directory-check', 'log-seal', 'done'} else None,
        provenance=state.get('provenance') if isinstance(state.get('provenance'), str) and state.get('provenance') in
            {'original-owner-failure', 'validated-success'} else None,
        independentCheckErrorCount=state.get('independentCheckErrorCount') if type(state.get('independentCheckErrorCount')) is int else None,
        receiptPresent=bool(receipt), exitCode=code if type(code) is int and -2147483648 <= code <= 2147483647 else None,
        timedOut=timeout if type(timeout) is bool else None,
        executeErrorCount=count('errors', list), cleanupErrorCount=count('cleanupErrors', list),
        reportingErrorCount=count('reportingErrors', list), remainingOwnedCount=count('remainingOwned', dict))


def compiler_diagnostic_summary(logs, source, originals):
    # Emit only code/severity and allowlisted public source coordinates, never
    # compiler messages, arbitrary paths or raw lines. This is not an oracle.
    allowed = {}
    for path in originals:
        if path.is_relative_to(source):
            name = path.relative_to(source).as_posix()
            if re.fullmatch(r'[A-Za-z0-9_./-]+', name) and name.endswith(('.cs', '.csproj', '.props', '.targets')):
                for spelling in (str(path), '/_/' + name, name):
                    allowed[spelling] = name
    code_pattern = re.compile(r'\b(error|warning) ((?:CS|NU|MSB|NETSDK|TUnit)[0-9]{4}):')
    location_pattern = re.compile(r'^(.*?)\(([0-9]{1,9})(?:,([0-9]{1,9}))?(?:,[0-9]{1,9},[0-9]{1,9})?\):\s*$')
    rows = {}; files = []; omitted = 0; long_lines = 0
    for suffix in ('.stdout', '.stderr'):
        path = logs / ('build' + suffix)
        try:
            fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        except FileNotFoundError:
            files.append(dict(stream=suffix[1:], present=False)); continue
        with os.fdopen(fd, 'rb') as stream:
            require(stat.S_ISREG(os.fstat(stream.fileno()).st_mode), 'Diagnostic input is not a regular file')
            raw = stream.read(1024 * 1024 + 1)
        truncated = len(raw) > 1024 * 1024
        raw = raw[:1024 * 1024]
        files.append(dict(stream=suffix[1:], present=True, sampledBytes=len(raw), truncated=truncated,
                          sampledSha256=hashlib.sha256(raw).hexdigest()))
        for line in raw.decode('utf-8', errors='replace').splitlines():
            if len(line) > 16384:
                long_lines += 1; continue
            match = code_pattern.search(line)
            if match is None: continue
            location = location_pattern.fullmatch(line[:match.start()])
            name = allowed.get(location[1]) if location else None
            number = int(location[2]) if name else None
            column = int(location[3]) if name and location[3] else None
            key = (match[1], match[2], name, number, column)
            if key in rows: rows[key] += 1
            elif len(rows) < 64: rows[key] = 1
            else: omitted += 1
    return dict(files=files, omittedDiagnosticOccurrences=omitted, ignoredLongLines=long_lines, diagnostics=[dict(severity=k[0], code=k[1], source=k[2], line=k[3], column=k[4], occurrences=v)
        for k, v in rows.items()], qualification='Bounded diagnostic sample only; original command and all guards still decide acceptance.')


def report_command_diagnostics(states, logs, source, originals):
    for label, state in states.items():
        if state.get('phase') != 'done':
            print('command-diagnostic: ' + json.dumps(safe_command_record(label, state), sort_keys=True), flush=True)
            if label == 'build':
                print('compiler-diagnostic: ' + json.dumps(compiler_diagnostic_summary(logs, source, originals), sort_keys=True), flush=True)


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


def write_restore_config(work):
    # The explicit config must not become an implicit ancestor input of work/src.
    directory = work / 'nuget-config'
    directory.mkdir()
    config = directory / 'NuGet.Config'
    config.write_text('<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3"/></packageSources></configuration>\n')
    return config


def asset_identity(source, catalog, packages, expected_package_identity):
    assets = sorted(p.relative_to(source).as_posix() for p in source.rglob('project.assets.json'))
    expected = sorted(str(Path(p).parent / 'obj/project.assets.json') for p in catalog['projects'])
    require(assets == expected, 'Actual restored project assets differ from focused8')
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
    require('Microsoft.Testing.Platform/' + catalog['mtpPackageVersion'] in unit['libraries'], 'MTP UID/schema version mismatch')
    return {name: sha(source / name) for name in assets}


def restore_inputs(source):
    found = {}
    for path in source.rglob('*'):
        if path.is_file() and (path.name == 'project.assets.json' or path.name.endswith('.nuget.g.props') or path.name.endswith('.nuget.g.targets')):
            require(not path.is_symlink(), 'Unexpected import symlink')
            found[path.relative_to(source).as_posix()] = sha(path)
    return found


def verify_package_sources(packages):
    metadata = sorted(packages.rglob('.nupkg.metadata'))
    require(metadata, 'Empty restored package inventory')
    for path in metadata:
        value = results.read_json(path)
        require(value.get('source') == 'https://api.nuget.org/v3/index.json', 'Nonofficial installed package source')
    directories = {p.parent for p in packages.rglob('*.nupkg')}
    require(directories == {p.parent for p in metadata}, 'Ancillary package metadata missing')


def safe_record(record):
    # Rejection can happen before catalog validation. Never print catalog-derived
    # method/design/index/UID content just because it reached an internal record.
    def field_hash(value):
        payload = json.dumps(value, sort_keys=True, ensure_ascii=True, allow_nan=False).encode()
        return hashlib.sha256(payload).hexdigest()
    design = record.get('designId'); index = record.get('caseIndex'); status = record.get('status')
    return {
        'designId': design if type(design) is str and re.fullmatch(r'D2-(?:0[1-9]|[12][0-9]|3[0-5])', design) else None,
        'designIdSha256': field_hash(design),
        'methodSha256': field_hash(record.get('method')),
        'caseIndex': 0 if type(index) is int and index == 0 else None,
        'caseIndexSha256': field_hash(index),
        'status': status if type(status) is str and status in ('PASS', 'FAILED_OR_INCOMPLETE', 'INCOMPLETE', 'UNRUN') else 'UNKNOWN',
        'uidSha256': field_hash(record.get('uid')) if record.get('uid') is not None else None,
    }


def summarize_failure(error):
    known = {ValueError: 'value', TimeoutError: 'timeout', OSError: 'os', RuntimeError: 'runtime',
             BaseExceptionGroup: 'group', ExceptionGroup: 'group'}
    detail = {'category': known.get(type(error), 'other'),
              'independentMembers': len(error.exceptions) if isinstance(error, BaseExceptionGroup) else 1}
    print('failure: ' + json.dumps(detail, sort_keys=True), flush=True)


def main():
    catalog = results.read_json(ROOT / 'eng/d2-ownership-controls.json')
    limits = catalog['limits']
    owner.enable_subreaper()
    work = Path(tempfile.mkdtemp(prefix='sld235-', dir='/tmp'))
    work_identity = results.directory_identity_strict(work)
    work_fd = os.open(work, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    main.private_directory_fd = work_fd
    require((os.fstat(work_fd).st_dev, os.fstat(work_fd).st_ino) == work_identity, 'Private directory open identity changed')
    logs = work / 'logs'; logs.mkdir()
    logs_identity = results.directory_identity_strict(logs)
    log_seal = results.seal_directory(logs)
    report_seals = {}
    package_seal = None
    restored_inputs = {}
    env = environment(work)
    package_identity = results.directory_identity(str(work / 'packages'))
    stop_at = time.monotonic() + limits['commandBudgetSeconds']
    sequence = 0
    source = work / 'src'
    records = [dict(designId=c['designId'], method=c['methodName'], caseIndex=c['caseIndex'],
                    status='UNRUN', uid=None) for c in catalog['cases']]
    ledger = []
    matches = []
    original = {}
    built = {}
    runtime_output = None
    runtime_files = set()
    errors = []
    current = None
    case_context = None
    command_states = {}

    def run(label, argv, cwd=ROOT, seconds=120):
        nonlocal sequence, log_seal
        state = command_states[label] = {'phase': 'preflight', 'receipt': None, 'provenance': None}
        require(sequence < limits['ownedCommandCount'], 'Unselected command count')
        check_files()
        sequence += 1
        pipe = work / ('p' + str(sequence)); pipe.mkdir()
        require(len(os.fsencode(pipe)) + 1 + 32 <= 103, 'Testing platform Unix socket path too long')
        identity = results.directory_identity_strict(pipe)
        child = dict(env, TESTINGPLATFORM_PIPE_DIRECTORY=str(pipe))
        failure = None
        output = None
        actual_argv = [str(x) for x in argv]
        # Recalculate only AFTER every potentially costly preflight guard.
        actual_seconds = results.launch_budget(seconds, stop_at - time.monotonic())
        state['phase'] = 'owner-call'
        try:
            output = owner.run_owned(label, actual_argv, cwd, child, logs, actual_seconds)
        except BaseException as error:
            failure = error
            if isinstance(error, owner.CommandFailure):
                state.update(receipt=error.outcome, provenance='original-owner-failure')
        checks = []
        if output is not None:
            try:
                state['phase'] = 'receipt-check'
                receipt = results.read_json(logs / (label + '.json'))
                results.verify_receipt(receipt, label, actual_argv, actual_seconds)
                state.update(receipt=receipt, provenance='validated-success')
            except BaseException as error:
                checks.append(error)
        if failure is None and not checks:
            state['phase'] = 'directory-check'
        for directory, expected, empty in ((work, work_identity, False),
                                            (logs, logs_identity, False), (pipe, identity, True)):
            try:
                results.verify_directory(directory, expected, empty=empty)
            except BaseException as error:
                checks.append(error)
        try:
            pending = tuple(label + suffix for suffix in ('.stdout', '.stderr', '.json') if (logs / (label + suffix)).exists())
            check_files(pending)
        except BaseException as error:
            checks.append(error)
        if failure is not None or checks:
            state['independentCheckErrorCount'] = len(checks)
            if failure is not None:
                checks.insert(0, failure)
            raise BaseExceptionGroup('Command and actual containment checks: ' + label, checks)
        state['phase'] = 'log-seal'
        log_seal = results.verify_seal(logs, log_seal,
            expected_new=(label + '.stdout', label + '.stderr', label + '.json'))
        require(time.monotonic() <= stop_at, 'Whole lane deadline exceeded after cleanup')
        state['phase'] = 'done'
        print('stage: ' + label + ' exit=0 owned=joined', flush=True)
        return output[0]

    def git(label, *args, cwd=ROOT):
        return run(label, ['git', '-c', 'core.hooksPath=/dev/null', *args], cwd, 60).read_text().strip()

    def check_evidence(pending_logs=()):
        failures = []
        for directory, sealed in [(logs, log_seal), *report_seals.items()]:
            try:
                results.verify_seal(directory, sealed, expected_new=pending_logs if directory == logs else ())
            except BaseException as error:
                failures.append(error)
        if failures:
            raise BaseExceptionGroup('Completed evidence changed', failures)

    def check_files(pending_logs=()):
        failures = []
        for directory, expected in ((work, work_identity), (logs, logs_identity)):
            try:
                results.verify_directory(directory, expected)
            except BaseException as error:
                failures.append(error)
        try:
            results.verify_inputs(original, built, runtime_output, runtime_files, time.monotonic(), stop_at)
        except BaseException as error:
            failures.append(error)
        try:
            check_evidence(pending_logs)
        except BaseException as error:
            failures.append(error)
        try:
            # Before tracked identity is established, all 31 selected inputs and
            # the complete ancestor/graph/span authority are already guarded.
            results.verify_catalog(ROOT, catalog)
            if source.is_dir() and (source / catalog['project']).is_file():
                results.verify_catalog(source, catalog)
            if package_seal is not None:
                results.verify_seal(work / 'packages', package_seal)
                require(restore_inputs(source) == restored_inputs, 'Restored imports changed')
        except BaseException as error:
            failures.append(error)
        if failures:
            raise BaseExceptionGroup('Independent final/input guard failures', failures)

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
        results.verify_catalog(source, catalog)
        run('classifier-checks', [sys.executable, '-B', source / 'eng/tests/test_d2_ownership_results.py'], source, 30)
        dotnet = Path(shutil.which('dotnet', path=env.get('PATH')) or '').resolve()
        require(dotnet.is_file(), 'Official setup-dotnet executable unavailable')
        built[dotnet] = sha(dotnet)
        sdk = run('sdk-version', [dotnet, '--version'], source).read_text().strip()
        require(re.fullmatch(r'10\.0\.1\d\d', sdk) is not None, 'global.json selected an unexpected SDK feature band')
        run('sdk-info', [dotnet, '--info'], source)
        runtimes = run('runtimes', [dotnet, '--list-runtimes'], source).read_text()
        print('identity: ' + json.dumps(dict(head=head, tree=tree, sdk=sdk, dotnetSha256=built[dotnet], projects=8, selected=35)), flush=True)
        config = write_restore_config(work)
        project = source / catalog['project']
        run('restore', [dotnet, 'restore', project, '--configfile', config, '--disable-parallel', '-p:Configuration=Release', '-p:RestoreIgnoreFailedSources=false', '-m:1', '-nr:false'], source, limits['restoreSeconds'])
        assets = asset_identity(source, catalog, work / 'packages', package_identity)
        built.update({source / name: value for name, value in assets.items()})
        restored_inputs = restore_inputs(source)
        require(len(restored_inputs) >= 24, 'Missing assets/generated imports')
        package_seal = results.seal_directory(work / 'packages')
        verify_package_sources(work / 'packages')
        built.update({source / name: value for name, value in restored_inputs.items()})
        check_files()
        print('source-path-policy: requested <selected-checkout>/=/_/; actual discovery/native paths must independently match /_/ plus exact catalog source', flush=True)
        run('build', [dotnet, 'build', project, '-c', 'Release', '--no-restore', '-v:minimal', '-p:UseSharedCompilation=false', '-p:PathMap=' + results.compile_path_map(source), '-m:1', '-nr:false'], source, limits['buildSeconds'])
        output = project.parent / 'bin/Release/net10.0'
        dll = output / 'SharpLink.UnitTests.dll'
        require(dll.is_file(), 'Unit binary missing')
        for project_name in catalog['projects']:
            parent = (source / project_name).parent
            name = Path(project_name).stem
            framework = 'netstandard2.0' if name == 'SharpLink.Generator' else 'net10.0'
            assembly_name = 'SharpLink.UnitTests' if name == 'SharpLink.D2OwnershipTests' else name
            actual = parent / 'bin/Release' / framework / (assembly_name + '.dll')
            require(actual.is_file() and not actual.is_symlink(), 'Selected graph binary missing')
            built[actual] = sha(actual)
            if name not in ('SharpLink.Generator', 'SharpLink.D2OwnershipTests'):
                require(sha(output / (name + '.dll')) == sha(actual), 'Graph runtime copy mismatch')
            for item in results.ordinary_inventory(parent / 'bin/Release' / framework):
                built[item] = sha(item)
        info = project.parent / 'obj/Release/net10.0/SharpLink.D2OwnershipTests.AssemblyInfo.cs'
        require(info.is_file() and '[assembly: System.Reflection.AssemblyMetadataAttribute("SharpLink.TestSubset", "D2Ownership-v1")]' in info.read_text(),
                'Actual generated subset metadata missing')
        built[info] = sha(info)
        runtime_output = output
        runtime_files = results.ordinary_inventory(output)
        built.update({p: sha(p) for p in runtime_files})
        print('binary: SharpLink.UnitTests.dll sha256=' + sha(dll), flush=True)
        check_files()
        for number, class_name in enumerate(sorted({c['className'] for c in catalog['cases']})):
            cases = [dict(c, sourceSHA256=catalog['sourcePins'][c['source']])
                     for c in catalog['cases'] if c['className'] == class_name]
            require({c['namespace'] for c in cases} == {'SharpLink.UnitTests.Client'}, 'Unexpected namespace scope')
            selector = '/*/SharpLink.UnitTests.Client/' + class_name + '/*'
            check_files()
            path = run('list-' + str(number), [dotnet, dll, '--list-tests', 'json', '--treenode-filter', selector],
                       source, limits['discoverySeconds'])
            matches.extend(results.discovery(results.read_json(path), cases, source, catalog['assemblyFullName']))
            check_files()
        ledger = results.freeze_ledger(matches, catalog['cases'])
        for record, entry in zip(records, ledger):
            record['uid'] = entry['discovery']['uid']
        identity = {'head': head, 'tree': tree, 'sourceManifest': results.digest({str(p.relative_to(source)): h
                    for p, h in original.items() if p.is_relative_to(source)}),
                    'binaryManifest': results.digest({str(p): h for p, h in built.items()})}
        frozen = {'identity': identity, 'actualCases': ledger}
        ledger_hash = results.digest(frozen)
        ledger_path = work / 'actual-ledger.json'
        results.write_private_json(work_fd, 'actual-ledger.json', frozen)
        built[ledger_path] = sha(ledger_path)
        print('actual-ledger: ' + json.dumps({'cases': len(ledger), 'sha256': ledger_hash}), flush=True)
        check_files()
        runtime = None
        generations = set()
        for index, record in enumerate(records):
            current = record
            entry = ledger[index]; row = entry['discovery']
            case_context = {'index': index, 'entry': entry, 'phase': 'input-guard', 'report': None}
            require(row['uid'] == record['uid'] and results.digest(frozen) == ledger_hash, 'Frozen UID ledger changed')
            check_files()
            case_context['phase'] = 'report-directory'
            report = work / ('r' + str(index)); report.mkdir()
            report_identity = results.directory_identity_strict(report)
            results.verify_directory(report, report_identity, empty=True)
            argv = results.case_argv(dotnet, dll, row['uid'], report, limits['engineTimeout'])
            case_context.update(report=report, reportIdentity=report_identity, argv=argv, phase='owned-command')
            record['status'] = 'INCOMPLETE'
            run('case-' + str(index), argv, source, limits['caseSeconds'])
            case_context['phase'] = 'report-open'
            native_path = results.regular_report(report, report_identity, 'session.tunit-report.json')
            junit_path = results.regular_report(report, report_identity, 'junit.xml')
            case_context['phase'] = 'native-junit-oracle'
            verdict = results.passed(results.read_json(native_path), junit_path.read_text(), row,
                results.reported_source_path(entry['case']['source']), catalog['tunitRuntimeVersion'], runtime, generations)
            print('native-filter-diagnostic: ' + json.dumps(verdict['filterDiagnostic'], sort_keys=True), flush=True)
            if runtime is None:
                print('runtime: ' + verdict['runtime'], flush=True)
            runtime = verdict['runtime']
            require(any(line.startswith('Microsoft.NETCore.App ' + runtime.removeprefix('.NET ') + ' [') for line in runtimes.splitlines()), 'Loaded runtime is not in actual installed runtime inventory')
            require(verdict['publicationGeneration'] not in generations, 'Reused native publication')
            generations.add(verdict['publicationGeneration'])
            case_context['phase'] = 'post-input-guard'
            check_files()
            case_context['phase'] = 'report-seal'
            results.regular_report(report, report_identity, 'session.html')
            report_seals[report] = results.seal_directory(report)
            record['status'] = 'PASS'
            print('case: ' + json.dumps(safe_record(record), sort_keys=True), flush=True)
            current = None
    except BaseException as error:
        errors.append(error)
        if current is not None:
            current['status'] = 'FAILED_OR_INCOMPLETE'
            # Original command/report exceptions are preserved privately below.
            # No arbitrary native exception, stdout, stderr or path is emitted.
    finally:
        try:
            check_files()
        except BaseException as error:
            errors.append(error)
        try:
            results.write_private_json(work_fd, 'completed-evidence.json',
                {'completedCommandLogs': log_seal,
                 'completedReports': {p.name: value for p, value in report_seals.items()}})
        except BaseException as error:
            errors.append(error)
        try:
            summary = {status: sum(r['status'] == status for r in records)
                       for status in ('PASS', 'FAILED_OR_INCOMPLETE', 'INCOMPLETE', 'UNRUN')}
            results.write_private_json(work_fd, 'summary.json',
                dict(groups=summary, cases=records, errors=[type(e).__name__ for e in errors]))
            print('summary: ' + json.dumps(summary, sort_keys=True), flush=True)
            for record in records:
                if record['status'] != 'PASS':
                    print('not-passed: ' + json.dumps(safe_record(record), sort_keys=True), flush=True)
            print('qualification: D2 focused35 only; 41 source fixtures are not 41 UIDs. Quarantine is not refund. Shared EntryPlugin regression47/59 and integration remain unaccepted.', flush=True)
        except BaseException as error:
            errors.append(error)
    if not errors and not all(r['status'] == 'PASS' for r in records):
        errors.append(ValueError('Incomplete finite lane'))
    if errors:
        try:
            report_command_diagnostics(command_states, logs, source, original)
        except BaseException as diagnostic_error:
            errors.append(diagnostic_error)
        for error in tuple(errors):
            try:
                summarize_failure(error)
            except BaseException as diagnostic_error:
                errors.append(diagnostic_error)
        main.private_failure_receipt = results.persist_failures(work_fd, errors)
        main.last_failure = BaseExceptionGroup('D2 focused35 lane failed', errors)
        return 1
    return 0



def exception_graph(errors):
    """Private graph; duplicate root/group occurrences remain distinct ordered edges.

    Node IDs preserve shared identity/cycles, NOT occurrence deduplication. A failed
    message formatter becomes an independent root and is not recursively formatted.
    Tracebacks preserve every frame/file/function/line/instruction offset without
    invoking repr on locals, arbitrary object state or traceback formatter code.
    """
    nodes = []; refs = {}; queue = []; diagnostics = set()
    def ref(error):
        key = id(error)
        if key not in refs:
            refs[key] = len(nodes); nodes.append(None); queue.append(error)
        return refs[key]
    def diagnostic(error):
        diagnostics.add(id(error)); errors.append(error); return ref(error)
    roots = [ref(error) for error in errors]
    index = 0
    while index < len(queue):
        error = queue[index]; index += 1
        node = {'type': type(error).__module__ + '.' + type(error).__qualname__}
        if id(error) in diagnostics:
            node['messageUnavailable'] = 'independent formatter failure; not formatted recursively'
        else:
            try:
                node['message'] = str(error)
            except BaseException as failure:
                node['messageFailure'] = diagnostic(failure)
        tb = error.__traceback__; frames = []
        while tb is not None:
            frames.append({'file': tb.tb_frame.f_code.co_filename, 'function': tb.tb_frame.f_code.co_name,
                           'line': tb.tb_lineno, 'instruction': tb.tb_lasti})
            tb = tb.tb_next
        node['traceback'] = frames
        node['cause'] = ref(error.__cause__) if error.__cause__ is not None else None
        node['context'] = ref(error.__context__) if error.__context__ is not None else None
        node['suppressContext'] = error.__suppress_context__
        if isinstance(error, BaseExceptionGroup):
            node['members'] = [ref(member) for member in error.exceptions]
        # Public owner contract: preserve its terminal outcome plus every original
        # independent cause, including those not installed as Python __cause__.
        if hasattr(error, 'outcome') and hasattr(error, 'causes'):
            node['ownerOutcome'] = error.outcome
            node['ownerCauses'] = [ref(member) for member in error.causes]
        nodes[refs[id(error)]] = node
    roots = [ref(error) for error in errors]
    return {'schemaVersion': 1, 'orderedRootOccurrences': roots, 'nodes': nodes,
            'qualification': 'Private original exception graph; repeated edges are independent occurrences. No PASS implied.'}



def preserve_startup(errors):
    # No imported helper is needed for this failure path. Keep originals in memory
    # even if private directory creation or durable serialization itself fails.
    try:
        private = Path(tempfile.mkdtemp(prefix='sld235-startup-'))
        fd = os.open(private, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        main.private_directory_fd = fd
        for name in ('failure-graph.json', 'failure-graph-fallback.json'):
            try:
                payload = (json.dumps(exception_graph(errors), ensure_ascii=True, allow_nan=False, indent=2) + '\n').encode()
                output = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=fd)
                with os.fdopen(output, 'wb') as stream:
                    stream.write(payload); stream.flush(); os.fsync(stream.fileno())
                main.private_failure_receipt = name
                return
            except BaseException as error:
                errors.append(error)
    except BaseException as error:
        errors.append(error)


def entry():
    status = 1
    try:
        initialize()
        status = main()
    except BaseException as error:
        errors = [error]
        try:
            summarize_failure(error)
        except BaseException as diagnostic_error:
            errors.append(diagnostic_error)
        fd = getattr(main, 'private_directory_fd', None)
        if fd is not None and results is not None:
            main.private_failure_receipt = results.persist_failures(fd, errors)
        else:
            preserve_startup(errors)
        main.last_failure = BaseExceptionGroup('Startup failure', errors)
    # This O_CLOEXEC directory capability is intentionally process-lifetime:
    # keep it available for every last diagnostic, including path replacement.
    # Kernel process teardown closes it; no child inherits it. No path-based
    # fallback or successful-close claim substitutes for durable receipt outcome.
    return status


if __name__ == '__main__':
    sys.exit(entry())
