#!/usr/bin/env python3
"""Matched actual readonly-inner-lease RPC paths and unchanged owned root layout, without timing comparisons."""
import argparse
import importlib.util
import json
import os
import pathlib
import re
import subprocess
import tarfile
import traceback

from prepare import KIT, SOURCE, trees, pristine, save, sha, git

SDK = '10.0.112'
RUNTIME = '10.0.12'
RPC = pathlib.Path('test/SharpLink.Benchmarks/bin/Release/net10.0')
BUDGET = 24 * 1024 * 1024
CASES = [('tcp', 'Client100x16'), ('sharedmemory', 'Client100x4096'),
         ('sharedmemory', 'Server1x16'), ('sharedmemory', 'Server100x16')]
OLD = SOURCE / 'eng/preflight-742-send-validator-jit'
CONFIG = json.loads((KIT / 'config.json').read_text())
ROOT_NAMES = ('SendClientStreamAsync', 'PumpGeneratedOutboundStreamAsync')



def validate_config():
    original = json.loads((OLD / 'config.json').read_text())
    assert CONFIG['workloads'] == [list(cell) for cell in CASES] == original['workloads']
    assert CONFIG['arms'] == ['G2', 'R'] and CONFIG['launches'] == len(CASES) * 2
    for name in ('runtime_environment', 'diagnostic_environment', 'repeats',
                 'warmup_operations', 'measurement_seconds', 'operation_cap', 'processor_count'):
        assert CONFIG[name] == original[name], ('Frozen diagnostic configuration drift', name)
    assert set(original['method_filters']) <= set(CONFIG['method_filters'])
    assert len(CONFIG['method_filters']) == len(set(CONFIG['method_filters']))


validate_config()

