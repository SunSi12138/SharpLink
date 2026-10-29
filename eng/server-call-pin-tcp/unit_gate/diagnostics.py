"""Small allowlisted failure diagnostics; never serialize test output or traces."""
from collections import Counter
import json
import re
import unicodedata

MAX_FAILURES = 12
MAX_UID = 360
MAX_MESSAGE = 240
MAX_TYPE = 96
STATUSES = ('passed', 'failed', 'skipped', 'cancelled', 'timedOut', 'flaky', 'unknown')
SUMMARY_KEYS = ('total', 'passed', 'failed', 'skipped', 'cancelled', 'timedOut', 'flaky')
ANSI = re.compile(r'\x1b\][^\x07]*(?:\x07|\x1b\\)|\x1b\[[0-?]*[ -/]*[@-~]')
RAW_BOUNDARY = re.compile(
    r'(?i)(?:\b(?:stdout|stderr|stack\s*trace|stacktrace|test\s+output|raw\s+output|child\s+results)\s*[:=]|'
    r'\b[A-Za-z0-9_]*(?:password|passwd|secret|token|api_?key|authorization|cookie|credential)[A-Za-z0-9_]*\s*[:=]|'
    r'\bbearer\s+|[a-z][a-z0-9+.-]*://[^\s/:]+:[^\s@]+@)')


def clean_text(value, limit, *, message=False):
    if not isinstance(value, str):
        return ''
    # Bound scanning of malformed/huge messages. Only the first logical line is useful
    # as a public assertion summary; subsequent lines may contain child logs or stacks.
    text = ANSI.sub('', value[:4096])
    # First-line only, before removing controls, so a new-line annotation cannot be
    # joined back into a message. Remove other controls rather than splitting sensitive
    # labels ("std\0out:") into harmless-looking words before boundary detection.
    if message:
        text = text.splitlines()[0] if text.splitlines() else ''
    text = ''.join(ch for ch in text if not unicodedata.category(ch).startswith('C'))
    if message:
        boundary = RAW_BOUNDARY.search(text)
        if boundary:
            text = text[:boundary.start()] + '[details omitted]'
    text = text.replace('::', ': :').replace('%', '_')
    text = ' '.join(text.split())
    return text[:limit - 3] + '...' if len(text) > limit else text


def exception_summary(test):
    # TUnit 1.73.19's pinned ReportDataJson.WriteException writes exactly these nested
    # fields. Never fall back to output/errorOutput or guessed top-level error fields,
    # spans, trace data, stackTrace, innerException trees, or arbitrary stringification.
    value = test.get('exception')
    kind = value.get('type') if isinstance(value, dict) else None
    message = value.get('message') if isinstance(value, dict) else None
    kind = clean_text(kind, MAX_TYPE)
    if not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_.+`\[\],]*', kind):
        kind = 'missing-exception-type'
    return {'exceptionType': kind, 'message': clean_text(message, MAX_MESSAGE, message=True) or 'missing-message'}


def report_diagnostics(report, expected_uids, exit_code, timed_out):
    expected = set(expected_uids)
    result = {'expectedUIDCount': len(expected), 'observedUniqueUIDCount': 0,
              'missingUIDCount': len(expected), 'unexpectedUIDCount': 0, 'duplicateUIDCount': 0,
              'exit': exit_code if type(exit_code) is int else None, 'timedOut': bool(timed_out),
              'reportedSummary': None, 'observedStatuses': {}, 'failures': [], 'omittedFailures': 0}
    if not isinstance(report, dict):
        result['reportShape'] = 'unavailable'
        return result
    summary = report.get('summary')
    if isinstance(summary, dict):
        result['reportedSummary'] = {key: summary[key] if type(summary.get(key)) is int and
            0 <= summary[key] <= 1_000_000 else None for key in SUMMARY_KEYS}
    groups = report.get('groups')
    if not isinstance(groups, list):
        result['reportShape'] = 'invalid-groups'
        return result
    observed = Counter()
    counts = Counter()
    failure_count = 0
    for group in groups:
        if not isinstance(group, dict) or not isinstance(group.get('tests'), list):
            result['reportShape'] = 'invalid-group'
            continue
        for test in group['tests']:
            if not isinstance(test, dict):
                counts['unknown'] += 1
                continue
            uid = test.get('id')
            if isinstance(uid, str):
                observed[uid] += 1
            status = test.get('status')
            status = status if isinstance(status, str) and status in STATUSES else 'unknown'
            counts[status] += 1
            if status == 'passed':
                continue
            failure_count += 1
            if len(result['failures']) >= MAX_FAILURES:
                continue
            uid = test.get('id')
            # Only frozen public corpus identities may reach logs. An unexpected UID or
            # displayName may contain parameter values/secrets; never echo either.
            known = isinstance(uid, str) and uid in expected
            name_match = re.search(r'\.1\.1\.([A-Za-z_][A-Za-z0-9_]*)', uid) if known else None
            failure = {'uid': clean_text(uid, MAX_UID) if known else '[unrecognized-uid]',
                'name': name_match.group(1)[:120] if name_match else '[unrecognized-test]', 'status': status}
            failure.update(exception_summary(test) if known else {'exceptionType': 'unavailable', 'message': 'unavailable'})
            result['failures'].append(failure)
    result.update(observedUniqueUIDCount=len(observed), missingUIDCount=len(expected - observed.keys()),
        unexpectedUIDCount=len(observed.keys() - expected),
        duplicateUIDCount=sum(count - 1 for count in observed.values()))
    result['observedStatuses'] = dict(sorted(counts.items()))
    result['omittedFailures'] = max(0, failure_count - len(result['failures']))
    return result


def emit_diagnostics(report, expected_uids, exit_code, timed_out):
    value = report_diagnostics(report, expected_uids, exit_code, timed_out)
    summary = {key: item for key, item in value.items() if key != 'failures'}
    print('UNIT_TEST_STATUS ' + json.dumps(summary, ensure_ascii=True, separators=(',', ':')), flush=True)
    for failure in value['failures']:
        print('UNIT_TEST_FAILURE ' + json.dumps(failure, ensure_ascii=True, separators=(',', ':')), flush=True)
    return value
