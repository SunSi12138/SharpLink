"""Generator-only TUnit1.73.19 discovery/native/JUnit predicates.

Selection comes from actual UID request + actual native id, NOT native.filter.
Exact official schema/UID sources are documented in the reviewed Generator8 plan.
No import triggers SDK, test discovery or test execution.
"""
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import re
import xml.etree.ElementTree as ET

_spec = importlib.util.spec_from_file_location('generator_identity_helpers', Path(__file__).with_name('retirement-results.py'))
_shared = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_shared)
require = _shared.require
read_json = _shared.read_json
exact_summary = _shared.exact_summary
package_directories = _shared.package_directories
directory_identity = _shared.directory_identity
compile_path_map = _shared.compile_path_map
reported_source_path = _shared.reported_source_path


def filter_diagnostic(native):
    if 'filter' not in native:
        return {'present': False}
    value = native['filter']
    result = {'present': True, 'type': type(value).__name__}
    if isinstance(value, str):
        result['sha256'] = hashlib.sha256(value.encode()).hexdigest()
    return result


def verify_span(source, span):
    raw = Path(source).read_bytes()
    require(len(raw) == span['sourceBytes'] and hashlib.sha256(raw).hexdigest() == span['sourceSha256'],
            'Selected Generator source bytes changed')
    lines = raw.splitlines(keepends=True)
    a, s, se, e = (span[k] for k in ('attributeStartLine', 'signatureStartLine', 'signatureEndLine', 'methodEndLine'))
    require(all(type(n) is int for n in (a, s, se, e)) and 1 <= a <= s <= se < e <= len(lines), 'Invalid explicit source span')
    require(lines[a - 1].decode().rstrip('\r\n') == span['attributeText'] == '    [Test]' and
            b''.join(lines[s - 1:se]).decode() == span['signatureText'] and
            lines[e - 1].decode().rstrip('\r\n') == span['closingText'] == '    }', 'Selected source boundary changed')
    block = b''.join(lines[a - 1:e])
    require(len(block) == span['methodBytes'] and hashlib.sha256(block).hexdigest() == span['methodBytesSha256'],
            'Selected method byte slice changed')
    following = next((i for i in range(e, len(lines)) if lines[i].strip()), None)
    require((None if following is None else following + 1) == span['nextNonemptyLine'] and
            (None if following is None else lines[following].decode().rstrip('\r\n')) == span['nextNonemptyText'],
            'Selected next method/class boundary changed')
    return a, s, e


def discovery(document, catalog, cases, source):
    require(type(document.get('schemaVersion')) is int and document['schemaVersion'] == 1, 'Discovery schema mismatch')
    tests = document.get('tests')
    require(isinstance(tests, list) and len(tests) == len(cases), 'Discovery case count mismatch')
    expected = {item['expectedDisplayName']: item for item in cases}
    require(len(expected) == len(cases), 'Duplicate expected display/arguments')
    rows, seen = {}, set()
    for row in tests:
        require(isinstance(row, dict), 'Invalid discovery row')
        uid, display = row.get('uid'), row.get('displayName')
        require(isinstance(uid, str) and uid and uid not in rows, 'Missing/duplicate actual UID')
        require(display in expected and display not in seen, 'Missing/extra/duplicate argument display')
        item = expected[display]
        meta = row.get('type', {})
        require(meta.get('namespace') == catalog['namespace'] and
                meta.get('typeName') in (catalog['class'], catalog['namespace'] + '.' + catalog['class']) and
                meta.get('methodName') == item['method'] and meta.get('assemblyFullName') == catalog['assembly'],
                'Discovered method/class/assembly mismatch')
        require(type(meta.get('methodArity')) is int and meta['methodArity'] == 0 and
                meta.get('parameterTypeFullNames') == item['parameterTypes'] and
                meta.get('returnTypeFullName') == catalog['return'], 'Discovered signature mismatch')
        span = catalog['sourceSpans'][item['spanKey']]
        require(span['source'] == item['source'], 'Span/source binding mismatch')
        start, signature, end = verify_span(source / item['source'], span)
        location = row.get('location', {})
        require(location.get('file') == str(reported_source_path(item['source'])), 'Discovered source path mismatch')
        require(type(location.get('lineStart')) is int and start <= location['lineStart'] <= signature and
                type(location.get('lineEnd')) is int and location['lineEnd'] == end, 'Discovered explicit source span mismatch')
        rows[uid] = dict(row, selectedCase=item)
        seen.add(display)
    require(seen == set(expected), 'Missing selected source case')
    return rows


