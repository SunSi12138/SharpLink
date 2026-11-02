#!/usr/bin/env python3
"""Finite isolated main baseline/fixed lane. Raw evidence stays private/local.
Expected baseline failures are qualified individually, never retried or counted as
fixed successes. Every unexpected command/schema/cleanup/guard failure stops.
"""
import sys
sys.dont_write_bytecode = True
import argparse
import ast
import traceback
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import tempfile
import xml.etree.ElementTree as ET
import audit
import classify as c
import process_owner as owner

SOURCE_SHA = '9cdb79b3be5a3e4e191759316931421741f5df5db7001d5dbfdba2ddc6541538'
BASE = '9d1635c8988870ee295098fe9e65cb49924013d2'
FOCUSED = 'test/SharpLink.TerminalSignal.Focused'
SOURCE_FILE = 'ServerTerminalSignalObservationTests.cs'
VIRTUAL_ROOT = '/_/terminal-main'
REPORTED_SOURCE = VIRTUAL_ROOT+'/'+FOCUSED+'/'+SOURCE_FILE
PAYLOAD = Path(__file__).resolve().parent
FILTERS = {'regular': f'/*/{c.NS}/{c.REGULAR}/*', 'positive': f'/*/{c.NS}/{c.POSITIVE}/{c.POS_METHOD}',
           'start': f'/*/{c.NS}/{c.REGULAR}/{c.START}', 'stop': f'/*/{c.NS}/{c.REGULAR}/{c.STOP}'}


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def files(root, excluded=()):
    result = {}
    for current, dirs, names in os.walk(root, followlinks=False):
        current = Path(current)
        dirs[:] = [name for name in dirs if name != '.git' and not any((current/name).is_relative_to(x) for x in excluded)]
        for name in dirs + names:
            path = current/name
            if any(path.is_relative_to(x) for x in excluded):
                continue
            c.require(not path.is_symlink(), 'Symlink in guarded tree')
            c.require(path.is_file() or path.is_dir(), 'Special node in guarded tree')
            if path.is_file():
                result[str(path)] = (sha(path), stat.S_IMODE(path.stat().st_mode))
    return result


def selection_members(root):
    """Pin resolution candidates, not contents of unrelated historical SDKs."""
    c.require(root.is_dir() and not root.is_symlink(), 'SDK selection parent missing/symlink')
    result = {}
    for path in [root, *sorted(root.iterdir())]:
        info = path.lstat()
        c.require(not stat.S_ISLNK(info.st_mode) and
                  (stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode)), 'Special/symlink SDK selection candidate')
        result['.' if path == root else path.name] = (stat.S_IFMT(info.st_mode), info.st_dev, info.st_ino, stat.S_IMODE(info.st_mode))
    return result


def equal_snapshot(expected):
    for path, identity in expected.items():
        p = Path(path)
        c.require(p.is_file() and not p.is_symlink() and (sha(p), stat.S_IMODE(p.stat().st_mode)) == identity,
                  'Previously sealed evidence/input changed')



def path_state(base, path):
    """Exact existence/type/identity chain; no following symlinks, including absent parents."""
    c.require(path.is_relative_to(base), 'Selection input escaped container')
    result = []
    current = base
    for part in (None, *path.relative_to(base).parts):
        if part is not None: current = current / part
        try: info = current.lstat()
        except FileNotFoundError:
            result.append(('missing', str(current.relative_to(base)))); break
        c.require(not stat.S_ISLNK(info.st_mode) and (stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode)), 'Selection input symlink/special')
        result.append((str(current.relative_to(base)), stat.S_IFMT(info.st_mode), info.st_dev, info.st_ino, stat.S_IMODE(info.st_mode)))
    return result


def manifest_feature_band(value):
    c.require(isinstance(value,str) and re.fullmatch(r'\d+\.\d+\.\d+',value), 'Unsupported manifest feature-band syntax')
    numbers=tuple(map(int,value.split('.')))
    c.require('.'.join(map(str,numbers))==value and numbers[2]%100==0, 'Noncanonical manifest feature-band spelling')
    return numbers


def manifest_identifier(value):
    return isinstance(value,str) and value not in ('.','..') and re.fullmatch(r'[A-Za-z0-9_.-]+',value) is not None


