#!/usr/bin/env python3
"""BS1: three exact products, two paired contrasts, deadline off/on, 48 TCP samples."""
import argparse
import datetime
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys

HERE = Path(__file__).resolve().parent
SOURCE = HERE.parent
sys.path[:0] = [str(SOURCE), str(SOURCE/'tcp'), str(SOURCE/'wakeup-performance')]
from logical_forwarder_common import (SDK, PACKAGES, capture, run, write, verify_source,
    validate_runtime, verify_dependencies, pin_snapshot, verify_process)
from run_matrix import execute_sample
from protocol import validate_ticks, cpu_set
from contract import SOURCES, TREES, COMPARISONS, DEADLINES, WARMUP, SECONDS, schedule, validate_complete_summary


def clean_environment(runtime):
    env = dict(os.environ, DOTNET_ROOT=str(runtime.parent), DOTNET_MULTILEVEL_LOOKUP='0')
    allowed = {'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO', 'DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER',
        'DOTNET_ROOT', 'DOTNET_MULTILEVEL_LOOKUP', 'DOTNET_INSTALL_DIR', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE'}
    for name in list(env):
        if name.startswith(('COMPlus_', 'CORECLR_', 'COR_')) or (name.startswith('DOTNET_') and name not in allowed): del env[name]
    return env


def validate_loaded(data, arm, snapshot, hashes, entry):
    loaded = data['loadedAssemblies']; names = [a['name'] for a in loaded]
    if len(set(names)) != len(names) or not {'SharpLink.Runtime', 'SharpLink.Client', 'SharpLink.Server', 'SharpLink.Abstractions', entry, 'ScalarStreamModule'} <= set(names): raise ValueError('Loaded inventory mismatch')
    for assembly in loaded:
        path = Path(assembly['location'])
        if path.parent != snapshot or path.name not in hashes or not assembly['mvid']: raise ValueError('Assembly outside snapshot')
        if assembly['name'].startswith('SharpLink.') and assembly['productVersion'].split('+')[-1] != SOURCES[arm]: raise ValueError('Product source stamp mismatch')


def validate_sample(data, arm, cpus, snapshot, hashes, runtime):
    verify_process(data, runtime.parent)
    if (data['sourceRevision'], data['binaryHashes']) != (SOURCES[arm], hashes): raise ValueError('Source/binary identity mismatch')
    if (data['mode'], data['itemCount'], data['workers'], data['warmupSeconds'], data['requestedSeconds']) != ('static-intercepted', 1, 1, WARMUP, SECONDS): raise ValueError('Workload mismatch')
    if (data['processorCount'], data['tieredCompilation'], data['tieredPgo'], data['readyToRun']) != (2, 'default', 'default', 'default'): raise ValueError('Runtime profile mismatch')
    if (data['transport'], data['connectionPoolMin'], data['connectionPoolMax'], data['readyConnections'], data['requestTimeout'], data['heartbeatIntervalSeconds'], data['serverInterceptors'], data['clientInterceptors']) != ('tcp-loopback', 1, 1, 1, 'disabled', 3600, 0, 1): raise ValueError('TCP path mismatch')
    if data['validationFailures'] != 0 or type(data['operations']) is not int or data['operations'] <= 0: raise ValueError('Invalid operations')
    if not data['interceptorObserved'] or not any('InterceptedAsyncEnumerable' in t for t in data['wrapperTypes']) or 'ModuleRpcChannel' in data['channelType'] or 'InvokeServerStreamingDeferred' in data['streamType']: raise ValueError('Static intercepted path missing')
    if not data['threadAffinities'] or any(cpu_set(t['allowed']) != set(cpus) for t in data['threadAffinities']): raise ValueError('Thread affinity differs')
    validate_loaded(data, arm, snapshot, hashes, 'PhysicalTcpProbe')
    for key in ('operationsPerSecond', 'p50Us', 'p99Us', 'p999Us', 'allocatedBytesPerOperation', 'cpuUsPerOperation', 'totalAllocatedBytes', 'totalCpuMicroseconds'):
        if type(data[key]) not in (int, float) or not math.isfinite(data[key]) or data[key] < 0: raise ValueError('Invalid metric '+key)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dev', 'a', 'b', 'build-dotnet', 'runtime-dotnet', 'output'): parser.add_argument('--'+name, type=Path, required=True)
    args = parser.parse_args(); output = args.output.resolve(); output.mkdir(parents=True, exist_ok=False)
    tools = SOURCE.parent.parent; build = args.build_dotnet.resolve(strict=True); runtime = args.runtime_dotnet.resolve(strict=True)
    roots = {arm: getattr(args, arm).resolve(strict=True) for arm in SOURCES}
    tool_sha = capture(['git', 'rev-parse', 'HEAD'], tools); verify_source(tools, tool_sha)
    for arm, root in roots.items():
        verify_source(root, SOURCES[arm])
        if capture(['git', 'rev-parse', 'HEAD^{tree}'], root) != TREES[arm]: raise ValueError('Wrong product tree')
    if capture([build, '--version'], tools) != SDK: raise ValueError('Wrong SDK')
    runtimes = validate_runtime(runtime)
    available = sorted(os.sched_getaffinity(0))
    if len(available) < 2: raise ValueError('Two allowed CPUs required')
    cpus = available[:2]; affinity = ','.join(map(str, cpus))
    for name, command in [('lscpu', ['lscpu']), ('lscpu-mapping', ['lscpu', '-e=CPU,CORE,SOCKET,NODE,ONLINE']), ('uname', ['uname', '-a']), ('runtime-info', [runtime, '--info']), ('build-info', [build, '--info'])]: run(command, output/(name+'.txt'), tools)
    planned = schedule(); write(output/'schedule.json', planned)
    write(output/'experiment.json', {'toolRevision': tool_sha, 'sources': SOURCES, 'trees': TREES, 'sdk': SDK, 'runtimes': runtimes,
        'allowedCpus': available, 'selectedCpus': cpus, 'plannedSamples': 48, 'coldProcesses': 6,
        'deadlinePolicies': {'off': {'clientDefaultTimeout': 'disabled', 'generatedMethodTimeoutSeconds': None}, 'on': {'clientDefaultTimeout': 'disabled', 'generatedMethodTimeoutSeconds': 30}},
        'boundary': 'Current dev to A and A to B narrow fixed-cost screen; not 2.0.3 acceptance, physical isolation, pending frequency, or steady-state Tier1 proof.',
        'runner': {k: os.environ.get(k) for k in ('GITHUB_SHA', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT', 'RUNNER_ARCH', 'ImageOS', 'ImageVersion')}})
    for arm, root in roots.items():
        logs = output/arm; logs.mkdir(); common = ['-c', 'Release', '-m:1', '-nr:false', '-p:UseSharedCompilation=false']
        run([build, 'build', 'test/SharpLink.UnitTests/SharpLink.UnitTests.csproj', *common, f'-bl:{logs/"build.binlog"}'], logs/'build.log', root)
        for package in PACKAGES:
            run([build, 'pack', f'src/SharpLink.{package}/SharpLink.{package}.csproj', *common, '-o', logs/'packages', f'-bl:{logs/("pack-"+package+".binlog")}'], logs/f'pack-{package}.log', root)
        run(['bash', 'eng/verify-packages.sh', logs/'packages', '3.0.0'], logs/'package-verification.log', root)
        verify_source(root, SOURCES[arm])
    libraries = {arm: root/'test/SharpLink.UnitTests/bin/Release/net10.0' for arm, root in roots.items()}
    cold_build = output/'cold-build'; shutil.copytree(HERE/'cold', cold_build)
    for name in ('Directory.Build.props', 'Directory.Build.targets'): (cold_build/name).write_text('<Project />\n')
    run([build, 'build', cold_build/'cold.csproj', '-c', 'Release', '-m:1', '-nr:false', '-p:UseSharedCompilation=false', f'-p:LibraryRoot={libraries["dev"]}'], output/'cold-build.log', tools)
    prepared = {}; hashes = {}
    for deadline in DEADLINES:
        destination = output/('prepared-'+deadline)
        run([sys.executable, HERE/'prepare.py', '--baseline-libraries', libraries['dev'], '--candidate-libraries', libraries['b'], '--baseline-generator', roots['dev']/'src/SharpLink.Generator/bin/Release/netstandard2.0/SharpLink.Generator.dll', '--probe-source', HERE/'timed', '--module-source', HERE/('module-'+deadline), '--dotnet', build, '--output', destination], output/('prepare-'+deadline+'.log'), tools)
        (destination/'baseline').rename(destination/'dev'); (destination/'candidate').rename(destination/'b')
        shutil.copytree(destination/'dev', destination/'a')
        for path in (destination/'a').glob('*.dll'):
            if path.name not in ('PhysicalTcpProbe.dll', 'ScalarStreamModule.dll'): shutil.copy2(libraries['a']/path.name, path)
        for arm in SOURCES:
            snapshot = destination/arm
            pin_snapshot(snapshot)
            for extension in ('dll', 'deps.json'):
                shutil.copy2(cold_build/'bin/Release/net10.0'/('ColdBootstrapProof.'+extension), snapshot)
            shutil.copy2(snapshot/'PhysicalTcpProbe.runtimeconfig.json', snapshot/'ColdBootstrapProof.runtimeconfig.json')
        pair = verify_dependencies(destination/'dev', destination/'a', ('PhysicalTcpProbe.dll', 'ScalarStreamModule.dll', 'ColdBootstrapProof.dll'))
        other = verify_dependencies(destination/'a', destination/'b', ('PhysicalTcpProbe.dll', 'ScalarStreamModule.dll', 'ColdBootstrapProof.dll'))
        hashes[deadline] = {'dev': pair['baseline'], 'a': pair['candidate'], 'b': other['candidate']}; prepared[deadline] = destination
    write(output/'snapshot-integrity.json', hashes)
    run([build, 'build-server', 'shutdown'], output/'build-server-shutdown.log', tools)
    env = clean_environment(runtime); write(output/'runtime-environment.json', {k: v for k, v in env.items() if k.startswith(('DOTNET_', 'COMPlus_', 'CORECLR_', 'COR_'))})
    preflight = output/'preflight'; preflight.mkdir()
    for deadline in DEADLINES:
        for arm in SOURCES:
            key = f'{deadline}-{arm}'; dest = preflight/(key+'.json')
            command = ['taskset', '-c', affinity, str(runtime), str(prepared[deadline]/arm/'ColdBootstrapProof.dll'), arm, deadline, str(dest)]
            code = execute_sample(command, preflight/(key+'.log'), preflight/(key+'.host.json'), arm, 0, dict(env, SHARPLINK_BENCHMARK_SHA=SOURCES[arm]), 90)
            if code: raise ValueError('Cold proof failed; no timing: '+key)
            proof = json.loads(dest.read_text()); verify_process(proof, runtime.parent)
            validate_loaded(proof, arm, prepared[deadline]/arm, hashes[deadline][arm], 'ColdBootstrapProof')
            if (proof['arm'], proof['deadlineOn'], proof['operations'], proof['drained'], proof['sourceRevision'], proof['binaryHashes']) != (arm, deadline == 'on', 128, True, SOURCES[arm], hashes[deadline][arm]): raise ValueError('Cold identity/outcome mismatch')
            if proof['generatedHasMethodTimeout'] != (deadline == 'on') or proof['generatedMethodTimeoutSeconds'] != (30 if deadline == 'on' else None): raise ValueError('Cold generated deadline mismatch')
            if proof['failures']: raise ValueError('Cold proof reports validation failures')
            if deadline == 'on' and not proof['actualPending']: raise ValueError('Actual pending operation not observed')
            if deadline == 'off' and proof['bootstrapSnapshots'] != 0: raise ValueError('Off control unexpectedly pending')
    write(output/'measurement-start.json', {'timestampUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'policy': 'All builds and six cold proofs completed. No cold instrumentation, reflection observers or counters in timed hot path.'})
    reports = {}
    for contrast in COMPARISONS:
        for deadline in DEADLINES:
            key = contrast+'-'+deadline; root = output/key
            for folder in ('runs', 'ticks'): (root/folder).mkdir(parents=True)
            reports[key] = {'status': 'Passed', 'inventoryErrors': [], 'samples': {}}
            write(root/'scenario.json', {'contrast': contrast, 'arms': COMPARISONS[contrast], 'deadline': deadline, 'clientDefaultTimeout': 'disabled', 'generatedMethodTimeoutSeconds': 30 if deadline == 'on' else None, 'preparedSnapshot': str(prepared[deadline]), 'coldProofDirectory': str(preflight)})
    for cell in planned:
        key = cell['contrast']+'-'+cell['deadline']; root = output/key; name = cell['sample']; arm = cell['arm']; deadline = cell['deadline']
        dest = root/'runs'/(name+'.json'); ticks = root/'ticks'/(name+'.json'); snapshot = prepared[deadline]/arm
        command = ['taskset', '-c', affinity, str(runtime), str(snapshot/'PhysicalTcpProbe.dll'), 'static-intercepted', '1', '1', str(WARMUP), str(SECONDS), str(dest), 'measure', str(ticks)]
        try:
            code = execute_sample(command, root/'runs'/(name+'.log'), root/'runs'/(name+'.host.json'), cell['variant'], cell['round'], dict(env, SHARPLINK_BENCHMARK_SHA=SOURCES[arm]), WARMUP+SECONDS+90)
            if code: raise ValueError(f'Exit {code}')
            data = json.loads(dest.read_text()); validate_sample(data, arm, cpus, snapshot, hashes[deadline][arm], runtime); validate_ticks(json.loads(ticks.read_text()), data)
            reports[key]['samples'][name] = {'status': 'Passed'}
            print(key, name, 'QPS', data['operationsPerSecond'], 'P99', data['p99Us'], flush=True)
        except (OSError, ValueError, KeyError, TypeError, RuntimeError, subprocess.TimeoutExpired) as error:
            reports[key]['status'] = 'Failed'; reports[key]['samples'][name] = {'status': 'Failed', 'reason': repr(error)}
        write(root/'sample-validation.json', reports[key])
    for key, report in reports.items():
        root = output/key; expected = {c['sample'] for c in planned if c['contrast']+'-'+c['deadline'] == key}
        inventories = {'raw': {p.stem for p in (root/'runs').glob('*.json') if not p.name.endswith('.host.json')}, 'host': {p.name.removesuffix('.host.json') for p in (root/'runs').glob('*.host.json')}, 'ticks': {p.stem for p in (root/'ticks').glob('*.json')}}
        for name, actual in inventories.items():
            if actual != expected: report['inventoryErrors'].append({'kind': name, 'missing': sorted(expected-actual), 'unexpected': sorted(actual-expected)}); report['status'] = 'Failed'
        write(root/'sample-validation.json', report)
        try:
            run([sys.executable, SOURCE/'tcp/summarize.py', '--input', root, '--validation-manifest', root/'sample-validation.json'], root/'summary.log', tools)
            if report['status'] == 'Passed':
                validate_complete_summary(json.loads((root/'summary.json').read_text()),
                    json.loads((root/'summary-integrity.json').read_text()))
        except (OSError, ValueError, KeyError, TypeError, subprocess.CalledProcessError) as error:
            report['status'] = 'Failed'; report['summaryError'] = repr(error)
            write(root/'sample-validation.json', report)
    failed = any(r['status'] != 'Passed' for r in reports.values())
    write(output/'complete.json', {'status': 'Failed' if failed else 'Passed', 'plannedSamples': 48, 'performanceAcceptance': 'Not evaluated; independent raw review required'})
    if failed: raise ValueError('BS1 retains failures; no replacements or retries')

if __name__ == '__main__': main()
