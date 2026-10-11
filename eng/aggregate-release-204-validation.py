#!/usr/bin/env python3
"""This workflow is necessary limited evidence, never sufficient release authority."""
import argparse
import json
import math
from datetime import datetime
from pathlib import Path
from release_204_evidence import (add_context_arguments, context_from_args, expected_script_hashes,
                                  read_json, validate_provenance)

REQUIRED_JOBS = {'tooling-checks', 'sampler', 'matrix', 'release-soak', 'abi-churn', 'dependency-scan'}
EXTERNAL_GATES = ['Exact-source three-platform Release Gate, package/AOT/compatibility checks',
                  'Protected merge and required reviews; fresh main/tag identity check',
                  'Release environment, publication approval and security configuration']
SOAK_OPERATIONS = {'Unary', 'ServerStreamingEarlyBreak', 'ClientStreaming', 'Cancellation', 'OneWay', 'DuplexStreaming'}
SOAK_METRICS = {'sharplink.connections.active', 'sharplink.calls.active', 'sharplink.requests.pending',
                'sharplink.streams.active', 'sharplink.send.queue.bytes'}


def validate_soak_report(report, sha, transport):
    failures, timing = [], {}
    def check(ok, message):
        if not ok:
            failures.append(transport + ': ' + message)
    try:
        check(report['Commit'] == sha and report['WorkingTreeDirty'] is False and report['Status'] == 'Passed' and
              report['IsFinal'] is True and type(report['ExitCode']) is int and report['ExitCode'] == 0, 'invalid soak final result')
        # Canonical Chaos starts its duration CTS before setup/warmup, then resets
        # soakStarted after warmup. ActualElapsedSeconds therefore is not the CTS window.
        started, ended = (datetime.fromisoformat(report[key]) for key in ('StartedUtc', 'TimestampUtc'))
        check(started.tzinfo is not None and ended.tzinfo is not None, 'timestamps need explicit timezones')
        wall = (ended - started).total_seconds()
        actual = report['ActualElapsedSeconds']
        check(math.isfinite(wall) and wall >= 600, 'short total wall interval')
        check(type(actual) in (int, float) and math.isfinite(actual) and actual > 0, 'invalid reported post-warmup interval')
        timing = dict(DurationSeconds=report['DurationSeconds'], WallElapsedSeconds=wall, ActualElapsedSeconds=actual)
        check(report['DurationSeconds'] == 600 and report['Concurrency'] == 32 and report['CheckpointIntervalSeconds'] == 120 and
              report['RestartIntervalSeconds'] == 60 and report['Transport'] == transport, 'soak settings mismatch')
        check(report['StopOnUnexpectedFailure'] is True and report['DumpOnFailure'] is True and report['Drain']['Drained'] is True and
              report['UnexpectedFailures'] == 0 and report['UnobservedTaskExceptions'] == 0 and not report['ClientErrors'] and not report['ServerErrors'],
              'soak correctness failure')
        check(report['Success'] > 0 and report['ExpectedFailures'] > 0 and report['RestartCount'] > 0, 'incomplete fault/recovery coverage')
        attempts = report['OperationAttempts']
        check(set(attempts) == SOAK_OPERATIONS and all(type(value) is int and value > 0 for value in attempts.values()), 'incomplete six-operation coverage')
        for label, metrics in [('final', report['FinalMetrics']), ('drain', report['Drain']['Metrics'])]:
            check(set(metrics) == SOAK_METRICS and all(type(value) is int and value == 0 for value in metrics.values()), label + ' resources not zero')
    except (KeyError, ValueError, TypeError, OSError, OverflowError) as error:
        failures.append(transport + ': invalid soak report: ' + str(error))
    return failures, timing


