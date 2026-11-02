"""Synthetic schema controls only; these are not .NET/GC/test-runtime evidence."""
import copy
from pathlib import Path
import xml.etree.ElementTree as ET
import classify as c

SOURCE = Path(__file__).with_name('ServerTerminalSignalObservationTests.cs').resolve()


def specimen(kind='error', scope='baseline-start'):
    baseline = scope.startswith('baseline-')
    method = c.START if scope == 'baseline-start' else c.STOP if scope == 'baseline-stop' else c.POS_METHOD
    cls = c.POSITIVE if scope == 'positive' else c.REGULAR
    row = {'uid': 'synthetic-uid', 'displayName': method,
           'type': {'namespace': c.NS, 'typeName': cls, 'methodName': method,
                    'assemblyFullName': 'SharpLink.UnitTests, Version=2.0.4.0, Culture=neutral, PublicKeyToken=null',
                    'parameterTypeFullNames': [], 'methodArity': 0, 'returnTypeFullName': 'System.Void'},
           'location': {'file': str(SOURCE), 'lineStart': 12, 'lineEnd': 14}}
    t = dict(id=row['uid'], displayName=method, methodName=method, className=cls,
             filePath=str(SOURCE), lineNumber=12, status=kind if baseline else 'passed')
    numbers = c.source_lines(SOURCE)
    frames = [f'   at {c.NS}.TerminalSignalObservation.RequireCount(Int32 expected) in {SOURCE}:line {numbers[0]}',
              f'   at {c.NS}.{c.REGULAR}.RequireNoUnobservedSignal(Boolean failDuringStart) in {SOURCE}:line {numbers[1]}',
              f'   at {c.NS}.{c.REGULAR}.{method}() in {SOURCE}:line {numbers[2 if scope == "baseline-start" else 3]}']
    if baseline:
        t['exception'] = dict(type='System.Exception', message=c.COUNTER, stackTrace='\n'.join(frames))
    s = c.summary(1, int(baseline))
    n = dict(schemaVersion=1, publicationGeneration='a'*32, assemblyName='SharpLink.UnitTests',
             tunitVersion='1.73.19.0', runtimeVersion='.NET 10.0.14', summary=s,
             groups=[dict(className=cls, namespace=c.NS, summary=s.copy(), tests=[t])])
    attrs = dict(name='SharpLink.UnitTests', tests='1', failures=str(int(baseline and kind=='failed')),
                 errors=str(int(baseline and kind=='error')), skipped='0')
    root = ET.Element('testsuites', attrs); suite = ET.SubElement(root, 'testsuite', attrs)
    case = ET.SubElement(suite, 'testcase', dict(name=method, classname=cls))
    if baseline:
        child = ET.SubElement(case, 'error' if kind == 'error' else 'failure', dict(type='System.Exception', message=c.COUNTER))
        child.text = 'System.Exception: '+c.COUNTER+'\n'+t['exception']['stackTrace']
    return n, root, {row['uid']: row}, scope


def fixed_specimen():
    n, xml, rows, scope = specimen(scope='positive')
    template = next(iter(rows.values()))
    rows = {}; tests = []
    suite = xml[0]
    for child in list(suite): suite.remove(child)
    for method, count in c.EXPECTED.items():
        for index in range(count):
            row = copy.deepcopy(template)
            row['uid'] = method + '-' + str(index)
            row['displayName'] = method + (('(' + ('False' if index == 0 else 'True') + ')') if count == 2 else '')
            row['type'].update(typeName=c.REGULAR, methodName=method,
                               parameterTypeFullNames=['System.Boolean'] if count == 2 else [],
                               returnTypeFullName='System.Threading.Tasks.Task' if count == 2 or method == c.SUCCESS else 'System.Void')
            rows[row['uid']] = row
            tests.append(dict(id=row['uid'], displayName=row['displayName'], methodName=method,
                              className=c.REGULAR, filePath=str(SOURCE), lineNumber=12, status='passed'))
            ET.SubElement(suite, 'testcase', dict(name=row['displayName'], classname=c.REGULAR))
    n['summary'] = c.summary(7, 0)
    n['groups'] = [dict(className=c.REGULAR, namespace=c.NS, summary=n['summary'].copy(), tests=tests)]
    xml.set('tests', '7'); suite.set('tests', '7')
    return n, xml, rows, 'fixed'


def check(parts):
    n, xml, rows, scope = parts
    return c.classify(n, ET.tostring(xml, encoding='unicode'), rows, scope, SOURCE)


