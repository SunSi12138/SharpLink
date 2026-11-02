#!/usr/bin/env python3
"""One ordinary Unit graph; fourteen selected actual UIDs, each in its own process.

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
import stat
import traceback
import time
import xml.etree.ElementTree as ET

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


def require(value, message):
    # Bootstrap-only fallback, replaced by the unchanged shared classifier on
    # successful initialization. Allows private boot evidence without that file.
    if not value: raise ValueError(message)


def initialize_helpers():
    global owner, results, require
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


def validate_catalog(catalog):
    expected = [
        ('ABScope', 'Runtime', 'RetirementTestIsolationScopeTests', 'class', [], [
            'ActualRunSnapshotFailureDoesNotLeakGrantedPermit',
            'SecondSnapshotFailureDoesNotMutateEitherEnvironmentValue',
            'MutatingSecondSetterFailureRestoresBothSnapshots',
            'BodyCleanupAndBothRestoreFailuresKeepFourOccurrencesAndRoot',
            'PendingOriginalOwnerPreventsRestoreAndPermitRelease',
            'PermitReleaseFailurePreservesBodyAndItsScopeRoot']),
        ('B2a', 'Client', 'SharpLinkMultiClusterDynamicAssemblyTests', 'methods', ['System.Threading.CancellationToken'], [
            'RuntimeReplaceShouldMigrateDynamicAssemblyBeforeSwitchingRoute',
            'DynamicRegistrationShouldRejectASlotChangedWhileItsManifestLoads']),
        ('B2c', 'Server', 'ServiceRegistrationTests', 'methods', ['System.Threading.CancellationToken'], [
            'RegisteredServiceCleanupShouldPreserveDynamicAndFrameworkOwnedStaticFailures']),
        ('B3a', 'Runtime', 'DynamicRollbackTests', 'methods', ['System.Threading.CancellationToken'], [
            'HugeDynamicDrainTimeoutShouldRemainPendingUntilLeaseRelease',
            'ClientUnregisterRetainedLeaseShouldUseOnlyItsRuntimeContextProvider',
            'ServerUnregisterRetainedLeaseShouldUseOnlyItsRuntimeContextProvider']),
        ('B3b', 'Runtime', 'DynamicRollbackTests', 'methods', ['System.Threading.CancellationToken'], [
            'ClientRegistrationRollbackShouldPreserveConflictAndAdapterCleanupFailure',
            'ServerRegistrationRollbackShouldPreserveConflictAndAdapterCleanupFailure'])]
    require(catalog['schemaVersion'] == 1 and catalog['expectedCaseCount'] == 14 and
            catalog['expectedGroups'] == {'ABScope': 6, 'B2a': 2, 'B2c': 1, 'B3a': 3, 'B3b': 2} and len(catalog['scopes']) == 5,
            'Invalid independent fourteen-control catalog')
    require(isinstance(catalog.get('rollbackPluginSourceSha256'), str) and
            re.fullmatch('[0-9a-f]{64}', catalog['rollbackPluginSourceSha256']) is not None, 'Invalid exact rollback plugin pin')
    for scope, (group, suffix, cls, mode, parameters, names) in zip(catalog['scopes'], expected):
        require((scope['group'], scope['namespace'], scope['class'], scope['source'], scope['discovery']) ==
                (group, 'SharpLink.UnitTests.' + suffix, cls, 'test/SharpLink.UnitTests/' + suffix + '/' + cls + '.cs', mode),
                'Catalog scope changed')
        require([m['name'] for m in scope['methods']] == names and
                all(m['parameters'] == parameters and m['return'] == 'System.Threading.Tasks.Task' for m in scope['methods']),
                'Catalog method/signature changed')
        require(re.fullmatch('[0-9a-f]{64}', scope['sourceSha256']) is not None, 'Invalid source pin')


def verify_graph(source, catalog):
    validate_catalog(catalog)
    require(sha(source / 'test/SharpLink.RollbackPlugin/RollbackManifest.cs') == catalog['rollbackPluginSourceSha256'],
            'Exact rollback plugin source changed')
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
            require(sha(source / scope['source']) == scope['sourceSha256'], 'Selected source hash changed')
            require(list(results.source_span(source / scope['source'], item['name'])) == item['expectedSpan'], 'Selected source span changed')
            counts[scope['group']] += 1
    require(counts == catalog['expectedGroups'] and sum(counts.values()) == 14 and counts == {'ABScope': 6, 'B2a': 2, 'B2c': 1, 'B3a': 3, 'B3b': 2}, 'Candidate catalog mismatch')


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


# Utilities below are local to this NEW lane; old47 and shared owner/parser stay unchanged.

TOP_LEVEL_FAILURES = []
ACTIVE_FAILURE_LEDGERS = []


def emit(event, **fields):
    """Only typed public identifiers/enums. Never accept an arbitrary error string."""
    require(event in {'stage', 'identity', 'policy', 'binary', 'runtime', 'case', 'path-mismatch', 'summary', 'failure'}, 'Invalid event')
    allowed = {'stage', 'status', 'group', 'method', 'uidSha256', 'sha256', 'version', 'head', 'tree', 'count', 'counts', 'integrity', 'category'}
    require(set(fields) <= allowed, 'Invalid console fields')
    for key, value in fields.items():
        if key in ('sha256', 'uidSha256'):
            require(isinstance(value, str) and re.fullmatch('[0-9a-f]{64}', value) is not None, 'Invalid digest')
        elif key in ('head', 'tree'):
            require(isinstance(value, str) and re.fullmatch('[0-9a-f]{40}', value) is not None, 'Invalid Git digest')
        elif key == 'version':
            require(isinstance(value, str) and re.fullmatch(r'\d+\.\d+\.\d+(?:\.\d+)?', value) is not None, 'Invalid version')
        elif key == 'method':
            require(isinstance(value, str) and re.fullmatch('[A-Za-z][A-Za-z0-9_]{0,159}', value) is not None, 'Invalid method')
        elif key == 'stage':
            fixed = {'head', 'tree', 'clean', 'tracked', 'clone', 'checkout', 'cloned-head', 'classifier-checks', 'catalog-checks',
                     'sdk-version', 'sdk-info', 'runtimes', 'restore', 'build', 'execution', 'failed-report-seal', 'final-source',
                     'final-evidence', 'summary-write', 'graph-format', 'graph-write', 'terminal-evidence', 'terminal-graph', 'console', 'entry'}
            require(value in fixed or value in {'list-0-0', 'list-1-0', 'list-1-1', 'list-2-0', 'list-3-0', 'list-3-1', 'list-3-2', 'list-4-0', 'list-4-1'} or value in {f'case-{n}' for n in range(14)}, 'Invalid stage')
        elif key == 'status':
            require(value in {'PASS', 'FAILED_OR_INCOMPLETE', 'INCOMPLETE', 'UNRUN', 'REJECTED', 'STANDARD_SOURCE_ROOT'}, 'Invalid status')
        elif key == 'group':
            require(value in {'ABScope', 'B2a', 'B2c', 'B3a', 'B3b'}, 'Invalid group')
        elif key == 'integrity':
            require(value in {'VERIFIED', 'FAILED'}, 'Invalid integrity')
        elif key == 'category':
            require(value in {'value', 'timeout', 'os', 'command', 'group', 'path-diagnostic', 'other'}, 'Invalid category')
        elif key == 'count':
            require(type(value) is int and 0 <= value <= 100000, 'Invalid count')
        elif key == 'counts':
            require(type(value) is dict and set(value) == {'ABScope', 'B2a', 'B2c', 'B3a', 'B3b'}, 'Invalid groups')
            for row in value.values():
                require(type(row) is dict and set(row) == {'PASS', 'FAILED_OR_INCOMPLETE', 'INCOMPLETE', 'UNRUN'} and
                        all(type(n) is int and 0 <= n <= 14 for n in row.values()), 'Invalid counters')
    print(json.dumps(dict(event=event, **fields), sort_keys=True), flush=True)


def error_category(error):
    if isinstance(error, results.SourcePathDiagnosticFailure): return 'path-diagnostic'
    if isinstance(error, owner.CommandFailure): return 'command'
    if isinstance(error, BaseExceptionGroup): return 'group'
    if type(error) is ValueError: return 'value'
    if type(error) is TimeoutError: return 'timeout'
    if type(error) in (OSError, FileNotFoundError, PermissionError): return 'os'
    return 'other'


class FailureLedger:
    def __init__(self):
        self.occurrences = []  # Repeated raw objects in different stages stay repeated.

    def add(self, stage, error):
        self.occurrences.append((stage, error))

    def graph(self):
        nodes, objects, identities, formatting = [], [], {}, []

        def node(error):
            key = id(error)
            if key not in identities:
                identities[key] = len(objects)
                objects.append(error)
                nodes.append({'id': identities[key]})
            return identities[key]

        occurrences = [{'stage': stage, 'node': node(error)} for stage, error in self.occurrences]
        index = 0
        while index < len(objects):
            error = objects[index]; record = nodes[index]
            record['type'] = type(error).__module__ + '.' + type(error).__qualname__
            record['edges'] = []
            try: record['message'] = str(error)
            except BaseException as failure:
                formatting.append(failure)
                record['messageUnavailable'] = True
                record['edges'].append({'kind': 'message-format', 'node': node(failure)})
            try:
                record['traceback'] = [dict(file=f.filename, line=f.lineno, function=f.name)
                                       for f in traceback.extract_tb(error.__traceback__)]
                record['suppressContext'] = error.__suppress_context__
                for kind, related in (('cause', error.__cause__), ('context', error.__context__)):
                    if related is not None: record['edges'].append({'kind': kind, 'node': node(related)})
                if isinstance(error, BaseExceptionGroup):
                    for position, related in enumerate(error.exceptions):
                        record['edges'].append({'kind': 'group', 'position': position, 'node': node(related)})
                for position, related in enumerate(getattr(error, 'causes', ())):
                    require(isinstance(related, BaseException), 'Invalid private exception relation')
                    record['edges'].append({'kind': 'custom-cause', 'position': position, 'node': node(related)})
                if isinstance(error, getattr(globals().get('owner'), 'CommandFailure', ())): record['commandOutcome'] = error.outcome
                if isinstance(error, (OSError, SyntaxError)):
                    record['filename'] = error.filename
            except BaseException as failure:
                formatting.append(failure)
                record['edges'].append({'kind': 'graph-format', 'node': node(failure)})
            index += 1
            if len(objects) > 10000:
                failure = ValueError('Private failure graph exceeds finite bound')
                formatting.append(failure)
                break
        occurrences.extend({'stage': 'graph-format', 'node': node(error)} for error in formatting)
        return dict(occurrences=occurrences, nodes=nodes), formatting


class EvidenceSeals:
    """Closed roots have exact member sets, not just hashes of surviving files."""
    def __init__(self, workspace):
        self.workspace = Path(workspace)
        self.base = self._directory(self.workspace)
        self.roots = {}
        self.parents = {self.workspace: self.base}

    @staticmethod
    def _stat(value):
        return (value.st_dev, value.st_ino, value.st_mode, value.st_size,
                value.st_mtime_ns, value.st_ctime_ns, value.st_nlink)

    @staticmethod
    def _directory(path):
        value = path.lstat()
        require(stat.S_ISDIR(value.st_mode) and not stat.S_ISLNK(value.st_mode), 'Evidence directory required')
        return (value.st_dev, value.st_ino, value.st_mode)

    def _snapshot(self, root):
        root = Path(root)
        require(root.is_absolute() and '..' not in root.parts and root != self.workspace, 'Invalid evidence root')
        root.relative_to(self.workspace)  # lexical containment, then actual components below
        require(self._directory(self.workspace) == self.base, 'Evidence workspace replaced')
        current = root
        while current != self.workspace:
            identity = self._directory(current)
            require(self.parents.setdefault(current, identity) == identity, 'Evidence ancestor replaced')
            current = current.parent
        result = {}

        def walk(path):
            before = path.lstat(); relative = path.relative_to(root).as_posix()
            require(not stat.S_ISLNK(before.st_mode), 'Symlink evidence forbidden')
            if stat.S_ISDIR(before.st_mode):
                result[relative] = ('directory', self._stat(before))
                for child in sorted(path.iterdir()): walk(child)
                require(self._stat(path.lstat()) == self._stat(before), 'Evidence directory changed during scan')
            else:
                require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1, 'Nonregular or aliased evidence')
                descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
                try:
                    require(self._stat(os.fstat(descriptor)) == self._stat(before), 'Evidence replaced before open')
                    digest = hashlib.sha256()
                    while True:
                        chunk = os.read(descriptor, 1024 * 1024)
                        if not chunk: break
                        digest.update(chunk)
                    require(self._stat(os.fstat(descriptor)) == self._stat(before) and
                            self._stat(path.lstat()) == self._stat(before), 'Evidence changed while hashing')
                    result[relative] = ('file', self._stat(before), digest.hexdigest())
                finally: os.close(descriptor)
        walk(root)
        return result

    def contains(self, root):
        return Path(root) in self.roots

    def register(self, root, expected=None):
        root = Path(root)
        require(root not in self.roots, 'Evidence root registered twice')
        snapshot = self._snapshot(root)
        # Store even incomplete owner receipts; never invent missing evidence.
        self.roots[root] = snapshot
        if expected is not None:
            require(set(snapshot) == {'.'} | set(expected), 'Evidence member set incomplete or unexpected')

    def verify(self):
        failures = []
        for root, expected in self.roots.items():
            try: require(self._snapshot(root) == expected, 'Sealed evidence changed')
            except BaseException as failure: failures.append(failure)
        if failures: raise BaseExceptionGroup('Evidence verification occurrences', failures)


def write_sealed_json(work, name, value, seals, filename=None):
    root = work / name
    root.mkdir()
    path = root / (filename or (name + '.json'))
    try:
        with path.open('x') as stream:
            json.dump(value, stream, indent=2)
            stream.write('\n')
    except BaseException as original:
        errors = [original]
        try: seals.register(root)
        except BaseException as failure: errors.append(failure)
        raise BaseExceptionGroup('Private report writing and evidence capture', errors)
    seals.register(root, {path.name})


def persist_final(work, seals, failures, records, summary):
    # Observations are immutable; terminal integrity is a separate decision.
    try: write_sealed_json(work, 'observations', dict(groups=summary, cases=records, terminal=False), seals)
    except BaseException as error: failures.add('summary-write', error)
    covered = -1
    try:
        graph, formatting = failures.graph()
        for error in formatting: failures.add('graph-format', error)
        # graph already contains formatting edges; all source objects remain rooted.
        covered = len(failures.occurrences)
        write_sealed_json(work, 'failure-graph', graph, seals)
    except BaseException as error: failures.add('graph-write', error)
    try: seals.verify()
    except BaseException as error: failures.add('terminal-evidence', error)
    if len(failures.occurrences) != covered:
        # One bounded private supplement for final/reporting errors; never rewrite
        # the earlier graph. Failure to persist remains rooted, not a false seal.
        try:
            graph, formatting = failures.graph()
            for error in formatting: failures.add('graph-format', error)
            write_sealed_json(work, 'terminal-failure-graph', graph, seals)
        except BaseException as error: failures.add('terminal-graph', error)
        try: seals.verify()
        except BaseException as error: failures.add('terminal-evidence', error)


def render_terminal(failures, summary):
    try:
        for stage, error in list(failures.occurrences):
            emit('failure', stage=stage, category=error_category(error))
        emit('summary', counts=summary, integrity='FAILED' if failures.occurrences else 'VERIFIED', count=len(failures.occurrences))
    except BaseException as error:
        failures.add('console', error)
        try: os.write(2, b'{"event":"failure","stage":"console","category":"other"}\n')
        except BaseException as reporting: failures.add('console', reporting)


def main():
    catalog = results.read_json(ROOT / 'eng/isolation-boundary-controls.json')
    limits = catalog['limits']
    owner.enable_subreaper()
    work = Path(tempfile.mkdtemp(prefix='sli9-', dir='/tmp'))
    logs = work / 'logs'; logs.mkdir()
    seals = EvidenceSeals(work)
    failures = FailureLedger()
    ACTIVE_FAILURE_LEDGERS.append(failures)
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
    reports = []
    current = None

    def run(label, argv, cwd=ROOT, seconds=120):
        nonlocal sequence
        seals.verify()
        remaining = stop_at - time.monotonic()
        require(remaining > 0, 'Remaining command budget exhausted before ' + label)
        sequence += 1
        pipe = work / ('p' + str(sequence)); pipe.mkdir()
        require(len(os.fsencode(pipe)) + 1 + 32 <= 103, 'Testing platform Unix socket path too long')
        identity = (pipe.stat().st_dev, pipe.stat().st_ino)
        command_logs = logs / ('s' + str(sequence)); command_logs.mkdir()
        child = dict(env, TESTINGPLATFORM_PIPE_DIRECTORY=str(pipe))
        caught = []
        output = None
        try:
            output = owner.run_owned(label, [str(x) for x in argv], cwd, child, command_logs, min(seconds, remaining))
        except BaseException as error:
            caught.append(error)
        # Always run all independent checks AFTER the original owner's cleanup.
        for check in (
            lambda: require((pipe.stat().st_dev, pipe.stat().st_ino) == identity and not any(pipe.iterdir()),
                            'Pipe identity/contents changed'),
            seals.verify,
            lambda: seals.register(command_logs, {label + '.stdout', label + '.stderr', label + '.json'})):
            try: check()
            except BaseException as error: caught.append(error)
        if caught:
            try: emit('failure', stage=label, category='group', count=len(caught))
            except BaseException as reporting: caught.append(reporting)
            raise BaseExceptionGroup('Original command and independent evidence outcomes', caught)
        emit('stage', stage=label, status='PASS')
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
        run('catalog-checks', [sys.executable, '-B', source / 'eng/tests/test_isolation_boundary_catalog.py'], source, 30)
        dotnet = Path(shutil.which('dotnet', path=env.get('PATH')) or '').resolve()
        require(dotnet.is_file(), 'Official setup-dotnet executable unavailable')
        built[dotnet] = sha(dotnet)
        sdk = run('sdk-version', [dotnet, '--version'], source).read_text().strip()
        require(re.fullmatch(r'10\.0\.1\d\d', sdk) is not None, 'global.json selected an unexpected SDK feature band')
        run('sdk-info', [dotnet, '--info'], source)
        runtimes = run('runtimes', [dotnet, '--list-runtimes'], source).read_text()
        emit('identity', head=head, tree=tree, version=sdk, sha256=built[dotnet], count=14)
        config = work / 'NuGet.Config'
        config.write_text('<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3"/></packageSources></configuration>\n')
        project = source / catalog['project']
        run('restore', [dotnet, 'restore', project, '--configfile', config, '--disable-parallel', '-p:Configuration=Release', '-p:RestoreIgnoreFailedSources=false', '-m:1', '-nr:false'], source, limits['restoreSeconds'])
        assets = asset_identity(source, catalog, work / 'packages', package_identity)
        built.update({source / name: value for name, value in assets.items()})
        check_files()
        emit('policy', status='STANDARD_SOURCE_ROOT')
        run('build', [dotnet, 'build', project, '-c', 'Release', '--no-restore', '-v:minimal', '-p:UseSharedCompilation=false', '-p:PathMap=' + results.compile_path_map(source), '-m:1', '-nr:false'], source, limits['buildSeconds'])
        output = project.parent / 'bin/Release/net10.0'
        dll = output / 'SharpLink.UnitTests.dll'
        require(dll.is_file(), 'Unit binary missing')
        for plugin in ('SharpLink.RollbackPlugin', 'SharpLink.RetirementEntryPlugin'):
            expected = source / 'test' / plugin / 'bin/Release/net10.0' / (plugin + '.dll')
            require(sha(output / (plugin + '.dll')) == sha(expected), 'Plugin runtime copy mismatch')
        built.update({p: sha(p) for p in output.rglob('*') if p.is_file()})
        emit('binary', sha256=sha(dll))
        for number, scope in enumerate(catalog['scopes']):
            batches = [scope['methods']] if scope['discovery'] == 'class' else [[m] for m in scope['methods']]
            for part, methods in enumerate(batches):
                selected = '*' if scope['discovery'] == 'class' else methods[0]['name']
                filter_value = '/*/' + scope['namespace'] + '/' + scope['class'] + '/' + selected
                path = run('list-' + str(number) + '-' + str(part), [dotnet, dll, '--list-tests', 'json', '--treenode-filter', filter_value], source, limits['discoverySeconds'])
                def diagnostic(row):
                    detail = results.source_path_diagnostic(row, source / scope['source'],
                        results.reported_source_path(scope['source']), scope['source'], ROOT / scope['source'])
                    # No raw UID or unknown path; identity was checked by discovery.
                    emit('path-mismatch', method=row['type']['methodName'],
                         uidSha256=detail['actualUidSha256'], sha256=detail['selectedSourceSha256'],
                         status='REJECTED')
                rows = results.discovery(results.read_json(path), scope, methods, source / scope['source'],
                    catalog['assemblyFullName'], results.reported_source_path(scope['source']), diagnostic)
                seals.verify()
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
        write_sealed_json(work, 'ledger', ledger, seals, 'actual-ledger.json')
        check_files()
        runtime = None
        generations = set()
        for index, record in enumerate(records):
            current = record
            row = ledger[record['uid']]
            report = work / ('r' + str(index)); report.mkdir()
            reports.append(report)
            filter_value = '/*/' + record['namespace'] + '/' + record['className'] + '/' + record['method']
            # The exact filter is bound above to ONE real UID. No guessed UID/filter CLI.
            record['status'] = 'INCOMPLETE'
            run('case-' + str(index), [dotnet, dll, '--treenode-filter', filter_value, '--maximum-parallel-tests', '1', '--timeout', limits['engineTimeout'], '--results-directory', report, '--report-html', '--report-html-filename', report / 'session.html', '--junit-output-path', report / 'junit.xml'], source, limits['caseSeconds'])
            seals.register(report)
            verdict = results.passed(results.read_json(report / 'session.tunit-report.json'), (report / 'junit.xml').read_text(), row, results.reported_source_path(row['selection']['source']), catalog['tunitRuntimeVersion'], runtime, filter_value)
            if runtime is None:
                emit('runtime', version=verdict['runtime'].removeprefix('.NET '))
            runtime = verdict['runtime']
            require(any(line.startswith('Microsoft.NETCore.App ' + runtime.removeprefix('.NET ') + ' [') for line in runtimes.splitlines()), 'Loaded runtime is not in actual installed runtime inventory')
            require(verdict['publicationGeneration'] not in generations, 'Reused native publication')
            generations.add(verdict['publicationGeneration'])
            check_files()
            seals.verify()
            record['status'] = 'PASS'
            emit('case', group=record['group'], method=record['method'], uidSha256=hashlib.sha256(record['uid'].encode()).hexdigest(), status='PASS')
            current = None
    except BaseException as error:
        failures.add('execution', error)
        if current is not None:
            current['status'] = 'FAILED_OR_INCOMPLETE'
    finally:
        # Include available failed report evidence too, without reclassifying it.
        for report in reports:
            if not seals.contains(report):
                try: seals.register(report)
                except BaseException as error: failures.add('failed-report-seal', error)
        for stage, check in (('final-source', check_files), ('final-evidence', seals.verify)):
            try: check()
            except BaseException as error: failures.add(stage, error)
        summary = {group: {status: sum(r['group'] == group and r['status'] == status for r in records)
                          for status in ('PASS', 'FAILED_OR_INCOMPLETE', 'INCOMPLETE', 'UNRUN')}
                   for group in catalog['expectedGroups']}
        persist_final(work, seals, failures, records, summary)
        # Observations survive an integrity failure; this is never a false PASS.
        count_before_console = len(failures.occurrences)
        render_terminal(failures, summary)
        if len(failures.occurrences) != count_before_console:
            try:
                graph, formatting = failures.graph()
                for error in formatting: failures.add('graph-format', error)
                write_sealed_json(work, 'console-failure-graph', graph, seals)
            except BaseException as error: failures.add('graph-write', error)
            try: seals.verify()
            except BaseException as error: failures.add('terminal-evidence', error)
    return 0 if not failures.occurrences and all(r['status'] == 'PASS' for r in records) else 1


def bootstrap_failure_exit():
    # This branch uses stdlib + local evidence/graph code only. Helpers may be
    # absent or syntactically broken. Never expose Python's automatic traceback.
    try:
        work = Path(tempfile.mkdtemp(prefix='sli9-boot-'))
        seals = EvidenceSeals(work)
        summary = {group: dict(PASS=0, FAILED_OR_INCOMPLETE=0, INCOMPLETE=0, UNRUN=count)
                   for group, count in [('ABScope', 6), ('B2a', 2), ('B2c', 1), ('B3a', 3), ('B3b', 2)]}
        persist_final(work, seals, BOOTSTRAP_FAILURES, [], summary)
    except BaseException as reporting:
        BOOTSTRAP_FAILURES.add('graph-write', reporting)
    try: os.write(2, b'{"event":"failure","stage":"entry","category":"other"}\n')
    except BaseException as reporting: BOOTSTRAP_FAILURES.add('console', reporting)
    return 1


def entry():
    if BOOTSTRAP_FAILURES.occurrences:
        return bootstrap_failure_exit()
    try:
        return main()
    except BaseException as error:
        # No interpreter traceback/raw text, including setup or renderer failures.
        TOP_LEVEL_FAILURES.append(error)
        if ACTIVE_FAILURE_LEDGERS: ACTIVE_FAILURE_LEDGERS[-1].add('entry', error)
        try: os.write(2, b'{"event":"failure","stage":"entry","category":"other"}\n')
        except BaseException as reporting: TOP_LEVEL_FAILURES.append(reporting)
        return 1


# The real helper import boundary is guarded on BOTH direct execution and
# synthetic import. A successful import never invokes main or any SDK command.
BOOTSTRAP_FAILURES = FailureLedger()
try:
    initialize_helpers()
except BaseException as boot_error:
    BOOTSTRAP_FAILURES.add('entry', boot_error)
    ACTIVE_FAILURE_LEDGERS.append(BOOTSTRAP_FAILURES)


if __name__ == '__main__':
    sys.exit(entry())
