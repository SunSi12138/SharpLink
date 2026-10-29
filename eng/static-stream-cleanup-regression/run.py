#!/usr/bin/env python3
"""New ordinary source regression, never an old performance/evidence campaign.

Default: three conditions x early-break/explicit-dispose = six fresh processes.
Optional --termination consumer-throw adds a separately labelled correctness case.
All projects (including the source generator) are built from this same checkout.
No upload, package publication, performance timing, or prior-result equivalence.
Extended: baseline9 + long non-expiring method deadline3 + enum-only cancellation1
+ static None noncooperative MoveNext/Dispose ownership1.
Coverage: cooperative serverstream; default request timeout disabled.
Not covered here: actual deadline expiry, duplex, dynamic module retirement/unload.
"""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parent
BASE_CONDITIONS = ('static-none', 'static-cancellable', 'dynamic-none')
CONDITIONS = BASE_CONDITIONS + ('static-deadline', 'static-enumerator-only', 'static-none-held')
TERMINATIONS = ('early-break', 'explicit-dispose', 'consumer-throw', 'enumerator-cancel')


def probe_command(dotnet, probe, condition, termination, runtime_version=None):
    """Build the launch command; explicit runtime selection must be independently witnessed."""
    if runtime_version is None:
        return [dotnet, str(probe), condition, termination]
    if runtime_version != '10.0.14':
        raise ValueError('Only the explicitly reviewed runtime10.0.14 may be pinned')
    return [dotnet, 'exec', '--fx-version', runtime_version, str(probe),
            condition, termination, runtime_version]


def probe_environment(runtime_dotnet, inherited=None):
    """Isolate only child execution; SDK build environment is never modified."""
    if runtime_dotnet is None:
        return None
    host = Path(shutil.which(runtime_dotnet) or runtime_dotnet).resolve()
    root = str(host.parent)
    env = dict(os.environ if inherited is None else inherited)
    env.update(DOTNET_ROOT=root, DOTNET_ROOT_X64=root, DOTNET_HOST_PATH=str(host),
               DOTNET_MULTILEVEL_LOOKUP='0', PATH=root + os.pathsep + env.get('PATH', ''))
    return env


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet', help='SDK host used for builds.')
    parser.add_argument('--runtime-dotnet', help='Execution-only host; defaults to --dotnet.')
    parser.add_argument('--runtime-version', choices=('10.0.14',),
                        help='Pin probe execution and require actual runtime identity in each result.')
    parser.add_argument('--configuration', default='Release', choices=('Debug', 'Release'))
    parser.add_argument('--extended', action='store_true', help='Run baseline9 + long-deadline3 + enum-only1 + static-none-held1 newly reconstructed cases.')
    parser.add_argument('--no-build', action='store_true', help='Use only after building this exact current source.')
    parser.add_argument('--condition', choices=CONDITIONS, action='append')
    parser.add_argument('--termination', choices=TERMINATIONS, action='append')
    args = parser.parse_args()
    if args.extended and (args.condition or args.termination):
        parser.error('--extended cannot be combined with case selectors')
    if args.extended:
        cases = [(c, t) for c in BASE_CONDITIONS + ('static-deadline',) for t in TERMINATIONS[:3]]
        cases.append(('static-enumerator-only', 'enumerator-cancel'))
        cases.append(('static-none-held', 'explicit-dispose'))
    else:
        cases = [(c, t) for c in (args.condition or BASE_CONDITIONS)
                 for t in (args.termination or TERMINATIONS[:2])]
    if any((c == 'static-enumerator-only') != (t == 'enumerator-cancel') for c, t in cases):
        parser.error('static-enumerator-only must be paired only with enumerator-cancel')
    if any(c == 'static-none-held' and t != 'explicit-dispose' for c, t in cases):
        parser.error('static-none-held must be paired only with explicit-dispose')
    if not args.no_build:
        for project in ('module', 'probe'):
            subprocess.run([args.dotnet, 'build', str(ROOT / project / f'{project}.csproj'),
                            '-c', args.configuration, '-m:1', '-nr:false',
                            '-p:UseSharedCompilation=false'], check=True)
        shutil.copy2(ROOT / 'module/bin' / args.configuration / 'net10.0/StaticStreamCleanupModule.dll',
                     ROOT / 'probe/bin' / args.configuration / 'net10.0/StaticStreamCleanupModule.dll')
    probe = ROOT / 'probe/bin' / args.configuration / 'net10.0/StaticStreamCleanupProbe.dll'
    if not probe.is_file() or not probe.with_name('StaticStreamCleanupModule.dll').is_file():
        parser.error('Both current-source probe and module must be built; no stale fallback.')
    failures = []
    for condition, termination in cases:
        label = f'{condition}/{termination}'
        print(f'NEW_CORRECTNESS_CASE {label}', flush=True)
        try:
            result = subprocess.run(probe_command(args.runtime_dotnet or args.dotnet, probe, condition, termination,
                                                  args.runtime_version), timeout=100,
                                    env=probe_environment(args.runtime_dotnet))
            if result.returncode:
                failures.append(label)
        except subprocess.TimeoutExpired:
            # Entire child killed, not retried or accepted after teardown.
            failures.append(label + ' (process timeout)')
    print(f'NEW_CORRECTNESS_RESULT cases={len(cases)} failures={failures}', flush=True)
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
