#!/usr/bin/env python3
"""Capture paired source/layout/native evidence, never infer throughput from it."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess

PARSER = argparse.ArgumentParser(description=__doc__)
PARSER.add_argument('--baseline', type=Path, required=True)
PARSER.add_argument('--candidate', type=Path, required=True)
PARSER.add_argument('--output', type=Path, required=True)
PARSER.add_argument('--runtime', default='dotnet',
                    help='Runtime host for probes; builds always use dotnet from PATH.')
PARSER.add_argument('--default-repeats', type=int, default=1)
ARGS = PARSER.parse_args()
if ARGS.default_repeats < 1:
    PARSER.error('--default-repeats must be positive')
OUT = ARGS.output.resolve()
if OUT.exists() and any(OUT.iterdir()):
    raise SystemExit('Output directory must be new or empty; JitStdOutFile appends.')
OUT.mkdir(parents=True, exist_ok=True)
PROBE = Path(__file__).resolve().parent
FILTER = ('SharpLink.Server.SharpLinkServer:DispatchRpcAsync '
          'SharpLink.Server.SharpLinkServer:DispatchOneWayRpc '
          'SharpLink.Server.SharpLinkServer:InvokeServiceAsync '
          '*DispatchRequestAsync*:MoveNext')


def run(command, cwd, output, env=None):
    with output.open('w') as log:
        result = subprocess.run(command, cwd=cwd, env=env, stdout=log,
                                stderr=subprocess.STDOUT, check=False)
    if result.returncode:
        raise SystemExit(f'Command failed ({result.returncode}); retained {output}')


run(['dotnet', '--info'], PROBE, OUT / 'dotnet-info.txt')
run([ARGS.runtime, '--info'], PROBE, OUT / 'runtime-info.txt')
run(['dotnet', 'build', str(PROBE / 'layout/LayoutProbe.csproj'), '-c', 'Release',
     '-m:1', '-p:UseSharedCompilation=false'], PROBE, OUT / 'layout-build.txt')
summary = {}
for arm, checkout in [('baseline', ARGS.baseline.resolve()), ('candidate', ARGS.candidate.resolve())]:
    dest = OUT / arm
    dest.mkdir()
    head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=checkout, text=True).strip()
    dirty = subprocess.check_output(['git', 'status', '--porcelain'], cwd=checkout, text=True)
    run(['dotnet', 'build', 'test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj',
         '-c', 'Release', '-m:1', '-p:UseSharedCompilation=false'], checkout, dest / 'build.txt')
    binary = checkout / 'test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll'
    server = binary.with_name('SharpLink.Server.dll')
    run([ARGS.runtime, str(PROBE / 'layout/bin/Release/net10.0/LayoutProbe.dll'), str(server)],
        checkout, dest / 'layout.json')
    rows = []
    modes = [('fullopts', 1000, {'DOTNET_TieredCompilation': '0'}),
             ('nopgo', 50000, {'DOTNET_TieredPGO': '0'})]
    modes += [(f'default-{i + 1}', 50000, {}) for i in range(ARGS.default_repeats)]
    for mode, count, settings in modes:
        env = dict(os.environ)
        for key in ('DOTNET_TieredCompilation', 'DOTNET_TieredPGO', 'COMPlus_TieredCompilation',
                    'COMPlus_TieredPGO', 'DOTNET_JitStdOutFile', 'COMPlus_JitStdOutFile'):
            env.pop(key, None)
        env.update(settings)
        env.update(DOTNET_JitDisasm=FILTER, DOTNET_JitStdOutFile=str(dest / f'{mode}.asm'),
                   SHARPLINK_BENCHMARK_SHA=head)
        run([ARGS.runtime, str(binary), '--jit-evidence', 'server', str(count), str(dest / f'{mode}.json')],
            checkout, dest / f'{mode}.txt', env)
        for body in (dest / f'{mode}.asm').read_text().split('; Assembly listing')[1:]:
            size = re.search(r'Total bytes of code (\d+)', body)
            frame = re.search(r'sub\s+rsp, (\w+)', body)
            rows.append(dict(mode=mode, method=body.splitlines()[0].strip(),
                             nativeBytes=int(size.group(1)) if size else None,
                             stackSubtract=frame.group(1) if frame else None))
    summary[arm] = dict(head=head, dirty=bool(dirty),
                        runtime=ARGS.runtime,
                        serverSha256=hashlib.sha256(server.read_bytes()).hexdigest(), native=rows)
(OUT / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(json.dumps(summary, indent=2))
