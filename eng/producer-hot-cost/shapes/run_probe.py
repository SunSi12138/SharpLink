#!/usr/bin/env python3
"""Same-mode native/layout evidence, not a throughput benchmark. Source snapshots only."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--root', type=Path, default=Path(__file__).resolve().parent)
p.add_argument('--arms', nargs='+', default=['baseline', 'candidate-final'])
p.add_argument('--run-name', required=True)
p.add_argument('--modes', nargs='+', default=['fullopts','tier0','tier1','pgo'])
p.add_argument('--core-only', action='store_true')
p.add_argument('--registration-only', action='store_true', help='Predeclared paired PGO companion; only narrow native output, never change the workload.')
p.add_argument('--iterations', type=int, default=5000)
args = p.parse_args()
root = args.root.resolve()
filters = ' '.join([
    '*InvokeClientStreamingCoreAsync*:*', '*InvokeOneWayStreamingCoreAsync*:*', '*RunGeneratedClientStreamsAsync*:*', '*StartServerStreamingInvokerAsync*:*', '*StartDuplexStreamingInvokerAsync*:*', '*CheckHealthAsync*:*', '*InvokeUnaryWithRetryAsync*:*', '*InvokeUnary*', '*StartUnaryCall*', '*OnPendingCallRegistered*', '*OnRegistered*', '*ReleaseCallAdmissionReservation*', '*ClientStreaming*', '*OneWayStreaming*', '*PrepareGeneratedServerStreamAsync*', '*ServerStreaming*', '*DuplexStreaming*', '*RunGeneratedClientStreams*', '*RegisterStreamRequest*', '*CheckHealthAsync*',
    'SharpLink.Client.SharpLinkClient:EnsureExpansion',
    'SharpLink.Client.SharpLinkClient:GetReadyConnection',
    'SharpLink.Client.SharpLinkClient:EnsureReconnectLoop',
    'SharpLink.Client.SharpLinkClient:get_IsMaintenanceFrozen',
    'SharpLink.Client.SharpLinkClient:get_MaintenanceOwner',
    'SharpLink.Runtime.ProtocolV2FrameParser:*',
    'SharpLink.Runtime.RpcSessionProtocolRules:*',
    'SharpLink.Runtime.RpcSession:TryReadInboundFrame',
    'SharpLink.Runtime.RpcSession:EnsureInboundFrameAllowed',
    'SharpLink.Runtime.RpcSession:EnsureOutboundFrameAllowed',
    'SharpLink.Runtime.RpcSession:get_CanAcceptCalls',
    'SharpLink.Runtime.RpcSession:get_ProtocolPhase',
    '*ProcessRequestLoop*:MoveNext',
    '*DispatchRequestAsync*:MoveNext',
    'SharpLink.Server.SharpLinkServer:DispatchRpcAsync',
    'SharpLink.Server.SharpLinkServer:TryAcceptRequest',
    'SharpLink.Client.SharpLinkClient:DispatchRpc',
    'SharpLink.Client.ClientConnection:TryReserveOwnCallAdmission',
    'SharpLink.Client.ClientConnection:TryReserveCallAdmission',
])
modes = {
    'fullopts': {'DOTNET_TieredCompilation': '0'},
    'tier0': {'DOTNET_TieredCompilation': '1', 'DOTNET_TieredPGO': '0',
              'DOTNET_TC_QuickJit': '1', 'DOTNET_TC_QuickJitForLoops': '1', 'DOTNET_TC_CallCounting': '0'},
    'tier1': {'DOTNET_TieredCompilation': '1', 'DOTNET_TieredPGO': '0',
              'DOTNET_TC_QuickJit': '1', 'DOTNET_TC_QuickJitForLoops': '1',
              'DOTNET_TC_CallCountingDelayMs': '0', 'DOTNET_TC_CallCountThreshold': '1'},
    'pgo': {'DOTNET_TieredCompilation': '1', 'DOTNET_TieredPGO': '1',
            'DOTNET_TC_QuickJit': '1', 'DOTNET_TC_QuickJitForLoops': '1',
            'DOTNET_TC_CallCountingDelayMs': '0', 'DOTNET_TC_CallCountThreshold': '30'},
}
modes = {name: modes[name] for name in args.modes}
if args.core_only:
    filters = ' '.join(['SharpLink.Client.SharpLinkClient:' + name for name in ['InvokeUnaryCoreAsync','InvokeUnaryRetryAttemptAsync','PrepareGeneratedServerStreamAsync']] + ['SharpLink.Client.PendingRequestTable:OnRegistered','SharpLink.Client.ClientConnection:ReleaseCallAdmissionReservation'] + ['*' + name + '*:MoveNext' for name in ['InvokeUnaryWithRetryAsync','InvokeClientStreamingCoreAsync','RunGeneratedClientStreamsAsync','StartServerStreamingInvokerAsync','StartDuplexStreamingInvokerAsync','InvokeOneWayStreamingCoreAsync','CheckHealthAsync']])
if args.registration_only:
    if args.modes != ['pgo'] or args.arms != ['baseline', 'candidate-final']:
        p.error('--registration-only requires --modes pgo and both default arms')
    filters = 'SharpLink.Client.PendingRequestTable:OnRegistered'
out = root / 'evidence' / args.run_name
out.mkdir(exist_ok=False)

def run(cmd, output, env=None):
    with output.open('w') as f:
        r = subprocess.run(cmd, stdout=f, stderr=subprocess.STDOUT, env=env, cwd=root, timeout=300)
    return r.returncode

provenance = {'CaptureKind': 'registration-pgo-companion' if args.registration_only else 'primary', 'Iterations': args.iterations, 'Filter': filters, 'Modes': modes,
              'ProbeFiles': {str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest()
                             for p in [*sorted((root/'probe').glob('*.cs')), *sorted((root/'probe').glob('*.csproj')), *sorted((root/'contracts').glob('*.cs')), *sorted((root/'contracts').glob('*.csproj')), *sorted((root/'services').glob('*.cs')), *sorted((root/'services').glob('*.csproj')), *sorted(root.glob('*.props')), Path(__file__).resolve()]},
              'Arms': {}}
run(['dotnet', '--list-runtimes'], out / 'available-runtimes.txt')
for arm in args.arms:
    folder = out / arm
    folder.mkdir()
    binary = root / f'evidence/{arm}/build/bin/HotCostProbe/release/SharpLink.UnitTests.dll'
    provenance['Arms'][arm] = {'Binary': str(binary), 'Sources': {
        str(path.relative_to(root / 'arms' / arm)): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in (root / 'arms' / arm / 'src').rglob('*') if path.is_file()
    }, 'Binaries': {
        path.name: hashlib.sha256(path.read_bytes()).hexdigest()
        for path in binary.parent.glob('*.dll')
    }, 'AuxiliaryBinaries': {str(path.relative_to(binary.parent.parent.parent)): hashlib.sha256(path.read_bytes()).hexdigest() for path in binary.parent.parent.parent.glob('HotCostDynamic.*/release/*.dll')}, 'Exits': {}}
    provenance['Arms'][arm]['Exits']['metadata'] = run(['dotnet', str(binary), 'metadata', str(folder / 'metadata.json')], folder / 'metadata.log')
    if provenance['Arms'][arm]['Exits']['metadata'] != 0:
        raise RuntimeError('Metadata probe failed: ' + arm)
    binding = json.loads((folder / 'metadata.json').read_text())['RuntimeBinding']
    runtime_dir = Path(binding['CoreLibPath']).parent
    runtime_files = [Path(binding['CoreLibPath']), Path(binding['HostPath'])]
    runtime_files += [runtime_dir / name for name in ['libcoreclr.so', 'libclrjit.so', 'libhostpolicy.so'] if (runtime_dir/name).exists()]
    fxr_files = list(Path(binding['HostPath']).resolve().parent.glob('host/fxr/*/libhostfxr.so'))
    assert len(fxr_files) == 1, 'Use an isolated host with exactly one libhostfxr.so: ' + str(fxr_files)
    runtime_files += fxr_files
    provenance['Arms'][arm]['RuntimeBinding'] = binding
    provenance['Arms'][arm]['RuntimeFiles'] = {str(path): {'Bytes':path.stat().st_size, 'Sha256':hashlib.sha256(path.read_bytes()).hexdigest()} for path in runtime_files}
    for mode, settings in modes.items():
        env = {k: v for k, v in os.environ.items() if not k.startswith(('DOTNET_Tier', 'DOTNET_TC_', 'DOTNET_Jit', 'COMPlus_'))}
        env.update(settings)
        env.update(DOTNET_ReadyToRun='0', DOTNET_JitDisasm=filters, DOTNET_JitStdOutFile=str(folder / f'{mode}.asm'))
        code = run(['dotnet', str(binary), 'hot', str(args.iterations), str(folder / f'{mode}.json')], folder / f'{mode}.log', env)
        provenance['Arms'][arm]['Exits'][mode] = code
        print(f'{arm} {mode}: {code}', flush=True)
        (out / 'provenance.json').write_text(json.dumps(provenance, indent=2) + '\n')
        if code:
            print((folder / f'{mode}.log').read_text()[-4000:], flush=True)

summary = {}
for arm in args.arms:
    rows = []
    for mode in modes:
        asm = out / arm / f'{mode}.asm'
        if not asm.exists():
            continue
        for body in asm.read_text().split('; Assembly listing for method ')[1:]:
            label = body.splitlines()[0]
            total = re.search(r'Total bytes of code (\d+)', body)
            if total is None:
                continue
            rows.append({'Mode': mode, 'Label': label, 'NativeBytes': int(total[1]),
                         'StackSubtract': re.findall(r'sub\s+rsp, (\w+)', body),
                         'Tier': re.findall(r'\((Tier0|Instrumented Tier0|Tier1|Tier1-OSR|FullOpts)\)', label)})
    summary[arm] = rows
(out / 'native-summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(out)
