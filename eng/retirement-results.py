"""Strict pass-only TUnit 1.73.19 / MTP discovery classifiers.

Schema sources (not a claim of executed workload):
TUnit ff2dc5186b05eddc8f6aea03a18784df3aa6e737 ReportDataJson/JUnitXmlWriter;
Microsoft testfx 0bf77e3aedbf86f1fa16220bc951d3b52ba56763 DiscoveredTestsJsonSerializer.
Raw reports remain local. No exception container, retry, skip or timeout is PASS.
"""
import json
import hashlib
import math
from pathlib import Path
import re
import stat
import xml.etree.ElementTree as ET


class SourcePathDiagnosticFailure(BaseExceptionGroup):
    """Retains the two raw failures; console rendering must not stringify children."""


def require(value, message):
    if not value:
        raise ValueError(message)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'Duplicate JSON key: ' + key)
        result[key] = value
    return result


def read_json(path):
    data = Path(path).read_bytes()
    require(len(data) <= 16 * 1024 * 1024, 'Oversized JSON report')
    return json.loads(data.decode('utf-8-sig'), object_pairs_hook=unique_object,
                      parse_constant=lambda _: (_ for _ in ()).throw(ValueError('Nonfinite JSON number')))


def source_span(path, method):
    """The selected source methods have a Test attribute and a public Task/void signature.
    Only inspect this known formatting; reject ambiguity rather than guess an AST.
    """
    lines = Path(path).read_text().splitlines()
    matches = [i for i, line in enumerate(lines)
               if re.search(r'\bpublic\s+(?:async\s+)?(?:Task|void)\s+' + re.escape(method) + r'\s*\(', line)]
    require(len(matches) == 1, 'Ambiguous selected method declaration: ' + method)
    signature = matches[0]
    attribute = signature - 1
    while attribute >= 0 and (not lines[attribute].strip() or
                              lines[attribute].strip().startswith('//') or
                              (lines[attribute].strip().startswith('[') and lines[attribute].strip() != '[Test]')):
        attribute -= 1
    require(attribute >= 0 and lines[attribute].strip() == '[Test]', 'Expected explicit Test attribute: ' + method)
    boundary = next((i for i in range(signature + 1, len(lines))
                     if re.match(r'^    (?:\[|(?:private|public|internal|protected)\b)', lines[i])), len(lines) - 1)
    end = boundary - 1
    while end > signature and (not lines[end].strip() or lines[end].strip().startswith('//')):
        end -= 1
    require(end >= signature, 'Invalid selected source span')
    return attribute + 1, signature + 1, end + 1


def discovery(document, scope, methods, source, assembly, reported_source=None, on_source_path_mismatch=None):
    require(type(document.get('schemaVersion')) is int and document['schemaVersion'] == 1, 'Discovery schema mismatch')
    tests = document.get('tests')
    require(isinstance(tests, list) and len(tests) == len(methods), 'Discovery case count mismatch')
    expected = {item['name']: item for item in methods}
    require(len(expected) == len(methods), 'Duplicate selected method')
    rows = {}
    seen_methods = set()
    for test in tests:
        require(isinstance(test, dict), 'Invalid discovery row')
        uid = test.get('uid')
        require(isinstance(uid, str) and uid and uid not in rows, 'Missing/duplicate actual UID')
        meta = test.get('type', {})
        name = meta.get('methodName')
        require(name in expected and name not in seen_methods, 'Unselected or duplicate method/data row')
        item = expected[name]
        require(meta.get('namespace') == scope['namespace'] and
                meta.get('typeName') in (scope['class'], scope['namespace'] + '.' + scope['class']), 'Discovered class mismatch')
        require(meta.get('assemblyFullName') == assembly, 'Discovered assembly mismatch')
        require(type(meta.get('methodArity')) is int and meta['methodArity'] == 0, 'Generic method not selected')
        require(meta.get('parameterTypeFullNames') == item['parameters'] and
                meta.get('returnTypeFullName') == item['return'], 'Discovered signature mismatch')
        require(isinstance(test.get('displayName'), str) and test['displayName'], 'Missing actual display name')
        location = test.get('location', {})
        if location.get('file') != str(reported_source if reported_source is not None else source):
            rejection = ValueError('Discovered source path mismatch')
            if on_source_path_mismatch is not None:
                try:
                    on_source_path_mismatch(test)
                except BaseException as diagnostic:
                    raise SourcePathDiagnosticFailure('Source path rejection and diagnostic failure', [rejection, diagnostic]) from None
            raise rejection
        start, signature, end = source_span(source, name)
        require(type(location.get('lineStart')) is int and start <= location['lineStart'] <= signature and
                type(location.get('lineEnd')) is int and location['lineEnd'] == end, 'Discovered source span mismatch')
        seen_methods.add(name)
        rows[uid] = test
    require(seen_methods == set(expected), 'Missing selected method')
    return rows


