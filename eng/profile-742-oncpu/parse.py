#!/usr/bin/env python3
"""Exact CLOCK_MONOTONIC/PID slicing of actual user-IP cpu-clock samples."""
import argparse
import collections
import decimal
import json
import pathlib
import re
from common import Blocked, write_json

# perf emits pid/tid together when both fields were requested. Some supported
# versions separate them with whitespace. Unknown record text fails closed.
HEADER = re.compile(r'^\s*(\d+)(?:/|\s+)(\d+)\s+(\d+\.\d{9}):\s+(\d+)\s+(cpu-clock(?::u)?):\s*(.*)$')
FRAME = re.compile(r'^\s*([0-9a-fA-F]+)\s+(.+?)\s+\((.*)\)\s*$')

def ns(text):
    return int(decimal.Decimal(text) * 1_000_000_000)

def parse_script(path):
    samples = []
    current = None
    for number, line in enumerate(pathlib.Path(path).read_text().splitlines(), 1):
        if not line.strip() or line.lstrip().startswith('#'):
            continue
        if 'PERF_RECORD_LOST' in line or re.search(r'\bLOST\b', line):
            raise Blocked(f'Lost sample line {number}: {line}')
        header = HEADER.match(line)
        if header:
            pid, tid, stamp, period, event, rest = header.groups()
            current = dict(pid=int(pid), tid=int(tid), time_ns=ns(stamp), period_ns=int(period), frames=[])
            if current['period_ns'] <= 0:
                raise Blocked('Invalid cpu-clock sample period')
            samples.append(current)
            if not rest.strip():
                continue
            frame = FRAME.match(rest)
            if not frame:
                raise Blocked(f'Unrecognized sample IP on line {number}: {rest}')
            current['frames'].append(frame_dict(frame))
            continue
        frame = FRAME.match(line)
        if current is not None and frame:
            decoded = frame_dict(frame)
            # perf may print the leaf both on the sample header and in callchain.
            if not current['frames'] or decoded != current['frames'][-1]:
                current['frames'].append(decoded)
            continue
        raise Blocked(f'Unrecognized perf script line {number}: {line[:500]}')
    if not samples:
        raise Blocked('No decoded samples')
    return samples

def frame_dict(match):
    address, symbol, dso = match.groups()
    return dict(address=address.lower(), symbol=symbol, dso=dso,
                resolved=symbol not in ('[unknown]', 'unknown', '??') and not symbol.startswith('0x'))

def slice_samples(samples, window, audit):
    if window['clock'] != 'CLOCK_MONOTONIC' or window['clockId'] != 1 or window['beginNs'] >= window['endNs']:
        raise Blocked('Invalid or unaligned measurement clock/window')
    if len(samples) != audit['raw_sample_records']:
        raise Blocked(f'Raw/decoded sample count differs: {audit["raw_sample_records"]}/{len(samples)}')
    if any(s['pid'] != window['pid'] for s in samples):
        raise Blocked('Unexpected process ID in child-only capture')
    inside = [s for s in samples if window['beginNs'] < s['time_ns'] < window['endNs']]
    return inside

