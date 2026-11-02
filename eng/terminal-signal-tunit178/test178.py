"""Pure Python1.78 schema/protocol controls. No SDK/actual reports/test qualification."""
import copy
import hashlib
import json
from pathlib import Path
import re
import tempfile
import unittest
import xml.etree.ElementTree as ET
from unittest.mock import patch
import audit
import classify as old
import classify178 as new
import closure178
import source178
import run
from test_classify import specimen, fixed_specimen, SOURCE

REPORTED=run.REPORTED_SOURCE
RUNTIME='.NET 10.0.14'
# Independently read from immutable C# rows; NOT produced by source178.spans.
SOURCE_ROWS={old.START:(12,14),old.STOP:(16,18),old.LATE_START:(20,24),
             old.LATE_STOP:(26,30),old.SUCCESS:(32,54),old.POS_METHOD:(261,270)}


def sample(scope='positive'):
    n,xml,rows,_=fixed_specimen() if scope=='fixed' else specimen(scope='positive' if scope=='positive' else scope)
    n['tunitVersion']='1.78.0.0'
    for group in n['groups']:
        for t in group['tests']:
            t['lineNumber'],t['endLineNumber']=SOURCE_ROWS[t['methodName']]
            t['durationMs']=1
            t['filePath']=REPORTED
            if 'exception' in t:t['exception']['stackTrace']=t['exception']['stackTrace'].replace(str(SOURCE),REPORTED)
    for row in rows.values():
        row['location']['file']=REPORTED
        row['location']['lineStart'],row['location']['lineEnd']=SOURCE_ROWS[row['type']['methodName']]
    for item in xml.iter():
        if item.text:item.text=item.text.replace(str(SOURCE),REPORTED)
    if scope!='fixed':
        marker=('terminal-positive-' if scope=='positive' else 'terminal-owned-')+'b'*32
        failure={'type':'System.IO.IOException','message':marker}
        if scope!='positive':
            anchor=next(i+1 for i,line in enumerate(SOURCE.read_text().splitlines()) if 'try { task.GetAwaiter().GetResult(); return new(null, task.Exception); }' in line)
            failure['stackTrace']=f'   at {old.NS}.{old.REGULAR}.CaptureFailure(Task task) in {REPORTED}:line {anchor}'
        n['unobservedTaskExceptions']=[failure]
        name='Unobserved task exception - IOException: '+marker
        node=dict(id='12345678-1234-4234-9234-123456789abc',displayName=name,methodName=name,className='UnknownClass',
                  status='error',durationMs=0,exception=copy.deepcopy(failure))
        n['groups'].append(dict(className='UnknownClass',namespace='',summary=old.summary(1,1),tests=[node]))
        n['summary']=old.summary(2,1 if scope=='positive' else 2)
        for element in (xml,xml[0]):element.set('tests','2');element.set('errors','1' if scope=='positive' else '2')
        case=ET.SubElement(xml[0],'testcase',dict(name=name,classname='UnknownClass'))
        error=ET.SubElement(case,'error',dict(type=failure['type'],message=marker));error.text=new.exception_text(failure)
        output=ET.SubElement(xml[0],'system-err');output.text='Unobserved task exception: '+new.exception_text(failure)+'\n'
    return n,xml,rows,scope


def verdict(parts, generations=(),markers=()):
    n,x,rows,scope=parts
    return new.classify(n,ET.tostring(x,encoding='unicode'),rows,scope,SOURCE,RUNTIME,REPORTED,generations,markers)


