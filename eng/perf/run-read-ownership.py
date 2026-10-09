#!/usr/bin/env python3
"""Same-run read-ownership evidence, with exact revisions and six balanced orders."""
import argparse
import hashlib
import itertools
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import traceback

TRANSPORT = 'src/SharpLink.Runtime/Transport/TransportConnection.cs'
MICRO = 'eng/perf/read-ownership-micro'
ARMS = ('baseline', 'prior-harmonized', 'candidate')
SCENARIOS = (
    ('tcp-add-c1', 'SharpLink.LoadTest', 'tcp', 'add', 1),
    ('tcp-add-c32', 'SharpLink.LoadTest', 'tcp', 'add', 32),
    ('tcp-add-c128', 'SharpLink.LoadTest', 'tcp', 'add', 128),
    ('shm-add-c1', 'SharpLink.LoadTest', 'sharedmemory', 'add', 1),
    ('shm-add-c32', 'SharpLink.LoadTest', 'sharedmemory', 'add', 32),
    ('tcp-duplex256-c1', 'SharpLink.StreamLoadTest', 'tcp', 'duplex', 1),
)


def run(command, cwd, output=None, env=None):
    print('+', json.dumps([str(x) for x in command]), flush=True)
    if output:
        with open(output, 'w') as stream:
            subprocess.run(command, cwd=cwd, env=env, stdout=stream,
                           stderr=subprocess.STDOUT, check=True, timeout=300)
    else:
        subprocess.run(command, cwd=cwd, env=env, check=True, timeout=300)


def git(root, *args):
    return subprocess.check_output(['git', *args], cwd=root, text=True).strip()


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def replace_reader(current, prior):
    # Stable adjacent class anchors avoid copying old transport/disposal changes.
    start = 'internal sealed class ReadOwnershipPipeReader'
    end = '\ninternal sealed class AnonymousPipeTransportConnection'
    if any(text.count(start) != 1 or text.count(end) != 1 for text in (current, prior)):
        raise ValueError('ReadOwnershipPipeReader class anchors are not unique')
    left, right = current.index(start), current.index(end)
    old_left, old_right = prior.index(start), prior.index(end)
    return current[:left] + prior[old_left:old_right] + current[right:]


def load_manifest(path):
    manifest = json.loads(path.read_text())
    for key in ('baseline', 'candidate', 'prior'):
        if not re.fullmatch('[0-9a-f]{40}', manifest[key]):
            raise ValueError(f'{key} must be an immutable full commit SHA')
    if type(manifest.get('microIterations')) is not int or manifest['microIterations'] < 1000:
        raise ValueError('microIterations must be an integer >= 1000')
    if manifest['rounds'] < 6 or manifest['rounds'] % 6:
        raise ValueError('Three-arm rounds must be a positive multiple of all six permutations')
    if manifest['warmupSeconds'] < 2 or manifest['durationSeconds'] < 6:
        raise ValueError('Formal runs require at least 2s warmup and 6s measurement')
    return manifest


