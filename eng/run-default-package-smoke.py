#!/usr/bin/env python3
"""Only the finite, untrimmed default PackageSmoke against nine local packages.

Raw logs/receipts remain local to this disposable runner; CI prints stage identity
and bounded failure diagnostics, not raw log/report artifacts. No release/upload.
"""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parents[1]
PRODUCTS = tuple('SharpLink.' + name for name in (
    'Abstractions', 'Client', 'Compression.Zstd', 'GenerationControl', 'Hosting',
    'Runtime', 'Sdk', 'Serializer.SharpPack', 'Server'))
PRODUCTION = PRODUCTS + ('SharpLink.Generator',)
CONSUMERS = tuple('SharpLink.' + name for name in (
    'PackageSmoke', 'SdkOnlyPackageSmoke', 'ReferenceRooting.PackageClient',
    'ReferenceRooting.PackageServer', 'ReferenceRooting.PackageContracts',
    'ReferenceRooting.PackageServices'))
MARKERS = (
    'EXTERNAL_API_BOUNDARIES_PASS resolver+generated-sizing-boundary+lifecycle+inheritance; transport-constructors=compile-only',
    'GENERATION_CONTROL_SERIALIZATION_PASS full+empty+epoch-reset',
    'GENERATION_CONTROL_RPC_PASS transport=TCP epochs=query+duplex cancellation=query+duplex',
    'GENERATION_CONTROL_RPC_PASS transport=SharedMemory epochs=query+duplex cancellation=query+duplex',
    'PACKAGE_REFERENCE_ROOTING_PASS',
)
_spec = importlib.util.spec_from_file_location('smoke_process', ROOT / 'eng/package-smoke-process.py')
_process = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_process)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def git(*args):
    return subprocess.check_output(['git', '-c', 'core.hooksPath=/dev/null', *args],
                                   cwd=ROOT, text=True, timeout=30).strip()


def project(directory, name):
    return directory / name / (name + '.csproj')


def check_graph(root):
    # No solution, extra consumer, or production ProjectReference may enter the lane.
    for directory, names in (('src', PRODUCTION), ('test', CONSUMERS)):
        allowed = {project(root / directory, name).resolve() for name in names}
        for path in allowed:
            tree = ET.parse(path)
            for ref in tree.findall('.//ProjectReference'):
                target = (path.parent / ref.attrib['Include'].replace('\\', '/')).resolve()
                if target not in allowed:
                    raise ValueError('Unexpected project graph edge: ' + str(path) + ' -> ' + str(target))


def environment(root):
    # Do not forward tokens, credentials, or unrelated MSBuild/NuGet overrides.
    keep = ('PATH', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'LANG', 'LC_ALL',
            'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY', 'http_proxy',
            'https_proxy', 'all_proxy', 'no_proxy', 'SSL_CERT_FILE', 'SSL_CERT_DIR')
    env = {key: os.environ[key] for key in keep if key in os.environ}
    env.update(DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1',
               DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', MSBUILDDISABLENODEREUSE='1',
               CI='true', HOME=str(root / 'home'), DOTNET_MULTILEVEL_LOOKUP='0',
               DOTNET_CLI_HOME=str(root / 'cli'),
               NUGET_PACKAGES=str(root / 'packages'), NUGET_HTTP_CACHE_PATH=str(root / 'http'),
               NUGET_PLUGINS_CACHE_PATH=str(root / 'plugins'), TMPDIR=str(root / 'tmp'),
               GIT_CONFIG_GLOBAL='/dev/null', GIT_CONFIG_SYSTEM='/dev/null',
               GIT_ALLOW_PROTOCOL='file', GIT_NO_LAZY_FETCH='1', GIT_TERMINAL_PROMPT='0')
    for name in ('home', 'cli', 'packages', 'http', 'plugins', 'tmp'):
        (root / name).mkdir(parents=True)
    return env


