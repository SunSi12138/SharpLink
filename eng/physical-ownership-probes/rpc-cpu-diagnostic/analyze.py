#!/usr/bin/env python3
"""Offline, standard-library-only analysis of ONE fixed user-mode CPU capture.

No workload, perf, symbol server, build, network, replacement capture or process is
started here. Input files are retained. Fractions describe observed physical
stacks, not logical async ancestry, and are not proof of a fixed per-RPC cost.
"""
import argparse
from collections import Counter
from decimal import Decimal, InvalidOperation
import json
from pathlib import Path
import re
import statistics

FAMILIES = {
    'bootstrap': ('StartServerStreamingInvokerAsync',),
    'supervision': ('FrameworkTaskSupervisor', 'ContinuationTask', 'ContinueWith'),
    'dispatcher': ('StreamDispatcher', 'RequestDispatchers', 'DispatcherEntry'),
    'logical_wrapper': ('LogicalInvocationAsyncEnumerable', 'CompleteLogicalInvocation',
                        'InterceptedAsyncEnumerable', 'InvokeServerStreamingDeferred'),
    'cancellation': ('CancellationTokenSource', 'CancellationTokenRegistration',
                     'CancellationToken', 'ServerCallCancellationState', 'InvocationCancellation'),
    'server_context': ('SharpLinkCallContext', 'SharpLinkServerInvocationContext',
                       'CreateCallContext', 'CreateServerInvocationContext'),
}
OTHER = 'other_or_unknown'
CATEGORIES = ('startup', 'warmup', 'observation', 'teardown', 'unclassified')
PHASE_NAMES = ('process-start', 'warmup-start', 'warmup-end', 'observation-start', 'observation-end', 'process-end')
# perf prints fields in its canonical order, not the order supplied to -F.
HEADER = re.compile(r'^\s*(?P<comm>.+?)\s+(?P<pid>\d+)(?:/(?P<tid>\d+)|\s+(?P<tid2>\d+))'
                    r'\s+(?:\[\d+\]\s+)?(?P<time>\d+\.\d+):\s+'
                    r'(?:(?P<period>\d+)\s+)?(?P<event>\S+):\s*(?P<frame>.*)$')
FRAME = re.compile(r'^\s*(?P<ip>(?:0x)?[0-9a-fA-F]+)\s+(?P<symbol>.*?)\s+\((?P<dso>.*)\)\s*$')
LOST = re.compile(r'(?:lost\s+(\d+)\s+(?:samples?|events?)|(?:LOST|lost)[^\n]*?(?:lost|count)[:= ]+(\d+))', re.I)


def seconds_ns(value):
    """Never convert absolute perf timestamps through binary floating point."""
    scaled = Decimal(value) * 1_000_000_000
    if scaled != scaled.to_integral_value():
        raise ValueError('perf time has sub-nanosecond precision')
    return int(scaled)


def unresolved(frame):
    symbol = frame['symbol'].strip().lower()
    return (not symbol or symbol in ('[unknown]', 'unknown', '??', '[unknown] ([unknown])')
            or bool(re.fullmatch(r'(?:0x)?[0-9a-f]+', symbol)))