def workload_inputs(lane, sdk_root, version):
    """Seal the resolver's finite candidate closure, not a permissive SDK prefix.

    Current-band candidates, known-ID fallback candidates in older bands, and
    manifests explicitly referenced by candidate workload sets/install state.
    All versions in those candidate locations are sealed: selection is left to
    the real resolver, whose inputs and directory choices cannot change later.
    """
    major, minor, patch = map(int, version.split('.'))
    band = f'{major}.{minor}.{patch // 100 * 100}'
    manifest_root = sdk_root/'sdk-manifests'
    trees = set(); metadata = set(); selections = {}; paths = {}
    def track(path, base=sdk_root):
        paths[(base,path)] = path_state(base,path)
        return path.is_dir() if path.exists() else False
    def parent(path):
        if track(path): selections[path] = selection_members(path)
    def tree(path):
        if track(path):
            trees.add(path)
            for current,dirs,names in os.walk(path,followlinks=False):
                current=Path(current); selections[current]=selection_members(current)
                for name in names:
                    item=current/name
                    c.require(item.is_file() and not item.is_symlink(), 'Workload input not regular')
                    metadata.add(item)
    def file(path):
        track(path)
        if path.exists():
            c.require(path.is_file() and not path.is_symlink(), 'Workload metadata not regular')
            metadata.add(path)
    # Child environments already exclude overrides; prove absence instead of
    # setting MSBuildEnableWorkloadResolver=false or redirecting official inputs.
    forbidden=('DOTNETSDK_WORKLOAD_MANIFEST_ROOTS','DOTNETSDK_WORKLOAD_MANIFEST_IGNORE_DEFAULT_ROOTS',
               'DOTNETSDK_WORKLOAD_PACK_ROOTS','MSBuildEnableWorkloadResolver',
               'DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR','DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER')
    for env in lane.env.values():
        c.require(not any(key in env for key in forbidden), 'Workload resolver override present')
        profile=Path(env['DOTNET_CLI_HOME'])/'.dotnet'
        user_manifests=profile/'sdk-manifests'
        state=path_state(Path(env['DOTNET_CLI_HOME']),user_manifests)
        c.require(not user_manifests.exists(), 'User workload manifests outside selected SDK')
        paths[(Path(env['DOTNET_CLI_HOME']),user_manifests)] = state
    # This fixed main global.json has no workloadVersion/workload-set override.
    for root in lane.roots.values():
        c.require(sha(root/'global.json') == '099805cd1161ca87a57c0b0241c614b0ec35b5fce384c3fd1c86a16e5364f6e4', 'Wrong main global workload policy')
    userlocal=sdk_root/'metadata/workloads'/band/'userlocal'
    msi=sdk_root/'metadata/workloads'/band/'installertype/msi'
    for override in (userlocal,msi):
        track(override);c.require(not override.exists(), 'Unsupported user-local/MSI workload selection')
    install=sdk_root/'metadata/workloads/X64'/band/'InstallState/default.json'
    file(install)
    state=audit.read_workload_metadata(install,sdk_root,'install-state') if install.exists() else {}
    c.require(isinstance(state,dict), 'Invalid workload install state')
    parent(manifest_root)
    c.require((manifest_root/band).is_dir(), 'Selected workload feature band missing')
    tree(manifest_root/band)
    known=sdk_root/'sdk'/version/'KnownWorkloadManifests.txt'
    if not known.exists(): known=sdk_root/'sdk'/version/'IncludedWorkloadManifests.txt'
    c.require(known.is_file(), 'Selected SDK known manifest IDs unavailable')
    ids=[line for line in known.read_text().splitlines() if line]
    c.require(all(manifest_identifier(name) for name in ids), 'Unsafe known workload manifest ID')
    # Missing known IDs may fall back to older feature bands. Cover their exact
    # ID directories (all candidate versions), not every old SDK/manifest tree.
    for candidate in manifest_root.iterdir():
        if candidate.is_dir():
            feature_numbers=manifest_feature_band(candidate.name)
            if feature_numbers < (major,minor,patch//100*100):
                parent(candidate)
                for identifier in ids: tree(candidate/identifier)
    set_bands={band,'8.0.100'}
    requested=state.get('workloadVersion')
    if requested:
        c.require(isinstance(requested,str) and re.fullmatch(r'\d+\.\d+\.\d+(?:\.\d+)?',requested), 'Unsupported workload-set version syntax')
        numbers=list(map(int,requested.split('.')))
        set_bands.add(f'{numbers[0]}.{numbers[1]}.{numbers[2]//100*100}')
    specs=[]
    for set_band in sorted(set_bands):
        parent(manifest_root/set_band)
        sets=manifest_root/set_band/'workloadsets'
        tree(sets)
        if sets.exists():
            for spec in sorted(sets.glob('*/*.workloadset.json')):
                specs.append((audit.read_workload_metadata(spec,sdk_root,'workload-set'),set_band))
    if state.get('manifests') is not None: specs.append((state['manifests'],band))
    for spec,default_band in specs:
        c.require(isinstance(spec,dict), 'Invalid workload-set manifest map')
        for identifier,value in spec.items():
            c.require(manifest_identifier(identifier) and isinstance(value,str), 'Unsafe workload-set manifest identity')
            parts=value.split('/')
            c.require(len(parts) in (1,2) and re.fullmatch(r'\d+\.\d+\.\d+(?:[-+][A-Za-z0-9_.-]+)?',parts[0]), 'Unsupported workload manifest version syntax')
            feature=parts[1] if len(parts)==2 else default_band
            manifest_feature_band(feature)
            parent(manifest_root/feature);parent(manifest_root/feature/identifier)
            tree(manifest_root/feature/identifier/parts[0])
    # Seal exact regular bytes AND every visited directory membership. Snapshot
    # absent selectors too; introducing a higher version or override must fail.
    for path in metadata:
        lane.sdk_pins[str(path)]=(sha(path),stat.S_IMODE(path.stat().st_mode))
    lane.workload_pins={str(path):lane.sdk_pins[str(path)] for path in metadata}
    lane.workload_parents=selections;lane.workload_paths=paths
    return {'featureBand':band,'candidateFiles':len(metadata),'selectionDirectories':len(selections),
            'existenceGuards':len(paths),'policy':'finite candidate closure; actual imports require exact sealed file/hash'}


def environment(state):
    keep = ('PATH','DOTNET_ROOT','DOTNET_ROOT_X64','LANG','LC_ALL','HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','NO_PROXY',
            'http_proxy','https_proxy','all_proxy','no_proxy','SSL_CERT_FILE','SSL_CERT_DIR','REQUESTS_CA_BUNDLE','CURL_CA_BUNDLE')
    env = {k: os.environ[k] for k in keep if k in os.environ}
    env.update(HOME=str(state/'home'), DOTNET_CLI_HOME=str(state/'cli'), NUGET_PACKAGES=str(state/'packages'),
               NUGET_HTTP_CACHE_PATH=str(state/'http'), NUGET_PLUGINS_CACHE_PATH=str(state/'plugins'), TMPDIR=str(state/'tmp'),
               DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
               DOTNET_MULTILEVEL_LOOKUP='0', MSBUILDDISABLENODEREUSE='1', DOTNET_CLI_USE_MSBUILD_SERVER='0',
               CI='true', DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE='true', PYTHONDONTWRITEBYTECODE='1', GIT_TERMINAL_PROMPT='0', GIT_NO_LAZY_FETCH='1',
               GIT_CONFIG_GLOBAL='/dev/null', GIT_CONFIG_SYSTEM='/dev/null', GIT_ALLOW_PROTOCOL='file',
               TUNIT_ENABLE_JUNIT_REPORTER='1', TUNIT_DISABLE_ARTIFACT_UPLOAD='1',
               TUNIT_DISABLE_GITHUB_REPORTER='1', TUNIT_AGGREGATE_REPORTS='off', TUNIT_DISABLE_LOGO='1')
    for name in ('home','cli','packages','http','plugins','tmp'):
        (state/name).mkdir(parents=True)
    (state/'cli/.dotnet').mkdir()  # Stable private profile ancestor; manifests stay absent.
    return env


class Lane:
    def __init__(self, roots, manifest_sha):
        self.roots = roots
        self.manifest_sha = manifest_sha
        self.work = Path(tempfile.mkdtemp(prefix='sl971-', dir='/tmp'))
        self.root_identity = self.identity(self.work)
        self.owner_marker = self.work/'OWNER'
        self.owner_marker.write_text(manifest_sha+'\n')
        self.marker_identity = sha(self.owner_marker)
        self.env = {v: environment(self.work/v) for v in roots}
        self.sealed = {}; self.evidence_dirs = {}; self.pipes = []; self.results = []; self.stage = 'preflight'
        self.sdk_trees = {}; self.sdk_parents = {}; self.sdk_pins = {}; self.seen_publications = set()
        self.workload_pins={}; self.workload_parents={}; self.workload_paths={}
        self.active_scope = None; self.active_uid = None
        self.dotnet = Path(shutil.which('dotnet', path=self.env['baseline']['PATH']) or '').resolve()
        c.require(self.dotnet.is_file(), 'No selected dotnet host')
        self.dotnet_identity = sha(self.dotnet)
        self.inputs = files(PAYLOAD)
        self.current_sources = {v: files(p) for v,p in roots.items()}
        self.current_packages = {v: files(Path(e['NUGET_PACKAGES'])) for v,e in self.env.items()}

    @staticmethod
    def identity(path):
        info = path.lstat()
        c.require(stat.S_ISDIR(info.st_mode) and info.st_uid == os.getuid() and stat.S_IMODE(info.st_mode) == 0o700,
                  'Private owned directory identity failed')
        return info.st_dev, info.st_ino

    def root_guard(self):
        c.require(self.identity(self.work) == self.root_identity, 'Private root replaced')
        c.require(self.owner_marker.is_file() and not self.owner_marker.is_symlink() and sha(self.owner_marker) == self.marker_identity,
                  'Private owner marker changed')

    def inputs_guard(self):
        c.require(files(PAYLOAD) == self.inputs and sha(PAYLOAD/'CI.json') == self.manifest_sha, 'Frozen code changed')
        c.require(sha(self.dotnet) == self.dotnet_identity, 'Selected host changed')
        equal_snapshot(self.sealed)
        for root, members in self.sdk_parents.items():
            c.require(selection_members(root) == members, 'SDK/runtime resolution candidate set changed')
        for root, snapshot in self.sdk_trees.items():
            c.require(files(root) == snapshot, 'Sealed actual SDK/runtime input changed')
        equal_snapshot(self.workload_pins)
        for parent,members in self.workload_parents.items():
            c.require(selection_members(parent)==members, 'Workload selection directory changed')
        for (base,path),expected in self.workload_paths.items():
            c.require(path_state(base,path)==expected, 'Workload selection metadata/absence changed')
        for directory, snapshot in self.evidence_dirs.items():
            c.require(files(directory) == snapshot, 'Previously sealed evidence set changed')

    def command(self, label, variant, argv, seconds, phase='readonly', expected_exit=0, filtered=False, cwd=None):
        self.stage = label
        self.inputs_guard(); self.root_guard(); self.current_guard()
        directory = self.work/label
        c.require(not directory.exists(), 'Attempt label reuse forbidden')
        directory.mkdir(mode=0o700)
        env = dict(self.env[variant])
        pipe = None; pipe_identity = None; pipe_created = False
        exclude = {v: [] for v in self.roots}
        if phase in ('restore','build','prepare'):
            for project in audit.PROJECTS.values():
                parent = self.roots[variant]/Path(project).parent
                if phase in ('restore','build'): exclude[variant].append(parent/'obj')
                if phase == 'build': exclude[variant].append(parent/'bin')
            if phase == 'prepare':
                exclude[variant] = [self.roots[variant]/FOCUSED]
                if variant == 'fixed': exclude[variant].append(self.roots[variant]/'src/SharpLink.Server/ServerLifecycleCoordinator.cs')
        before = {v: files(p, exclude[v]) for v,p in self.roots.items()}
        package_before = {v: files(Path(e['NUGET_PACKAGES'])) for v,e in self.env.items() if not (v==variant and phase=='restore')}
        errors = []; output = None
        try:
            if filtered:
                pipe = self.work/('p'+str(len(self.pipes)))
                pipe.mkdir(mode=0o700); pipe_created = True
                pipe_identity = self.identity(pipe)
                self.pipes.append((pipe, pipe_identity))
                c.require(len(str(pipe).encode())+1+32 <= 103, 'MTP Unix socket path budget exceeded')
                env['TESTINGPLATFORM_PIPE_DIRECTORY'] = str(pipe)
            output = owner.run_owned(label, argv, cwd or self.roots[variant], env, directory, seconds, expected_exit)
        except BaseException as error:
            errors.append(error)
        finally:
            checks = [self.inputs_guard, self.root_guard]
            checks.extend(lambda v=v,p=p: c.require(files(p, exclude[v]) == before[v], 'Source/protected output mutation') for v,p in self.roots.items())
            checks.extend(lambda v=v,s=s: c.require(files(Path(self.env[v]['NUGET_PACKAGES'])) == s, 'Protected official cache changed') for v,s in package_before.items())
            if pipe_created:
                checks.append(lambda: c.require(pipe_identity is not None and self.identity(pipe) == pipe_identity and not list(pipe.iterdir()), 'Owned pipe missing/replaced/nonempty'))
            for check in checks:
                try: check()
                except BaseException as error: errors.append(error)
            # Seal all current actual outputs even on failure; no retry/relabel path.
            try:
                self.sealed.update(files(directory))
                self.evidence_dirs[directory] = files(directory)
            except BaseException as error: errors.append(error)
        if errors:
            raise BaseExceptionGroup(label+' failed (original outcomes retained)', errors)
        self.current_sources = {v: files(p) for v,p in self.roots.items()}
        self.current_packages = {v: files(Path(e['NUGET_PACKAGES'])) for v,e in self.env.items()}
        row = c.read_json(directory/(label+'.json'))
        print(json.dumps({'stage':label,'exit':row['exitCode'],'seconds':row['elapsedSeconds'],'ownedRemaining':len(row['remainingOwned']),
                          'expectedExitOnly':expected_exit,'classification':'COMMAND_JOINED_NOT_TEST_QUALIFICATION'}), flush=True)
        return directory, output

    def current_guard(self):
        for v,p in self.roots.items():
            c.require(files(p) == self.current_sources[v], 'Final/current source-output set changed')
            c.require(files(Path(self.env[v]['NUGET_PACKAGES'])) == self.current_packages[v], 'Final/current cache set changed')

    def final_guards(self):
        errors = []
        for check in (self.inputs_guard, self.root_guard, self.current_guard):
            try: check()
            except BaseException as e: errors.append(e)
        for path, identity in self.pipes:
            try: c.require(self.identity(path)==identity and not list(path.iterdir()), 'Final pipe guard failed')
            except BaseException as e: errors.append(e)
        return errors

    def filtered(self, label, variant, filter_key, listing=False, scope=None, ledger=None, runtime=None):
        self.active_scope = scope
        self.active_uid = next(iter(ledger)) if ledger is not None and len(ledger)==1 else None
        directory = self.work/label
        assembly = self.roots[variant]/FOCUSED/'bin/Release/net10.0/SharpLink.UnitTests.dll'
        argv = [str(self.dotnet), str(assembly), '--treenode-filter', FILTERS[filter_key], '--maximum-parallel-tests','1',
                '--no-progress', '--results-directory',str(directory/'reports')]
        if listing: argv += ['--list-tests','json']
        else: argv += ['--report-html','--report-html-filename',str(directory/'reports/session.html'),
                       '--junit-output-path',str(directory/'reports/junit.xml')]
        directory, output = self.command(label, variant, argv, 120, filtered=True,
                                          expected_exit=2 if scope and scope.startswith('baseline-') else 0)
        source = self.roots[variant]/FOCUSED/SOURCE_FILE
        if listing:
            document = c.read_json(output[0])
            discovery_scope = {'regular':'regular','positive':'positive','start':c.START,'stop':c.STOP}[filter_key]
            actual = c.discovery(document, discovery_scope, REPORTED_SOURCE)
            print(json.dumps({'stage':label,'classification':'ACTUAL_UID_DISCOVERY','count':len(actual),
                              'uidSetSHA256':hashlib.sha256(json.dumps(sorted(actual)).encode()).hexdigest(),'testsExecuted':False}), flush=True)
            return actual
        report = directory/'reports/session.tunit-report.json'
        native = c.read_json(report)
        c.check_optional_filter(native, FILTERS[filter_key])
        generation = native.get('publicationGeneration')
        c.check_publication(generation, self.seen_publications)
        result = c.classify(native, (directory/'reports/junit.xml').read_text(encoding='utf-8-sig'), ledger, scope, source, runtime, REPORTED_SOURCE)
        self.seen_publications.add(generation)
        # Framework stderr is separate from captured test stdout/stderr. Never invent marker IDs.
        stderr = output[1].read_text(errors='replace')
        markers = set(re.findall(r'terminal-(?:owned|positive)-[0-9a-f]{32}', stderr))
        if scope == 'fixed':
            c.require(not re.search('unobserved', stderr, re.I), 'Unexpected fixed unobserved warning')
        if markers:
            prefix = 'terminal-positive-' if scope=='positive' else 'terminal-owned-'
            c.require(len(markers)==1 and next(iter(markers)).startswith(prefix), 'Unexpected framework marker set')
        result['markerEmitted'] = bool(markers)
        result['stage'] = label
        self.results.append(result)
        print(json.dumps(result), flush=True)
        return result


def execute(lane):
    # Source preparation uses no network or SDK and is checked before any restore.
    for variant, root in lane.roots.items():
        lane.command('prepare-'+variant, variant, [sys.executable,str(PAYLOAD/'prepare.py'),'--checkout',str(root),
                     '--variant',variant,'--manifest-sha256',SOURCE_SHA], 60, phase='prepare')
        audit.static_graph(root)
    # Exact SDK selected by the same pinned global.json under each checkout.
    versions = []
    for variant in lane.roots:
        _, out = lane.command('sdk-version-'+variant, variant, [str(lane.dotnet),'--version'],30)
        version = out[0].read_text().strip()
        c.require(re.fullmatch(r'10\.0\.1\d\d',version) is not None and int(version.split('.')[-1]) >= 102, 'SDK outside main latestPatch feature band')
        versions.append(version)
    c.require(versions[0]==versions[1], 'Variants selected different SDK versions')
    _, info = lane.command('sdk-info','baseline',[str(lane.dotnet),'--info'],30)
    _, inventory = lane.command('runtime-inventory','baseline',[str(lane.dotnet),'--list-runtimes'],30)
    host = re.search(r'Host:\s*\n\s*Version:\s*(\d+\.\d+\.\d+)', info[0].read_text())
    c.require(host is not None, 'Actual host version missing')
    runtimes = re.findall(r'^(Microsoft\.(?:NETCore|AspNetCore)\.App) (\d+\.\d+\.\d+) \[', inventory[0].read_text(), re.M)
    c.require(any(n=='Microsoft.NETCore.App' and v.startswith('10.0.') for n,v in runtimes), 'Required installed runtime absent')
    sdk_root = lane.dotnet.parent
    c.require((sdk_root/'sdk'/versions[0]).is_dir(), 'Selected SDK root unavailable')
    # Seal actual selected SDK, chosen hostfxr and every installed .NET 10 candidate
    # runtime/reference/apphost pack. No historical directory/file count is reused.
    for parent in (sdk_root/'sdk', sdk_root/'host/fxr', sdk_root/'shared/Microsoft.NETCore.App',
                   sdk_root/'packs/Microsoft.NETCore.App.Ref', sdk_root/'packs/Microsoft.NETCore.App.Host.linux-x64'):
        lane.sdk_parents[parent] = selection_members(parent)
    seal_roots = [sdk_root/'sdk'/versions[0], sdk_root/'host/fxr'/host.group(1)]
    for base in (sdk_root/'shared/Microsoft.NETCore.App', sdk_root/'packs/Microsoft.NETCore.App.Ref',
                 sdk_root/'packs/Microsoft.NETCore.App.Host.linux-x64'):
        candidates = [p for p in base.iterdir() if p.is_dir() and re.fullmatch(r'10\.0\.\d+',p.name)]
        c.require(candidates, 'Actual runtime/reference/apphost candidates unavailable')
        seal_roots.extend(candidates)
    for root in seal_roots:
        c.require(root.is_dir() and not root.is_symlink(), 'Selected SDK/runtime seal root unavailable')
        snapshot = files(root)
        c.require(snapshot, 'Selected SDK/runtime input set empty')
        lane.sdk_trees[root] = snapshot
        lane.sdk_pins.update(snapshot)
    workload=workload_inputs(lane,sdk_root,versions[0])
    print(json.dumps({'stage':'workload-inputs-sealed',**workload}),flush=True)
    print(json.dumps({'stage':'sdk-qualified','actualSDK':versions[0],'hostSHA256':lane.dotnet_identity,
                      'globalPolicy':'10.0.102 latestPatch','actualHost':host.group(1),'installedRuntimes':runtimes,'sealedInputFiles':len(lane.sdk_pins),'sealedDirectories':[str(p.relative_to(sdk_root)) for p in seal_roots],'selectionParentCount':len(lane.sdk_parents),'notHistoricalReproduction':True}),flush=True)
    config = lane.work/'NuGet.Config'
    config.write_text('<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><clear/><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping><auditSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></auditSources></configuration>\n')
    lane.sealed[str(config)] = (sha(config), stat.S_IMODE(config.stat().st_mode))
    restored = {}; expansions = {}
    for variant, root in lane.roots.items():
        project = root/audit.PROJECTS[audit.NAMES[-1]]
        lane.command('restore-'+variant,variant,[str(lane.dotnet),'restore',str(project),'--configfile',str(config),
                     '--disable-parallel','--force','--verbosity','minimal'],600,phase='restore')
        packages = Path(lane.env[variant]['NUGET_PACKAGES'])
        restored[variant] = audit.restored(root,packages)
        print(json.dumps({'stage':'restore-audit-'+variant,'activePackages':restored[variant]['packages'],
                          'officialArchiveCount':len(restored[variant]['cache']),'projectCount':4}),flush=True)
        expansions[variant] = {}
        for index,(name,path) in enumerate(audit.PROJECTS.items()):
            label=f'preprocess-{variant}-{index}'
            expanded_path=lane.work/label/'expanded.xml'
            lane.command(label,variant,[str(lane.dotnet),'msbuild',str(root/path),'-nologo','-nodeReuse:false',
                         '-p:Configuration=Release','-p:PathMap='+str(root)+'='+VIRTUAL_ROOT,'-preprocess:'+str(expanded_path)],60)
            origins=audit.expanded(expanded_path,root,packages,lane.sdk_pins,diagnostic_sdk_root=lane.dotnet.parent,
                                   source_pins=lane.current_sources[variant],cache_pins=lane.current_packages[variant])
            text=expanded_path.read_text(encoding='utf-8-sig').replace(str(root),'<SOURCE>').replace(str(packages),'<PACKAGES>')
            expansions[variant][name]=hashlib.sha256(text.encode()).hexdigest()
            print(json.dumps({'stage':label+'-audit','actualImportOrigins':len(origins)}),flush=True)
    c.require(restored['baseline']==restored['fixed'], 'Variants restored different package bytes/identities/graph')
    c.require(expansions['baseline']==expansions['fixed'], 'Variants have different expanded build graphs')
    for variant,root in lane.roots.items():
        # Restore receipts/imports remain independently protected across build.
        restore_files = {str(p):(sha(p),stat.S_IMODE(p.stat().st_mode)) for p in root.rglob('*')
                         if p.is_file() and '/obj/' in str(p) and (p.name.startswith('project.') or '.nuget.' in p.name)}
        lane.sealed.update(restore_files)
        lane.command('build-'+variant,variant,[str(lane.dotnet),'build',str(root/audit.PROJECTS[audit.NAMES[-1]]),
                     '-c','Release','--no-restore','-m:1','-nodeReuse:false','-p:UseSharedCompilation=false','-p:PathMap='+str(root)+'='+VIRTUAL_ROOT,'--verbosity','minimal'],600,phase='build')
        result=audit.built(root,Path(lane.env[variant]['NUGET_PACKAGES']),restored[variant]['packages'])
        print(json.dumps({'stage':'build-audit-'+variant,**result}),flush=True)
    ledgers={}
    for variant in lane.roots:
        for scope in ('regular','positive'):
            ledgers[variant,scope]=lane.filtered('list-'+variant+'-'+scope,variant,scope,listing=True)
    for scope in ('start','stop'):
        ledgers['baseline',scope]=lane.filtered('list-baseline-'+scope,'baseline',scope,listing=True)
        c.require(set(ledgers['baseline',scope]) <= set(ledgers['baseline','regular']), 'Singleton UID not in regular discovery')
    for scope in ('regular','positive'):
        def normalized(variant):
            return json.dumps(ledgers[variant,scope],sort_keys=True).replace(str(lane.roots[variant]),'<SOURCE>')
        c.require(normalized('baseline')==normalized('fixed'), 'Variants discovered different actual cases')
    runtime=None
    for target in ('start','stop'):
        for attempt in (1,2):
            result=lane.filtered(f'baseline-{target}-{attempt}','baseline',target,scope='baseline-'+target,
                                 ledger=ledgers['baseline',target],runtime=runtime)
            runtime=result['runtime']
    lane.filtered('fixed-positive','fixed','positive',scope='positive',ledger=ledgers['fixed','positive'],runtime=runtime)
    lane.filtered('fixed-regular','fixed','regular',scope='fixed',ledger=ledgers['fixed','regular'],runtime=runtime)
    c.require([x['classification'] for x in lane.results]==['EXPECTED_BASELINE_FAILURE']*4+['CALIBRATION_PASS','FIXED_PASS'], 'Incomplete finite protocol')


def allowed_messages():
    # Only literal messages from our frozen assertions can leave the runner.
    # Never echo arbitrary exception args, JSON report text, logs, argv or env.
    result = set()
    for name in ('run.py', 'classify.py', 'audit.py'):
        tree = ast.parse((PAYLOAD/name).read_text())
        for node in ast.walk(tree):
            if isinstance(node, ast.Call) and len(node.args)>1:
                fn = node.func
                if ((isinstance(fn, ast.Name) and fn.id=='require') or
                    (isinstance(fn, ast.Attribute) and fn.attr=='require')):
                    message = node.args[1]
                    if isinstance(message, ast.Constant) and isinstance(message.value, str) and len(message.value)<=200:
                        result.add(message.value)
    return result


def failure_leaves(errors, stage, scope=None, uid=None):
    allowed = allowed_messages()
    rows = []; seen = set(); queue = list(errors)
    while queue and len(rows)<8:
        error = queue.pop(0)
        if id(error) in seen: continue
        seen.add(id(error))
        if isinstance(error, BaseExceptionGroup):
            queue[0:0] = list(error.exceptions)
            continue
        row = {'stage':stage, 'exceptionType':type(error).__name__}
        if scope in ('baseline-start','baseline-stop','positive','fixed'): row['scope']=scope
        if isinstance(uid,str) and len(uid)<=512 and re.fullmatch(r'[A-Za-z0-9_.()+, -]+',uid): row['uid']=uid
        if isinstance(error, owner.CommandFailure):
            outcome=error.outcome
            row.update(exit=outcome['exitCode'],timeout=outcome['timedOut'],
                       cleanupErrorCount=len(outcome['cleanupErrors']),
                       remainingOwnedEmpty=(outcome['remainingOwned']=={} if 'remainingOwned' in outcome else None))
            # No outcome strings/argv/error text are copied to public output.
            queue[0:0] = list(error.causes)
        elif type(error) is ValueError and len(error.args)==1 and isinstance(error.args[0],str) and error.args[0] in allowed:
            row['message']=error.args[0]
            diagnostic=getattr(error,'origin_diagnostics',None)
            if type(diagnostic) is audit.ImportDiagnostics:
                row['importOrigins']=diagnostic.public()
        metadata=getattr(error,'metadata_diagnostics',None)
        if type(metadata) is audit.MetadataDiagnostics:
            row['workloadMetadata']=metadata.public()
        rows.append(row)
    return rows


def exception_graph(error, seen, formatting_errors):
    """Private full cause/context/group graph. Never sent to the CI console."""
    identity=id(error)
    if identity in seen: return {'ref':seen[identity]}
    index=len(seen);seen[identity]=index
    node={'id':index,'type':type(error).__module__+'.'+type(error).__qualname__,
          'suppressContext':bool(error.__suppress_context__)}
    try: node['message']=str(error)
    except BaseException as failure:
        formatting_errors.append(failure);node['messageUnavailable']=True
    try: node['traceback']=''.join(traceback.format_exception(type(error),error,error.__traceback__,chain=False))
    except BaseException as failure:
        formatting_errors.append(failure);node['tracebackUnavailable']=True
    if isinstance(error,BaseExceptionGroup):
        node['exceptions']=[exception_graph(e,seen,formatting_errors) for e in error.exceptions]
    if isinstance(error,owner.CommandFailure):
        node['commandOutcome']=error.outcome
        node['ownedCauses']=[exception_graph(e,seen,formatting_errors) for e in error.causes]
    if error.__cause__ is not None: node['cause']=exception_graph(error.__cause__,seen,formatting_errors)
    if error.__context__ is not None: node['context']=exception_graph(error.__context__,seen,formatting_errors)
    return node


def private_receipt(lane, errors):
    # Append any serialization/I/O failure after original body/cleanup objects.
    # A failure to persist cannot replace the original graph or produce green.
    try:
        formatting_errors=[];seen={}
        graph=[exception_graph(e,seen,formatting_errors) for e in list(errors)]
        errors.extend(formatting_errors)
        graph.extend(exception_graph(e,seen,[]) for e in formatting_errors)
        receipt={'base':BASE,'sourceManifest':SOURCE_SHA,'ciManifest':lane.manifest_sha,
                 'lastStage':lane.stage,'qualifiedScopes':lane.results,
                 'status':'FAILED' if errors else 'PASSED','errors':graph}
        (lane.work/'RESULT.json').write_text(json.dumps(receipt,indent=2)+'\n')
    except BaseException as failure: errors.append(failure)


def finish(lane, errors, writer=print, fallback=os.write):
    private_receipt(lane, errors)
    if not errors:
        try:
            writer(json.dumps({'classification':'FINITE_PROTOCOL_PASS','baselineExpectedFailures':4,
                               'independentPositive':1,'fixedRegularPassed':7,'rawArtifactsUploaded':False}),flush=True)
            return 0
        except BaseException as failure: errors.append(failure)
    try:
        leaves=failure_leaves(errors,lane.stage,lane.active_scope,lane.active_uid)
        writer(json.dumps({'classification':'PIPELINE_FAILED','stage':lane.stage,'leaves':leaves,
                           'originalAndReportingErrors':len(errors),'qualifiedScopes':len(lane.results),
                           'rawArtifactsUploaded':False}),flush=True)
    except BaseException as failure:
        errors.append(failure)
        try:
            # Independent fd fallback contains only fixed vocabulary and counts.
            fallback(2,(json.dumps({'classification':'PIPELINE_FAILED','stage':lane.stage,
                                  'originalAndReportingErrors':len(errors),'diagnosticsUnavailable':True})+'\n').encode())
        except BaseException as second_failure: errors.append(second_failure)
    # Preserve formatter/console/fallback failures alongside the original graph.
    private_receipt(lane,errors)
    return 1


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--baseline',type=Path,required=True);parser.add_argument('--fixed',type=Path,required=True)
    parser.add_argument('--ci-manifest-sha256',required=True)
    args=parser.parse_args()
    c.require(sha(PAYLOAD/'CI.json')==args.ci_manifest_sha256, 'Wrong CI payload manifest')
    manifest=c.read_json(PAYLOAD/'CI.json')
    for name,digest in manifest['files'].items():
        c.require(Path(name).name==name and sha(PAYLOAD/name)==digest, 'Wrong frozen CI code bytes')
    c.require(sha(PAYLOAD/'SOURCE.json')==SOURCE_SHA, 'Wrong reviewed source manifest')
    roots={'baseline':args.baseline.resolve(strict=True),'fixed':args.fixed.resolve(strict=True)}
    c.require(roots['baseline']!=roots['fixed'] and not any(PAYLOAD.is_relative_to(x) for x in roots.values()), 'Source roots overlap')
    owner.enable_subreaper()
    lane=Lane(roots,args.ci_manifest_sha256)
    errors=[]
    try: execute(lane)
    except BaseException as error: errors.append(error)
    finally:
        try: errors.extend(lane.final_guards())
        except BaseException as failure: errors.append(failure)
    return finish(lane,errors)


def guarded_main():
    try: return main()
    except BaseException as original:
        # Preflight failures must not leak an uncaught full traceback either.
        from types import SimpleNamespace
        errors=[original]
        try:
            context=SimpleNamespace(work=Path(tempfile.mkdtemp(prefix='sl971-preflight-',dir='/tmp')),
                                    manifest_sha='unqualified',stage='preflight',results=[],active_scope=None,active_uid=None)
            return finish(context,errors)
        except BaseException as reporting_failure:
            errors.append(reporting_failure)
            try: os.write(2,b'{"classification":"PIPELINE_FAILED","stage":"preflight","diagnosticsUnavailable":true}\n')
            except BaseException as fallback_failure: errors.append(fallback_failure)
            return 1


if __name__=='__main__':
    raise SystemExit(guarded_main())
