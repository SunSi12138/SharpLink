#!/usr/bin/env python3
"""Completeness/accounting gate for the explicitly limited 2.0.4 matrix only."""
import argparse
from collections import Counter
import json
import math
from pathlib import Path
import re
from release_204_evidence import (add_context_arguments, context_from_args, expected_script_hashes,
                                  read_json, validate_provenance)

TRANSPORTS = ['tcp', 'uds', 'namedpipe', 'anonymous', 'sharedmemory']
PROFILES = ['balanced', 'lowlatency', 'throughput']
PAYLOADS = [0, 32, 65536, 1048576]
CONCURRENCY = [1, 32]
WORKLOADS = ['unary', 'oneway', 'oneway-backpressure', 'async', 'streams']


def expected():
    for transport in TRANSPORTS:
        for profile in PROFILES:
            for workload in WORKLOADS:
                shard = f'{transport}-{profile}-{workload}'
                for repetition in range(1, 4):
                    for pool in ([1] if transport == 'anonymous' else [1, 4]):
                        prefix = f'jit-r{repetition}-{transport}-{profile}-p1-{pool}'
                        cases = ([(f'unary-{p}', 'empty' if p == 0 else 'echo', p, CONCURRENCY) for p in PAYLOADS]
                                 if workload == 'unary' else
                                 [('oneway', 'oneway', 0, [1])] if workload == 'oneway' else
                                 [('oneway-backpressure', 'oneway', 0, CONCURRENCY)] if workload == 'oneway-backpressure' else
                                 [(op, op, 0, CONCURRENCY) for op in ['yield', 'delay']] if workload == 'async' else
                                 [('streams', 'all', None, CONCURRENCY)])
                        for suffix, operation, payload, concurrency in cases:
                            yield shard, prefix + '-' + suffix + '.json', transport, profile, workload, pool, operation, payload, concurrency