def validate_independent_reports(root, sha, soak_timings=None):
    failures = []
    def check(ok, message):
        if not ok:
            failures.append(message)
    try:
        baseline = root / f'release-204-sampler-{sha}' / 'baseline'
        gate = read_json(baseline / 'macro/gate-analysis.json')
        check(gate['SourceCommit'] == sha and gate['GatePassed'] is True and not gate['Failures'], 'Strict sampler analysis failed/mismatched')
        check({row['Concurrency'] for row in gate['MacroGates']} == {128, 512}, 'Sampler macro coverage mismatch')
        for row in gate['MacroGates']:
            check(all(row[key] is True for key in ['ThroughputGatePassed', 'ObserverP99GatePassed', 'ObserverP999GatePassed']), 'Sampler threshold failure')
        reports = read_json(baseline / 'report-validation.json')
        check(reports['SourceCommit'] == sha and reports['Passed'] is True and not reports['Failures'], 'Sampler report validation failed/mismatched')
        micro = read_json(baseline / 'micro/latency-recorder.json')
        check(micro['SourceCommit'] == sha and micro['TargetRecordsPerScenario'] == 1000000 and micro['Repetitions'] == 5 and len(micro['Measurements']) == 100, 'Sampler micro settings/coverage mismatch')
        for transport, enum in [('tcp', 'Tcp'), ('sharedmemory', 'SharedMemory')]:
            report = read_json(root / f'release-204-soak-{transport}-{sha}' / 'release-soak.json')
            soak_failures, timing = validate_soak_report(report, sha, enum)
            failures.extend(soak_failures)
            if soak_timings is not None:
                soak_timings[transport] = timing
        lines = (root / f'release-204-abi-churn-{sha}' / 'churn/summary.txt').read_text().splitlines()
        values = dict(line.split('=', 1) for line in lines)
        check(len(values) == len(lines) and values['commit'] == sha and int(values['duration_seconds']) >= 600 and int(values['rounds']) > 0,
              'ABI churn source/duration/rounds mismatch')
        rounds = int(values['rounds'])
        for key, multiplier in [('test_processes', 9), ('client_registration_replacements', 100), ('route_publication_before_old_drain', 1),
                                ('api4_stream_exits', 5), ('registration_rejections', 1), ('framework_reference_unloads', 1)]:
            check(int(values[key]) == rounds * multiplier, 'ABI churn incomplete ' + key)
        check(values['api4_stream_exit_modes'] == 'normal,cancellation-before-first,cancellation-mid-stream,consumer-break,service-exception',
              'ABI churn stream modes mismatch')
    except (KeyError, ValueError, TypeError, OSError) as error:
        failures.append('Missing/invalid independent gate report: ' + str(error))
    return failures


def aggregate(matrix, conclusions, context, evidence_failures):
    failures = list(evidence_failures)
    if set(conclusions) != REQUIRED_JOBS:
        failures.append('Missing or unexpected independent job conclusions')
    for name, result in conclusions.items():
        if result != 'success':
            failures.append(name + ': ' + str(result))
    identity_matches = all(type(matrix.get(key)) is type(value) and matrix.get(key) == value for key, value in context.items())
    matrix_passed = matrix.get('MatrixPassed') is True and identity_matches
    if not matrix_passed:
        failures.append('Matrix did not pass with this exact provenance')
    limited_passed = matrix_passed and not failures
    return dict(**context, MatrixPassed=matrix_passed, JobConclusions=conclusions,
                LimitedValidationPassed=limited_passed,
                FinalMainLimitedValidationPassed=limited_passed and context['FinalMain'],
                ReleaseEligible=False, UnverifiedExternalReleaseRequirements=EXTERNAL_GATES,
                Failures=failures,
                Scope='Limited 2.0.4 validation only; publication requires separately verified release requirements')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('root', type=Path)
    parser.add_argument('sha')
    parser.add_argument('matrix', type=Path)
    parser.add_argument('conclusions', type=Path)
    parser.add_argument('output', type=Path)
    add_context_arguments(parser)
    args = parser.parse_args()
    context = context_from_args(args)
    failures = []
    soak_timings = {}
    try:
        scripts = expected_script_hashes(args.source_root, args.sha)
        for artifact, binaries in {
            f'release-204-sampler-{args.sha}': {'SharpLink.Benchmarks', 'SharpLink.LoadTest', 'SharpLink.StreamLoadTest'},
            f'release-204-soak-tcp-{args.sha}': {'SharpLink.ChaosTests'},
            f'release-204-soak-sharedmemory-{args.sha}': {'SharpLink.ChaosTests'},
            f'release-204-abi-churn-{args.sha}': {'SharpLink.IntegrationTests'},
            f'release-204-dependencies-{args.sha}': None,
        }.items():
            failures.extend(validate_provenance(args.root / artifact, context, scripts, binaries))
        dependency = read_json(args.root / f'release-204-dependencies-{args.sha}' / 'dependency-validation.json')
        if dependency.get('DependencyScansPassed') is not True or dependency.get('Failures') or dependency.get('Findings'):
            failures.append('Dependency scan did not pass')
        failures.extend(validate_independent_reports(args.root, args.sha, soak_timings))
    except (KeyError, ValueError, TypeError, OSError) as error:
        failures.append('Invalid independent evidence: ' + str(error))
    try:
        matrix = read_json(args.matrix)
    except (ValueError, OSError):
        matrix = {}
    try:
        needs = read_json(args.conclusions)
        conclusions = {name: job['result'] for name, job in needs.items()}
    except (KeyError, ValueError, TypeError, OSError, AttributeError):
        conclusions = {}
    result = aggregate(matrix, conclusions, context, failures)
    result['SoakTimings'] = soak_timings
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))
    raise SystemExit(0 if result['LimitedValidationPassed'] else 1)


if __name__ == '__main__':
    main()