def module(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


def roots(work):
    proof = json.loads((work / 'identities.json').read_text())
    assert proof['trees'] == trees()
    result = {arm: pathlib.Path(proof['roots'][arm]) for arm in ('G2', 'R')}
    for arm, root in result.items():
        pristine(root, proof['trees'][arm])
    return result


def snapshot(root):
    return {name: sha(root / name) for name in git(root, 'ls-files', '--', 'src', 'test').splitlines()}


def binaries(root):
    return {str(path.relative_to(root)): sha(path) for path in (root / RPC).rglob('*') if path.is_file()}


def budget(path):
    size = sum(file.stat().st_size for file in path.rglob('*') if file.is_file())
    assert size < BUDGET, (path, size, BUDGET)
    return size


def checked(command, cwd, log, **kwargs):
    log.parent.mkdir(parents=True, exist_ok=True)
    with log.open('w') as output:
        result = subprocess.run(command, cwd=cwd, stdout=output, stderr=subprocess.STDOUT, **kwargs)
    if result.returncode:
        print(log.read_text(), flush=True)
        raise SystemExit(result.returncode)


def build(work, out):
    actual = roots(work)
    assert subprocess.check_output(['dotnet', '--version'], text=True).strip() == SDK
    lines = [line for line in subprocess.check_output(['dotnet', '--list-runtimes'], text=True).splitlines()
             if line.startswith('Microsoft.NETCore.App ' + RUNTIME + ' ')]
    assert len(lines) == 1
    runtime = pathlib.Path(lines[0].split('[', 1)[1].rstrip(']')) / RUNTIME
    save(out / 'index/runtime.json', dict(sdk=SDK, runtime=RUNTIME,
        runtime_files={name: sha(runtime / name) for name in ('libclrjit.so', 'libcoreclr.so', 'System.Private.CoreLib.dll')},
        environment=CONFIG['runtime_environment']))
    before = {arm: snapshot(root) for arm, root in actual.items()}
    save(out / 'index/production-before-build.json', before)
    probe = work / 'root-layout-probe'
    checked(['dotnet', 'build', str(KIT / 'layout/RootLayout.csproj'), '-c', 'Release',
             '-o', str(probe), '-m:1', '/nodeReuse:false', '-p:UseSharedCompilation=false'], SOURCE,
            out / 'index/layout-build.log')
    layouts = {}
    for arm, root in actual.items():
        target = out / (arm + '-build')
        command = ['dotnet', 'build', str(root / 'test/SharpLink.Benchmarks'), '-c', 'Release',
                   '-p:PublishAot=false', '-m:1', '/nodeReuse:false', '-p:UseSharedCompilation=false']
        save(target / 'build-command.json', command)
        checked(command, root, target / 'build.log')
        current = binaries(root)
        assert current and (root / RPC / 'SharpLink.Benchmarks.dll').is_file()
        save(target / 'rpc-binaries.json', current)
        generated = root / 'test/SharpLink.Benchmarks/obj/Generated'
        generated_files = sorted(generated.rglob('*.cs'))
        assert generated_files, 'Actual RPC generated sources are required for byte[]/unsized path mapping'
        save(target / 'generated-source.json', {str(path.relative_to(generated)): sha(path) for path in generated_files})
        with tarfile.open(target / 'generated-source.tar.gz', 'w:gz') as archive:
            archive.add(generated, arcname='generated')
        with tarfile.open(target / 'rpc-output.tar.gz', 'w:gz') as archive:
            archive.add(root / RPC, arcname='rpc-output')
        save(target / 'rpc-archive.json', {'sha256': sha(target / 'rpc-output.tar.gz')})
        checked(['dotnet', '--fx-version', RUNTIME, str(probe / 'RootLayout.dll'), str(root / RPC)],
                root, target / 'managed-root-layout.json', env=dict(os.environ, **CONFIG['runtime_environment']))
        layout = json.loads((target / 'managed-root-layout.json').read_text())
        assert layout['runtime'] == RUNTIME and layout['architecture'] == 'X64' and layout['dynamicCodeSupported']
        assert {item['root'] for item in layout['roots']} == set(ROOT_NAMES)
        for item in layout['roots']:
            assert item['managedSize'] > 0 and 'System.Byte[]' in item['stateMachine']
            assert item['assemblySha256'] == sha(pathlib.Path(item['assemblyPath']))
        layouts[arm] = layout
        budget(target)
    after = {arm: snapshot(root) for arm, root in actual.items()}
    assert before == after
    save(out / 'index/production-after-build.json', after)
    comparison = []
    for name in ROOT_NAMES:
        old = next(item for item in layouts['G2']['roots'] if item['root'] == name)
        new = next(item for item in layouts['R']['roots'] if item['root'] == name)
        comparison.append(dict(root=name, G2=old['managedSize'], R=new['managedSize'],
            delta=new['managedSize'] - old['managedSize'],
            fields_identical=old['fields'] == new['fields'],
            G2_fields=old['fields'], R_fields=new['fields']))
    for arm, layout in layouts.items():
        assert layout['lease']['managedSize'] == 40, 'Expected x64 owned lease size must be observed, not assumed'
        for method in layout['leaseMethods']:
            readonly_inner = arm == 'R' and method['method'] == 'SendUnsizedStreamChunkResolvedAsync'
            assert method['leaseParameter']['byRef'] == readonly_inner
            assert method['leaseParameter']['isIn'] == readonly_inner
    save(out / 'index/managed-layout-comparison.json', dict(roots=comparison,
        lease_layout_identical=layouts['G2']['lease'] == layouts['R']['lease'],
        lease_parameter_modes={arm: layout['leaseMethods'] for arm, layout in layouts.items()},
        proof_status='pending-independent-review',
        boundary='Actual managed sizeof and metadata only. Root layout must stay unchanged; '
                 'machine-code copy removal, ownership, NativeAOT layout and performance are not proved.'))
    assert all(row['delta'] == 0 and row['fields_identical'] for row in comparison)
    assert layouts['G2']['lease'] == layouts['R']['lease'], 'Readonly borrowing must not change owned lease representation'
    roots(work)


def selected_roots(captured, name):
    # Retain the frozen strict parser's original verdict. This distinct readonly-lease
    # diagnostic allows exact matched optimized labels, including Synthesized PGO.
    return [row for row in captured['methods'] if name in row['method']
            and '[System.__Canon]' in row['method']
            and row['complete'] and row['osr_label_consistent']
            and row['tier'] in ('Tier1', 'Tier1-OSR') and row['optimized_code']
            and row.get('code_bytes', 0) > 0 and row['instruction_byte_lines'] > 0]



def matching_key(row):
    # Only compiler-generated ordinal normalization is allowed. All labels and
    # signatures, including canonical instantiations, retain their exact values.
    return (re.sub(r'd__\d+', 'd__ORDINAL', row['method']), row['tier'], row['pgo_source'],
            row['profile_source'], row['osr_entry'])


def match_pair(left, right):
    keys = set(map(matching_key, left)) & set(map(matching_key, right))
    roles = {':MoveNext(' in value[0] for value in keys}
    return dict(matching_keys=sorted(keys, key=str), wrapper_and_movenext_matched=roles == {False, True})

def run(work, out, affinity):
    actual = roots(work)
    cpus = [int(value) for value in affinity.split(',')]
    assert len(cpus) == len(set(cpus)) == 4 and set(cpus) <= os.sched_getaffinity(0)
    inventory = module(OLD / 'inventory.py', 'original_jit_inventory').inventory
    common = module(SOURCE / 'eng/preflight-742-quiescent/common.py', 'original_validation')
    before = {arm: dict(sources=snapshot(root), binaries=binaries(root)) for arm, root in actual.items()}
    save(out / 'index/before-capture.json', before)
    records = []
    try:
        for transport, scenario in CASES:
            for arm, root in actual.items():
                cell = out / (arm + '-' + transport + '-' + scenario)
                cell.mkdir(parents=True, exist_ok=False)
                report, raw = cell / 'diagnostic-workload.json', cell / 'jit-all-tiers.txt'
                env = dict(os.environ)
                assert not any(key.startswith('COMPlus_') for key in env)
                for key, value in env.items():
                    if key.startswith(('DOTNET_Jit', 'DOTNET_TC_', 'DOTNET_Tiered')) or key == 'DOTNET_ReadyToRun':
                        assert key in CONFIG['runtime_environment'] and value == CONFIG['runtime_environment'][key]
                env.update(CONFIG['runtime_environment'])
                env.update(CONFIG['diagnostic_environment'])
                env.update(DOTNET_JitDisasm=' '.join(CONFIG['method_filters']), DOTNET_JitStdOutFile=str(raw),
                    SHARPLINK_BENCHMARK_SHA=trees()[arm], SHARPLINK_COMMIT=trees()[arm])
                command = ['taskset', '-c', affinity, 'dotnet', '--fx-version', RUNTIME,
                    str(root / RPC / 'SharpLink.Benchmarks.dll'), '--generated-abi-streaming-evidence', scenario,
                    '30', '5', '200000', str(report), transport]
                save(cell / 'invocation.json', dict(command=command, tree=trees()[arm], attempts=1,
                    environment={key: env[key] for key in (*CONFIG['runtime_environment'], *CONFIG['diagnostic_environment'],
                        'DOTNET_JitDisasm', 'DOTNET_JitStdOutFile')}, diagnostic_only=True))
                with (cell / 'program.log').open('w') as log:
                    try:
                        status = subprocess.run(command, cwd=root, env=env, stdout=log,
                                                stderr=subprocess.STDOUT, timeout=180).returncode
                    except subprocess.TimeoutExpired:
                        status = 124
                error = None
                try:
                    assert status == 0
                    value = json.loads(report.read_text())
                    common.rpc(value, trees()[arm], transport, scenario)
                    assert value['runtimeVersion'] == '.NET ' + RUNTIME and value['architecture'] == 'X64'
                    assert value['processorCount'] == 4 and value['tieredCompilation'] == value['tieredPgo'] == '1'
                except (AssertionError, OSError, ValueError, KeyError, TypeError) as failure:
                    error = repr(failure)
                captured = inventory(raw, cell / 'methods', scenario)
                name = ROOT_NAMES[0 if scenario.startswith('Client') else 1]
                selected = selected_roots(captured, name)
                structural = [problem for problem in captured['problems']
                              if problem['kind'] != 'target-root-Tier1-Dynamic-PGO-not-captured']
                save(cell / 'retained-path-inventory.json', dict(
                    groups={method: [row for row in captured['methods'] if method in row['method']]
                        for method in CONFIG['review_method_groups']},
                    proof_status='pending-independent-review',
                    boundary='Missing or inlined groups require caller inspection; symbol absence is never a mechanism pass.'))
                records.append(dict(arm=arm, transport=transport, scenario=scenario, exit_code=status,
                    workload_validation_error=error, original_dynamic_pgo_status=captured['status'],
                    original_problems=captured['problems'], structural_problems=structural,
                    optimized_roots=selected, raw_sha256=captured['raw_sha256'],
                    artifact_bytes=budget(cell)))
                save(out / 'index/captures.json', records)
    finally:
        after = {arm: dict(sources=snapshot(root), binaries=binaries(root)) for arm, root in actual.items()}
        save(out / 'index/after-capture.json', after)
        assert before == after, 'Source or RPC binaries changed during capture'
        roots(work)
    matches = []
    for transport, scenario in CASES:
        pair = [record for record in records if record['transport'] == transport and record['scenario'] == scenario]
        assert len(pair) == 2
        matches.append(dict(transport=transport, scenario=scenario,
                            **match_pair(pair[0]['optimized_roots'], pair[1]['optimized_roots'])))
    incomplete = len(records) != 8 or any(record['workload_validation_error'] or record['structural_problems']
        for record in records) or not all(row['wrapper_and_movenext_matched'] for row in matches)
    save(out / 'index/jit-review.json', dict(status='inconclusive' if incomplete else 'pending-independent-review',
        launches=len(records), matched_roots=matches, timing_comparison=False,
        artifact_bytes={path.name: budget(path) for path in out.iterdir() if path.is_dir()},
        required_review=['Map actual byte[] unsized RPC roots for TCP Client100x16, SHM Client100x4096 and SHM Server100x16; Server1x16 is first-send control.',
            'Trace each retained root state to its first owned by-value wrapper snapshot at the original pre-callback boundary.',
            'Trace R owned snapshot address through the readonly inner pointer argument into every lease field read.',
            'Show removal of the G2 second 40-byte whole-value copy; an 8-byte pointer replaces it, but total frames need not shrink.',
            'Check all entry/fast paths for an unconditional defensive or compensating whole-lease copy; do not remove the first owned snapshot.',
            'Verify deferred async admission captures a by-value lease before the synchronous borrower returns; refund copies stay owned.',
            'Inspect stale-generation, deadline/cancel, credit refund, writer/budget cleanup, and unchanged sized/first-send paths.'],
        boundary='Exact Synthesized/Dynamic/other tier labels stay distinct. Matched roots are collection coverage only. '
                 'No symbol-absence proof, timing comparison, mechanism acceptance, readiness or promotion.'))
    if incomplete:
        raise SystemExit(2)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=('build', 'run'))
    parser.add_argument('work', type=pathlib.Path)
    parser.add_argument('output', type=pathlib.Path)
    parser.add_argument('--affinity')
    args = parser.parse_args()
    try:
        if args.mode == 'run':
            assert args.affinity
            run(args.work.resolve(), args.output.resolve(), args.affinity)
        else:
            build(args.work.resolve(), args.output.resolve())
    except (Exception, SystemExit) as error:
        # Unexpected parser, identity, budget and build failures must travel with
        # downloaded evidence, not survive only in the Actions console.
        index = args.output.resolve() / 'index'
        index.mkdir(parents=True, exist_ok=True)
        trace = traceback.format_exc()
        (index / (args.mode + '-collection-failure.txt')).write_text(trace[:65536])
        save(index / (args.mode + '-collection-failure.json'), dict(
            phase=args.mode, status='failed', error=repr(error),
            traceback_truncated=len(trace) > 65536, timing_comparison=False,
            boundary='Failure does not erase per-cell logs or convert collection into acceptance.'))
        raise