def verify_uid_command(argv, dll, uid):
    require(len(argv) > 3 and str(argv[1]) == str(dll) and argv.count('--filter-uid') == 1 and
            '--treenode-filter' not in argv, 'Runtime must select one actual UID only')
    index = argv.index('--filter-uid')
    require(index + 1 < len(argv) and argv[index + 1] == uid and
            (index + 2 == len(argv) or argv[index + 2].startswith('--')), 'Runtime UID argument mismatch')


def unique_generation(verdict, generations):
    require(verdict['publicationGeneration'] not in generations, 'Reused native publication')
    generations.add(verdict['publicationGeneration'])


def passed(native, junit, row, source, tunit, previous_runtime=None, selector=None):
    require(type(native.get('schemaVersion')) is int and native['schemaVersion'] == 1, 'Native schema mismatch')
    require(native.get('assemblyName') == 'SharpLink.Generator.Tests' and native.get('tunitVersion') == tunit,
            'Native assembly/TUnit identity mismatch')
    runtime = native.get('runtimeVersion', '')
    require(re.fullmatch(r'\.NET 10\.0\.\d+', runtime) is not None, 'Unexpected actual test runtime')
    require(previous_runtime is None or previous_runtime == runtime, 'Actual runtime changed within lane')
    generation = native.get('publicationGeneration', '')
    require(re.fullmatch('[0-9a-f]{32}', generation) is not None, 'Missing native publication generation')
    exact_summary(native.get('summary'))
    groups = native.get('groups')
    require(isinstance(groups, list) and len(groups) == 1, 'Expected exactly one actual group')
    group = groups[0]
    exact_summary(group.get('summary'))
    require(not {'exception', 'skipReason', 'retryAttempt', 'attempts'}.intersection(native) and
            not {'exception', 'skipReason', 'retryAttempt', 'attempts'}.intersection(group),
            'Root/group exception or retry container is not PASS')
    tests = group.get('tests')
    require(isinstance(tests, list) and len(tests) == 1, 'Expected exactly one executed UID')
    test = tests[0]
    meta = row['type']
    require(test.get('id') == row['uid'] and test.get('displayName') == row['displayName'] and
            test.get('methodName') == meta['methodName'], 'Native UID/display/method differs from actual discovery')
    require(group.get('namespace') == meta['namespace'] and
            test.get('className') == group.get('className') == meta['typeName'], 'Native class mismatch')
    require(test.get('filePath') == str(source) and test.get('lineNumber') == row['location']['lineStart'] and
            test.get('endLineNumber') == row['location']['lineEnd'], 'Native source identity mismatch')
    require(test.get('status') == 'passed', 'Native result is not passed')
    require(not {'exception', 'skipReason', 'retryAttempt', 'attempts'}.intersection(test),
            'Exception/skip/retry fields forbidden, including null/empty containers')
    require(type(test.get('durationMs')) in (int, float) and math.isfinite(test['durationMs']) and test['durationMs'] >= 0,
            'Invalid native duration')
    require(len(junit) <= 16 * 1024 * 1024 and '<!DOCTYPE' not in junit and '<!ENTITY' not in junit,
            'Oversized or entity-bearing JUnit report')
    root = ET.fromstring(junit)
    require(root.tag == 'testsuites' and len(root) == 1 and root[0].tag == 'testsuite', 'JUnit suite shape mismatch')
    suite = root[0]
    require(all(child.tag in ('properties', 'testcase') for child in suite) and len(suite.findall('properties')) <= 1,
            'Unknown/duplicate JUnit suite metadata')
    cases = suite.findall('testcase')
    require(len(cases) == 1, 'JUnit must contain one executed case')
    case = cases[0]
    require(case.get('name') == row['displayName'] and case.get('classname') == meta['typeName'] and len(case) == 0,
            'JUnit identity mismatch or non-PASS child (failure/error/skip/output/unknown)')
    for element in (root, suite):
        require(all(element.get(k) == v for k, v in dict(name='SharpLink.Generator.Tests', tests='1', failures='0', errors='0', skipped='0').items()),
                'JUnit counts disagree with singleton PASS')
    return {'runtime': runtime, 'publicationGeneration': generation, 'uid': row['uid'], 'status': 'PASS', 'filterDiagnostic': filter_diagnostic(native)}