def parse_perf(text, stderr=''):
    samples, unknown, lost_lines = [], [], []
    current = None
    for number, line in enumerate(text.splitlines(), 1):
        if not line.strip():
            current = None
            continue
        if 'lost' in line.lower() or 'PERF_RECORD_LOST' in line:
            lost_lines.append({'source': 'perf-script.txt', 'line': number, 'text': line})
            current = None
            continue
        match = HEADER.match(line)
        if match:
            fields = match.groupdict()
            try:
                timestamp = seconds_ns(fields['time'])
            except (ValueError, InvalidOperation) as exc:
                unknown.append({'line': number, 'text': line, 'kind': 'invalid_timestamp', 'error': str(exc)})
                current = None
                continue
            current = {'comm': fields['comm'].strip(), 'pid': int(fields['pid']),
                       'tid': int(fields['tid'] or fields['tid2']),
                       'time_ns': timestamp, 'time_text': fields['time'],
                       'period': int(fields['period']) if fields['period'] else None,
                       'event': fields['event'], 'frames': [], 'line': number,
                       'unparsed_lines': []}
            samples.append(current)
            tail = fields['frame']
            if tail:
                frame = FRAME.match(tail)
                if frame:
                    current['frames'].append(frame.groupdict())
                else:
                    current['unparsed_lines'].append({'line': number, 'text': tail})
            continue
        frame = FRAME.match(line)
        if frame and current is not None:
            current['frames'].append(frame.groupdict())
        elif line.lstrip().startswith('#'):
            unknown.append({'line': number, 'text': line, 'kind': 'comment'})
        else:
            retained = {'line': number, 'text': line, 'kind': 'unrecognized'}
            unknown.append(retained)
            if current is not None:
                current['unparsed_lines'].append(retained)
    for number, line in enumerate(stderr.splitlines(), 1):
        if 'lost' in line.lower():
            lost_lines.append({'source': 'perf-script.stderr', 'line': number, 'text': line})
    # Diagnostic messages can describe the same loss repeatedly. Do NOT sum them.
    reports = [int(m.group(1) or m.group(2)) for line in lost_lines for m in LOST.finditer(line['text'])]
    sample_reports = [int(m.group(1)) for line in lost_lines
                      for m in re.finditer(r'lost\s+(\d+)\s+samples?', line['text'], re.I)]
    event_reports = [int(m.group(1)) for line in lost_lines
                     for m in re.finditer(r'lost\s+(\d+)\s+events?', line['text'], re.I)]
    return {'samples': samples, 'unknown_lines': unknown, 'lost_reports': lost_lines,
            'reported_lost_counts_unspecified_units': reports,
            'reported_lost_sample_counts': sample_reports,
            'reported_lost_event_counts': event_reports,
            'lost_sample_count': max(sample_reports) if sample_reports else None,
            'lost_event_count': max(event_reports) if event_reports else None,
            'lost_count_semantics': 'maximum explicitly parsed report, not summed; absent is unknown, not zero',
            'stderr': stderr}


def read_jsonl(path, errors):
    records = []
    if not path.exists():
        errors.append(f'missing input: {path.name}')
        return records
    for number, line in enumerate(path.read_text().splitlines(), 1):
        if not line.strip():
            continue
        try:
            item = json.loads(line)
            if not isinstance(item, dict):
                raise ValueError('expected JSON object')
            records.append(item)
        except (ValueError, TypeError) as exc:
            errors.append(f'{path.name}:{number}: {exc}; retained text={line!r}')
    return records


def normalize_phase(record):
    return {key: record[key] for key in ('Name', 'Completed', 'MonotonicNs', 'CpuReadEndNs',
                                         'UserUs', 'SystemUs', 'TotalUs', 'Pid')}


def validate_phases(records, errors):
    phases = {}
    previous = -1
    for raw in records:
        try:
            row = normalize_phase(raw)
            for key in ('Completed', 'MonotonicNs', 'CpuReadEndNs', 'Pid'):
                row[key] = int(row[key])
            for key in ('UserUs', 'SystemUs', 'TotalUs'):
                row[key] = float(row[key])
                if not Decimal(str(row[key])).is_finite() or row[key] < 0:
                    raise ValueError(f'invalid {key}')
            if row['Name'] in phases:
                raise ValueError(f'duplicate phase {row["Name"]}')
            if row['MonotonicNs'] < previous or row['CpuReadEndNs'] < row['MonotonicNs']:
                raise ValueError('nonmonotonic phase clock or invalid CPU read interval')
            if row['Completed'] < 0 or row['Pid'] <= 0:
                raise ValueError('invalid Completed or Pid')
            previous = row['MonotonicNs']
            phases[row['Name']] = row
        except (KeyError, ValueError, TypeError, OverflowError) as exc:
            errors.append(f'invalid phase record: {exc}; {raw!r}')
    for name in PHASE_NAMES:
        if name not in phases:
            errors.append(f'missing phase: {name}')
    if set(phases) - set(PHASE_NAMES):
        errors.append('unexpected phase names: ' + ', '.join(sorted(set(phases) - set(PHASE_NAMES))))
    if len({row['Pid'] for row in phases.values()}) > 1:
        errors.append('phase records contain multiple process IDs')
    ordered = [phases[n]['MonotonicNs'] for n in ('process-start', 'warmup-start', 'warmup-end', 'observation-start', 'observation-end', 'process-end') if n in phases]
    if ordered != sorted(ordered):
        errors.append('phase order disagrees with expected lifecycle')
    for name, minimum in (('warmup', 10_000_000_000), ('observation', 20_000_000_000)):
        start, end = phases.get(name + '-start'), phases.get(name + '-end')
        if start and end and end['MonotonicNs'] - start['MonotonicNs'] < minimum:
            errors.append(f'{name} duration below fixed capture minimum {minimum} ns')
    return phases