def main():
    positives = 0
    for scope in ('baseline-start', 'baseline-stop'):
        for kind in ('error', 'failed'):
            check(specimen(kind, scope)); positives += 1
    check(specimen(scope='positive')); positives += 1
    check(fixed_specimen()); positives += 1
    negatives = []
    def reject(label, change, base=None):
        parts = copy.deepcopy(base or specimen())
        change(*parts)
        try:
            check(parts)
        except (ValueError, TypeError, KeyError):
            negatives.append(label)
        else:
            raise AssertionError('Malformed schema accepted: '+label)
    def t(n): return n['groups'][0]['tests'][0]
    reject('green', lambda n,x,r,s: t(n).update(status='passed'))
    reject('skip', lambda n,x,r,s: t(n).update(status='skipped'))
    reject('timeout', lambda n,x,r,s: t(n).update(status='timedOut'))
    reject('retained-inconclusive', lambda n,x,r,s: t(n)['exception'].update(message='INCONCLUSIVE: retained'))
    reject('wrong-count', lambda n,x,r,s: t(n)['exception'].update(message=c.COUNTER.replace('received 1','received 2')))
    reject('wrong-type', lambda n,x,r,s: t(n)['exception'].update(type='System.IO.IOException'))
    reject('inner-exception', lambda n,x,r,s: t(n)['exception'].update(innerException={'type':'System.Exception'}))
    reject('null-inner', lambda n,x,r,s: t(n)['exception'].update(innerException=None))
    reject('null-skipreason', lambda n,x,r,s: t(n).update(skipReason=None))
    reject('wrong-stack', lambda n,x,r,s: t(n)['exception'].update(stackTrace='at unrelated'))
    reject('gc-barrier-not-reached', lambda n,x,r,s: t(n)['exception'].update(stackTrace=t(n)['exception']['stackTrace'].replace('RequireNoUnobservedSignal','RequireCollection')))
    reject('wrong-uid', lambda n,x,r,s: t(n).update(id='wrong'))
    reject('duplicate-case', lambda n,x,r,s: n['groups'][0]['tests'].append(copy.deepcopy(t(n))))
    reject('extra-group', lambda n,x,r,s: n['groups'].append(copy.deepcopy(n['groups'][0])))
    reject('summary-skip', lambda n,x,r,s: n['summary'].update(skipped=1))
    reject('summary-bool', lambda n,x,r,s: n['summary'].update(failed=True))
    reject('junit-wrong-mapping', lambda n,x,r,s: setattr(x[0][0][0], 'tag', 'failure'))
    reject('junit-extra-sibling', lambda n,x,r,s: ET.SubElement(x[0][0], 'error'))
    reject('junit-inner-element', lambda n,x,r,s: ET.SubElement(x[0][0][0], 'inner'))
    reject('junit-wrong-text', lambda n,x,r,s: setattr(x[0][0][0], 'text', 'other'))
    reject('wrong-runtime', lambda n,x,r,s: n.update(runtimeVersion='.NET 9.0.1'))
    reject('wrong-tunit', lambda n,x,r,s: n.update(tunitVersion='1.78.0.0'))
    for value in (None, {}, ''):
        reject('passed-exception-field-'+repr(value), lambda n,x,r,s,v=value: t(n).update(exception=v), specimen(scope='positive'))
    reject('passed-null-skip', lambda n,x,r,s: t(n).update(skipReason=None), specimen(scope='positive'))
    reject('passed-junit-skip', lambda n,x,r,s: ET.SubElement(x[0][0], 'skipped'), specimen(scope='positive'))
    reject('fixed-one-case-failed', lambda n,x,r,s: t(n).update(status='error'), fixed_specimen())
    reject('fixed-null-exception', lambda n,x,r,s: t(n).update(exception=None), fixed_specimen())
    # Discovery is parsed separately; duplicate UIDs and extra/missing rows must fail.
    n,x,rows,s = specimen()
    d = dict(schemaVersion=1, tests=list(rows.values()))
    c.discovery(d, c.START, SOURCE)
    d['tests'].append(copy.deepcopy(d['tests'][0]))
    try: c.discovery(d, c.START, SOURCE)
    except ValueError: negatives.append('discovery-duplicate')
    else: raise AssertionError('duplicate discovery accepted')
    try: c.unique_object([('a', 1), ('a', 2)])
    except ValueError: negatives.append('duplicate-json-key')
    else: raise AssertionError('duplicate JSON key accepted')
    print(f'SYNTHETIC_SCHEMA_PASS positive={positives} negative={len(negatives)} runtimeTests=0')


if __name__ == '__main__':
    main()