def summarize(samples, window, audit, operations, items, executable):
    measured = slice_samples(samples, window, audit)
    if len(measured) < 100:
        raise Blocked(f'Insufficient in-window on-CPU samples: {len(measured)}')
    no_stack = sum(not x['frames'] for x in measured)
    resolved_leaf = sum(bool(x['frames']) and x['frames'][0]['resolved'] for x in measured)
    app = pathlib.Path(executable).name
    app_seen = sum(any(pathlib.Path(f['dso']).name == app and f['resolved'] for f in x['frames']) for x in measured)
    if no_stack or resolved_leaf < 0.9 * len(measured) or app_seen < 100:
        raise Blocked(f'Insufficient native symbols/stacks: no_stack={no_stack}, resolved_leaf={resolved_leaf}, attributed_app={app_seen}, total={len(measured)}')
    groups = collections.defaultdict(lambda: [0, 0])
    inclusive = collections.defaultdict(lambda: [0, 0])
    leaves = collections.defaultdict(lambda: [0, 0])
    total = sum(s['period_ns'] for s in measured)
    for sample in measured:
        # DSO basename and resolved symbol are stable across process address randomization.
        frames = [pathlib.Path(f['dso']).name + '!' + f['symbol'] for f in sample['frames']]
        full = ';'.join(reversed(frames))
        for group, key in ((groups, full), (leaves, frames[0])):
            group[key][0] += 1
            group[key][1] += sample['period_ns']
        for frame in set(frames):
            inclusive[frame][0] += 1
            inclusive[frame][1] += sample['period_ns']
    def rows(values, key):
        return sorted([dict(**{key: k}, samples=v[0], observed_period_ns=v[1],
                            samples_per_operation=v[0]/operations, observed_period_ns_per_operation=v[1]/operations,
                            observed_period_ns_per_item=v[1]/items, fraction_of_sample_weight=v[1]/total)
                       for k, v in values.items()], key=lambda r: -r['observed_period_ns'])
    return dict(schema_version=1, diagnostic_only=True, event='cpu-clock:u', clock='CLOCK_MONOTONIC',
                pid=window['pid'], begin_ns=window['beginNs'], end_ns=window['endNs'],
                operations=operations, items=items, in_window_samples=len(measured),
                excluded_samples=len(samples)-len(measured), raw_sample_records=len(samples),
                resolved_leaf_samples=resolved_leaf, app_stack_samples=app_seen, no_stack_samples=no_stack,
                observed_period_ns=total, observed_period_ns_per_operation=total/operations,
                events_lost=audit['events_lost'], throttle_records=audit['throttle_records'],
                caveat='Actual user-IP on-CPU software-clock samples. Period weights are sampled observations, not exact per-method CPU or call counts. Kernel CPU is excluded. Inline/caller attribution and unwind omissions remain limitations. Inclusive rows overlap. NativeAOT results cannot explain JIT causally. Observer controls and independent untraced timings are mandatory.',
                leaf_methods=rows(leaves, 'method'), inclusive_methods=rows(inclusive, 'method'), stacks=rows(groups, 'stack'))

def calibration(samples, window, audit):
    measured = slice_samples(samples, window, audit)
    def hits(rows, method):
        return [s for s in rows if any(method in f['symbol'] for f in s['frames'])]
    before = [s for s in samples if s['time_ns'] < window['beginNs']]
    after = [s for s in samples if s['time_ns'] > window['endNs']]
    busy_main = hits(measured, 'PerfBusyInside')
    busy_worker = hits(measured, 'PerfWorkerInside')
    sleep = [s for s in measured if window['sleepBeginNs'] + 20_000_000 < s['time_ns'] < window['endNs'] - 20_000_000]
    result = dict(before_samples=len(hits(before, 'PerfBusyBefore')), inside_main_samples=len(busy_main),
                  inside_worker_samples=len(busy_worker), after_samples=len(hits(after, 'PerfBusyAfter')),
                  parked_interval_samples=len(sleep), user_cpu_ns=window['userCpuNs'],
                  observed_period_ns=sum(s['period_ns'] for s in measured))
    checks = dict(before_attributed=result['before_samples'] >= 10, after_attributed=result['after_samples'] >= 10,
                  main_busy_attributed=len(busy_main) >= 25, worker_busy_attributed=len(busy_worker) >= 25,
                  worker_inherited=any(s['tid'] == window['workerTid'] for s in busy_worker),
                  outside_excluded=not hits(measured, 'PerfBusyBefore') and not hits(measured, 'PerfBusyAfter'),
                  parked_excluded=len(sleep) <= 3,
                  period_cpu_sanity=0.5 < result['observed_period_ns']/window['userCpuNs'] < 1.5)
    if not all(checks.values()):
        raise Blocked(f'Clock/on-CPU/symbol calibration failed: {checks}; {result}')
    return dict(checks=checks, **result)

if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('script', type=pathlib.Path)
    p.add_argument('window', type=pathlib.Path)
    p.add_argument('audit', type=pathlib.Path)
    p.add_argument('output', type=pathlib.Path)
    p.add_argument('--calibration', action='store_true')
    p.add_argument('--executable')
    a = p.parse_args()
    w = json.loads(a.window.read_text())
    samples = parse_script(a.script)
    audit = json.loads(a.audit.read_text())
    result = calibration(samples, w, audit) if a.calibration else summarize(samples, w, audit, w['operations'], w['items'], a.executable)
    write_json(a.output, result)
