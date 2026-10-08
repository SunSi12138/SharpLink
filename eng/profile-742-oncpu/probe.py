#!/usr/bin/env python3
"""Cheap fail-closed existing-perf capability test. A blocked diagnostic exits zero."""
import argparse
import json
import os
import pathlib
import shutil
from common import KIT, Blocked, build_id, decode, host_identity, record_command, require, sha, write_json
from parse import calibration, parse_script

def probe(out):
    out.mkdir(parents=True, exist_ok=False)
    result = dict(schema_version=1, state='unknown', profiling_pass=False,
                  message='Availability is unknown until this host completes the recorded probe.',
                  host=host_identity(), source_sha256={name: sha(KIT/name) for name in ('probe.py','common.py','parse.py','calibration.c')})
    try:
        host = result['host']
        if host['machine'] != 'x86_64' or host['euid'] == 0:
            raise Blocked('This probe requires an unprivileged Linux x86_64 runner; it will not elevate or run as root.')
        if int(host['cap_eff'] or '0', 16) & ((1 << 38) | (1 << 21) | (1 << 19)):
            raise Blocked('Existing elevated perf/admin/ptrace capabilities would invalidate an unprivileged capability proof.')
        perf = shutil.which('perf')
        result['perf_path'] = perf
        if not perf:
            raise Blocked('Existing perf executable not found on PATH. No install or fallback was attempted.')
        path = pathlib.Path(perf).resolve()
        if path.stat().st_mode & 0o6000:
            raise Blocked('Existing perf is setuid/setgid; refusing implicit elevation.')
        try:
            capabilities = os.getxattr(path, 'security.capability')
        except OSError:
            capabilities = b''
        if capabilities:
            raise Blocked('Existing perf has file capabilities; refusing implicit privilege acquisition.')
        result['perf_sha256'] = sha(path)
        result['perf_version'] = require([perf, '--version'], out, 'perf-version', timeout=15).strip()
        # Tools below are used only after an executable perf has been verified.
        cc, readelf, taskset = (shutil.which(name) for name in ('cc', 'readelf', 'taskset'))
        result['support_tools'] = dict(cc=cc, readelf=readelf, taskset=taskset)
        if not all((cc, readelf, taskset)):
            raise Blocked('Existing cc/readelf/taskset is missing; no installation was attempted.')
        require([cc, '--version'], out, 'cc-version', timeout=15)
        require([readelf, '--version'], out, 'readelf-version', timeout=15)
        executable = out / 'perf-calibration'
        require([cc, '-O2', '-g', '-fno-omit-frame-pointer', '-fno-inline', '-pthread',
                 '-Werror', '-Wall', '-Wextra', KIT/'calibration.c', '-o', executable], out, 'build-calibration', timeout=30)
        result['calibration_elf_sha256'] = sha(executable)
        result['calibration_build_id'] = build_id(readelf, executable, out, 'calibration-build-id')
        cpus = host['affinity'][:4]
        if len(cpus) != 4:
            raise Blocked('Four available CPUs are required; no silent processor-count change.')
        result['affinity'] = cpus
        trace = out/'calibration.perf.data'
        window = out/'calibration.window.json'
        command = [taskset, '-c', ','.join(map(str, cpus)), *record_command(perf, trace, executable, [window])]
        require(command, out, 'record-calibration', timeout=30)
        if not trace.is_file() or not window.is_file():
            raise Blocked('Collector exited without complete trace and calibration window.')
        audit = decode(perf, trace, out)
        symbols = require([perf, 'buildid-list', '-i', trace], out, 'recorded-build-ids')
        if result['calibration_build_id'] not in symbols.lower():
            raise Blocked('The calibration ELF build ID is absent from recorded perf mappings.')
        result['calibration'] = calibration(parse_script(out/'script.stdout'), json.loads(window.read_text()), audit)
        result.update(state='available', capability_pass=True,
                      message='Existing unprivileged perf cpu-clock:u, monotonic alignment, user stacks and inherited worker sampling calibrated on this host. NativeAOT symbolization and observer acceptance remain unproven.')
    except Blocked as error:
        result.update(state='blocked', capability_pass=False, message=str(error))
    finally:
        write_json(out/'capability.json', result)
    return result

if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('output', type=pathlib.Path)
    p.add_argument('--github-output', type=pathlib.Path)
    a = p.parse_args()
    result = probe(a.output.resolve())
    print(json.dumps(result, indent=2))
    if a.github_output:
        with a.github_output.open('a') as f:
            f.write('supported=' + str(result['state'] == 'available').lower() + '\n')
    # A recorded unsupported/denied capability is a successful diagnostic, never a profiling pass.
