#!/usr/bin/env python3
"""Discriminating TCP diagnostic: each default/configured process attempted once."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
PLAN = [('default', 0), ('262144', 0), ('262144', 1), ('default', 1)]


def validate_report(document):
    if document.get('metadata', {}).get('DiagnosticCapture') is not True:
        raise ValueError('Socket control must remain explicitly diagnostic, never timing evidence')
    return document.get('status') == 'completed' and document.get('error') is None and len(document.get('samples', [])) == 48


def execute(root, output, dll, affinity):
    targets = [output / f'{index}-{profile}-r{launch}.json' for index, (profile, launch) in enumerate(PLAN)]
    for target in targets:
        if any(path.exists() for path in (target, target.with_suffix('.log'), target.with_suffix('.exit'))):
            raise FileExistsError('Do not overwrite successful or failed socket controls')
    failures = []
    for index, ((profile, launch), target) in enumerate(zip(PLAN, targets)):
        env = dict(os.environ, SHARPLINK_TCP_RCVBUF_CONTROL=profile, SHARPLINK_READY_ORDER=str(launch),
                   SHARPLINK_READY_PREPARED_BYTES='0', DOTNET_TieredPGO='0', DOTNET_TieredCompilation='1',
                   DOTNET_ReadyToRun='0', DOTNET_PROCESSOR_COUNT='4')
        args = ['taskset', '-c', ','.join(map(str, affinity)), 'dotnet', str(dll),
                '--ready-writer-evidence', 'tcp', '128', '128', '4096', '12', '524288', '16', '16384', str(target.resolve())]
        print('RUN', index, profile, launch, flush=True)
        started = time.monotonic()
        with target.with_suffix('.log').open('w') as log:
            try:
                code = subprocess.run(args, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=180).returncode
            except subprocess.TimeoutExpired:
                code = 124
            except OSError as error:
                log.write(str(error)); code = 127
        valid = False
        try:
            valid = validate_report(json.loads(target.read_text()))
        except (ValueError, OSError) as error:
            print('REPORT', index, str(error), flush=True)
        record = dict(code=code, diagnostic_complete=valid, seconds=time.monotonic()-started,
                      profile=profile, order=launch, requested_receive_buffer=None if profile=='default' else 262144)
        target.with_suffix('.exit').write_text(json.dumps(record, indent=2))
        if code or not valid:
            failures.append(index)
        print('END', index, record, flush=True)
    return failures


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=True)
    dll = ROOT/'test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll'
    affinity = sorted(os.sched_getaffinity(0))[:4]
    source = subprocess.check_output(['git', 'write-tree'], cwd=ROOT, text=True).strip()
    subprocess.run(['git', 'diff', '--exit-code'], cwd=ROOT, check=True)
    provenance = dict(source_tree=source, binary_sha256=hashlib.sha256(dll.read_bytes()).hexdigest(),
                      profiles=PLAN, affinity=affinity, diagnostic_only=True,
                      comparison='socket permission only; unchanged 45s case / 180s process timeout; no B3 performance acceptance')
    with (output/'provenance.json').open('x') as file:
        json.dump(provenance, file, indent=2)
    os.environ['SHARPLINK_SOURCE_TREE'] = source
    failures = execute(ROOT, output, dll, affinity)
    (output/'outcome.json').write_text(json.dumps(dict(failures=failures, attempted=len(PLAN), diagnostic_only=True)))
    if failures:
        raise SystemExit(f'Socket controls failed {failures}; retained all four attempts; no retry or timeout relaxation')


if __name__ == '__main__':
    main()