def category(ns, phases):
    start, end = phases.get('observation-start'), phases.get('observation-end')
    if start is None or end is None:
        return 'unclassified'
    if ns >= end['MonotonicNs']:
        return 'teardown'
    if ns >= start['MonotonicNs']:
        return 'observation'
    warmup = phases.get('warmup-start')
    if warmup and ns >= warmup['MonotonicNs']:
        return 'warmup'  # includes the short warmup-end to observation-start gap
    return 'startup'


def process_cpu(phases, errors):
    result = {}
    for phase in ('warmup', 'observation'):
        start, end = phases.get(phase + '-start'), phases.get(phase + '-end')
        if not start or not end:
            result[phase] = None
            continue
        # RunPhase counters reset at each start; subtract only within this phase.
        completed = end['Completed'] - start['Completed']
        elapsed = end['MonotonicNs'] - start['MonotonicNs']
        deltas = {key: end[key] - start[key] for key in ('UserUs', 'SystemUs', 'TotalUs')}
        valid = completed > 0 and elapsed > 0 and all(v >= 0 for v in deltas.values())
        if not valid:
            errors.append(f'invalid {phase} operation/clock/CPU delta')
        result[phase] = {'completed_rpcs': completed, 'wall_ns': elapsed, 'cpu_us': deltas,
                         'cpu_us_per_rpc': {k: v / completed if valid else None for k, v in deltas.items()},
                         'cpu_read_spans_ns': [start['CpuReadEndNs'] - start['MonotonicNs'],
                                               end['CpuReadEndNs'] - end['MonotonicNs']],
                         'cpu_counters_source': 'single Environment.CpuUsage snapshot; read interval retained',
                         'total_minus_user_system_us': deltas['TotalUs'] - deltas['UserUs'] - deltas['SystemUs'],
                         'valid': valid}
    return result


def family_for(symbol):
    # Ordered tie-break is explicit; matching is heuristic, never ownership proof.
    return next((name for name, needles in FAMILIES.items() if any(n in symbol for n in needles)), None)


def summarize_samples(samples, cpu_per_rpc):
    weighted = bool(samples) and all(s['period'] is not None and s['period'] > 0 for s in samples)
    weights = [s['period'] if weighted else 1 for s in samples]
    denominator = sum(weights)
    inclusive, disjoint, count_inclusive, count_disjoint = Counter(), Counter(), Counter(), Counter()
    unresolved_samples = unresolved_leaf = empty = truncated = parse_incomplete = 0
    symbols = Counter()
    for sample, weight in zip(samples, weights):
        frames = sample['frames']
        seen = [family_for(frame['symbol']) for frame in frames]
        families = set(f for f in seen if f)
        nearest = next((f for f in seen if f), OTHER)
        disjoint[nearest] += weight
        count_disjoint[nearest] += 1
        for family in families:
            inclusive[family] += weight
            count_inclusive[family] += 1
        unresolved_samples += any(unresolved(f) for f in frames) or not frames
        unresolved_leaf += not frames or unresolved(frames[0])
        empty += not frames
        truncated += len(frames) >= 64
        parse_incomplete += bool(sample['unparsed_lines'])
        if frames:
            symbols[frames[0]['symbol']] += weight
    def rows(counter, counts, names):
        return {name: {'samples': counts[name], 'weight': counter[name],
                       'fraction_of_all_observation_samples': counter[name] / denominator if denominator else None,
                       'estimated_user_cpu_us_per_rpc': (cpu_per_rpc * counter[name] / denominator
                            if denominator and counts[name] and cpu_per_rpc is not None else None),
                       'status': 'observed' if counts[name] else 'not_observed_not_zero'} for name in names}
    return {'sample_count': len(samples), 'weight_basis': 'period_fraction' if weighted else 'sample_fraction',
            'weight_denominator': denominator, 'period_sum_if_complete': denominator if weighted else None,
            'period_semantics': 'Sampled-clock weights, not an exact measured user-CPU time; shares are scaled by independent process CPU snapshot.',
            'periods_missing_or_nonpositive': sum(s['period'] is None or s['period'] <= 0 for s in samples),
            'all_parsed_target_samples_in_denominator': True,
            'inclusive_family_presence': rows(inclusive, count_inclusive, FAMILIES),
            'disjoint_nearest_leaf_family': rows(disjoint, count_disjoint, (*FAMILIES, OTHER)),
            'unresolved_samples': unresolved_samples, 'unresolved_leaf_samples': unresolved_leaf,
            'empty_stack_samples': empty, 'potentially_truncated_samples_ge_64_frames': truncated,
            'samples_with_unparsed_stack_lines': parse_incomplete,
            'leaf_symbol_weights': dict(symbols.most_common()),
            'estimator': 'Measured observation process user CPU/RPC multiplied by observed physical-stack weight fraction; not direct family CPU accounting. System CPU is not attributed.',
            'inclusive_warning': 'Inclusive presence overlaps: never sum family rows.',
            'disjoint_warning': 'First recognized family scanning captured frames leaf-to-root, not necessarily the actual executing leaf; unrecognized frames above/below it remain visible in coverage. Not logical async ancestry.'}