def bounded_diagnostics(logs, stage):
    for suffix in ('stdout', 'stderr'):
        path = logs / (stage + '.' + suffix)
        if not path.exists():
            continue
        lines = path.read_text(errors='replace').splitlines()
        important = [line for line in lines if re.search(
            r'error|exception|failed|permission|cancel|timeout', line, re.I)]
        selected = (important or lines[-3:])[:12]
        for line in selected:
            # Prefix prevents tool output from becoming GitHub workflow commands.
            print('diagnostic: ' + line[:500], flush=True)
        if len(lines) > len(selected):
            print('diagnostic: remaining raw output retained locally, not uploaded', flush=True)


def raise_with_diagnostics(error, logs, stage):
    try:
        bounded_diagnostics(logs, stage)
    except BaseException as reporting_error:
        # Keep both original objects, body first. A broken reporter is not success.
        raise BaseExceptionGroup('Command failure and diagnostic reporting failure',
                                 [error, reporting_error]) from None
    raise error.with_traceback(error.__traceback__)


def report_failure(error):
    """Best-effort concise console output without replacing any prior failure."""
    def summaries(failure):
        if isinstance(failure, BaseExceptionGroup):
            return [line for child in failure.exceptions for line in summaries(child)]
        if isinstance(failure, _process.CommandFailure):
            outcome = failure.outcome
            result = ['CommandFailure: stage=' + outcome['stage'] +
                      ' exit=' + str(outcome['exitCode']) +
                      ' timeout=' + str(outcome['timedOut']) +
                      ' remainingOwned=' + repr(outcome.get('remainingOwned'))[:300]]
            for category in ('errors', 'cleanupErrors', 'reportingErrors'):
                entries = outcome[category]
                if entries:
                    result.append(category + ' count=' + str(len(entries)) +
                                  ' first=' + str(entries[0])[:600])
            return result
        return [type(failure).__name__ + ': ' + str(failure)[:1200]]

    lines = summaries(error)
    reporting_errors = []
    for line in lines[:8]:
        try:
            print('DEFAULT_PACKAGE_SMOKE_FAIL ' + line, flush=True)
        except BaseException as reporting_error:
            reporting_errors.append(reporting_error)
    if reporting_errors:
        retained = BaseExceptionGroup('Original failure and console reporting failures',
                                      [error, *reporting_errors])
        # Separate stderr descriptor fallback; still exit nonzero if it also fails.
        try:
            text = '\n'.join('failure: ' + line for line in summaries(retained)[:10]) + '\n'
            os.write(2, text.encode('utf-8', errors='replace'))
        except BaseException as fallback_error:
            return BaseExceptionGroup('Original failure and all reporting failures',
                                      [retained, fallback_error])
        return retained
    return error


def sdk_identity(run, root, env, logs):
    executable = shutil.which('dotnet', path=env['PATH'])
    if executable is None:
        raise RuntimeError('Selected environment has no dotnet executable')
    executable = Path(executable).resolve(strict=True)
    version_output, _ = run('sdk-version', [executable, '--version'], root, env, 30)
    info_output, _ = run('sdk-info', [executable, '--info'], root, env, 30)
    runtime_output, _ = run('runtime-inventory', [executable, '--list-runtimes'], root, env, 30)
    version = version_output.read_text().strip()
    if re.fullmatch(r'\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?', version) is None:
        raise RuntimeError('Could not read actual selected SDK version')
    info = info_output.read_text()
    host = re.search(r'^Host:\s*\n\s+Version:\s+(\S+)', info, re.MULTILINE)
    base_path = re.search(r'^\s*Base Path:\s+(.+)$', info, re.MULTILINE)
    if host is None or base_path is None:
        raise RuntimeError('Could not read actual SDK base path and dotnet host identity')
    runtimes = []
    for line in runtime_output.read_text().splitlines():
        match = re.fullmatch(r'(\S+) (\S+) \[(.+)\]', line)
        if match is None:
            raise RuntimeError('Unrecognized installed runtime inventory entry')
        runtimes.append(dict(name=match[1], version=match[2], path=match[3]))
    identity = dict(executable=str(executable), executableSha256=digest(executable),
                    selectedSdkVersion=version, sdkBasePath=base_path[1].strip(),
                    hostVersion=host[1], installedRuntimes=runtimes,
                    qualification='Observed CLI selection/host/inventory, not proof of app runtime selection')
    (logs / 'sdk-identity.json').write_text(json.dumps(identity, indent=2) + '\n')
    print('sdk actual=' + version + ' host=' + host[1] + ' path=' + str(executable) +
          ' sha256=' + identity['executableSha256'], flush=True)
    print('runtimes installed=' + ','.join(item['name'] + '/' + item['version'] for item in runtimes), flush=True)
    return identity


