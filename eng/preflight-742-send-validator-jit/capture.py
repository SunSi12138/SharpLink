#!/usr/bin/env python3
"""One fixed same-source Tier1/PGO diagnostic, never a new timing experiment."""
import argparse
import hashlib
import importlib.util
import json
import os
import pathlib
import shutil
import subprocess
import tarfile

from inventory import inventory

KIT = pathlib.Path(__file__).resolve().parent
SOURCE = KIT.parents[1]
BASE = 'dbd7ea9b32facce062f351bb462c85f0ec81b466'
BASELINE = json.loads((KIT / 'baseline.json').read_text())
CONFIG = json.loads((KIT / 'config.json').read_text())
RPC = 'test/SharpLink.Benchmarks/bin/Release/net10.0'
BUDGET = 24 * 1024 * 1024


def git(root, *args):
    return subprocess.check_output(['git', *args], cwd=root, text=True).strip()


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + '\n')


def canonical(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def sources(root):
    return {name: sha(root / name) for name in git(root, 'ls-files', '--', 'src', 'test').splitlines()}


def binaries(root):
    return {str(path.relative_to(root)): sha(path) for path in (root / RPC).rglob('*') if path.is_file()}


def load_module(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def recipe(work):
    return load_module(work / 'jit-timing-helper/eng/preflight-742-send-validator-screen/prepare.py', 'frozen_recipe')


def roots(work):
    value = json.loads((work / 'send-validator-screen/identities.json').read_text())
    assert value['identities'] == BASELINE['identities']
    result = {arm: pathlib.Path(path) for arm, path in value['roots'].items()}
    recipe(work).check_roots(result)
    for arm, root in result.items():
        assert canonical(sources(root)) == BASELINE['source_map_sha256'][arm], 'Historical timed source drift: ' + arm
    return result


def prepare(work, out):
    exclusions = [':(exclude)eng/preflight-742-send-validator-jit/**',
                  ':(exclude).github/workflows/742-send-validator-jit.yml']
    for revision in ((BASE, 'HEAD'), ()):
        assert not git(SOURCE, 'diff', '--name-only', *revision, '--', '.', *exclusions), 'Inherited helper/source drift'
    pinned = work / 'jit-timing-helper'
    subprocess.run(['git', 'worktree', 'add', '--detach', str(pinned), BASE], cwd=SOURCE, check=True)
    # Invoke the frozen recipe in its own original helper checkout, so its
    # inherited-drift guard is retained rather than relaxed for this diagnostic.
    subprocess.run(['python3', str(pinned / 'eng/preflight-742-send-validator-screen/prepare.py'),
        str(work / 'send-validator-screen'), str(out / 'index/source'), '--require-codegen-review'], check=True)
    actual = roots(work)
    save(out / 'index/diagnostic-provenance.json', dict(execution_commit=git(SOURCE, 'rev-parse', 'HEAD'),
        frozen_timing_helper=BASE, historical_screen=37828725331, config=CONFIG,
        baseline_sha256=sha(KIT / 'baseline.json'), historical_identities=BASELINE['identities'],
        current_source_sha256={arm: sources(root) for arm, root in actual.items()},
        helper_sha256={str(path.relative_to(SOURCE)): sha(path) for path in
            [*(path for path in KIT.iterdir() if path.is_file()), SOURCE / '.github/workflows/742-send-validator-jit.yml']},
        boundary='V remains No-Go. Fixed12 traced diagnostic launches; no performance comparisons or acceptance rerun.'))


def build(work, out):
    actual = roots(work)
    assert subprocess.check_output(['dotnet', '--version'], cwd=SOURCE, text=True).strip() == BASELINE['sdk']
    runtime_line = [line for line in subprocess.check_output(['dotnet', '--list-runtimes'], text=True).splitlines()
                    if line.startswith('Microsoft.NETCore.App ' + BASELINE['runtime'] + ' ')]
    assert len(runtime_line) == 1, 'Measured .NET10.0.12 runtime is required'
    runtime = pathlib.Path(runtime_line[0].split('[', 1)[1].rstrip(']')) / BASELINE['runtime']
    save(out / 'index/runtime.json', dict(sdk=BASELINE['sdk'], selected_runtime=BASELINE['runtime'],
        explicit_host_argument='--fx-version ' + BASELINE['runtime'],
        runtime_files={name: sha(runtime / name) for name in ('libclrjit.so', 'libcoreclr.so', 'System.Private.CoreLib.dll')}))
    for arm, root in actual.items():
        target = out / arm
        target.mkdir(parents=True, exist_ok=True)
        command = ['dotnet', 'build', str(root / 'test/SharpLink.Benchmarks'), '-c', 'Release',
            '-p:PublishAot=false', '-m:1', '/nodeReuse:false', '-p:UseSharedCompilation=false']
        with (target / 'build.log').open('w') as log:
            status = subprocess.run(command, cwd=root, stdout=log, stderr=subprocess.STDOUT).returncode
        if status:
            print((target / 'build.log').read_text(), flush=True)
            raise SystemExit(status)
        current, old = binaries(root), BASELINE['rpc_output_sha256'][arm]
        comparison = dict(historical_expected_files=len(old), current_files=len(current),
            matches=sorted(name for name in old if current.get(name) == old[name]),
            missing=sorted(set(old) - set(current)), extra=sorted(set(current) - set(old)),
            changed={name: dict(historical=old[name], diagnostic=current[name]) for name in old.keys() & current.keys()
                     if old[name] != current[name]}, current_sha256=current,
            boundary='Any mismatch is disclosed. Same source does not imply the historical measured binary bytes.')
        comparison['exact_historical_rpc_output'] = not any(comparison[key] for key in ('missing', 'extra', 'changed'))
        save(target / 'binary-comparison.json', comparison)
        assert current and (root / RPC / 'SharpLink.Benchmarks.dll').is_file()
        with tarfile.open(target / 'rpc-output.tar.gz', 'w:gz') as archive:
            archive.add(root / RPC, arcname='rpc-output')
        save(target / 'rpc-output-archive.json', {'sha256': sha(target / 'rpc-output.tar.gz')})
        assert sum(path.stat().st_size for path in target.rglob('*') if path.is_file()) < BUDGET, 'Per-arm artifact budget exceeded'
    roots(work)


def run(work, out, affinity):
    actual = roots(work)
    cpu_ids = [int(value) for value in affinity.split(',')]
    assert len(cpu_ids) == len(set(cpu_ids)) == 4 and set(cpu_ids) <= os.sched_getaffinity(0)
    common = load_module(work / 'jit-timing-helper/eng/preflight-742-quiescent/common.py', 'frozen_validation')
    before = {arm: dict(source_sha256=sources(root), rpc_binary_sha256=binaries(root)) for arm, root in actual.items()}
    save(out / 'index/source-and-binaries-before.json', before)
    plan = [(transport, scenario, arm) for transport, scenario in CONFIG['workloads'] for arm in CONFIG['arms']]
    assert len(plan) == CONFIG['launches'] == 12
    records = []
    try:
        for transport, scenario, arm in plan:
            root = actual[arm]
            cell = out / arm / (transport + '-' + scenario)
            cell.mkdir(parents=True, exist_ok=False)
            report, disasm = cell / 'diagnostic-workload.json', cell / 'jit-all-tiers.txt'
            env = dict(os.environ)
            assert not any(key.startswith('COMPlus_') for key in env), 'Unexpected runtime overrides'
            expected_runtime = CONFIG['runtime_environment']
            for key, value in env.items():
                if key.startswith(('DOTNET_Jit', 'DOTNET_TC_', 'DOTNET_Tiered')) or key == 'DOTNET_ReadyToRun':
                    assert key in expected_runtime and value == expected_runtime[key], 'Unexpected runtime override: ' + key
            env.update(expected_runtime)
            env.update(CONFIG['diagnostic_environment'])
            env.update(DOTNET_JitDisasm=' '.join(CONFIG['method_filters']), DOTNET_JitStdOutFile=str(disasm),
                SHARPLINK_BENCHMARK_SHA=BASELINE['identities'][arm], SHARPLINK_COMMIT=BASELINE['identities'][arm])
            command = ['taskset', '-c', affinity, 'dotnet', '--fx-version', BASELINE['runtime'],
                str(root / RPC / 'SharpLink.Benchmarks.dll'), '--generated-abi-streaming-evidence', scenario,
                '30', '5', '200000', str(report), transport]
            save(cell / 'invocation.json', dict(command=command, source=BASELINE['identities'][arm],
                runtime_environment={key: env[key] for key in (*expected_runtime, *CONFIG['diagnostic_environment'],
                    'DOTNET_JitDisasm', 'DOTNET_JitStdOutFile')}, diagnostic_only=True, attempts=1))
            with (cell / 'program.log').open('w') as log:
                try:
                    status = subprocess.run(command, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT,
                                            timeout=180).returncode
                except subprocess.TimeoutExpired:
                    status = 124
            validation = None
            try:
                assert status == 0
                value = json.loads(report.read_text())
                common.rpc(value, BASELINE['identities'][arm], transport, scenario)
                assert value['runtimeVersion'] == '.NET 10.0.12' and value['architecture'] == 'X64'
                assert value['processorCount'] == 4 and value['tieredCompilation'] == value['tieredPgo'] == '1'
            except (AssertionError, OSError, ValueError, KeyError, TypeError) as error:
                validation = repr(error)
            captured = inventory(disasm, cell / 'methods', scenario)
            records.append(dict(arm=arm, transport=transport, scenario=scenario, exit_code=status,
                workload_validation_error=validation, capture_status=captured['status'], problems=captured['problems'],
                target_root_versions=captured['target_root_versions'], raw_jit_sha256=captured['raw_sha256']))
            save(out / 'index/captures.json', records)
            print(arm, transport, scenario, captured['status'], flush=True)
    finally:
        after = {arm: dict(source_sha256=sources(root), rpc_binary_sha256=binaries(root)) for arm, root in actual.items()}
        save(out / 'index/source-and-binaries-after.json', after)
        assert before == after, 'Source or RPC binary bytes changed during diagnostic capture'
        roots(work)
    for arm in actual:
        assert sum(path.stat().st_size for path in (out / arm).rglob('*') if path.is_file()) < BUDGET, 'Per-arm artifact budget exceeded'
    incomplete = len(records) != 12 or any(r['workload_validation_error'] or r['capture_status'] == 'inconclusive' for r in records)
    save(out / 'index/result.json', dict(status='inconclusive' if incomplete else 'pending-independent-path-review',
        launches=len(records), acceptance='V remains No-Go', call_chain_proved=False,
        boundary='No retry, tier forcing, method forcing, new warmup or duration change. All traced rates are diagnostic artifacts only. '
                 'Independent instruction review must establish matching optimized call paths or stop inconclusive.'))
    if incomplete:
        raise SystemExit(2)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=('prepare', 'build', 'run'))
    parser.add_argument('work', type=pathlib.Path)
    parser.add_argument('output', type=pathlib.Path)
    parser.add_argument('--affinity')
    args = parser.parse_args()
    args.work.mkdir(parents=True, exist_ok=True)
    if args.mode == 'run':
        assert args.affinity
        run(args.work.resolve(), args.output.resolve(), args.affinity)
    else:
        (prepare if args.mode == 'prepare' else build)(args.work.resolve(), args.output.resolve())