def runtime_analysis(events, coverage, phases, errors):
    phase_events = {}
    unknown = []
    for event in events:
        if event.get('provider') != 'SharpLink-RpcCpu-Phase':
            continue
        payload = event.get('payload', {})
        try:
            name = payload['name']
            ns, count = int(payload['monotonicNs']), int(payload['count'])
            read_end = int(payload['cpuReadEndNs'])
            when = Decimal(str(event['timeMs']))
            if not when.is_finite():
                raise ValueError('nonfinite event time')
            if name in phase_events:
                raise ValueError(f'duplicate runtime phase marker {name}')
            ledger = phases.get(name)
            if not ledger or (ns, count, read_end, int(event['pid'])) != (ledger['MonotonicNs'], ledger['Completed'], ledger['CpuReadEndNs'], ledger['Pid']):
                raise ValueError(f'runtime/ledger phase disagreement: {name}')
            phase_events[name] = {'time_ms': when, 'monotonic_ns': ns, 'cpu_read_end_ns': read_end}
        except (KeyError, ValueError, TypeError, InvalidOperation) as exc:
            errors.append(f'invalid runtime phase marker: {exc}')
    missing = sorted(set(phases) - set(phase_events))
    if missing:
        errors.append('missing matching runtime phase markers: ' + ', '.join(missing))
    offsets = [p['monotonic_ns'] - p['time_ms'] * 1_000_000 for p in phase_events.values()]
    offset = statistics.median(offsets) if offsets else None
    offset_spread = max(offsets) - min(offsets) if offsets else None
    starts, ends = phase_events.get('observation-start'), phase_events.get('observation-end')
    if starts and ends and starts['time_ms'] >= ends['time_ms']:
        errors.append('runtime observation markers have invalid order')
        starts = ends = None
    runtime_counts = {name: Counter() for name in CATEGORIES}
    jit, allocation, foreign = [], [], []
    boundary = []
    mapped = []
    target_pid = phases.get('observation-start', {}).get('Pid')
    for event in events:
        if event.get('provider') == 'SharpLink-RpcCpu-Phase':
            continue
        if event.get('pid') is not None and target_pid is not None and str(event['pid']) != str(target_pid):
            foreign.append(event)
            continue
        try:
            when = Decimal(str(event['timeMs']))
            if not when.is_finite():
                raise ValueError('nonfinite runtime timestamp')
        except (KeyError, ValueError, InvalidOperation):
            unknown.append(event)
            continue
        ns = int(when * 1_000_000 + offset) if offset is not None else None
        phase = category(ns, phases) if ns is not None else 'unclassified'
        # Use native EventPipe phase bounds for this stream; mapping is approximate.
        if starts and ends:
            if starts['time_ms'] <= when < ends['time_ms']:
                phase = 'observation'
            elif when >= ends['time_ms']:
                phase = 'teardown'
            elif 'warmup-start' in phase_events and when >= phase_events['warmup-start']['time_ms']:
                phase = 'warmup'
            else:
                phase = 'startup'
        name = str(event.get('name', event.get('eventName', 'unknown')))
        runtime_counts[phase][name] += 1
        item = dict(event, phase=phase, approximate_monotonic_ns=ns)
        mapped.append(item)
        if event.get('provider') != 'Microsoft-Windows-DotNETRuntime':
            unknown.append(item)
        # IL maps/load events may indicate new generated code, including tier changes.
        lowered = name.lower()
        if phase == 'observation' and any(x in lowered for x in ('jit', 'tier', 'method/load', 'methodload', 'method/ilto', 'methodilto')):
            jit.append(item)
        if 'allocation' in lowered:
            allocation.append(item)
        if ns is not None and starts and ends:
            uncertainty = int(offset_spread or 0) + max(p['cpu_read_end_ns'] - p['monotonic_ns'] for p in phase_events.values())
            if min(abs(ns - p['monotonic_ns']) for p in (starts, ends)) <= uncertainty:
                boundary.append(item)
    return {'coverage_input': coverage,
            'phase_events_input': [e for e in events if e.get('provider') == 'SharpLink-RpcCpu-Phase'],
            'matching_phase_markers': sorted(phase_events),
            'missing_phase_markers': missing,
            'clock_mapping': {'method': 'median phase marker offset; runtime category uses EventPipe phase marker timeMs when available',
                              'offset_ns': int(offset) if offset is not None else None,
                              'offset_spread_ns': int(offset_spread) if offset_spread is not None else None,
                              'caveat': 'Marker emission follows CPU reads; mapping is approximate, offset spread/read spans are diagnostics not a strict error bound.'},
            'event_counts_by_phase': {k: dict(v) for k, v in runtime_counts.items()},
            'observation_jit_or_tier_transition_events': jit,
            'stationarity': 'nonstationary_jit_or_code_transition_observed' if jit else 'not_established_no_transition_observed',
            'unexpected_allocation_events': allocation, 'unrecognized_events': unknown,
            'foreign_process_events': foreign, 'near_boundary_events': boundary,
            'mapped_events': mapped,
            'absence_warning': 'No event/sample is not evidence of zero cost or absence; CLR 0x19 Informational coverage is not exhaustive. Method-load/version events do not provide a complete JIT-compilation timeline or proof of warmup stability.'}


