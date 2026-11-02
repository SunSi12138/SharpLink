#!/usr/bin/env python3
"""One ordinary Unit graph; 59 parameter-expanded actual UIDs, each in its own process.

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


owner = module('route59_process', ROOT / 'eng/package-smoke-process.py')
results = module('route59_results', ROOT / 'eng/route59-results.py')
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
    require('Microsoft.Testing.Platform/' + catalog['mtpPackageVersion'] in unit['libraries'], 'MTP UID/schema version mismatch')
    return {name: sha(source / name) for name in assets}


def safe_record(record):
    return {k: record[k] for k in ('designId', 'method', 'caseIndex', 'status')} | {
        'uidSha256': hashlib.sha256(record['uid'].encode()).hexdigest() if record['uid'] is not None else None}


def summarize_failure(error):
    # Never stringify raw exceptions, receipts, paths or report data on the console.
    detail = {'type': type(error).__name__,
              'independentMembers': len(error.exceptions) if isinstance(error, BaseExceptionGroup) else 1}
    if isinstance(error, results.DiscoveryRejected):
        detail.update(reasonCode=error.reason_code, selection=error.safe_context, facts=error.safe_details)
    print('failure: ' + json.dumps(detail, sort_keys=True), flush=True)


def main():
    catalog = results.read_json(ROOT / 'eng/route59-controls.json')
    limits = catalog['limits']
    owner.enable_subreaper()
    work = Path(tempfile.mkdtemp(prefix='slr59-', dir='/tmp'))
    work_identity = results.directory_identity_strict(work)
    work_fd = os.open(work, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    main.private_directory_fd = work_fd
    require((os.fstat(work_fd).st_dev, os.fstat(work_fd).st_ino) == work_identity, 'Private directory open identity changed')
    logs = work / 'logs'; logs.mkdir()
    logs_identity = results.directory_identity_strict(logs)
    log_seal = results.seal_directory(logs)
    report_seals = {}
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
        remaining = stop_at - time.monotonic()
        require(remaining > 0, 'Remaining command budget exhausted before ' + label)
        results.verify_directory(work, work_identity)
        results.verify_directory(logs, logs_identity)
        check_evidence()
        sequence += 1
        pipe = work / ('p' + str(sequence)); pipe.mkdir()
        require(len(os.fsencode(pipe)) + 1 + 32 <= 103, 'Testing platform Unix socket path too long')
        identity = results.directory_identity_strict(pipe)
        child = dict(env, TESTINGPLATFORM_PIPE_DIRECTORY=str(pipe))
        failure = None
        output = None
        actual_seconds = min(seconds, remaining)
        actual_argv = [str(x) for x in argv]
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
        if failure is not None or checks:
            if failure is not None:
                checks.insert(0, failure)
            raise BaseExceptionGroup('Command and actual containment checks: ' + label, checks)
        state['phase'] = 'log-seal'
        log_seal = results.verify_seal(logs, log_seal,
            expected_new=(label + '.stdout', label + '.stderr', label + '.json'))
        state['phase'] = 'done'
        print('stage: ' + label + ' exit=0 owned=joined', flush=True)
        return output[0]

    def git(label, *args, cwd=ROOT):
        return run(label, ['git', '-c', 'core.hooksPath=/dev/null', *args], cwd, 60).read_text().strip()

    def check_evidence():
        failures = []
        for directory, sealed in [(logs, log_seal), *report_seals.items()]:
            try:
                results.verify_seal(directory, sealed)
            except BaseException as error:
                failures.append(error)
        if failures:
            raise BaseExceptionGroup('Completed evidence changed', failures)

    def check_files():
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
            check_evidence()
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
        run('classifier-checks', [sys.executable, '-B', source / 'eng/tests/test_route59_results.py'], source, 30)
        dotnet = Path(shutil.which('dotnet', path=env.get('PATH')) or '').resolve()
        require(dotnet.is_file(), 'Official setup-dotnet executable unavailable')
        built[dotnet] = sha(dotnet)
        sdk = run('sdk-version', [dotnet, '--version'], source).read_text().strip()
        require(re.fullmatch(r'10\.0\.1\d\d', sdk) is not None, 'global.json selected an unexpected SDK feature band')
        run('sdk-info', [dotnet, '--info'], source)
        runtimes = run('runtimes', [dotnet, '--list-runtimes'], source).read_text()
        print('identity: ' + json.dumps(dict(head=head, tree=tree, sdk=sdk, dotnetSha256=built[dotnet], projects=14, selected=59)), flush=True)
        config = work / 'NuGet.Config'
        config.write_text('<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3"/></packageSources></configuration>\n')
        project = source / catalog['project']
        run('restore', [dotnet, 'restore', project, '--configfile', config, '--disable-parallel', '-p:Configuration=Release', '-p:RestoreIgnoreFailedSources=false', '-m:1', '-nr:false'], source, limits['restoreSeconds'])
        assets = asset_identity(source, catalog, work / 'packages', package_identity)
        built.update({source / name: value for name, value in assets.items()})
        check_files()
        print('source-path-policy: requested <selected-checkout>/=/_/; actual discovery/native paths must independently match /_/ plus exact catalog source', flush=True)
        run('build', [dotnet, 'build', project, '-c', 'Release', '--no-restore', '-v:minimal', '-p:UseSharedCompilation=false', '-p:PathMap=' + results.compile_path_map(source), '-m:1', '-nr:false'], source, limits['buildSeconds'])
        output = project.parent / 'bin/Release/net10.0'
        dll = output / 'SharpLink.UnitTests.dll'
        require(dll.is_file(), 'Unit binary missing')
        for plugin in ('SharpLink.RollbackPlugin', 'SharpLink.RetirementEntryPlugin'):
            expected = source / 'test' / plugin / 'bin/Release/net10.0' / (plugin + '.dll')
            require(sha(output / (plugin + '.dll')) == sha(expected), 'Plugin runtime copy mismatch')
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
            if case_context is not None:
                try:
                    diagnostic, terminal = results.owner_case_diagnostic(case_context,
                        command_states.get('case-' + str(case_context['index'])))
                    print('case-owner-diagnostic: ' + json.dumps(diagnostic, sort_keys=True), flush=True)
                    report_diagnostic, diagnostic_errors = results.failure_report_diagnostic(
                        case_context, terminal, catalog, source)
                    errors.extend(diagnostic_errors)
                    print('case-report-diagnostic: ' + json.dumps(report_diagnostic, sort_keys=True), flush=True)
                except BaseException as diagnostic_error:
                    errors.append(diagnostic_error)
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
            print('qualification: route59 only, not original47 or full Unit bodies. Retained fixture owners are not quota-zero; process termination is not proof original Tasks joined.', flush=True)
        except BaseException as error:
            errors.append(error)
    if not errors and not all(r['status'] == 'PASS' for r in records):
        errors.append(ValueError('Incomplete finite lane'))
    if errors:
        for error in tuple(errors):
            try:
                summarize_failure(error)
            except BaseException as diagnostic_error:
                errors.append(diagnostic_error)
        main.private_failure_receipt = results.persist_failures(work_fd, errors)
        main.last_failure = BaseExceptionGroup('Route59 lane failed', errors)
        return 1
    return 0


def entry():
    status = 1
    try:
        status = main()
    except BaseException as error:
        errors = [error]
        try:
            summarize_failure(error)
        except BaseException as diagnostic_error:
            errors.append(diagnostic_error)
        fd = getattr(main, 'private_directory_fd', None)
        if fd is not None:
            main.private_failure_receipt = results.persist_failures(fd, errors)
        main.last_failure = BaseExceptionGroup('Startup failure', errors)
    # This O_CLOEXEC directory capability is intentionally process-lifetime:
    # keep it available for every last diagnostic, including path replacement.
    # Kernel process teardown closes it; no child inherits it. No path-based
    # fallback or successful-close claim substitutes for durable receipt outcome.
    return status


if __name__ == '__main__':
    sys.exit(entry())
