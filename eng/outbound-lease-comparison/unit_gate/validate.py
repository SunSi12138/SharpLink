#!/usr/bin/env python3
"""Strict local-only parsing for MTP discovery and TUnit 1.73.19 JSON results.

Never print the report: it contains stdout, stack traces and spans.
UIDs are exact opaque identifiers, not display-name regex matches. The digest is
SHA-256 of lexically sorted UTF-8 UIDs, each followed by one LF (including last).
"""
from __future__ import annotations

from collections import Counter
import hashlib
import json
from pathlib import Path


class GateError(ValueError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateError(message)


def unique_uids(values: object, label: str) -> list[str]:
    require(isinstance(values, list) and len(values) > 0, f"{label}: empty or invalid UID list")
    require(all(isinstance(v, str) and v and not any(c in v for c in '\r\n\x00') for v in values),
            f"{label}: invalid UID")
    require(len(set(values)) == len(values), f"{label}: duplicate UID")
    return sorted(values)


def uid_digest(uids: list[str]) -> str:
    return hashlib.sha256(''.join(uid + '\n' for uid in sorted(uids)).encode('utf-8')).hexdigest()


def strict_json(text: str) -> object:
    def pairs(items):
        obj = {}
        for key, value in items:
            require(key not in obj, "duplicate JSON property")
            obj[key] = value
        return obj
    return json.loads(text, object_pairs_hook=pairs,
                      parse_constant=lambda _: (_ for _ in ()).throw(GateError("non-finite JSON number")))


def discovery_uids(document: object) -> list[str]:
    require(isinstance(document, dict) and type(document.get('schemaVersion')) is int
            and document['schemaVersion'] == 1,
            "unsupported discovery schema")
    tests = document.get('tests')
    require(isinstance(tests, list) and all(isinstance(t, dict) for t in tests),
            "invalid discovery tests")
    return unique_uids([t.get('uid') for t in tests], 'discovery')


def read_report(path: Path) -> dict:
    result = strict_json(path.read_text(encoding='utf-8-sig'))
    require(isinstance(result, dict), 'invalid report document')
    return result


def check_summary(summary: object, count: int, label: str) -> None:
    keys = {'total', 'passed', 'failed', 'skipped', 'cancelled', 'timedOut', 'flaky'}
    require(isinstance(summary, dict) and set(summary) == keys, f'{label}: unsupported counters')
    require(all(type(v) is int and v >= 0 for v in summary.values()), f'{label}: invalid counters')
    require(summary['total'] == count and summary['passed'] == count
            and all(summary[k] == 0 for k in keys - {'total', 'passed'}),
            f'{label}: not complete and all-passing; counters={json.dumps(summary, sort_keys=True)}')


def validate_run(discovered: list[str], report: dict, exit_code: int,
                 timed_out: bool = False, on_stage=None) -> dict:
    stage = on_stage if on_stage is not None else lambda _: None
    stage('RESULT_SCHEMA')
    require(isinstance(report, dict), 'invalid result document')
    expected = unique_uids(discovered, 'discovery')
    require(not timed_out, 'test process timed out')
    require(type(report.get('schemaVersion')) is int and report['schemaVersion'] == 1,
            'unsupported result schema')
    require(report.get('assemblyName') == 'SharpLink.UnitTests', 'wrong report project')
    require(report.get('tunitVersion') == '1.73.19.0', 'unsupported TUnit report version')
    generation = report.get('publicationGeneration')
    require(isinstance(generation, str) and len(generation) == 32
            and all(c in '0123456789abcdef' for c in generation), 'missing publication generation')
    groups = report.get('groups')
    require(isinstance(groups, list) and groups, 'missing result groups')
    tests = []
    for group in groups:
        require(isinstance(group, dict) and isinstance(group.get('tests'), list), 'invalid result group')
        require(all(isinstance(t, dict) for t in group['tests']), 'invalid result tests')
        tests.extend(group['tests'])
    stage('RESULT_UID')
    executed = unique_uids([t.get('id') for t in tests], 'execution')
    require(executed == expected,
            f"UID census mismatch: missing={len(set(expected) - set(executed))} "
            f"unexpected={len(set(executed) - set(expected))}")
    stage('RESULT_TEST_STATUS')
    require(all(isinstance(t.get('status'), str) for t in tests), 'missing result status')
    statuses = Counter(t['status'] for t in tests)
    # Deliberately no known-failure, platform, UDS, skipped, or retry exemption.
    bad = [t for t in tests if t['status'] != 'passed']
    if bad:
        kinds = ','.join(f'{key}={value}' for key, value in sorted(statuses.items()))
        # Only bounded names, never serialized tests/spans/output/error objects.
        names = [str(t.get('displayName', '<unnamed>')).replace('\n', ' ')[:160] for t in bad[:12]]
        raise GateError(f"non-passing results: {kinds}; examples={json.dumps(names, ensure_ascii=True)}; "
                        f"exit={exit_code}")
    stage('RESULT_COUNTERS')
    check_summary(report.get('summary'), len(expected), 'run')
    for group in groups:
        check_summary(group.get('summary'), len(group['tests']), 'group')
    stage('RESULT_EXIT')
    require(exit_code == 0, f'test process exit={exit_code}')
    return {'discovered': len(expected), 'executed': len(executed),
            'passed': len(tests), 'failed_or_skipped': 0,
            'uid_sha256': uid_digest(expected)}