def verify_consumed(root, cache, feed, version):
    paths = list(root.rglob('project.assets.json'))
    expected = {project(root / 'test', name).parent / 'obj/project.assets.json' for name in CONSUMERS}
    if set(paths) != expected:
        raise ValueError('Consumer assets escaped the exact six-project graph')
    consumed = set()
    for path in paths:
        assets = json.loads(path.read_text())
        for identity, library in assets['libraries'].items():
            name, resolved_version = identity.split('/')
            if library['type'] == 'project':
                actual = (path.parent.parent / library['msbuildProject'].replace('\\', '/')).resolve()
                if actual not in {project(root / 'test', item).resolve() for item in CONSUMERS}:
                    raise ValueError('Non-consumer project in actual assets: ' + identity)
            if library['type'] != 'package' or not name.startswith('SharpLink.'):
                continue
            if name not in PRODUCTS or resolved_version != version:
                raise ValueError('Non-candidate SharpLink package: ' + identity)
            archive = cache / library['path'] / (identity.replace('/', '.').lower() + '.nupkg')
            if digest(archive) != digest(feed / (name + '.' + version + '.nupkg')):
                raise ValueError('Consumed archive differs from local candidate: ' + identity)
            consumed.add(name)
    if consumed != set(PRODUCTS) - {'SharpLink.Hosting'}:
        raise ValueError('Expected exactly eight local product packages in consumer assets')
    if any((root / 'src').rglob('bin')) or any((root / 'src').rglob('obj')):
        raise ValueError('Production build output leaked into consumer root')


def verify_runtime_dlls(root, feed, version):
    output = root / 'test/SharpLink.PackageSmoke/bin/Release/net10.0'
    for name in set(PRODUCTS) - {'SharpLink.Hosting'}:
        with ZipFile(feed / (name + '.' + version + '.nupkg')) as archive:
            if (output / (name + '.dll')).read_bytes() != archive.read('lib/net10.0/' + name + '.dll'):
                raise ValueError('Runtime DLL does not match candidate archive: ' + name)