def prepare(root, output, work, manifest):
    output.mkdir(parents=True, exist_ok=True)
    work.mkdir(parents=True, exist_ok=True)
    for revision in ('baseline', 'candidate', 'prior'):
        run(['git', 'cat-file', '-e', manifest[revision] + '^{commit}'], root)
    run(['git', 'merge-base', '--is-ancestor', manifest['baseline'], manifest['candidate']], root)
    production_changes = git(root, 'diff', '--name-only', manifest['baseline'],
                             manifest['candidate'], '--', 'src').splitlines()
    if production_changes != [TRANSPORT]:
        raise ValueError(f'Unexpected production differences: {production_changes}')
    # All load-test and dependency/build inputs must be identical between the exact arms.
    same_paths = ['test/SharpLink.LoadTest', 'test/SharpLink.StreamLoadTest',
                  'test/SharpLink.LoadTestBase', 'global.json', 'Directory.Build.props',
                  'Directory.Build.targets', 'Directory.Packages.props', 'NuGet.Config']
    changed = git(root, 'diff', '--name-only', manifest['baseline'], manifest['candidate']).splitlines()
    same_paths += [path for path in changed if Path(path).suffix in ('.props', '.targets', '.csproj')
                   or Path(path).name.lower() in ('global.json', 'nuget.config', 'packages.lock.json')]
    for path in same_paths:
        if git(root, 'diff', '--name-only', manifest['baseline'], manifest['candidate'], '--', path):
            raise ValueError(f'Baseline/candidate harness or build input differs: {path}')
    prior = git(root, 'show', manifest['prior'] + ':' + TRANSPORT) + '\n'
    provenance = {'manifest': manifest, 'harnessCommit': git(root, 'rev-parse', 'HEAD'),
                  'arms': {}, 'orders': list(itertools.permutations(ARMS)),
                  'scenarioDefinitions': SCENARIOS}
    for arm in ARMS:
        revision = manifest['baseline' if arm == 'baseline' else 'candidate']
        tree = work / arm
        run(['git', 'worktree', 'add', '--detach', str(tree), revision], root)
        if arm == 'prior-harmonized':
            target = tree / TRANSPORT
            target.write_text(replace_reader(target.read_text(), prior))
        shutil.copytree(root / MICRO, tree / MICRO,
                        ignore=shutil.ignore_patterns('bin', 'obj'))
        provenance['arms'][arm] = {
            'checkoutCommit': revision,
            'identity': revision if arm != 'prior-harmonized' else
                        f"{revision}+ReadOwnershipPipeReader-from-{manifest['prior']}",
            'readerFileSha256': digest(tree / TRANSPORT),
            'readerOrigin': manifest['prior'] if arm == 'prior-harmonized' else revision,
            'synthetic': arm == 'prior-harmonized',
        }
        (output / f'{arm}-source-diff.patch').write_text(git(tree, 'diff', '--', TRANSPORT) + '\n')
        for project in ('test/SharpLink.LoadTest/SharpLink.LoadTest.csproj',
                        'test/SharpLink.StreamLoadTest/SharpLink.StreamLoadTest.csproj',
                        MICRO + '/ReadOwnershipMicro.csproj'):
            name = Path(project).stem
            run(['dotnet', 'build', project, '-c', 'Release', '-v', 'minimal',
                 '-m:1', '-nr:false', '-p:UseSharedCompilation=false'], tree,
                output / f'{arm}-build-{name}.log')
        provenance['arms'][arm]['binarySha256'] = {
            str(p.relative_to(tree)): digest(p) for p in (
                tree / 'src/SharpLink.Runtime/bin/Release/net10.0/SharpLink.Runtime.dll',
                tree / 'test/SharpLink.LoadTest/bin/Release/net10.0/SharpLink.LoadTest.dll',
                tree / 'test/SharpLink.StreamLoadTest/bin/Release/net10.0/SharpLink.StreamLoadTest.dll',
                tree / MICRO / 'bin/Release/net10.0/SharpLink.Benchmarks.dll')}
        runtime_hash = digest(tree / 'src/SharpLink.Runtime/bin/Release/net10.0/SharpLink.Runtime.dll')
        for folder in ('test/SharpLink.LoadTest', 'test/SharpLink.StreamLoadTest', MICRO):
            copied_runtime = tree / folder / 'bin/Release/net10.0/SharpLink.Runtime.dll'
            if digest(copied_runtime) != runtime_hash:
                raise RuntimeError(f'Executed runtime DLL differs from built runtime: {copied_runtime}')
            provenance['arms'][arm]['binarySha256'][str(copied_runtime.relative_to(tree))] = runtime_hash
    (output / 'provenance.json').write_text(json.dumps(provenance, indent=2) + '\n')
    return provenance