def validate(root, sha, context, script_hashes):
    failures = []
    files = list(root.glob('*/raw/*.json'))
    actual = {f'{p.parent.parent.name}/{p.name}': p for p in files}
    wanted = list(expected())
    keys = {f'{row[0]}/{row[1]}' for row in wanted}
    if set(actual) != keys:
        failures.append(f'File coverage mismatch: missing={sorted(keys-set(actual))}; unexpected={sorted(set(actual)-keys)}')
    stages = 0
    def check(ok, message):
        if not ok:
            failures.append(message)
    for shard, name, transport, profile, workload, pool, operation, payload, concurrency in wanted:
        path = actual.get(f'{shard}/{name}')
        if path is None:
            continue
        label = f'{shard}/{name}'
        try:
            d = read_json(path)
            check(d['SourceCommit'] == sha and type(d['SchemaVersion']) is int and d['SchemaVersion'] == 1, label + ': source/schema mismatch')
            check(d['Workload'] == ('SharpLink.StreamLoadTest' if workload == 'streams' else 'SharpLink.LoadTest'), label + ': workload identity')
            check(d['AssemblyVersion'] == '2.0.4.0', label + ': report assembly version')
            c = d['Configuration']
            config = dict(Mode=0, Transport=TRANSPORTS.index(transport), PerformanceProfile=PROFILES.index(profile),
                          MinConnections=1, MaxConnections=pool, Operation=operation, ConcurrencyConfig=concurrency,
                          WarmupSeconds=5, DurationSeconds=20, MaximumRecordedOperations=300000000)
            if payload is not None:
                config['PayloadSize'] = payload
                config['MetricsPort'] = 0
            else:
                config['StreamSize'] = 256
            for key, value in config.items():
                check(type(c.get(key)) is type(value) and c.get(key) == value, f'{label}: {key} expected {value!r}, got {c.get(key)!r}')
            check(all(type(value) is int for value in c['ConcurrencyConfig']), label + ': invalid concurrency configuration')
            queue = None if workload == 'oneway-backpressure' else 67108864
            check(type(c.get('MaxSendQueueBytes')) is type(queue) and c.get('MaxSendQueueBytes') == queue, label + ': queue override mismatch')
            operations = ['unary', 'c2s', 's2c', 'duplex'] if workload == 'streams' else [operation]
            check(Counter((r['Operation'], r['Concurrency']) for r in d['Results']) == Counter((o, n) for o in operations for n in concurrency), label + ': stage coverage mismatch')
            check(type(d['Results']) is list, label + ': results must be an array')
            for r in d['Results']:
                stages += 1
                tag = f"{label} {r['Operation']}/c{r['Concurrency']}"
                check(r['RecorderMode'] == 'formal' and r['FormalComparable'] is True and r['RecorderVersion'] == 'worker-local-shared-capacity-v4', tag + ': recorder mismatch')
                success, failed = r['Success'], r['Failure']
                for key in ['Success', 'Failure', 'OperationsStartedDuringMeasurement', 'OperationsCompleted',
                            'SampleCount', 'MaximumSampleCapacity', 'WorkerCount', 'Concurrency', 'StopwatchFrequency']:
                    check(type(r[key]) is int and r[key] >= 0, tag + ': invalid integer ' + key)
                check(success > 0 and failed >= 0, tag + ': invalid counters')
                check(r['OperationsStartedDuringMeasurement'] == r['OperationsCompleted'] == success + failed, tag + ': incomplete drain/accounting')
                check(r['SampleCount'] == success and r['MaximumSampleCapacity'] == 300000000 and success <= 300000000, tag + ': sample coverage/capacity mismatch')
                mandatory = ['ValidationFailure', 'Cancelled'] if workload == 'streams' else ['TailObserverFailure', 'TailObserverSampleCount', 'SendQueueBackpressureRetries']
                for key in mandatory:
                    check(type(r[key]) is int and r[key] >= 0, tag + ': invalid integer ' + key)
                for key in ['ValidationFailure', 'Cancelled', 'TailObserverFailure']:
                    if key in r:
                        check(type(r[key]) is int and r[key] == 0, tag + ': ' + key)
                # The frozen recorder cannot distinguish every ResourceExhausted
                # cause. A first-example log line is never a waiver for any failure.
                check(failed == 0 and r['TopFailures'] == '', tag + ': nonzero/unclassified measured failures')
                if workload == 'oneway-backpressure':
                    check(r['SendQueueBackpressureRetries'] == 0, tag + ': raw backpressure must not retry')
                for key in ['P50Us', 'P95Us', 'P99Us', 'P999Us', 'AvgUs', 'MinUs', 'MaxUs', 'Qps', 'ErrorRatePercent', 'MeasurementDurationSeconds', 'WarmupDurationSeconds', 'DrainDurationSeconds']:
                    value = r.get(key)
                    check(type(value) in (int, float) and math.isfinite(value) and value >= 0, tag + ': invalid ' + key)
                check(r['MeasurementDurationSeconds'] >= 20 and r['WarmupDurationSeconds'] >= 5, tag + ': shortened window')
                check(math.isclose(r['Qps'], success / r['MeasurementDurationSeconds'], rel_tol=1e-9, abs_tol=1e-9), tag + ': inconsistent QPS')
                check(r['ErrorRatePercent'] == 0, tag + ': nonzero error rate')
                check(r['MinUs'] <= r['P50Us'] <= r['P95Us'] <= r['P99Us'] <= r['P999Us'] <= r['MaxUs'] and
                      r['MinUs'] <= r['AvgUs'] <= r['MaxUs'], tag + ': invalid latency ordering')
                check(r['StopwatchFrequency'] > 0 and r['WorkerCount'] == r['Concurrency'], tag + ': worker/timing mismatch')
        except (KeyError, ValueError, TypeError, OSError, ZeroDivisionError, OverflowError) as e:
            failures.append(f'{label}: invalid report: {e}')
    for shard in sorted({row[0] for row in wanted}):
        failures.extend(validate_provenance(root / shard, context, script_hashes, {'SharpLink.LoadTest', 'SharpLink.StreamLoadTest'}))
    return dict(scope='2.0.4 explicitly limited release matrix; NOT full matrix', SourceCommit=sha,
                ExpectedFiles=729, Files=len(files), ExpectedStages=1863, Stages=stages,
                MatrixPassed=not failures, Failures=failures, **{key: value for key, value in context.items() if key != 'SourceCommit'})


def main():
    p = argparse.ArgumentParser()
    p.add_argument('root', type=Path)
    p.add_argument('sha')
    p.add_argument('output', type=Path)
    add_context_arguments(p)
    a = p.parse_args()
    if not re.fullmatch('[0-9a-f]{40}', a.sha):
        p.error('SHA must be 40 lowercase hex characters')
    result = validate(a.root, a.sha, context_from_args(a), expected_script_hashes(a.source_root, a.sha))
    a.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({k: v for k, v in result.items() if k != 'Failures'}))
    raise SystemExit(0 if result['MatrixPassed'] else 1)


if __name__ == '__main__':
    main()
