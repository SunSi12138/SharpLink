"""D2-only protocol authority. No SDK or C# runs on import."""
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
HELPER_PINS = {'eng/route59-results.py': 'eb95aaf6a6799b0c216ee3687d71e5e1aeb5651867d515e3a4db16561bfcda8b', 'eng/retirement-results.py': '6774c8bdd8bd8b3f618c01aee34e429d8a3b8218cdb169e752cd81ab6a9e79eb', 'eng/package-smoke-process.py': 'ba9f6ae43784ac752acdbfee1af60f33361ba76398a558948da721994449dda3', 'eng/tests/test_retirement_results.py': '47a46c4c575690d54b3e2e143b5690ab8e3459031f415059a214d536adb8946b'}

def verify_helpers(root):
    for name, expected in HELPER_PINS.items():
        path = root / name
        if not path.is_file() or path.is_symlink() or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
            raise ValueError('Pinned helper input changed')

verify_helpers(ROOT)
_spec = importlib.util.spec_from_file_location('d2_pinned_route', ROOT / 'eng/route59-results.py')
base = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(base)
# Only generic, unchanged protocol/security primitives. Fixed route59 catalog,
# count, budget and source-parser functions are deliberately not reused.
for _name in ('require', 'read_json', 'digest', 'compile_path_map', 'reported_source_path',
              'directory_identity', 'directory_identity_strict', 'package_directories',
              'verify_directory', 'seal_directory', 'verify_seal', 'write_private_json',
              'persist_failures', 'ordinary_inventory', 'verify_inputs', 'regular_report',
              'verify_receipt', 'case_key', 'case_argv', 'discovery'):
    globals()[_name] = getattr(base, _name)

INPUT_NAMES = ('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props',
               'NuGet.Config', 'nuget.config', 'global.json')

def check_ancestors(source, catalog):
    table = catalog['ancestorInputs']; actual = {}
    for project in catalog['projects']:
        directory = (source / project).parent; names = []
        while directory.is_relative_to(source):
            names.extend((directory / n).relative_to(source).as_posix() for n in INPUT_NAMES)
            if directory == source: break
            directory = directory.parent
        actual[project] = names
    require(actual == table['projects'], 'Project ancestor scope changed')
    require(set(table['inputs']) == {n for ns in actual.values() for n in ns}, 'Ancestor inventory changed')
    for name, expected in table['inputs'].items():
        path = source / name
        require(not path.is_symlink(), 'Unexpected ancestor symlink')
        if expected['present']:
            require(path.is_file() and path.stat().st_size == expected['bytes'] and
                    hashlib.sha256(path.read_bytes()).hexdigest() == expected['sha256'], 'Ancestor input changed')
        else:
            require(not path.exists(), 'Unselected ancestor input appeared')
    for parent in source.parents:
        require(not any((parent / n).exists() or (parent / n).is_symlink() for n in INPUT_NAMES),
                'External ancestor input exists')

