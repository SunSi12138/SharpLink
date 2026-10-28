#!/usr/bin/env python3
"""Fail-closed validation of the fixed focused correctness campaign (not timing)."""
import argparse
import json
from pathlib import Path
import re

EXPECTED = {
    'WakeupSignalTests': 14,
    'SendPumpTests': 13,
    'SendPumpTimedWaitStopTests': 1,
    'SendPumpIdleShutdownTests': 7,
    'SendPumpProgressIsolationTests': 14,
    'SendPumpTimeBudgetPublicationTests': 3,
    'SendPumpUnobservedTaskTests': 2,
    'RpcSessionRuntimeFlushPolicyTests': 6,
    'BackpressuredSendOwnershipTests': 28,
}

def validate(root):
    errors = []
    totals = {}
    for name in ['dotnet-info', 'architecture', 'maintainability', 'build', *EXPECTED]:
        try:
            status = int((root / f'{name}.exit').read_text().strip())
            if status != 0:
                errors.append(f'{name}: exit {status}')
        except (OSError, ValueError) as exc:
            errors.append(f'{name}: missing/invalid exit evidence: {exc}')
    for name, expected in EXPECTED.items():
        try:
            text = (root / f'{name}.log').read_text()
        except OSError as exc:
            errors.append(f'{name}: missing log: {exc}')
            continue
        parsed = {}
        for field in ['total', 'failed', 'succeeded', 'skipped']:
            matches = re.findall(rf'^\s*{field}:\s*(\d+)\s*$', text, re.MULTILINE)
            if len(matches) != 1:
                errors.append(f'{name}: expected exactly one {field}, observed {len(matches)}')
            else:
                parsed[field] = int(matches[0])
        wanted = {'total': expected, 'failed': 0, 'succeeded': expected, 'skipped': 0}
        if parsed != wanted:
            errors.append(f'{name}: counts {parsed}, expected {wanted}')
        totals[name] = parsed
    return {'passed': not errors, 'expectedTotal': sum(EXPECTED.values()), 'classes': totals, 'errors': errors}

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('evidence', type=Path)
    args = parser.parse_args()
    result = validate(args.evidence)
    print(json.dumps(result, indent=2))
    raise SystemExit(0 if result['passed'] else 1)
