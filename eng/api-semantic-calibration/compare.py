#!/usr/bin/env python3
"""Exhaustive, bounded semantic difference discovery; never grants API approval."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import sys

SOURCE = 'a91ce4bf48523264282928231f35fce339fb1567'
TREE = '83fe67363af2c3c815a333e93806d8a5fc6136aa'
HISTORICAL_SOURCE = 'de0d871f7a13662010fdf47d140ee885d7ba04ea'
HISTORICAL_TREE = 'ccc99fbab3daa767ef1bb340bd3a73c9472d798c'
EMPTY = hashlib.sha256(b'').hexdigest()
SYMBOL_KEYS = set('schemaVersion id semanticId project framework assembly kind name metadataName signature declaredAccessibility effectiveAccessibility accessibilityConstraints origin isImplicit hasGeneratedSource externallyVisible nonSealedClass hasPublicInstanceConstructor associatedSymbol containingSymbol details declarations locations'.split())
RELATION_KEYS = set('source kind target targetAssembly targetAccessibility'.split())
MAX_IDS, MAX_RECORD, MAX_TOTAL = 32, 8192, 65536


def require(ok, message):
    if not ok:
        raise ValueError(message)


def pairs(items):
    result = {}
    for k, v in items:
        require(k not in result, 'duplicate JSON key')
        result[k] = v
    return result


def parse(text):
    return json.loads(text, object_pairs_hook=pairs,
                      parse_constant=lambda _: (_ for _ in ()).throw(ValueError('nonfinite number')))


def read(path):
    return parse(path.read_text(encoding='utf-8'))


def rows(path):
    return [parse(line) for line in path.read_text(encoding='utf-8').splitlines() if line.strip()]


def canonical(value):
    # Array order and scalar types are preserved, including integer versus float.
    return json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(',', ':'), allow_nan=False).encode('utf-8')


def digest(value):
    return hashlib.sha256(canonical(value)).hexdigest()


def sha(value):
    return isinstance(value, str) and re.fullmatch('[0-9a-f]{64}', value) is not None


def indexed(values, key):
    result = {}
    for row in values:
        identity = key(row)
        require(identity not in result, 'duplicate identity')
        result[identity] = row
    return result


def semantic(symbol):
    require(set(symbol) == SYMBOL_KEYS and type(symbol['schemaVersion']) is int and symbol['schemaVersion'] == 1,
            'unknown symbol schema')
    for key in ('isImplicit', 'hasGeneratedSource', 'externallyVisible', 'nonSealedClass', 'hasPublicInstanceConstructor'):
        require(type(symbol[key]) is bool, 'invalid symbol boolean')
    require(isinstance(symbol['details'], dict), 'invalid symbol details')
    return {key: value for key, value in symbol.items() if key not in ('declarations', 'locations')}


def project(symbols, relations):
    by_id = indexed(symbols, lambda s: s['id'])
    projected = {identity: semantic(s) for identity, s in by_id.items()}
    required = {k: s for k, s in projected.items() if s['externallyVisible'] or s['nonSealedClass']}
    assemblies = {s['project'] + '@' + s['framework'] + '|' + s['assembly'] for s in symbols}
    relation_bytes, relation_values = set(), {}
    outgoing, incoming = {}, {}
    for relation in relations:
        require(set(relation) == RELATION_KEYS and all(isinstance(v, str) for v in relation.values()), 'unknown relation schema')
        require(relation['source'] in by_id or (relation['source'] in assemblies and relation['kind'] == 'internals-visible-to' and relation['target'] == relation['targetAssembly'] and relation['targetAccessibility'] == 'AssemblyGrant'), 'relation source missing from full inventory')
        encoded = canonical(relation)
        require(encoded not in relation_bytes, 'duplicate relation')
        relation_bytes.add(encoded)
        hashed = hashlib.sha256(encoded).hexdigest()
        require(hashed not in relation_values, 'relation digest collision')
        relation_values[hashed] = relation
        outgoing.setdefault(relation['source'], set()).add(hashed)
        incoming.setdefault(relation['target'], set()).add(hashed)
    result = {}
    for identity, s in required.items():
        related = outgoing.get(identity, set()) | incoming.get(s['semanticId'], set())
        result[identity] = {'symbolSha256': digest(s), 'relationSha256': sorted(related)}
    return result, required, relation_values


def verify_reports(root):
    provenance = read(root / 'provenance.json')
    require(type(provenance['schemaVersion']) is int and provenance['schemaVersion'] == 1, 'unknown provenance schema')
    manifest = provenance['outputsSha256']
    require(isinstance(manifest, dict) and manifest, 'missing output hashes')
    actual = {p.relative_to(root).as_posix() for p in root.rglob('*') if p.is_file() and p != root / 'provenance.json'}
    require(actual == set(manifest), 'output hash manifest is not exhaustive')
    for name, expected in manifest.items():
        relative = Path(name)
        require(not relative.is_absolute() and '..' not in relative.parts and sha(expected), 'invalid output hash entry')
        path = root / relative
        require(not path.is_symlink() and path.resolve().is_relative_to(root.resolve()), 'output escapes inventory')
        require(hashlib.sha256(path.read_bytes()).hexdigest() == expected, 'output hash mismatch')
    return provenance


def validate_inventory(root, expected_source, expected_tree, sdk, targets, tool_hashes):
    provenance = verify_reports(root)
    for label in ('source', 'tool'):
        source = provenance[label]
        require(source['commit'] == expected_source and source['tree'] == expected_tree, 'source/tool commit/tree mismatch')
        require(source['status'] == '' and source['untrackedFilesSha256'] == {} and source['trackedDiffSha256'] == EMPTY,
                'source/tool is dirty')
        require(source['sourceDirectoryWithinRepository'] == '.', 'unexpected source/tool directory')
    require(provenance['toolSourceSha256'] == tool_hashes, 'inventory helper inputs changed')
    coverage = read(root / 'coverage.json')
    require(type(coverage['schemaVersion']) is int and coverage['schemaVersion'] == 1, 'unknown coverage schema')
    require(coverage['complete'] is True and coverage['failures'] == [] and coverage['scope'] == 'production-declarations', 'incomplete inventory')
    require((coverage['hostOs'], coverage['hostArchitecture'], coverage['configuration']) == ('linux', 'x86_64', 'Release'), 'wrong host profile')
    require(provenance.get('discoveredProjects') == sorted(t['project'] for t in targets), 'project discovery mismatch')
    expected = indexed(targets, lambda t: (t['project'], t['framework']))
    for label in ('expectedTargets', 'actualTargets'):
        actual = indexed(coverage[label], lambda t: (t['project'], t['framework']))
        require(set(actual) == set(expected), 'target coverage mismatch')
        if label == 'actualTargets':
            require(all(t['complete'] is True and t['sdkVersion'] == sdk for t in actual.values()), 'target failed or wrong SDK')
    symbols, relations = rows(root / 'symbols.jsonl'), rows(root / 'relations.jsonl')
    counts = Counter()
    for s in symbols:
        t = expected.get((s['project'], s['framework']))
        require(t is not None and t['assembly'] == s['assembly'], 'unknown symbol target')
        require(s['id'] == s['project'] + '@' + s['framework'] + '|' + s['semanticId'] and s['semanticId'].startswith(s['assembly'] + '|'), 'inconsistent identity')
        counts[s['project'] + '@' + s['framework']] += 1
    for field in ('symbolCountByTarget', 'symbolCountByOrigin', 'symbolCountByEffectiveAccessibility'):
        require(all(type(v) is int and v >= 0 for v in coverage[field].values()), 'invalid numeric count')
    require(dict(counts) == coverage['symbolCountByTarget'], 'per-target count mismatch')
    snapshot, required, relation_values = project(symbols, relations)
    decisions = read(root / 'decisions.json')
    require(type(decisions['schemaVersion']) is int and decisions['schemaVersion'] == 1 and decisions['approvalStatus'] == 'not-reviewed', 'unknown decision schema/status')
    raw = indexed(decisions['decisions'], lambda d: d['symbolId'])
    require(set(raw) == set(required) and all(d['classification'] == 'needs-decision' and d['rationale'] is None and d['evidence'] == [] for d in raw.values()), 'needs-decision coverage changed')
    require(type(coverage['pendingDecisionCount']) is int and coverage['pendingDecisionCount'] == len(required), 'pending decision count mismatch')
    require(dict(Counter(s['origin'] for s in symbols)) == coverage['symbolCountByOrigin'], 'origin counts mismatch')
    require(dict(Counter(s['effectiveAccessibility'] for s in symbols)) == coverage['symbolCountByEffectiveAccessibility'], 'visibility counts mismatch')
    require(type(coverage['unionIdentityCountIgnoringTfm']) is int and coverage['unionIdentityCountIgnoringTfm'] == len({(s['project'], s['semanticId']) for s in symbols}), 'union count mismatch')
    type_counts = Counter((s['project'], s['framework'], s['effectiveAccessibility'], s['details']['isSealed'], s['details']['isAbstract']) for s in symbols if s['kind'] == 'NamedType')
    actual_types = indexed(coverage['typeCounts'], lambda t: (t['project'], t['framework'], t['accessibility'], t['sealed'], t['abstract']))
    require(all(type(t['count']) is int and type(t['sealed']) is bool and type(t['abstract']) is bool for t in actual_types.values()), 'invalid type count')
    require({k: t['count'] for k, t in actual_types.items()} == dict(type_counts), 'type counts mismatch')
    for target in coverage['actualTargets']:
        if target['project'] in ('src/SharpLink.Client/SharpLink.Client.csproj', 'src/SharpLink.GenerationControl/SharpLink.GenerationControl.csproj'):
            require(type(target['generatedDocumentCount']) is int and target['generatedDocumentCount'] > 0, 'required generated scope missing')
    return snapshot, required, relation_values, {'source': expected_source, 'tree': expected_tree, 'sdk': sdk, 'targets': len(expected), 'declarations': len(symbols), 'requiredIds': len(required), 'types': sum(s['kind'] == 'NamedType' for s in required.values()), 'originCounts': coverage['symbolCountByOrigin'], 'symbolCountByTarget': dict(counts), 'verifiedOutputHashes': len(provenance['outputsSha256'])}


def validate_fixture(fixture):
    require(set(fixture) == {'schemaVersion', 'projection', 'historical', 'targets', 'toolSourceSha256', 'required', 'requiredSha256', 'assemblyRelationSha256', 'sourceControlFilesSha256'}, 'unknown fixture schema')
    require(type(fixture['schemaVersion']) is int and fixture['schemaVersion'] == 1 and fixture['projection'] == 'all-symbol-fields-except-declarations-locations+incident-relations-v1', 'unknown projection')
    require(fixture['historical']['source'] == HISTORICAL_SOURCE and fixture['historical']['tree'] == HISTORICAL_TREE and fixture['historical']['sdk'] == '10.0.113', 'wrong historical source')
    require(len(fixture['required']) == 3397 and digest(fixture['required']) == fixture['requiredSha256'], 'fixture coverage/digest mismatch')
    grants = fixture['assemblyRelationSha256']
    require(isinstance(grants, list) and grants == sorted(set(grants)) and all(sha(h) for h in grants), 'invalid assembly relation hashes')
    for item in fixture['required'].values():
        require(set(item) == {'symbolSha256', 'relationSha256'} and sha(item['symbolSha256']), 'invalid expected symbol hash')
        hashes = item['relationSha256']
        require(isinstance(hashes, list) and all(sha(h) for h in hashes) and hashes == sorted(set(hashes)), 'invalid relation hashes')


def write_review_report(path, summary, records):
    """Export current differences only; never historical fixture contents or approval."""
    if path is None:
        return
    require(not any(parent.is_symlink() for parent in path.absolute().parents), 'review report parent is a symlink')
    require(not path.exists() and not path.is_symlink(), 'review report already exists')
    path.parent.mkdir(parents=True, exist_ok=True)
    require(not any(parent.is_symlink() for parent in path.absolute().parents), 'review report parent is a symlink')
    value = {'schemaVersion': 1, 'source': SOURCE, 'tree': TREE,
             'approvalStatus': 'not-reviewed', 'summary': summary, 'records': records}
    with path.open('xb') as stream:
        stream.write(canonical(value) + b'\n')


def differences(old, new, symbols, relations, old_grants=(), new_grants=(), limits=(MAX_IDS, MAX_RECORD, MAX_TOTAL), report=None):
    added, removed = sorted(new.keys() - old.keys()), sorted(old.keys() - new.keys())
    changed = sorted(k for k in old.keys() & new.keys() if old[k] != new[k])
    unchanged = len(old.keys() & new.keys()) - len(changed)
    require(len(old) == unchanged + len(changed) + len(removed) and len(new) == unchanged + len(changed) + len(added), 'comparison conservation failed')
    summary = {'oldIds': len(old), 'currentIds': len(new), 'unchanged': unchanged, 'changed': len(changed), 'added': len(added), 'removed': len(removed), 'historicalDigest': digest(old), 'currentDigest': digest(new)}
    records = []
    for identity in sorted(set(added + removed + changed)):
        before, after = old.get(identity), new.get(identity)
        record = {'id': identity, 'change': 'added' if before is None else 'removed' if after is None else 'changed', 'beforeDigest': digest(before) if before else None, 'afterDigest': digest(after) if after else None}
        if after is not None:
            if before is None or before['symbolSha256'] != after['symbolSha256']:
                record['symbol'] = symbols[identity]
            old_rel = set(before['relationSha256']) if before else set()
            record['addedRelations'] = [relations[h] for h in sorted(set(after['relationSha256']) - old_rel)]
            record['removedRelationSha256'] = sorted(old_rel - set(after['relationSha256']))
        records.append(record)
    if set(old_grants) != set(new_grants):
        records.append({'id': '<assembly-grants>', 'change': 'changed', 'addedRelations': [relations[h] for h in sorted(set(new_grants)-set(old_grants))], 'removedRelationSha256': sorted(set(old_grants)-set(new_grants))})
    summary['assemblyRelationsChanged'] = set(old_grants) != set(new_grants)
    # Measure the exact JSON-lines diagnostics including prefix/newline before emitting any.
    encoded = [b'API_DIFF ' + canonical(r) + b'\n' for r in records]
    total = sum(map(len, encoded))
    if len(records) > limits[0] or any(len(r) > limits[1] for r in encoded) or total > limits[2]:
        summary = {**summary, 'status': 'INCOMPLETE_REVIEW_OUTPUT', 'diagnosticBytes': total, 'maxDiagnosticBytes': max(map(len, encoded), default=0), 'limits': list(limits)}
        write_review_report(report, summary, records)
        return 2, summary, []
    summary = {**summary, 'status': 'COMPLETE_DIFF_REQUIRES_REVIEW' if records else 'NO_SEMANTIC_DIFF', 'diagnosticBytes': total}
    write_review_report(report, summary, records)
    return (1 if records else 0), summary, records


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('inventory', type=Path)
    parser.add_argument('--fixture', type=Path, required=True, help='Runner-local reconstructed expected mapping')
    parser.add_argument('--report', type=Path, help='New temporary current-difference report, never approval')
    args = parser.parse_args()
    try:
        import baseline
        require(not args.fixture.is_symlink() and args.fixture.stat().st_size == 1724048, 'invalid reconstructed baseline')
        require(hashlib.sha256(args.fixture.read_bytes()).hexdigest() == baseline.FIXTURE_SHA, 'frozen fixture digest mismatch')
        fixture = read(args.fixture)
        require(fixture['requiredSha256'] == baseline.REQUIRED_SHA, 'frozen required digest mismatch')
        validate_fixture(fixture)
        snapshot, symbols, relations, metadata = validate_inventory(args.inventory, SOURCE, TREE, '10.0.102', fixture['targets'], fixture['toolSourceSha256'])
        provenance = read(args.inventory / 'provenance.json')
        require(provenance['sourceControlFilesSha256'] == fixture['sourceControlFilesSha256'], 'production build controls changed')
        grants = sorted(h for h, r in relations.items() if r['kind'] == 'internals-visible-to')
        code, summary, records = differences(fixture['required'], snapshot, symbols, relations, fixture['assemblyRelationSha256'], grants, report=args.report)
        print('API_METADATA ' + canonical(metadata).decode())
        print('API_COMPARISON ' + canonical(summary).decode())
        for record in records:
            print('API_DIFF ' + canonical(record).decode())
        return code
    except (ValueError, KeyError, TypeError, OSError) as error:
        # No arbitrary input/file contents or unbounded exception messages in logs.
        diagnostic = {'status': 'INCOMPLETE', 'errorType': type(error).__name__, 'reason': str(error)[:160] if isinstance(error, ValueError) else 'invalid or unreadable input'}
        print('API_COMPARISON ' + canonical(diagnostic).decode())
        if args.report is not None and not args.report.exists() and not args.report.is_symlink():
            try:
                write_review_report(args.report, diagnostic, [])
            except (ValueError, OSError):
                print('API_REVIEW_REPORT_UNAVAILABLE')
        return 2


if __name__ == '__main__':
    sys.exit(main())