def source_path_diagnostic(row, source, reported_source, relative_source, checkout_source):
    """Safe description only, NEVER a path resolver or acceptance rule.

    Called only for the first rejected row in the selected nine-method math list,
    after discovery has checked its UID/method/type/signature. Unknown absolute
    prefixes are hashed, not printed or read from the filesystem.
    """
    actual = row.get('location', {}).get('file')
    detail = dict(method=row['type']['methodName'],
                  actualUidSha256=hashlib.sha256(row['uid'].encode()).hexdigest(),
                  selectedGitRelativeSource=relative_source,
                  selectedSourceSha256=hashlib.sha256(source.read_bytes()).hexdigest(),
                  expectedReportedPath=str(reported_source),
                  qualification='diagnostic-only; rejected path remains rejected')
    if not isinstance(actual, str):
        detail['actualPathClass'] = 'missing-or-nonstring'
        return detail
    detail['actualPathSha256'] = hashlib.sha256(actual.encode()).hexdigest()
    detail['actualPathLength'] = len(actual)
    if actual == str(reported_source):
        detail['actualPathClass'] = 'exact-expected-reported-path'
    elif actual == str(source):
        detail['actualPathClass'] = 'exact-private-build-source'
    elif actual == str(checkout_source):
        detail['actualPathClass'] = 'exact-invocation-checkout-source'
    elif actual == relative_source:
        detail['actualPathClass'] = 'exact-git-relative-text-unbound'
    elif re.fullmatch(r'/_[0-9]{0,3}/' + re.escape(relative_source), actual):
        # Only this controlled virtual-root grammar plus the exact catalog path
        # is printable. It does not establish an actual compiler mapping.
        detail['actualPathClass'] = 'other-underscore-virtual-root-unbound'
        detail['controlledReportedPath'] = actual
    elif actual.endswith('/' + relative_source):
        detail['actualPathClass'] = 'selected-relative-suffix-with-untrusted-prefix'
        detail['prefixSha256'] = hashlib.sha256(actual[:-len(relative_source)].encode()).hexdigest()
    else:
        detail['actualPathClass'] = 'other-path-unbound'
    return detail


def directory_identity(raw):
    """Linux lane directory identity; separators/dot normalize, links/traversal do not.

    Require existing real directories, including every ancestor. Returning both the
    canonical path and inode identity prevents a replaced selected cache from being
    mistaken for the directory reserved before restore.
    """
    require(isinstance(raw, str) and raw and '\x00' not in raw, 'invalid-directory-text')
    path = Path(raw)
    require(path.is_absolute() and path.anchor == '/', 'nonabsolute-directory')
    require('..' not in path.parts, 'directory-traversal')
    try:
        for component in reversed((path, *path.parents)):
            mode = component.lstat().st_mode
            require(not stat.S_ISLNK(mode), 'symlink-directory')
            require(stat.S_ISDIR(mode), 'nondirectory-component')
        canonical = path.resolve(strict=True)
        current = canonical.stat()
    except OSError as error:
        raise ValueError('unavailable-directory-' + type(error).__name__) from error
    return canonical, current.st_dev, current.st_ino


def package_directories(folders, restored_path, packages, expected_identity, source, asset_name):
    """Exactly one actual cache, with safe classifications instead of raw paths.

    A single textual key may have a trailing slash or not. Multiple keys never
    become one accepted folder merely because they normalize to the same path.
    """
    asset = Path(asset_name)
    require(not asset.is_absolute() and '..' not in asset.parts and asset.as_posix() == asset_name,
            'invalid-source-relative-asset-name')
    count = len(folders) if isinstance(folders, dict) else None

    def classify(raw):
        try:
            identity = directory_identity(raw)
        except ValueError as error:
            # directory_identity only emits fixed reasons/type names, not input text.
            return str(error), None
        canonical = identity[0]
        if canonical == packages:
            return ('selected-cache' if identity == expected_identity else 'selected-cache-replaced'), identity
        if canonical.is_relative_to(packages):
            return 'selected-cache-descendant', identity
        if canonical.is_relative_to(source):
            return 'source-root', identity
        if canonical.is_relative_to(packages.parent):
            return 'lane-root', identity
        return 'outside-known-roots', identity

    keys = list(folders) if isinstance(folders, dict) else []
    classifications = [classify(key) for key in keys[:4]]
    restore_class, restore_identity = classify(restored_path)
    selected_class, selected_identity = classify(str(packages))
    valid = (count == 1 and classifications[0][1] == expected_identity and
             restore_identity == expected_identity and selected_identity == expected_identity)
    if not valid:
        detail = dict(asset=asset_name, folderCount=count,
                      folderClasses=[item[0] for item in classifications],
                      omittedFolderCount=max(0, len(keys) - len(classifications)),
                      restorePathClass=restore_class, selectedCacheClass=selected_class)
        raise ValueError('Package directory identity rejected: ' + json.dumps(detail, sort_keys=True))


def exact_summary(value):
    expected = dict(total=1, passed=1, failed=0, skipped=0, cancelled=0, timedOut=0, flaky=0)
    require(isinstance(value, dict) and set(value) == set(expected) and
            all(type(x) is int for x in value.values()) and value == expected, 'Not an exact singleton PASS summary')


def passed(native, junit, row, source, tunit, previous_runtime=None, selector=None):
    require(type(native.get('schemaVersion')) is int and native['schemaVersion'] == 1, 'Native schema mismatch')
    require(native.get('assemblyName') == 'SharpLink.UnitTests' and native.get('tunitVersion') == tunit,
            'Native assembly/TUnit identity mismatch')
    require('filter' not in native or (isinstance(selector, str) and native['filter'] == selector),
            'Optional native filter differs from the actual selector')
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
        require(all(element.get(k) == v for k, v in dict(name='SharpLink.UnitTests', tests='1', failures='0', errors='0', skipped='0').items()),
                'JUnit counts disagree with singleton PASS')
    return {'runtime': runtime, 'publicationGeneration': generation, 'uid': row['uid'], 'status': 'PASS'}