class Schema178(unittest.TestCase):
    def test_four_exact_conditional_shapes(self):
        for scope in ('positive','baseline-start','baseline-stop','fixed'):
            with self.subTest(scope=scope):verdict(sample(scope))

    def test_missing_extra_and_wrong_engine_graphs(self):
        changes={
            'missing-session':lambda n,x,r,s:n.pop('unobservedTaskExceptions'),
            'empty-session':lambda n,x,r,s:n.update(unobservedTaskExceptions=[]),
            'extra-session':lambda n,x,r,s:n['unobservedTaskExceptions'].append(copy.deepcopy(n['unobservedTaskExceptions'][0])),
            'unknown-group':lambda n,x,r,s:n['groups'].append(copy.deepcopy(n['groups'][-1])),
            'missing-engine':lambda n,x,r,s:n['groups'].pop(),
            'unknown-origin':lambda n,x,r,s:n['groups'][-1]['tests'][0].update(className='Other'),
            'source-metadata-on-engine':lambda n,x,r,s:n['groups'][-1]['tests'][0].update(filePath=REPORTED),
            'engine-skip':lambda n,x,r,s:n['groups'][-1]['tests'][0].update(status='skipped'),
            'engine-nonguid':lambda n,x,r,s:n['groups'][-1]['tests'][0].update(id='arbitrary-id'),
            'engine-nonzero-time':lambda n,x,r,s:n['groups'][-1]['tests'][0].update(durationMs=1),
            'engine-bool-time':lambda n,x,r,s:n['groups'][-1]['tests'][0].update(durationMs=False),
            'node-message-mismatch':lambda n,x,r,s:n['groups'][-1]['tests'][0]['exception'].update(message='other'),
            'session-type':lambda n,x,r,s:n['unobservedTaskExceptions'][0].update(type='System.Exception'),
            'session-wrong-marker':lambda n,x,r,s:n['unobservedTaskExceptions'][0].update(message='foreign'),
            'positive-invented-stack':lambda n,x,r,s:n['unobservedTaskExceptions'][0].update(stackTrace='at made-up'),
            'positive-null-stack':lambda n,x,r,s:n['unobservedTaskExceptions'][0].update(stackTrace=None),
            'positive-empty-stack':lambda n,x,r,s:n['unobservedTaskExceptions'][0].update(stackTrace=''),
            'positive-inner':lambda n,x,r,s:n['unobservedTaskExceptions'][0].update(innerException={}),
            'positive-body-error':lambda n,x,r,s:n['groups'][0]['tests'][0].update(status='error'),
            'null-body-exception':lambda n,x,r,s:n['groups'][0]['tests'][0].update(exception=None),
            'wrong-real-uid':lambda n,x,r,s:n['groups'][0]['tests'][0].update(id='unbound'),
            'wrong-real-path':lambda n,x,r,s:n['groups'][0]['tests'][0].update(filePath='/unknown'),
            'real-retry':lambda n,x,r,s:n['groups'][0]['tests'][0].update(attempts=[]),
            'nan-duration':lambda n,x,r,s:n['groups'][0]['tests'][0].update(durationMs=float('nan')),
            'wrong-package':lambda n,x,r,s:n.update(tunitVersion='1.73.19.0'),
            'wrong-runtime':lambda n,x,r,s:n.update(runtimeVersion='.NET 10.0.2'),
            'bool-schema':lambda n,x,r,s:n.update(schemaVersion=True),
            'extra-junit-case':lambda n,x,r,s:ET.SubElement(x[0],'testcase'),
            'missing-suite-event':lambda n,x,r,s:x[0].remove(x[0].find('system-err')),
            'extra-suite-event':lambda n,x,r,s:ET.SubElement(x[0],'system-err'),
            'wrong-suite-prefix':lambda n,x,r,s:setattr(x[0].find('system-err'),'text','wrong'),
            'missing-suite-newline':lambda n,x,r,s:setattr(x[0].find('system-err'),'text',x[0].find('system-err').text.rstrip('\n')),
            'junit-engine-failure-not-error':lambda n,x,r,s:setattr(x[0].findall('testcase')[-1][0],'tag','failure'),
            'junit-extra-inner':lambda n,x,r,s:ET.SubElement(x[0].findall('testcase')[-1][0],'inner'),
        }
        for name,change in changes.items():
            parts=sample();change(*parts)
            with self.subTest(name=name):
                with self.assertRaises((ValueError,TypeError,KeyError)):verdict(parts)

    def test_unique_generation_and_marker(self):
        with self.assertRaises(ValueError):verdict(sample(),generations={'a'*32})
        with self.assertRaises(ValueError):verdict(sample(),markers={'terminal-positive-'+'b'*32})

    def test_baseline_requires_distinct_counter_and_event_stacks(self):
        for scope in ('baseline-start','baseline-stop'):
            for name in ('counter-wrong','counter-retained','counter-missing-gc','event-no-stack','event-wrong-anchor','event-inner'):
                parts=sample(scope);n=parts[0]
                if name=='counter-wrong':n['groups'][0]['tests'][0]['exception']['message']=old.COUNTER.replace('received 1','received 0')
                elif name=='counter-retained':n['groups'][0]['tests'][0]['exception']['message']='INCONCLUSIVE: retained'
                elif name=='counter-missing-gc':n['groups'][0]['tests'][0]['exception']['stackTrace']='at unrelated'
                elif name=='event-no-stack':n['unobservedTaskExceptions'][0].pop('stackTrace')
                elif name=='event-wrong-anchor':n['unobservedTaskExceptions'][0]['stackTrace']='at foreign origin'
                else:n['unobservedTaskExceptions'][0]['innerException']={}
                with self.subTest(scope=scope,name=name):
                    with self.assertRaises(ValueError):verdict(parts)

    def test_fixed_has_no_session_event_even_null_or_empty(self):
        for value in (None,[],{}):
            parts=sample('fixed');parts[0]['unobservedTaskExceptions']=value
            with self.assertRaises(ValueError):verdict(parts)
        parts=sample('fixed');ET.SubElement(parts[1][0],'system-err')
        with self.assertRaises(ValueError):verdict(parts)

    def test_new_namespace_never_projects_into_old_version_oracle(self):
        with patch.object(old,'classify',side_effect=AssertionError('old version oracle')):
            verdict(sample('fixed'));verdict(sample())

    def test_fixed_safe_native_filter_stays_exact_when_present(self):
        old.check_optional_filter({},'tree');old.check_optional_filter({'filter':'tree'},'tree')
        for value in (None,{},'other'):
            with self.assertRaises(ValueError):old.check_optional_filter({'filter':value},'tree')


