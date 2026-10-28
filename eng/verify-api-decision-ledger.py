#!/usr/bin/env python3
"""Validate a proposed decision ledger against one complete Roslyn inventory.

This verifies coverage and evidence binding, never grants review approval and never
writes decisions back into the inventory. See doc/3.0-api-decision-ledger.md.
"""

import argparse
from collections import Counter
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]


class LedgerError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise LedgerError(message)


def unique(rows, key, label):
    result = {}
    for row in rows:
        identity = key(row)
        require(identity not in result, f"Duplicate {label}: {identity}")
        result[identity] = row
    return result


def same_ids(actual, expected, label):
    missing, extra = set(expected) - set(actual), set(actual) - set(expected)
    require(not missing and not extra,
            f"{label}: missing={sorted(missing)[:5]}, extra={sorted(extra)[:5]}")


def object_without_duplicates(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f"Duplicate JSON key: {key}")
        result[key] = value
    return result


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=object_without_duplicates)


def read_rows(path):
    return [json.loads(line, object_pairs_hook=object_without_duplicates)
            for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def expand_member(type_doc_id, member):
    # '=' escapes a complete DocID, required by Roslyn's C# 14 extension containers.
    if member.startswith('='):
        return member[1:]
    require(len(member) > 2 and member[1] == ':' and member[0] in 'MPEF',
            f"Invalid compact member ID: {member}")
    return member[:2] + type_doc_id[2:] + '.' + member[2:]


def validate(ledger, identities, coverage, provenance, symbols, decisions, relations, root):
    require(ledger['schemaVersion'] == identities['schemaVersion'] == 1, 'Unsupported ledger schema')
    require(ledger['status'] == 'proposal-not-approved', 'This validator only verifies proposals; it cannot grant approval')
    require(coverage['complete'] and not coverage['failures'], 'Inventory coverage is incomplete')
    require(coverage['scope'] == 'production-declarations', 'Expected production-declarations inventory')
    bound = ledger['inventory']
    source = provenance['source']
    require(source['commit'] == bound['sourceCommit'] and source['tree'] == bound['sourceTree'],
            'Inventory source commit/tree differs from reviewed proposal input; reclassify before rebinding')
    require(not source['status'] and not source['untrackedFilesSha256'], 'Inventory source is dirty')
    for field in ('hostOs', 'hostArchitecture', 'configuration'):
        require(coverage[field] == bound[field], f'Inventory profile differs: {field}')

    targets = ledger['targets']
    expected = unique(targets.values(), lambda x: (x['project'], x['framework']), 'ledger target')
    for label, rows in (('expected', coverage['expectedTargets']), ('actual', coverage['actualTargets'])):
        actual = unique(rows, lambda x: (x['project'], x['framework']), label + ' target')
        same_ids(actual, expected, label + ' target coverage')
        if label == 'actual':
            require(all(x['complete'] for x in rows), 'A target is incomplete')

    by_id = unique(symbols, lambda x: x['id'], 'inventory symbol ID')
    require(len(by_id) == bound['declarations'], 'Declaration count differs from bound inventory')
    counts = Counter()
    for s in symbols:
        target = expected.get((s['project'], s['framework']))
        require(target is not None and s['assembly'] == target['assembly'], 'Symbol belongs to an unknown target/assembly')
        counts[s['project'] + '@' + s['framework']] += 1
        require(s['id'] == s['project'] + '@' + s['framework'] + '|' + s['semanticId'],
                f"Inconsistent symbol identity: {s['id']}")
        require(s['semanticId'].startswith(s['assembly'] + '|'), 'Inconsistent semantic assembly identity')
    require(dict(counts) == coverage['symbolCountByTarget'], 'Per-target declaration counts differ')
    required = {s['id']: s for s in symbols if s['externallyVisible'] or s['nonSealedClass']}
    raw_decisions = unique(decisions['decisions'], lambda x: x['symbolId'], 'inventory decision ID')
    same_ids(raw_decisions, required, 'Inventory needs-decision coverage')
    require(all(d['classification'] == 'needs-decision' for d in raw_decisions.values()),
            'Raw inventory decisions must remain needs-decision')
    require(len(required) == bound['decisionIds'], 'Decision count differs from bound inventory')

    def prefix(row):
        require(row['target'] in targets, f"Unknown ledger target: {row['target']}")
        target = targets[row['target']]
        return target['project'] + '@' + target['framework'] + '|' + target['assembly'] + '|'

    def type_id(row):
        require(row['type'].startswith('T:'), f"Invalid type ID: {row['type']}")
        return prefix(row) + row['type']

    typed = unique(ledger['types'], type_id, 'ledger type')
    locked = unique(identities['types'], type_id, 'identity-lock type')
    required_types = {k: v for k, v in required.items() if v['kind'] == 'NamedType'}
    same_ids(typed, required_types, 'Type decisions')
    same_ids(locked, required_types, 'Type identity lock')
    expanded = {}
    exception_count = 0

    def evidence(row):
        require(isinstance(row['rationale'], str) and row['rationale'].strip(), 'Missing rationale')
        require(row['evidence'], 'Missing evidence')
        for item in row['evidence']:
            path, separator, line = item.rpartition(':')
            if not separator:
                path, line = item, None
            relative = Path(path)
            require(not relative.is_absolute() and '..' not in relative.parts, f'Invalid evidence path: {item}')
            file = root / relative
            require(file.is_file(), f'Missing evidence file: {path}')
            if line is not None:
                require(line.isdigit() and 1 <= int(line) <= len(file.read_text(encoding='utf-8').splitlines()),
                        f'Invalid evidence line: {item}')

    for identity, row in typed.items():
        symbol = required_types[identity]
        evidence(row)
        require(row['role'] in ledger['roles'] and ledger['roles'][row['role']].strip(), 'Unknown/empty role')
        expected_decision = 'keep-inheritable' if symbol['nonSealedClass'] else 'keep-public'
        require(row['decision'] == expected_decision, f'Invalid retention decision: {identity}')
        require(row['memberDecision'] == 'keep-public', 'Unexpected default member decision')
        expanded[identity] = row['decision']
        actual_derived = {r['source'] for r in relations if r['kind'] == 'base' and r['target'] == symbol['semanticId']}
        claimed = row.get('directDerivedTypes', [])
        require(len(claimed) == len(set(claimed)), 'Duplicate direct-derived evidence')
        same_ids(claimed, actual_derived, f'Direct inheritance evidence for {identity}')
        if symbol['nonSealedClass']:
            require(actual_derived, f'No direct-derived evidence for this inheritance proposal: {identity}')
        member_ids = set()
        compact_members = locked[identity]['members']
        require(len(compact_members) == len(set(compact_members)), 'Duplicate compact member ID')
        for member in compact_members:
            full = prefix(row) + expand_member(row['type'], member)
            require(full not in expanded, f'Duplicate expanded decision ID: {full}')
            require(full in required and required[full]['kind'] != 'NamedType', f'Unknown/non-required member: {full}')
            require(required[full]['containingSymbol'] == symbol['semanticId'], f'Wrong containing type: {full}')
            require(required[full]['externallyVisible'], f'keep-public on an invisible member: {full}')
            expanded[full] = row['memberDecision']
            member_ids.add(member)
        exceptions = unique(row['memberExceptions'], lambda x: x['member'], 'member exception')
        locked_exceptions = locked[identity].get('exceptionMembers', [])
        require(len(locked_exceptions) == len(set(locked_exceptions)), 'Duplicate exception identity lock')
        same_ids(exceptions, locked_exceptions, 'Member exception explanations')
        for member, exception in exceptions.items():
            require(member in member_ids, f'Orphan member exception: {member}')
            evidence(exception)
            require(exception['decision'] == 'keep-public', 'Unexpected exception decision')
            exception_count += 1
    same_ids(expanded, required, 'Expanded decision ID coverage')
    return {'status': ledger['status'], 'targets': len(expected), 'declarations': len(by_id),
            'types': len(typed), 'decisionIds': len(expanded), 'memberExceptions': exception_count,
            'decisions': dict(sorted(Counter(expanded.values()).items()))}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('inventory', type=Path, help='Complete production inventory directory')
    parser.add_argument('--ledger', type=Path, default=ROOT / 'eng/api-inventory/3.0-decisions.json')
    parser.add_argument('--identities', type=Path, default=ROOT / 'eng/api-inventory/3.0-decision-ids.json')
    args = parser.parse_args()
    try:
        result = validate(read_json(args.ledger), read_json(args.identities),
                          read_json(args.inventory / 'coverage.json'), read_json(args.inventory / 'provenance.json'),
                          read_rows(args.inventory / 'symbols.jsonl'), read_json(args.inventory / 'decisions.json'),
                          read_rows(args.inventory / 'relations.jsonl'), ROOT)
    except (LedgerError, KeyError, TypeError, ValueError, OSError) as error:
        print(f'API decision ledger failed: {error}', file=sys.stderr)
        return 1
    print(json.dumps(result, indent=2))
    return 0


if __name__ == '__main__':
    sys.exit(main())