def measure(root, output, work, manifest, provenance):
    if os.environ.get('GITHUB_ACTIONS') != 'true' or os.environ.get('RUNNER_ENVIRONMENT') != 'github-hosted':
        raise RuntimeError('Formal measurement is restricted to a GitHub-hosted runner')
    processes = subprocess.check_output(['ps', '-eo', 'pid,comm,args'], text=True)
    (output / 'processes-before-measurement.txt').write_text(processes)
    if re.search(r'SharpLink\.(?:LoadTest|StreamLoadTest|Chaos|Benchmarks)(?:\.dll)?(?: |$)', processes):
        raise RuntimeError('Competing SharpLink measurement/diagnostic process; invalidate the whole batch')
    raw = output / 'raw'
    raw.mkdir()
    orders = list(itertools.permutations(ARMS))
    index = []
    # Per-scenario six permutations put every arm in every position twice, with
    # each pair before/after three times. No sample filtering or selective retry.
    for scenario in (('micro', None, None, None, None), *SCENARIOS):
        name, project, transport, operation, concurrency = scenario
        for round_number in range(manifest['rounds']):
            for position, arm in enumerate(orders[round_number % 6]):
                identity = provenance['arms'][arm]['identity']
                env = dict(os.environ, SHARPLINK_COMMIT=identity)
                tree = work / arm
                relative = f'raw/{name}-r{round_number + 1}-{position + 1}-{arm}.json'
                result = output / relative
                if project is None:
                    env['DOTNET_TieredCompilation'] = '0'
                    command = ['dotnet', str(tree / MICRO / 'bin/Release/net10.0/SharpLink.Benchmarks.dll'),
                               arm, str(result), str(manifest['microIterations'])]
                else:
                    command = ['dotnet', str(tree / f'test/{project}/bin/Release/net10.0/{project}.dll'),
                               '--mode', 'local', '--transport', transport, '--operation', operation,
                               '--concurrency', str(concurrency), '--warmup', str(manifest['warmupSeconds']),
                               '--duration', str(manifest['durationSeconds']), '--profile', 'balanced',
                               '--min-connections', '1', '--max-connections', '1',
                               '--max-send-queue-bytes', '67108864', '--recording', 'formal',
                               '--maximum-recorded-operations', '30000000', '--json-output', str(result)]
                    command += ['--metrics-port', '0'] if project == 'SharpLink.LoadTest' else ['--stream-size', '256']
                entry = {'scenario': name, 'round': round_number + 1, 'position': position + 1,
                         'arm': arm, 'identity': identity, 'file': relative, 'command': command,
                         'startedUtc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}
                index.append(entry)
                (output / 'execution-index.json').write_text(json.dumps(index, indent=2) + '\n')
                run(command, tree, result.with_suffix('.log'), env)
                if not result.is_file():
                    raise RuntimeError(f'Missing result: {result}')
                entry['sha256'] = digest(result)
                entry['completedUtc'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
                (output / 'execution-index.json').write_text(json.dumps(index, indent=2) + '\n')
                time.sleep(1)
    run(['python3', str(root / 'eng/perf/summarize-read-ownership.py'), str(output)], root)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--work', type=Path, required=True)
    parser.add_argument('--prepare-only', action='store_true')
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    output, work = args.output.resolve(), args.work.resolve()
    output.mkdir(parents=True, exist_ok=True)
    status = {'state': 'preparing', 'startedUtc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}
    status_file = output / 'run-status.json'
    try:
        status_file.write_text(json.dumps(status, indent=2) + '\n')
        manifest = load_manifest(args.manifest)
        provenance = prepare(root, output, work, manifest)
        if not args.prepare_only:
            status['state'] = 'measuring'
            status_file.write_text(json.dumps(status, indent=2) + '\n')
            measure(root, output, work, manifest, provenance)
        status['state'] = 'prepared' if args.prepare_only else 'completed'
    except Exception as exception:
        status.update(state='failed', exception=type(exception).__name__, error=str(exception),
                      traceback=traceback.format_exc())
        raise
    finally:
        status['finishedUtc'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
        status_file.write_text(json.dumps(status, indent=2) + '\n')


if __name__ == '__main__':
    main()
