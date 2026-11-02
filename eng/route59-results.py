"""NEW route59 classifiers. No SDK execution. native.filter is diagnostic ONLY.

Pinned MTP2.5.1 / TUnit1.73.19 source chain: 0bf77e3 / ff2dc518.
All discovery UIDs are opaque actual metadata, never synthesized from source.
"""
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import re
import stat
import xml.etree.ElementTree as ET

_spec = importlib.util.spec_from_file_location('route59_common', Path(__file__).with_name('retirement-results.py'))
common = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(common)
require = common.require
read_json = common.read_json
compile_path_map = common.compile_path_map
reported_source_path = common.reported_source_path
directory_identity = common.directory_identity
package_directories = common.package_directories


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':'),
                                    ensure_ascii=True, allow_nan=False).encode()).hexdigest()


def case_key(case):
    return (case['namespace'], case['className'], case['methodName'], case['displayName'])


def verify_catalog(source, catalog):
    require(catalog['schemaVersion'] == 1 and catalog['scope'] == 'contract-route59', 'Catalog scope mismatch')
    cases = catalog['cases']
    require(len(cases) == catalog['expectedCaseCount'] == 59, 'Source case count mismatch')
    require(len({case_key(c) for c in cases}) == 59, 'Duplicate source case identity')
    require(len({case_key(c)[:3] for c in cases}) == catalog['expectedMethodCount'] == 40, 'Method count mismatch')
    require(len({case_key(c)[:2] for c in cases}) == catalog['expectedClassCount'] == 11, 'Class count mismatch')
    require({c['designId'] for c in cases} == {'R' + str(i).zfill(2) for i in range(1, 38)}, 'Design IDs mismatch')
    require(all(c['actualUID'] is None for c in cases), 'Catalog cannot preassign actual UID')
    require(len(catalog['sourcePins']) == 24, 'Selected source pin count mismatch')
    for relative, expected in {**catalog['sourcePins'], **catalog['helperPins']}.items():
        path = source / relative
        require(path.is_file() and not path.is_symlink() and hashlib.sha256(path.read_bytes()).hexdigest() == expected,
                'Pinned source/helper bytes changed')
    found = set(); todo = [catalog['project']]
    while todo:
        relative = todo.pop()
        if relative in found:
            continue
        found.add(relative)
        path = source / relative
        for ref in ET.parse(path).findall('.//ProjectReference'):
            target = (path.parent / ref.attrib['Include'].replace('\\', '/')).resolve()
            todo.append(target.relative_to(source).as_posix())
    require(sorted(found) == catalog['projects'] and len(found) == 14, 'Ordinary Unit14 closure changed')
    for c in cases:
        s = c['span']; lines = (source / c['source']).read_text().splitlines()
        require(lines[s['attributeLine'] - 1].strip() == '[Test]' and
                lines[s['signatureLine'] - 1] == s['signatureText'] and lines[s['endLine'] - 1] == s['endText'] and
                s['attributeLine'] < s['signatureLine'] <= s['endLine'], 'Pinned source span mismatch')
        attrs = [line.strip() for line in lines[s['attributeLine']:s['signatureLine'] - 1]]
        expected = None if c['argumentExpression'] is None else '[Arguments(' + c['argumentExpression'] + ')]'
        require((not attrs and expected is None) or expected in attrs, 'Source argument attribute mismatch')
        require(re.search(r'\b' + re.escape(c['methodName']) + r'\(', s['signatureText']) is not None,
                'Source method mismatch')
    validate_budget(catalog)


def validate_budget(catalog):
    l = catalog['limits']
    active = 480 + 30 + 360 + l['restoreSeconds'] + l['buildSeconds'] + 11*l['discoverySeconds'] + 59*l['caseSeconds']
    require(l['caseSeconds'] == 120 and l['engineTimeout'] == '90s', 'Body budgets changed')
    require(l['ownedCommandCount'] == 83 and active == l['activeMaximumSeconds'] == 10470,
            'Active finite budget mismatch')
    require(l['ownedCleanupAllowanceSeconds'] == 83*16 == 1328 and
            active + 1328 < l['commandBudgetSeconds'] == 12600 < l['workflowMinutes']*60 == 14400,
            'Total finite budget insufficient or changed')


DISCOVERY_REASONS = frozenset(('RD_SCHEMA', 'RD_COUNT', 'RD_EXPECTED', 'RD_ROW', 'RD_UID',
    'RD_TYPE', 'RD_IDENTITY', 'RD_CASE', 'RD_SIGNATURE', 'RD_LOCATION', 'RD_SOURCE', 'RD_MISSING'))


class DiscoveryRejected(ValueError):
    """Fixed reason code and bounded diagnostic facts, never arbitrary messages."""
    def __init__(self, code, context):
        require(code in DISCOVERY_REASONS, 'Unknown controlled discovery reason')
        self.reason_code = code
        self.safe_context = context
        self.safe_details = {}
        super().__init__(code)


