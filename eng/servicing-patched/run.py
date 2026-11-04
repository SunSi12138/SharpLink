#!/usr/bin/env python3
"""Patched-only production codec servicing validator. No raw evidence leaves temp storage."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import sys
import tempfile
import xml.etree.ElementTree as ET

SOURCE = 'ba91b050cb7ea0c52c808f2e905133b184649d3b'
BASE_SDK = '10.0.114'
BASE_RUNTIME = '10.0.14'
HELPER_HASH = 'ba9f6ae43784ac752acdbfee1af60f33361ba76398a558948da721994449dda3'
SUMMARY_HASH = 'a64b4c1ade8baeb367927723653d6ff44fcf5a3440d8b145bc79759df4714053'
SOURCE_TREE = 'ad047ee3f21436af428fe5695649c7e1af22b720'
CODEC_TREE = 'b3f0831484762e74a11b152485bbbd97df62720f'
POLICY_BLOB = '889885faedf4102b9c98113a7c3df8088ed11838'
PROJECT = 'test/SharpLink.CodecCompatibility/SharpLink.CodecCompatibility.csproj'
LANES = ('baseline', 'latest')
HERE = Path(__file__).resolve().parent

FAILURE = {'stage': 'preflight'}
ALLOWED_STAGES = {'preflight', 'metadata-baseline', 'metadata-latest', 'custody', 'fixture-policy',
                  'copy-baseline', 'copy-latest', 'manifest-baseline', 'manifest-latest',
                  'final-custody', 'evidence', 'summary', 'source-head', 'source-status', 'source-files',
                  'source-src-tree', 'source-codec-tree', 'source-final-status', 'tool-head', 'tool-status', 'tool-final-status'}
ALLOWED_STAGES.update(lane + '-' + suffix for lane in LANES for suffix in ('sdk', 'build', 'produce'))
ALLOWED_STAGES.update(consumer + '-from-' + producer for consumer in LANES for producer in LANES)


def stage(name):
    require(name in ALLOWED_STAGES)
    FAILURE.clear()
    FAILURE['stage'] = name


def command_failure(error, logs, label):
    outcome = getattr(error, 'outcome', {})
    code = outcome.get('exitCode')
    FAILURE.update(exitCode=code if type(code) is int and -255 <= code <= 255 else None,
                   timedOut=outcome.get('timedOut') is True)
    for key in ('errors', 'cleanupErrors', 'reportingErrors', 'remainingOwned'):
        FAILURE[key + 'Count'] = min(len(outcome.get(key) or []), 10000)
    codes = set()
    if label not in {'baseline-build', 'latest-build'}:
        FAILURE['diagnosticCodes'] = []
        return
    for suffix in ('.stdout', '.stderr'):
        try:
            with (logs / (label + suffix)).open('rb') as stream:
                data = stream.read(4 * 1024 * 1024).decode('utf-8', errors='replace')
            codes.update(re.findall(r'\b(?:CS|NU|NETSDK|MSB)[0-9]{3,5}\b', data))
        except OSError:
            pass
    FAILURE['diagnosticCodes'] = sorted(codes)[:16]


def require(condition):
    if not condition:
        raise ValueError('validation gate failed')


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_module(name, path, expected):
    require(sha(path) == expected)
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def version(value):
    require(isinstance(value, str) and re.fullmatch(r'10\.0\.\d{1,5}', value) is not None)
    return tuple(map(int, value.split('.')))


def sdk_identity(root, baseline=False):
    # Filesystem metadata checks precede every first dotnet execution. No old SDK execution.
    require(root.is_dir() and (root / 'dotnet').is_file())
    sdks = [p.name for p in (root / 'sdk').iterdir() if p.is_dir()]
    require(len(sdks) == 1)
    sdk = sdks[0]
    require(version(sdk) >= version(BASE_SDK))
    if baseline:
        require(sdk == BASE_SDK)
    props = ET.parse(root / 'sdk' / sdk / 'Microsoft.NETCoreSdk.BundledVersions.props')
    values = {node.text for node in props.iter() if node.tag.split('}')[-1] == 'BundledNETCoreAppPackageVersion'}
    require(len(values) == 1)
    runtime = values.pop()
    require(version(runtime) >= version(BASE_RUNTIME))
    if baseline:
        require(runtime == BASE_RUNTIME)
    require((root / 'shared/Microsoft.NETCore.App' / runtime / 'libcoreclr.so').is_file())
    config = json.loads((root / 'sdk' / sdk / 'dotnet.runtimeconfig.json').read_text())
    framework = config['runtimeOptions']['framework']
    require(framework['name'] == 'Microsoft.NETCore.App' and framework['version'] == runtime)
    # Reject an installation containing an unpatched runtime even if selection would avoid it.
    for path in (root / 'shared/Microsoft.NETCore.App').iterdir():
        require(version(path.name) >= version(BASE_RUNTIME))
    return sdk, runtime


def clean_env(home, root=None, sdk=None):
    home.mkdir(parents=True, exist_ok=True)
    tmp = home / 'tmp'
    tmp.mkdir(exist_ok=True)
    env = {'PATH': '/usr/bin:/bin', 'HOME': str(home), 'TMPDIR': str(tmp),
           'LANG': 'C.UTF-8', 'LC_ALL': 'C.UTF-8', 'DOTNET_CLI_HOME': str(home),
           'NUGET_PACKAGES': str(home / 'packages'), 'NUGET_HTTP_CACHE_PATH': str(home / 'http'),
           'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1',
           'DOTNET_NOLOGO': '1', 'DOTNET_MULTILEVEL_LOOKUP': '0',
           'DOTNET_ROLL_FORWARD': 'Disable', 'DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER': '1',
           'MSBUILDDISABLENODEREUSE': '1', 'DOTNET_CLI_USE_MSBUILD_SERVER': '0',
           'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE': 'true',
           'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': '/dev/null',
           'SHARPLINK_COMMIT': SOURCE}
    if root:
        env.update(DOTNET_ROOT=str(root), DOTNET_ROOT_X64=str(root), SHARPLINK_SDK_VERSION=sdk)
    return env


def pin_copy(source, destination, files, sdk):
    for relative in files:
        path = Path(relative)
        require(not path.is_absolute() and '..' not in path.parts)
        original = source / path
        require(original.is_file() and not original.is_symlink())
        target = destination / path
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(original, target)
    global_path = destination / 'global.json'
    value = json.loads(global_path.read_text())
    value['sdk'].update(version=sdk, rollForward='disable')
    require(value['sdk'].get('allowPrerelease') is False)
    global_path.write_text(json.dumps(value, indent=2) + '\n')
    for relative in files:
        if relative != 'global.json':
            require(sha(source / relative) == sha(destination / relative))


def fixture_policy(source):
    data = (source / 'test/SharpLink.CodecCompatibility/CompatibilityPolicy.cs').read_bytes()
    require(hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest() == POLICY_BLOB)
    entries = re.findall(r'new\("([^"]+)", "([^"]+)", (true|false), (true|false)\)', data.decode())
    require(len(entries) == 58 and len({x[0] for x in entries}) == 58)
    return {key: (category, native == 'true', segmented == 'true') for key, category, native, segmented in entries}


def guard_manifest(manifest, identity, policy):
    require(manifest['sharpLinkCommit'] == SOURCE)
    require((manifest['sdkVersion'], manifest['runtimeVersion']) == identity)
    require(version(manifest['runtimeVersion']) >= version(BASE_RUNTIME))
    cases = manifest['cases']
    registry = manifest['fixtureRegistry']
    require(len(cases) == len(registry) == len(policy))
    require({x['id'] for x in cases} == {x['id'] for x in registry} == set(policy))
    for item in registry:
        category, native, _ = policy[item['id']]
        require(item['category'] == category and item['nativeWidth'] is native)
    for item in cases:
        category, _, segmented = policy[item['id']]
        require(item['category'] == category and (item['size'] > 1) == segmented)
        wire = Path(item['wireFile'])
        require(not wire.is_absolute() and '..' not in wire.parts)


def validate_evidence(evidence, identities, policy, summary):
    manifests = {}
    for lane in LANES:
        path = evidence / 'producers' / lane / 'manifest.json'
        manifest = json.loads(path.read_text())
        guard_manifest(manifest, identities[lane], policy)
        manifests[lane] = summary.validate_manifest(path, lane)
    summary.validate_non_servicing_identity(manifests['baseline'], manifests['latest'])
    require(version(identities['latest'][0]) >= version(identities['baseline'][0]))
    require(version(identities['latest'][1]) >= version(identities['baseline'][1]))
    edges = []
    for consumer in LANES:
        for producer in LANES:
            path = evidence / 'verifications' / consumer / ('from-' + producer) / 'verification.json'
            edge = summary.validate_report(path, manifests[producer], manifests[consumer], producer, consumer)
            rows = json.loads(path.read_text())['results']
            classifications = {key: sum(row['classification'] == key for row in rows) for key in
                ('IDENTICAL_BYTES_AND_COMPATIBLE', 'DIFFERENT_BYTES_BUT_CROSS_COMPATIBLE')}
            edges.append({'producer': producer, 'consumer': consumer, 'resultCount': edge['resultCount'],
                          'blockingFailures': edge['blockingFailures'], 'classifications': classifications})
    return {'status': 'pass', 'sourceCommit': SOURCE, 'summarySha256': SUMMARY_HASH,
            'processHelperSha256': HELPER_HASH, 'validatorSha256': sha(HERE / 'run.py'),
            'baseline': dict(zip(('sdk', 'runtime'), identities['baseline'])),
            'latest': dict(zip(('sdk', 'runtime'), identities['latest'])),
            'distinctSdkVersion': identities['baseline'][0] != identities['latest'][0],
            'distinctRuntimeVersion': identities['baseline'][1] != identities['latest'][1],
            'fixtureCount': len(policy), 'edgeCount': len(edges), 'blockingFailures': 0, 'edges': edges,
            'sourceSrcTree': SOURCE_TREE, 'sourceCodecTree': CODEC_TREE,
            'resultCount': sum(e['resultCount'] for e in edges),
            'classification': 'PATCHED_SDK_SERVICING_COMPATIBLE'}


def execute(args):
    helper = load_module('owner', HERE / 'package-smoke-process.py', HELPER_HASH)
    summary = load_module('summary', HERE / 'summarize-codec-servicing-evidence.py', SUMMARY_HASH)
    helper.enable_subreaper()
    roots = {'baseline': args.baseline_root.resolve(), 'latest': args.latest_root.resolve()}
    require(roots['baseline'] != roots['latest'])
    identities = {}
    for lane in LANES:
        stage('metadata-' + lane)
        identities[lane] = sdk_identity(roots[lane], lane == 'baseline')
    source = args.source.resolve()
    with tempfile.TemporaryDirectory(prefix='codec-servicing-') as temporary:
        work = Path(temporary)
        logs = work / 'logs'
        logs.mkdir(mode=0o700)
        env = clean_env(work / 'git-home')
        def command(label, argv, cwd, environ=env, seconds=60):
            stage(label)
            try:
                return helper.run_owned(label, [str(x) for x in argv], cwd, environ, logs, seconds)[0]
            except BaseException as error:
                command_failure(error, logs, label)
                raise
        stage('custody')
        require(re.fullmatch(r'[0-9a-f]{40}', args.tool_commit) is not None)
        tool_root = args.tools_root.resolve()
        require(HERE == tool_root / 'eng/servicing-patched')
        require(command('tool-head', ['/usr/bin/git', 'rev-parse', 'HEAD'], tool_root).read_text().strip() == args.tool_commit)
        require(not command('tool-status', ['/usr/bin/git', 'status', '--porcelain', '--untracked-files=all'], tool_root).read_text().strip())
        require(command('source-src-tree', ['/usr/bin/git', 'rev-parse', 'HEAD:src'], source).read_text().strip() == SOURCE_TREE)
        require(command('source-codec-tree', ['/usr/bin/git', 'rev-parse', 'HEAD:test/SharpLink.CodecCompatibility'], source).read_text().strip() == CODEC_TREE)
        require(command('source-head', ['/usr/bin/git', 'rev-parse', 'HEAD'], source).read_text().strip() == SOURCE)
        require(not command('source-status', ['/usr/bin/git', 'status', '--porcelain', '--untracked-files=all'], source).read_text().strip())
        files = command('source-files', ['/usr/bin/git', 'ls-files', '-z'], source).read_bytes().decode().rstrip('\0').split('\0')
        stage('fixture-policy')
        policy = fixture_policy(source)
        require(sha(source / 'eng/summarize-codec-servicing-evidence.py') == SUMMARY_HASH)
        evidence = work / 'evidence'
        for lane in LANES:
            sdk, runtime = identities[lane]
            lane_source = work / lane
            stage('copy-' + lane)
            pin_copy(source, lane_source, files, sdk)
            lane_env = clean_env(work / (lane + '-home'), roots[lane], sdk)
            dotnet = roots[lane] / 'dotnet'
            require(command(lane + '-sdk', [dotnet, '--version'], lane_source, lane_env).read_text().strip() == sdk)
            command(lane + '-build', [dotnet, 'build', PROJECT, '-c', 'Release', '--disable-build-servers',
                    '-p:UseSharedCompilation=false', '-nodeReuse:false', '-maxcpucount:1',
                    '-p:RuntimeFrameworkVersion=' + runtime, '-p:NuGetAudit=true'], lane_source, lane_env, 600)
            assembly = lane_source / 'test/SharpLink.CodecCompatibility/bin/Release/net10.0/SharpLink.CodecCompatibility.dll'
            output = evidence / 'producers' / lane
            command(lane + '-produce', [dotnet, 'exec', '--fx-version', runtime, assembly, 'produce', '--output', output], lane_source, lane_env, 120)
            stage('manifest-' + lane)
            guard_manifest(json.loads((output / 'manifest.json').read_text()), identities[lane], policy)
        for consumer in LANES:
            sdk, runtime = identities[consumer]
            lane_source = work / consumer
            env = clean_env(work / (consumer + '-home'), roots[consumer], sdk)
            assembly = lane_source / 'test/SharpLink.CodecCompatibility/bin/Release/net10.0/SharpLink.CodecCompatibility.dll'
            for producer in LANES:
                output = evidence / 'verifications' / consumer / ('from-' + producer) / 'verification.json'
                output.parent.mkdir(parents=True)
                command(consumer + '-from-' + producer, [roots[consumer] / 'dotnet', 'exec', '--fx-version', runtime, assembly,
                        'verify', '--input', evidence / 'producers' / producer, '--output', output], lane_source, env, 120)
        stage('final-custody')
        for lane in LANES:
            for relative in files:
                if relative != 'global.json':
                    require(sha(source / relative) == sha(work / lane / relative))
        require(not command('source-final-status', ['/usr/bin/git', 'status', '--porcelain', '--untracked-files=all'], source).read_text().strip())
        require(not command('tool-final-status', ['/usr/bin/git', 'status', '--porcelain', '--untracked-files=all'], tool_root).read_text().strip())
        stage('evidence')
        result = validate_evidence(evidence, identities, policy, summary)
        result['toolCommit'] = args.tool_commit
        # Also run unchanged original entrypoint, with the patched baseline global.json.
        command('summary', [sys.executable, HERE / 'summarize-codec-servicing-evidence.py', '--input', evidence,
                '--output', work / 'summary', '--global-json', work / 'baseline/global.json'], work, clean_env(work / 'summary-home'))
        return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--tool-commit', required=True)
    parser.add_argument('--tools-root', required=True, type=Path)
    parser.add_argument('--source', required=True, type=Path)
    parser.add_argument('--baseline-root', required=True, type=Path)
    parser.add_argument('--latest-root', required=True, type=Path)
    args = parser.parse_args()
    try:
        result = execute(args)
    except BaseException as error:
        category = type(error).__name__
        if category not in {'ValueError', 'KeyError', 'TypeError', 'FileNotFoundError', 'PermissionError', 'CommandFailure', 'InterruptedError', 'OSError'}:
            category = 'OtherError'
        # No exception text, paths, commands, logs, fixture contents, or corpora are public.
        print(json.dumps({'status': 'fail', 'sourceCommit': SOURCE, 'classification': 'VALIDATION_FAILED', 'errorCategory': category, **FAILURE}, sort_keys=True))
        return 1
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