class Protocol178(unittest.TestCase):
    def test_actual_positive_gate_blocks_before_any_baseline_command(self):
        lane=object.__new__(run.Lane);lane.strict_calibrated=False
        with patch.object(lane,'command',side_effect=AssertionError('must not execute')):
            with self.assertRaises(ValueError):lane.filtered('baseline','baseline','start',scope='baseline-start',ledger={'actual':{}},runtime=RUNTIME)

    def test_package_delta_only_follows_actual_tunit_reachability(self):
        def record(version):return {'packages':['TUnit/'+version],'projectEdges':{'a':[]},'cache':{'tunit/'+version:{'raw':version},'unrelated/1':{'raw':'same'}}}
        old_graph={'tunitReachableNames':['tunit','microsoft.testing.platform']}
        new_graph=copy.deepcopy(old_graph)
        delta=closure178.compare(record('1.73.19'),record('1.78.0'),old_graph,new_graph)
        self.assertEqual(delta['changedPackageNames'],['tunit'])
        changed=record('1.78.0');changed['cache']['unrelated/2']={'raw':'drift'}
        with self.assertRaises(ValueError):closure178.compare(record('1.73.19'),changed,old_graph,new_graph)
        changed=record('1.78.0');changed['cache']['unrelated/1']={'raw':'different'}
        with self.assertRaises(ValueError):closure178.compare(record('1.73.19'),changed,old_graph,new_graph)

    def test_baseline_does_not_get_strict_option_on173_reference(self):
        # Source-level command boundary: capture real composed argv without any subprocess.
        class StopBeforeExecution(Exception):pass
        lane=object.__new__(run.Lane);lane.strict_calibrated=True;lane.work=Path('/owned')
        lane.roots={'reference':Path('/r'),'baseline':Path('/b'),'fixed':Path('/f')}
        captured=[]
        def stop(label,variant,argv,seconds,**kwargs):captured.append((argv,seconds,kwargs));raise StopBeforeExecution()
        lane.command=stop;lane.dotnet=Path('/official/dotnet')
        for variant,scope in [('reference','fixed'),('fixed','positive'),('baseline','baseline-start')]:
            with self.assertRaises(StopBeforeExecution):lane.filtered('label',variant,'regular',scope=scope,ledger={},runtime=RUNTIME)
        self.assertNotIn('--fail-on-unobserved-task-exceptions',captured[0][0])
        self.assertEqual([entry[2]['expected_exit'] for entry in captured],[0,2,2])
        self.assertTrue(all('--fail-on-unobserved-task-exceptions' in entry[0] for entry in captured[1:]))
        self.assertTrue(all(entry[1]==120 for entry in captured))