def diagnostic_value(value, *, source_line=False):
    result = {'jsonType': type(value).__name__, 'sha256': digest(value)}
    if type(value) in (str, list, dict):
        result['count'] = len(value)
    elif source_line and type(value) is int and -1000000 <= value <= 1000000:
        result['integer'] = value
    return result


def discovery_expect(ok, code, context, details=lambda: {}):
    if ok:
        return
    original = DiscoveryRejected(code, context)
    try:
        original.safe_details = details()
    except BaseException as diagnostic_error:
        raise BaseExceptionGroup('Discovery rejection and independent diagnostic failure',
                                 [original, diagnostic_error])
    raise original


def discovery(document, cases, source, assembly):
    # Context strings come ONLY from the pinned catalog, never from actual metadata.
    context = {'selectionClass': cases[0]['className'] if cases else None}
    expect = lambda ok, code, details=lambda: {}: discovery_expect(ok, code, dict(context), details)
    expect(isinstance(document, dict) and type(document.get('schemaVersion')) is int and document['schemaVersion'] == 1,
           'RD_SCHEMA', lambda: {'document': diagnostic_value(document)})
    rows = document.get('tests')
    expect(isinstance(rows, list) and len(rows) == len(cases), 'RD_COUNT',
           lambda: {'expectedCount': len(cases), 'actualRows': diagnostic_value(rows)})
    expected = {case_key(c): c for c in cases}
    expect(len(expected) == len(cases), 'RD_EXPECTED')
    seen = set(); uids = set(); matched = []
    for row_index, row in enumerate(rows):
        context = {'selectionClass': cases[0]['className'], 'rowIndex': row_index}
        expect(isinstance(row, dict), 'RD_ROW', lambda: {'row': diagnostic_value(row)})
        uid = row.get('uid')
        expect(isinstance(uid, str) and 0 < len(uid) <= 8192 and not uid.startswith('-') and
                not any(c in uid for c in '\0\r\n') and uid not in uids, 'RD_UID',
                lambda: {'uid': diagnostic_value(uid), 'alreadySeen': isinstance(uid, str) and uid in uids})
        meta = row.get('type', {})
        expect(isinstance(meta, dict), 'RD_TYPE', lambda: {'type': diagnostic_value(meta)})
        ns = meta.get('namespace'); name = meta.get('typeName'); display = row.get('displayName')
        expect(isinstance(ns, str) and isinstance(name, str) and isinstance(display, str) and
               isinstance(meta.get('methodName'), str), 'RD_IDENTITY',
               lambda: {k: diagnostic_value(v) for k,v in dict(namespace=ns, className=name,
                        displayName=display, methodName=meta.get('methodName')).items()})
        short = name[len(ns)+1:] if name.startswith(ns + '.') else name
        key = (ns, short, meta.get('methodName'), display)
        expect(key in expected and key not in seen, 'RD_CASE',
               lambda: {'identity': diagnostic_value(list(key)), 'knownCase': key in expected, 'alreadySeen': key in seen})
        c = expected[key]; span = c['span']
        context.update(method=c['methodName'], source=c['source'], designId=c['designId'], caseIndex=c['caseIndex'])
        expect(meta.get('assemblyFullName') == assembly and type(meta.get('methodArity')) is int and
                meta['methodArity'] == 0 and meta.get('parameterTypeFullNames') == c['parameterTypes'] and
                meta.get('returnTypeFullName') == c['returnType'], 'RD_SIGNATURE',
                lambda: {'assemblyEqual': meta.get('assemblyFullName') == assembly,
                    'arity': diagnostic_value(meta.get('methodArity')),
                    'parameterTypesEqual': meta.get('parameterTypeFullNames') == c['parameterTypes'],
                    'parameterTypes': diagnostic_value(meta.get('parameterTypeFullNames')),
                    'returnTypeEqual': meta.get('returnTypeFullName') == c['returnType']})
        location = row.get('location', {})
        expect(isinstance(location, dict) and location.get('file') == str(reported_source_path(c['source'])) and
                type(location.get('lineStart')) is int and span['attributeLine'] <= location['lineStart'] <= span['signatureLine'] and
                type(location.get('lineEnd')) is int and location['lineEnd'] == span['endLine'], 'RD_LOCATION',
                lambda: {'expectedStartRange': [span['attributeLine'], span['signatureLine']], 'expectedEnd': span['endLine'],
                    'location': diagnostic_value(location),
                    **({'pathEqual': location.get('file') == str(reported_source_path(c['source'])),
                        'path': diagnostic_value(location.get('file')), 'lineStart': diagnostic_value(location.get('lineStart'), source_line=True),
                        'lineEnd': diagnostic_value(location.get('lineEnd'), source_line=True)} if isinstance(location,dict) else {})})
        expect(hashlib.sha256((source / c['source']).read_bytes()).hexdigest() == c['sourceSHA256'], 'RD_SOURCE')
        seen.add(key); uids.add(uid); matched.append({'case': c, 'discovery': row})
    expect(seen == set(expected), 'RD_MISSING', lambda: {'expectedCount':len(expected), 'seenCount':len(seen)})
    return matched


