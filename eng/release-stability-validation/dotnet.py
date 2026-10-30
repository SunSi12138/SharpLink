#!/usr/bin/env python3
"""Transparent original-dotnet forwarding plus non-invasive byte/command evidence."""
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import validate as v


def classify(args, config):
    source = Path(config['source'])
    if config['kind'] == 'abi':
        project = str(source / 'test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj')
        if args == ['build', project, '-c', 'Release', '-m:1', '-p:UseSharedCompilation=false', '-nodeReuse:false', '-v', 'minimal']:
            return 'build', None
        prefix = ['run', '-c', 'Release', '--no-build', '--no-restore', '--project', project, '--']
        if args[:len(prefix)] == prefix:
            tail = args[len(prefix):]
            if tail == ['--list-tests', 'json']:
                return 'discovery', None
            if len(tail) == 6 and tail[:5] == ['--maximum-parallel-tests', '1', '--timeout', '120s', '--filter-uid'] and tail[5]:
                return 'test', tail[5]
    else:
        if args == ['build', 'test/SharpLink.ChaosTests/SharpLink.ChaosTests.csproj', '-c', 'Release', '-v', 'minimal']:
            return 'build', None
        expected = ['run', '-c', 'Release', '--no-build', '--project', 'test/SharpLink.ChaosTests', '--',
                    '--duration', '10m', '--transport', config['kind'], '--shm-name', 'sharplink-release-soak',
                    '--concurrency', '32', '--restart-interval-seconds', '60', '--checkpoint-interval', '2m',
                    '--dump-on-failure', 'true', '--stop-on-unexpected', 'true',
                    '--json-output', f"artifacts/chaos/release-10m-{config['kind']}.json"]
        if args == expected:
            return 'chaos', None
    # Unrecognized commands retain normal execution; they cannot prove a soak.
    return 'other', None


def actual_test_runtime(trace, source):
    # A CLI/SDK CoreCLR line alone cannot prove the integration test application's
    # runtime. Require its own runtimeconfig marker before the selected CoreCLR.
    marker = str(source / 'test/SharpLink.IntegrationTests/bin/Release/net10.0/SharpLink.IntegrationTests.runtimeconfig.json')
    text = v.nonempty(trace)
    token = f'Executing as a framework-dependent app as per config file [{marker}]'
    if token not in text:
        raise ValueError('Actual integration test host runtimeconfig is absent from host trace')
    tail = text.split(token, 1)[1]
    match = re.search(r"CoreCLR path = '([^']+/shared/Microsoft.NETCore.App/([^/]+)/libcoreclr.so)'", tail)
    if match is None:
        raise ValueError('Actual integration test CoreCLR selection is absent')
    return match.group(2)


def write_json(path, data):
    path.write_text(json.dumps(data, indent=2) + '\n')


def forward(args, config):
    source = Path(config['source'])
    evidence = Path(config['evidence'])
    kind, uid = classify(args, config)
    ledger = evidence / 'commands.jsonl'
    sequence = len(ledger.read_text().splitlines()) if ledger.exists() else 0
    record = {'sequence': sequence, 'kind': kind, 'uid': uid, 'argv': args,
              'cwd': os.getcwd(), 'dotnet': config['dotnet'],
              'started_utc': datetime.datetime.now(datetime.timezone.utc).isoformat()}
    code = None
    try:
        if kind != 'other' and Path.cwd().resolve() != source:
            raise ValueError('Original runner working directory changed')
        if hashlib.sha256(Path(config['dotnet']).read_bytes()).hexdigest() != config['dotnet_sha256']:
            raise ValueError('Original dotnet executable changed')
        if kind in {'discovery', 'test', 'chaos'}:
            v.source_identity(source)
            before = v.inventory(source, config['kind'])
            manifest = evidence / 'built-before.json'
            if manifest.exists():
                v.assert_same(json.loads(v.nonempty(manifest))['files'], before)
            else:
                write_json(manifest, {**v.source_identity(source), 'files': before})
            record['binary_manifest_sha256'] = hashlib.sha256(manifest.read_bytes()).hexdigest()
        env = dict(os.environ)
        # Only the original script shell uses the shim; SDK/application children
        # resolve nested dotnet exactly as they did before the recording layer.
        env['PATH'] = config['original_path']
        if kind in {'discovery', 'test'}:
            trace = evidence / f'host-{sequence:05d}.log'
            env.update(DOTNET_HOST_TRACE='1', DOTNET_HOST_TRACE_VERBOSITY='4', DOTNET_HOST_TRACEFILE=str(trace))
            record['host_trace'] = trace.name
        code = subprocess.run([config['dotnet'], *args], env=env).returncode
        record['exit_code'] = code
        if kind in {'discovery', 'test', 'chaos'}:
            after = v.inventory(source, config['kind'])
            write_json(evidence / 'built-after.json', {**v.source_identity(source), 'files': after})
            v.assert_same(before, after)
            if kind == 'test' and code == 0:
                record['actual_runtime'] = actual_test_runtime(trace, source)
    except Exception as error:
        record['tool_error'] = str(error)
        print(f'STABILITY_EVIDENCE_ERROR: {error}', file=sys.stderr)
    finally:
        record['ended_utc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        if code is None:
            record['exit_code'] = None
        try:
            with ledger.open('a') as handle:
                handle.write(json.dumps(record) + '\n')
        except Exception as error:
            record['tool_error'] = f'Cannot persist command ledger: {error}'
            print(f'STABILITY_EVIDENCE_ERROR: {record["tool_error"]}', file=sys.stderr)
    # Never turn an original failure into success or replace its shell exit code.
    if code is not None and code != 0:
        return code if code > 0 else 128 - code
    return 86 if record.get('tool_error') else 0


def main():
    try:
        config = json.loads(Path(os.environ['STABILITY_CONFIG']).read_text())
        return forward(sys.argv[1:], config)
    except Exception as error:
        print(f'STABILITY_EVIDENCE_ERROR: {error}', file=sys.stderr)
        return 86


if __name__ == '__main__':
    sys.exit(main())
