"""Exact completed-state baseline causal check from calibrated 321ec6c9e."""
import re
from validate import require
BASELINE_UID = 'SharpLink.UnitTests.Runtime.SendPumpCleanupProgressTests.1.1.ThrowingBatchReturnMustNotReleaseRerentedOwnerOrAbandonEitherQueue.1.1.0'
BASELINE_ASSERTION = ('SENDPUMP_STALE_SLOT_CAUSAL_V1: completed cleanup retried a retired batch slot '
                      'and abandoned later batch and queued owners.')
def validate_baseline(report: dict, code: int, timed_out: bool) -> None:
    # A timeout/crash/any-error is not causal proof. The frozen test emits this
    # marker only after completed cleanup, explicit non-timeout prerequisites and
    # the exact duplicate/abandoned-owner census. Generic assertions cannot pass.
    # Bind one exact UID, complete counters and runtime before accepting it.
    require(not timed_out and code in (1, 2), 'baseline did not terminate with an ordinary failing-test exit')
    require(type(report.get('schemaVersion')) is int and report['schemaVersion'] == 1, 'wrong baseline report schema')
    require(report.get('assemblyName') == 'SharpLink.UnitTests' and report.get('tunitVersion') == '1.73.19.0', 'wrong baseline assembly/TUnit')
    require(report.get('runtimeVersion') == '.NET 10.0.14', 'wrong baseline runtime')
    require(isinstance(report.get('publicationGeneration'), str)
            and re.fullmatch('[0-9a-f]{32}', report['publicationGeneration']) is not None, 'missing final baseline generation')
    expected = dict(total=1, passed=0, failed=1, skipped=0, cancelled=0, timedOut=0, flaky=0)
    require(report.get('summary') == expected and all(type(v) is int for v in report['summary'].values()), 'baseline counters not exactly one failed test')
    groups = report.get('groups')
    require(isinstance(groups, list) and len(groups) == 1 and isinstance(groups[0], dict)
            and groups[0].get('summary') == expected
            and all(type(v) is int for v in groups[0]['summary'].values()), 'baseline group counters mismatch')
    tests = groups[0].get('tests')
    require(isinstance(tests, list) and len(tests) == 1, 'baseline execution count mismatch')
    test = tests[0]
    # Pinned TUnit 1.73.19 source ff2dc5186b05eddc8f6aea03a18784df3aa6e737:
    # a plain InvalidOperationException is Unknown -> ErrorTestNodeStateProperty
    # -> report status "error", while summary.failed increments. Not "failed".
    require(test.get('id') == BASELINE_UID and test.get('status') == 'error'
            and type(test.get('retryAttempt', 0)) is int and test.get('retryAttempt', 0) == 0 and not test.get('attempts'), 'baseline wrong UID/status/retry')
    error = test.get('exception')
    require(isinstance(error, dict), 'baseline missing assertion exception')
    require(error.get('type') in ('InvalidOperationException', 'System.InvalidOperationException'),
            'baseline wrong exception type (not the reviewed assertion type)')
    # Never copy arbitrary exception text into CI; keep the full report local.
    classification = ('former-generic-aggregate-assertion' if error.get('message') ==
                      'every distinct owner-cleanup failure must remain observable' else 'unreviewed-assertion')
    require(error.get('message') == BASELINE_ASSERTION, 'baseline wrong assertion: classification=' + classification
            + ' expected=' + BASELINE_ASSERTION)
    require(not error.get('innerException'), 'baseline unexpected inner exception')
    require(isinstance(error.get('stackTrace'), str) and 'ThrowingBatchReturnMustNotReleaseRerentedOwnerOrAbandonEitherQueue'
            in error['stackTrace'], 'baseline failure did not originate in the target regression')
