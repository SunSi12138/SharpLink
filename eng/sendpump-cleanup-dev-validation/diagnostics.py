"""Bounded observations only; full report/error details stay in temporary artifact."""
import hashlib
import json

COUNTERS = ('total', 'passed', 'failed', 'skipped', 'cancelled', 'timedOut', 'flaky')
STAGES = frozenset(('REQUEST', 'SOURCE', 'HOSTS', 'BUILD', 'BINARY_IDENTITY', 'DISCOVERY_PROCESS',
    'DISCOVERY_CENSUS', 'EXECUTION_PROCESS', 'RESULT_REPORT', 'RESULT_RUNTIME',
    'VALIDATION_SCHEMA', 'VALIDATION_GROUPS', 'VALIDATION_UID', 'VALIDATION_STATUS',
    'VALIDATION_RETRY', 'VALIDATION_COUNTERS', 'VALIDATION_EXIT', 'ALLOCATION_DEV_RESULT',
    'FINAL_DLL_IDENTITY', 'FINAL_SOURCE', 'FINAL_REQUEST', 'COMPLETE'))


def number(value):
    return value if type(value) is int and 0 <= value <= 1_000_000 else None


class Diagnostics:
    def __init__(self):
        self.current = 'REQUEST'
        self.rows = []
        self.failures = []

    def emit(self, **data):
        row = dict(stage=self.current, **data)
        self.rows.append(row)
        print('ORDINARY_DIAGNOSTIC ' + json.dumps(row, sort_keys=True), flush=True)

    def stage(self, name):
        if name not in STAGES: raise ValueError('unknown diagnostic stage')
        self.current = name
        self.emit(entered=True)

    def observe_report(self, report, ordinary, code, timed):
        # Observing malformed inputs must not replace their original validator.
        try:
            groups = report.get('groups')
            tests = [t for g in groups if isinstance(g, dict) for t in g.get('tests', []) if isinstance(t, dict)] if isinstance(groups, list) else []
            expected = set(ordinary)
            actual = [t.get('id') for t in tests]
            valid = [uid for uid in actual if isinstance(uid, str)]
            summary = report.get('summary', {})
            self.emit(exit=code if type(code) is int and -1000 <= code <= 1000 else None, timedOut=bool(timed),
                expectedCount=len(ordinary), reportedCount=len(tests), duplicateUIDs=len(valid)-len(set(valid)),
                missingUIDs=len(expected-set(valid)), unexpectedUIDs=len(set(valid)-expected),
                counters={k:number(summary.get(k)) if isinstance(summary, dict) else None for k in COUNTERS})
            bad = [t for t in tests if t.get('status') != 'passed']
            for test in bad[:16]:
                uid = test.get('id')
                identity = dict(uid=uid) if isinstance(uid, str) and uid in expected and len(uid) <= 300 else dict(
                    uidSHA256=hashlib.sha256(str(uid).encode()).hexdigest())
                self.emit(**identity, status=test.get('status') if test.get('status') in
                    ('failed', 'error', 'skipped', 'cancelled', 'timedOut', 'flaky', 'passed') else 'other')
            self.emit(nonPassing=len(bad), omittedNonPassing=max(0,len(bad)-16))
        except Exception:
            self.emit(reportObservationAvailable=False)

    def failure(self, error):
        self.failures.append(dict(stage=self.current, type=type(error).__name__, detail=str(error)))
        self.emit(failed=True, errorType=type(error).__name__)

    def save(self, path, identity):
        path.write_text(json.dumps(dict(identity=identity, rows=self.rows, failures=self.failures), indent=2) + '\n')