def analyze(case_dir):
    directory = Path(case_dir)
    errors, warnings = [], [
        'fp,64 can truncate native or managed stacks, including below 64 frames; inlining and async scheduling hide logical ancestors.',
        'One fixed diagnostic capture, not a baseline/candidate comparison, confidence interval or proof of true fixed cost.',
        'No zero-cost inference from absent samples; no replacement capture or automatic rerun.',
        'Process CPU covers all process threads and profiler overhead; user-mode perf cannot attribute system CPU.',
    ]
    phases = validate_phases(read_jsonl(directory / 'phases.jsonl', errors), errors)
    cpu = process_cpu(phases, errors)
    def read_text(name):
        path = directory / name
        if not path.exists():
            errors.append('missing input: ' + name)
            return ''
        return path.read_text(errors='replace')
    stderr = read_text('perf-script.stderr')
    stderr += '\n[record.stderr]\n' + read_text('record.stderr')
    decode_stderr = directory / 'decode.stderr'
    if decode_stderr.exists():
        stderr += '\n[decode.stderr]\n' + decode_stderr.read_text(errors='replace')
    perf = parse_perf(read_text('perf-script.txt'), stderr)
    events = read_jsonl(directory / 'decoded/events.jsonl', errors)
    try:
        coverage = json.loads((directory / 'decoded/coverage.json').read_text())
        if not isinstance(coverage, dict):
            raise ValueError('coverage must be object')
    except (OSError, ValueError) as exc:
        coverage = {}
        errors.append(f'cannot read decoded/coverage.json: {exc}')
    runtime = runtime_analysis(events, coverage, phases, errors)
    if runtime['foreign_process_events']:
        errors.append('Runtime events contain unexpected process IDs')
    target_pid = phases.get('observation-start', {}).get('Pid')
    identities = {}
    for filename in ('pid.json', 'result.json'):
        try:
            identities[filename] = json.loads((directory / filename).read_text())
            if not isinstance(identities[filename], dict):
                raise ValueError('expected JSON object')
        except (OSError, ValueError) as exc:
            errors.append(f'cannot read {filename}: {exc}')
    try:
        expected_pid = int(identities['pid.json']['pid'])
        result = identities['result.json']
        if expected_pid <= 0 or int(result['processId']) != expected_pid or any(row['Pid'] != expected_pid for row in phases.values()):
            errors.append('phase/pid.json/result.json process ID disagreement')
        end = phases.get('observation-end', {})
        if int(result['operations']) != end.get('Completed'):
            errors.append('observation-end Completed disagrees with result.json operations')
        if result.get('complete') is not True:
            errors.append('result.json is not complete')
    except (KeyError, TypeError, ValueError) as exc:
        errors.append(f'invalid process/result identity: {exc}')
    grouped = {name: [] for name in CATEGORIES}
    excluded = []
    for sample in perf['samples']:
        if sample['pid'] != target_pid or sample['event'] not in ('task-clock:u', 'task-clock'):
            excluded.append(sample)
            errors.append(f'unexpected sample PID/event at perf line {sample["line"]}')
        else:
            grouped[category(sample['time_ns'], phases)].append(sample)
    if any(s['event'] == 'task-clock' for group in grouped.values() for s in group):
        warnings.append('Some perf event labels omit :u; user-only mode must be verified against recorded capture command, not inferred from text.')
    obs_cpu = cpu.get('observation')
    user_per_rpc = obs_cpu['cpu_us_per_rpc']['UserUs'] if obs_cpu else None
    observation = summarize_samples(grouped['observation'], user_per_rpc)
    if not grouped['observation']:
        errors.append('No parsed target-process observation samples: attribution is unavailable, not zero.')
    if any(line['kind'] != 'comment' for line in perf['unknown_lines']) or any(sample['unparsed_lines'] for sample in perf['samples']):
        errors.append('Unparsed nonblank perf records retained: capture sample denominator cannot be established.')
    perf_loss = perf['reported_lost_counts_unspecified_units']
    if (perf['lost_reports'] and (not perf_loss or any(n > 0 for n in perf_loss))) or coverage.get('eventsLost') not in (0, '0'):
        errors.append('Loss reported or runtime loss counter unavailable: missing work is not redistributed.')
    if runtime['unexpected_allocation_events']:
        errors.append('Unexpected allocation events present despite minimal diagnostic intent; retained for coverage, not analyzed as allocation evidence.')
    if observation['potentially_truncated_samples_ge_64_frames']:
        warnings.append('At least one captured stack reaches the 64-frame limit.')
    if errors:
        # Preserve descriptive sample fractions, but never publish CPU estimates on bad alignment/input.
        for table in ('inclusive_family_presence', 'disjoint_nearest_leaf_family'):
            for row in observation[table].values():
                row['estimated_user_cpu_us_per_rpc'] = None
        warnings.append('Input/alignment errors suppress all family CPU/RPC estimates; raw measurements retained.')
    return {'schema_version': 1, 'diagnostic_only': True, 'case_directory': str(directory),
            'status': 'incomplete_or_invalid_inputs' if errors else 'analyzed_with_coverage_limits',
            'errors': errors, 'warnings': warnings, 'phases': phases, 'process_cpu': cpu,
            'identity_inputs': identities,
            'sample_counts_by_phase': {key: len(value) for key, value in grouped.items()},
            'observation': observation, 'runtime': runtime,
            'parser_coverage': {key: value for key, value in perf.items() if key != 'samples'},
            'excluded_samples': excluded, 'samples_by_phase': grouped,
            'family_matching_rules': FAMILIES,
            'retention': 'All parsed samples (including unknown stacks), excluded samples, unrecognized lines, runtime events, stderr and coverage retained; no stationarity filtering.'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('case_dir', type=Path)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    report = analyze(args.case_dir)
    rendered = json.dumps(report, indent=2, allow_nan=False) + '\n'
    if args.output:
        args.output.write_text(rendered)
    else:
        print(rendered, end='')
    return 0 if not report['errors'] else 2


if __name__ == '__main__':
    raise SystemExit(main())