def verify_spans(source, catalog):
    spans = catalog['sourceSpans']; cases = catalog['cases']
    require(len(spans) == 35 and {s['id'] for s in spans} == {c['designId'] for c in cases}, 'Span identity mismatch')
    by_id = {s['id']: s for s in spans}
    require(len(by_id) == 35, 'Duplicate span')
    for c in cases:
        s = by_id[c['spanKey']]; p = s['span']; content = (source / s['file']).read_bytes()
        lines = content.decode('utf-8').splitlines(); byte_lines = content.splitlines(keepends=True)
        require(s['actualUid'] is None and s['plannedUidCount'] == 1 and s['runtimeStatus'] == 'UNRUN', 'Prefabricated span result')
        require(len(content) == s['fileBytes'] and hashlib.sha256(content).hexdigest() == s['fileSha256'] == c['sourceSHA256'], 'Full span file changed')
        require(c['methodName'] == s['method'] and c['source'] == s['file'] and c['designId'] == s['id'] and
                c['fixtureInstances'] == s['fixtureInstances'] and c['hasExpectedQuarantine'] == s['hasExpectedQuarantine'], 'Case/span mismatch')
        require(c['span'] == dict(attributeLine=p['attributeLine'], signatureLine=p['signatureLine'], endLine=p['endLineInclusive']), 'Discovery span changed')
        require(p['startLine'] == p['attributeLine'] and p['byteStart'] == sum(map(len, byte_lines[:p['startLine']-1])) and
                p['byteEndExclusive'] == sum(map(len, byte_lines[:p['endLineInclusive']])), 'Byte/line span mismatch')
        part = content[p['byteStart']:p['byteEndExclusive']]
        require(len(part) == p['bytes'] and hashlib.sha256(part).hexdigest() == p['sha256'], 'Complete method slice changed')
        for line, expected in ((p['attributeLine'], p['attributeText']), (p['signatureLine'], p['signatureText']),
                               (p['bodyBeginLine'], p['signatureContinuationText']), (p['endLineInclusive'], p['closingLineText']),
                               (p['nextBoundary']['line'], p['nextBoundary']['text'])):
            require(lines[line-1] == expected, 'Method boundary changed')
        require(p['attributeText'].strip() == '[Test]' and p['signatureText'].strip() in ('public Task ' + c['methodName'] + '()', 'public async Task ' + c['methodName'] + '()'), 'Method signature changed')

def verify_catalog(source, catalog):
    source = Path(source).resolve(); cases = catalog['cases']
    require(catalog['scope'] == 'd2-ownership35' and catalog['schemaVersion'] == 1, 'Wrong lane')
    require(len(cases) == catalog['expectedCaseCount'] == catalog['expectedMethodCount'] == 35, 'D2 method count changed')
    require(len({case_key(c) for c in cases}) == 35 and {c['designId'] for c in cases} == {'D2-' + str(i).zfill(2) for i in range(1,36)}, 'Case identity changed')
    require(sum(c['fixtureInstances'] for c in cases) == catalog['expectedFixtureInstances'] == 41 and
            sum(c['hasExpectedQuarantine'] for c in cases) == 13, 'Fixture inventory changed')
    for c in cases:
        require(c['actualUID'] is None and c['parameterTypes'] == [] and c['caseIndex'] == 0 and
                c['namespace'] == 'SharpLink.UnitTests.Client' and c['className'] == 'D2AssemblyRetirementTests' and
                c['returnType'] == 'System.Threading.Tasks.Task' and c['displayName'] == c['methodName'], 'Case signature changed')
    require(catalog['helperPins'] == HELPER_PINS, 'Helper authority changed')
    require(len(catalog['sourcePins']) == 31, 'Source pin inventory changed')
    for name, expected in catalog['sourcePins'].items():
        path = source / name
        require(path.is_file() and not path.is_symlink() and hashlib.sha256(path.read_bytes()).hexdigest() == expected, 'Source input changed')
    check_ancestors(source, catalog); verify_helpers(source)
    seen = set(); todo = [catalog['project']]
    while todo:
        name = todo.pop()
        if name in seen: continue
        seen.add(name); project = source / name
        for ref in ET.parse(project).findall('.//ProjectReference'):
            todo.append((project.parent / ref.attrib['Include'].replace('\\', '/')).resolve().relative_to(source).as_posix())
    require(sorted(seen) == catalog['projects'] and len(seen) == 8, 'Focused graph changed')
    project = ET.parse(source / catalog['project'])
    expected_local = {'D2AssemblyRetirementFixture.cs', 'D2AssemblyRetirement.Decorator.cs',
        'D2AssemblyRetirement.SelectionTests.cs', 'D2AssemblyRetirement.PublicationTests.cs',
        'D2AssemblyRetirement.AccountingTests.cs', 'D2AssemblyRetirement.OutcomesTests.cs', 'D2AssemblyRetirement.CustodyTests.cs'}
    expected_links = {'../SharpLink.RetirementBudgetOwnershipTests/GlobalUsings.cs', '../SharpLink.UnitTests/Runtime/ManualTimeProvider.cs'}
    require({n.attrib['Include'] for n in project.findall('.//Compile')} == expected_local | expected_links and
            project.findtext('.//EnableDefaultCompileItems') == 'false' and project.findtext('.//AssemblyName') == 'SharpLink.UnitTests', 'Focused Compile changed')
    require([(x.attrib.get('Include'), x.attrib.get('Value')) for x in project.findall('.//AssemblyMetadata')] == [('SharpLink.TestSubset','D2Ownership-v1')], 'Subset marker changed')
    verify_spans(source, catalog); validate_budget(catalog)