def freeze_ledger(matches, cases):
    by_key = {case_key(m['case']): m for m in matches}
    uids = [m['discovery']['uid'] for m in matches]
    require(len(matches) == len(cases) == 59 and len(by_key) == 59 and len(set(uids)) == 59,
            'Actual global UID/case bijection mismatch')
    require(set(by_key) == {case_key(c) for c in cases}, 'Actual ledger source scope mismatch')
    return [by_key[case_key(c)] for c in cases]


def filter_diagnostic(native):
    if 'filter' not in native:
        return {'present': False}
    value = native['filter']
    return {'present': True, 'jsonType': type(value).__name__, 'sha256': digest(value)}


def passed(native, junit, row, source, tunit, previous_runtime=None, generations=()):
    # Deliberate NEW lane non-authority. Do not mutate original report or any other field.
    diagnostic = filter_diagnostic(native)
    projected = {k: v for k, v in native.items() if k != 'filter'}
    verdict = common.passed(projected, junit, row, source, tunit, previous_runtime)
    require(verdict['publicationGeneration'] not in generations, 'Reused native publication')
    return dict(verdict, filterDiagnostic=diagnostic)


def verify_receipt(receipt, label, argv, seconds):
    require(receipt.get('stage') == label and receipt.get('argv') == [str(x) for x in argv], 'Actual argv/stage mismatch')
    require(type(receipt.get('exitCode')) is int and receipt['exitCode'] == 0 and receipt.get('timedOut') is False,
            'Command did not successfully terminate')
    require(type(receipt.get('timeoutSeconds')) in (int, float) and receipt['timeoutSeconds'] == seconds and
            math.isfinite(seconds) and seconds > 0, 'Original command deadline mismatch')
    for key in ('errors', 'cleanupErrors', 'reportingErrors'):
        require(type(receipt.get(key)) is list and receipt[key] == [], 'Command owner failure')
    require(type(receipt.get('remainingOwned')) is dict and receipt['remainingOwned'] == {}, 'Owned descendants remain or unknown')
    require(type(receipt.get('elapsedSeconds')) in (int, float) and math.isfinite(receipt['elapsedSeconds']) and
            receipt['elapsedSeconds'] >= 0, 'Missing terminal owner duration')


def case_argv(dotnet, dll, uid, report, engine_timeout):
    return [str(x) for x in (dotnet, dll, '--filter-uid', uid, '--maximum-parallel-tests', '1',
            '--timeout', engine_timeout, '--results-directory', report, '--report-html',
            '--report-html-filename', report / 'session.html', '--junit-output-path', report / 'junit.xml')]


def regular_report(report, expected_identity, name):
    verify_directory(report, expected_identity)
    path = report / name
    info = path.lstat()
    require(stat.S_ISREG(info.st_mode) and not path.is_symlink() and info.st_nlink == 1,
            'Report is not a fresh regular file')
    require(info.st_size <= 16*1024*1024, 'Oversized report')
    return path


def verify_inputs(original, built, output, expected_files, now, deadline):
    require(now <= deadline, 'Total lane budget exhausted')
    if output is not None:
        require(ordinary_inventory(output) == expected_files, 'Runtime output inventory changed')
    for path, identity in original.items():
        require(path.is_file() and not path.is_symlink() and
                (hashlib.sha256(path.read_bytes()).hexdigest(), path.stat().st_mode & 0o777) == identity,
                'Tracked source changed')
    for path, expected in built.items():
        require(path.is_file() and not path.is_symlink() and hashlib.sha256(path.read_bytes()).hexdigest() == expected,
                'Runtime/build input changed')


def directory_identity_strict(path):
    info = path.lstat()
    require(stat.S_ISDIR(info.st_mode), 'Directory path is replaced, symlinked or nonordinary')
    return (info.st_dev, info.st_ino)


def verify_directory(path, identity, empty=False):
    require(directory_identity_strict(path) == identity, 'Directory identity changed')
    if empty:
        require(not any(path.iterdir()), 'Owned directory retained entries')


def ordinary_inventory(root):
    directory_identity_strict(root)
    files = set()
    for path in root.rglob('*'):
        info = path.lstat()
        require(stat.S_ISREG(info.st_mode) or stat.S_ISDIR(info.st_mode), 'Special or symlinked output node')
        if stat.S_ISREG(info.st_mode):
            files.add(path)
    return files