class Input178(unittest.TestCase):
    def test_exact_source_package_pre_and_postimages(self):
        import prepare178
        before=(Path(__file__).parent/'package-preimage.props').read_bytes()
        after=prepare178.upgrade_bytes(before)
        self.assertEqual(after.replace(b'Version="1.78.0"',b'Version="1.73.19"'),before)
        self.assertEqual(hashlib.sha1(b'blob '+str(len(after)).encode()+b'\0'+after).hexdigest(),prepare178.AFTER_PACKAGES_BLOB)
        for bad in (after,before+b' ',before.replace(b'1.73.19',b'1.73.18'),before+before):
            with self.assertRaises(RuntimeError):prepare178.upgrade_bytes(bad)

    def test_actual_nuspec_asset_edges_and_closed_package_graph(self):
        from zipfile import ZipFile
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)/'source';packages=Path(temporary)/'packages'
            target=root/audit.PROJECTS[audit.NAMES[-1]];target.parent.mkdir(parents=True)
            obj=target.parent/'obj';obj.mkdir()
            cache={}
            for identifier,deps in [('tunit','<dependency id="MTP" version="[2.5.2]"/>'),('mtp','')]:
                version='1.78.0' if identifier=='tunit' else '2.5.2'
                folder=packages/identifier/version;folder.mkdir(parents=True)
                archive=folder/(identifier+'.'+version+'.nupkg')
                with ZipFile(archive,'w') as z:z.writestr(identifier+'.nuspec','<package><metadata><dependencies><group targetFramework="net10.0">'+deps+'</group></dependencies></metadata></package>')
                cache[identifier+'/'+version]={'archiveSHA256':audit.digest(archive)}
            record={'packages':['TUnit/1.78.0','MTP/2.5.2'],'cache':cache}
            assets={'targets':{'net10.0':{'TUnit/1.78.0':{'dependencies':{'MTP':'2.5.2'}},'MTP/2.5.2':{}}}}
            path=obj/'project.assets.json';path.write_text(json.dumps(assets))
            graph=closure178.package_graph(root,packages,record)
            self.assertEqual(graph['tunitReachableNames'],['mtp','tunit'])
            self.assertEqual(graph['activeEdges']['tunit/1.78.0'],['mtp/2.5.2'])
            assets['targets']['net10.0']['TUnit/1.78.0']['dependencies']={'foreign':'1'};path.write_text(json.dumps(assets))
            with self.assertRaises(ValueError):closure178.package_graph(root,packages,record)
            assets['targets']['net10.0']['TUnit/1.78.0']['dependencies']={'MTP':'2.5.2'};path.write_text(json.dumps(assets))
            bad=copy.deepcopy(record);bad['cache']['tunit/1.78.0']['archiveSHA256']='0'*64
            with self.assertRaises(ValueError):closure178.package_graph(root,packages,bad)
            bad=copy.deepcopy(record);bad['packages']=['TUnit/1.78.0']
            with self.assertRaises(ValueError):closure178.package_graph(root,packages,bad)

    def test_positive_real_uid_collision_and_wrong_counts(self):
        parts=sample();native,xml,rows,scope=parts
        real=next(iter(rows));engine=native['groups'][-1]['tests'][0]['id']
        rows[engine]=rows.pop(real);rows[engine]['uid']=engine;native['groups'][0]['tests'][0]['id']=engine
        with self.assertRaises(ValueError):verdict(parts)
        for field,value in [('passed',True),('total',3),('flaky',1),('skipped',1),('failed',0)]:
            parts=sample();parts[0]['summary'][field]=value
            with self.assertRaises(ValueError):verdict(parts)

    def test_malformed_raw_json_and_junit_remain_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'report.json'
            for raw in ('{"schemaVersion":1,"schemaVersion":1}','{"x":NaN}','{"x":Infinity}'):
                path.write_text(raw)
                with self.assertRaises(ValueError):old.read_json(path)
        native,xml,rows,scope=sample()
        with self.assertRaises(ValueError):new.classify(native,'<!DOCTYPE bad>'+ET.tostring(xml,encoding='unicode'),rows,scope,SOURCE,RUNTIME,REPORTED)

    def test_new_sources_keep_original_fixture_owner_and173_oracle_pins(self):
        root=Path(__file__).parent
        source=old.read_json(root/'SOURCE.json')
        for name,digest in source['files'].items():self.assertEqual(hashlib.sha256((root/name).read_bytes()).hexdigest(),digest)
        self.assertEqual(hashlib.sha256((root/'classify.py').read_bytes()).hexdigest(),'4255d3dad8c6f67a3718257a1eccff082d2efecffbe1d38fd788a65413ce1c23')
        self.assertEqual(hashlib.sha256((root/'process_owner.py').read_bytes()).hexdigest(),'44d66d88933b21d525241ac76e0dbe421a10682dba461fe784e4706cc578a9b5')