def validate_budget(catalog):
    l = catalog['limits']
    require(l == dict(restoreSeconds=600, buildSeconds=600, discoverySeconds=120, caseSeconds=180,
        engineTimeout='150s', ownedCommandCount=49, activeMaximumSeconds=8490,
        ownedCleanupAllowanceSeconds=784, commandBudgetSeconds=9600, workflowMinutes=180), 'Budget changed')
    require(480+30+360+600+600+120+35*180 == 8490 and 8490+49*16 < 9600 < 180*60, 'Insufficient budget')

def launch_budget(seconds, remaining):
    require(type(seconds) in (int,float) and math.isfinite(seconds) and seconds > 0 and
            type(remaining) in (int,float) and math.isfinite(remaining) and remaining > 16, 'No launch budget')
    return min(seconds, remaining - 16)

def freeze_ledger(matches, cases):
    by_key = {case_key(m['case']):m for m in matches}; uids = [m['discovery']['uid'] for m in matches]
    require(len(matches) == len(cases) == len(by_key) == len(set(uids)) == 35 and
            set(by_key) == {case_key(c) for c in cases}, 'Actual 35 UID bijection changed')
    return [by_key[case_key(c)] for c in cases]

def safe_failure(error):
    # Do not emit dynamic type names, exception text, path, data or raw log lines.
    known = {ValueError:'value', TimeoutError:'timeout', OSError:'os', RuntimeError:'runtime',
             BaseExceptionGroup:'group', ExceptionGroup:'group'}
    return {'category':known.get(type(error),'other'),
            'independentMembers':len(error.exceptions) if isinstance(error, BaseExceptionGroup) else 1}


def passed(native, junit, row, source, tunit, previous_runtime=None, generations=()):
    # New-lane strict structure: properties are inert leaves, never another place
    # to hide executed cases or failure/skip/output elements from direct counts.
    require(isinstance(junit, str) and len(junit) <= 16 * 1024 * 1024 and
            '<!DOCTYPE' not in junit and '<!ENTITY' not in junit, 'Unsafe JUnit input')
    root = ET.fromstring(junit)
    require(root.tag == 'testsuites' and len(root) == 1 and root[0].tag == 'testsuite', 'JUnit root changed')
    suite = root[0]; cases = suite.findall('testcase'); properties = suite.findall('properties')
    require(len(cases) == 1 and list(root.iter('testcase')) == cases and
            len(properties) <= 1 and list(root.iter('properties')) == properties and
            list(root.iter('testsuite')) == [suite], 'Hidden JUnit selection')
    require(all(node.tag in ('testsuites', 'testsuite', 'testcase', 'properties', 'property') for node in root.iter()),
            'JUnit non-PASS or unknown nested element')
    leaves = []
    for container in properties:
        require(all(child.tag == 'property' and len(child) == 0 and 'name' in child.attrib and
                    set(child.attrib) <= {'name', 'value'} for child in container), 'Malformed JUnit property')
        leaves.extend(container)
    require(list(root.iter('property')) == leaves, 'Misplaced JUnit property')
    return base.passed(native, junit, row, source, tunit, previous_runtime, generations)