def exception_graph(errors):
    """Private graph; duplicate root/group occurrences remain distinct ordered edges.

    Node IDs preserve shared identity/cycles, NOT occurrence deduplication. A failed
    message formatter becomes an independent root and is not recursively formatted.
    Tracebacks preserve every frame/file/function/line/instruction offset without
    invoking repr on locals, arbitrary object state or traceback formatter code.
    """
    nodes = []; refs = {}; queue = []; diagnostics = set()
    def ref(error):
        key = id(error)
        if key not in refs:
            refs[key] = len(nodes); nodes.append(None); queue.append(error)
        return refs[key]
    def diagnostic(error):
        diagnostics.add(id(error)); errors.append(error); return ref(error)
    roots = [ref(error) for error in errors]
    index = 0
    while index < len(queue):
        error = queue[index]; index += 1
        node = {'type': type(error).__module__ + '.' + type(error).__qualname__}
        if id(error) in diagnostics:
            node['messageUnavailable'] = 'independent formatter failure; not formatted recursively'
        else:
            try:
                node['message'] = str(error)
            except BaseException as failure:
                node['messageFailure'] = diagnostic(failure)
        tb = error.__traceback__; frames = []
        while tb is not None:
            frames.append({'file': tb.tb_frame.f_code.co_filename, 'function': tb.tb_frame.f_code.co_name,
                           'line': tb.tb_lineno, 'instruction': tb.tb_lasti})
            tb = tb.tb_next
        node['traceback'] = frames
        node['cause'] = ref(error.__cause__) if error.__cause__ is not None else None
        node['context'] = ref(error.__context__) if error.__context__ is not None else None
        node['suppressContext'] = error.__suppress_context__
        if isinstance(error, BaseExceptionGroup):
            node['members'] = [ref(member) for member in error.exceptions]
        # Public owner contract: preserve its terminal outcome plus every original
        # independent cause, including those not installed as Python __cause__.
        if hasattr(error, 'outcome') and hasattr(error, 'causes'):
            node['ownerOutcome'] = error.outcome
            node['ownerCauses'] = [ref(member) for member in error.causes]
        nodes[refs[id(error)]] = node
    roots = [ref(error) for error in errors]
    return {'schemaVersion': 1, 'orderedRootOccurrences': roots, 'nodes': nodes,
            'qualification': 'Private original exception graph; repeated edges are independent occurrences. No PASS implied.'}


def write_private_json(directory_fd, name, value):
    import os
    require(name in ('actual-ledger.json', 'summary.json', 'completed-evidence.json', 'failure-graph.json', 'failure-graph-fallback.json'),
            'Unselected private receipt name')
    payload = (json.dumps(value, ensure_ascii=True, allow_nan=False, indent=2) + '\n').encode()
    fd = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=directory_fd)
    with os.fdopen(fd, 'wb') as file:
        file.write(payload); file.flush(); os.fsync(file.fileno())


def persist_failures(directory_fd, errors):
    """Two distinct private receipts at most; never overwrite uncertain first output.

    Any formatter/write/fsync/close failure stays an ordered root and keeps exit
    nonzero. If both writes fail, durable evidence is explicitly unavailable;
    callers retain the complete in-process graph and do not claim successful save.
    """
    for name in ('failure-graph.json', 'failure-graph-fallback.json'):
        try:
            graph = exception_graph(errors)
            write_private_json(directory_fd, name, graph)
            return name
        except BaseException as error:
            errors.append(error)
    return None


def seal_directory(path):
    identity = directory_identity_strict(path)
    files = {}; directories = {}
    for item in path.rglob('*'):
        info = item.lstat(); relative = item.relative_to(path).as_posix()
        require(stat.S_ISREG(info.st_mode) or stat.S_ISDIR(info.st_mode), 'Special/symlink evidence node')
        if stat.S_ISREG(info.st_mode):
            require(info.st_nlink == 1, 'Aliased evidence file')
            files[relative] = {'sha256': hashlib.sha256(item.read_bytes()).hexdigest(),
                               'mode': info.st_mode & 0o777, 'bytes': info.st_size}
        else:
            directories[relative] = {'identity': (info.st_dev, info.st_ino), 'mode': info.st_mode & 0o777}
    verify_directory(path, identity)
    return {'identity': identity, 'files': files, 'directories': directories}


def verify_seal(path, sealed, expected_new=()):
    current = seal_directory(path)
    require(current['identity'] == sealed['identity'] and current['directories'] == sealed['directories'],
            'Completed evidence directory changed')
    require(set(current['files']) == set(sealed['files']) | set(expected_new), 'Completed evidence inventory changed')
    require(not set(sealed['files']).intersection(expected_new), 'Completed command evidence name reused')
    for name, expected in sealed['files'].items():
        require(current['files'][name] == expected, 'Completed evidence bytes/mode changed')
    return current