class SourceSpan178(unittest.TestCase):
    def test_source_rows_match_independently_read_literal_lines(self):
        self.assertEqual(source178.spans(SOURCE),SOURCE_ROWS)
        lines=SOURCE.read_text().splitlines()
        for method,(start,end) in SOURCE_ROWS.items():
            self.assertEqual(lines[start-1].strip(),'[Test]')
            self.assertIn(method,'\n'.join(lines[start-1:end]))
            self.assertTrue(lines[end-1].rstrip().endswith((';','}')))
        for scope in ('positive','baseline-start','baseline-stop','fixed'):
            parts=sample(scope);source178.validate_ledger(parts[2],SOURCE)

    def test_agreeing_discovery_and_native_wrong_spans_cannot_rescue(self):
        for scope in ('positive','baseline-start','baseline-stop','fixed'):
            for field,native_field in [('lineStart','lineNumber'),('lineEnd','endLineNumber')]:
                parts=sample(scope);native,xml,rows,_=parts
                test=native['groups'][0]['tests'][0];row=rows[test['id']]
                row['location'][field]+=1;test[native_field]+=1
                with self.subTest(scope=scope,field=field):
                    with self.assertRaises(ValueError):verdict(parts)
                    with self.assertRaises(ValueError):source178.validate_ledger(rows,SOURCE)
        parts=sample('positive');row=next(iter(parts[2].values()))
        row['location']['lineStart'],row['location']['lineEnd']=272,278
        with self.assertRaises(ValueError):source178.validate_ledger(parts[2],SOURCE)

    def test_missing_null_bool_and_wrong_native_end_rejected(self):
        for bad in ('missing',None,True,269,271):
            parts=sample();test=parts[0]['groups'][0]['tests'][0]
            if bad=='missing':test.pop('endLineNumber')
            else:test['endLineNumber']=bad
            with self.assertRaises(ValueError):verdict(parts)
        parts=sample();parts[0]['groups'][0]['tests'][0]['lineNumber']=True
        with self.assertRaises(ValueError):verdict(parts)

    def test_changed_or_ambiguous_source_not_a_new_catalog(self):
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'fixture.cs'
            for raw in (SOURCE.read_bytes()+b'\n',SOURCE.read_bytes().replace(b'[Test]',b'[Other]'),SOURCE.read_bytes()*2):
                path.write_bytes(raw)
                with self.assertRaises(ValueError):source178.spans(path)



if __name__=='__main__':unittest.main(verbosity=2)