def main():
    if sys.platform != 'linux':
        raise RuntimeError('This validation requires the selected Linux runner')
    if git('status', '--porcelain'):
        raise RuntimeError('Start from a clean committed checkout')
    commit, tree = git('rev-parse', 'HEAD'), git('rev-parse', 'HEAD^{tree}')
    if os.environ.get('GITHUB_SHA', commit) != commit:
        raise RuntimeError('Checkout is not the triggering source commit')
    check_graph(ROOT)
    props = ET.parse(ROOT / 'Directory.Build.props')
    version = props.findtext('.//VersionPrefix')
    if version != '3.0.0':
        raise ValueError('This narrowly reviewed lane expects package version 3.0.0')
    _process.enable_subreaper()
    work = Path(tempfile.mkdtemp(prefix='sharplink-default-smoke-', dir=os.environ.get('RUNNER_TEMP')))
    logs = work / 'logs'
    logs.mkdir()
    producer, consumer = work / 'producer', work / 'consumer'
    penv, cenv = environment(work / 'producer-state'), environment(work / 'consumer-state')
    print('source commit=' + commit + ' tree=' + tree, flush=True)
    deadline = time.monotonic() + 25 * 60

    def run(stage, command, cwd=producer, env=penv, seconds=420):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError('Overall finite package validation budget exhausted before ' + stage)
        print('stage ' + stage, flush=True)
        try:
            return _process.run_owned(stage, [str(arg) for arg in command], cwd, env, logs, min(seconds, remaining))
        except BaseException as error:
            raise_with_diagnostics(error, logs, stage)

    for name, root, env in (('producer', producer, penv), ('consumer', consumer, cenv)):
        run(name + '-clone', ['git', '-c', 'core.hooksPath=/dev/null', 'clone', '--no-local',
            '--depth', '1', '--no-checkout', ROOT, root], work, env, 60)
        run(name + '-checkout', ['git', '-c', 'core.hooksPath=/dev/null', 'checkout', '--detach', commit], root, env, 30)
        check_graph(root)
    sdk_identity(run, producer, penv, logs)
    config = work / 'NuGet.Config'
    config.write_text('<configuration><packageSources><clear/><add key="nuget.org" '
                      'value="https://api.nuget.org/v3/index.json" protocolVersion="3"/>'
                      '</packageSources></configuration>\n')
    serial = ['-m:1', '-nr:false']
    for name in PRODUCTION:
        target = project(Path('src'), name)
        run('restore-' + name, ['dotnet', 'restore', target, '--configfile', config,
            '--disable-parallel', '--verbosity', 'minimal', '-p:Configuration=Release',
            '-p:RestoreIgnoreFailedSources=false', *serial])
    for name in PRODUCTION:
        run('build-' + name, ['dotnet', 'build', project(Path('src'), name), '--no-restore',
            '-c', 'Release', '-v', 'minimal', '-p:UseSharedCompilation=false', *serial])
    feed = producer / 'artifacts/nuget'
    feed.mkdir(parents=True)
    for name in PRODUCTS:
        run('pack-' + name, ['dotnet', 'pack', project(Path('src'), name), '--no-build',
            '--no-restore', '-c', 'Release', '-o', feed, '-v', 'minimal', *serial])
    expected = {name + '.' + version + '.' + ext for name in PRODUCTS for ext in ('nupkg', 'snupkg')}
    if {path.name for path in feed.iterdir()} != expected:
        raise ValueError('Expected exactly nine local package/symbol pairs')
    run('verify-packages', ['bash', 'eng/verify-packages.sh', feed, version], seconds=60)
    pins = {path.name: digest(path) for path in sorted(feed.iterdir())}
    print('packages count=9 pairs=9 sha256=' + hashlib.sha256(json.dumps(pins, sort_keys=True).encode()).hexdigest(), flush=True)
    cfeed = consumer / 'artifacts/nuget'
    shutil.copytree(feed, cfeed)
    target = project(Path('test'), 'SharpLink.PackageSmoke')
    run('consumer-restore', ['dotnet', 'restore', target, '--configfile',
        'test/SharpLink.PackageSmoke/NuGet.config', '--disable-parallel', '-v', 'minimal',
        '-p:Configuration=Release', '-p:RestoreIgnoreFailedSources=false', *serial], consumer, cenv)
    verify_consumed(consumer, Path(cenv['NUGET_PACKAGES']), cfeed, version)
    run('consumer-build', ['dotnet', 'build', target, '--no-restore', '-c', 'Release',
        '-v', 'minimal', '-p:UseSharedCompilation=false', *serial], consumer, cenv)
    verify_runtime_dlls(consumer, cfeed, version)
    output, errors = run('default-package-smoke', ['dotnet',
        consumer / 'test/SharpLink.PackageSmoke/bin/Release/net10.0/SharpLink.PackageSmoke.dll'], consumer, cenv, 120)
    if output.read_text().splitlines() != list(MARKERS) or errors.read_bytes():
        error = RuntimeError('Default smoke requires five exact ordered markers and empty stderr')
        raise_with_diagnostics(error, logs, 'default-package-smoke')
    if {path.name: digest(path) for path in cfeed.iterdir()} != pins:
        raise RuntimeError('Candidate feed changed during consumer execution')
    print('DEFAULT_PACKAGE_SMOKE_PASS markers=5 exit=0 owned_remaining=0', flush=True)


if __name__ == '__main__':
    try:
        main()
    except BaseException as error:
        retained_failure = report_failure(error)
        sys.exit(1)
