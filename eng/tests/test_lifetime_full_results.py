"""Python-only full-document adversarial controls; never executes .NET."""
import contextlib
import copy
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
sys.dont_write_bytecode=True
ROOT=Path(__file__).resolve().parents[2]
BASE=ROOT if (ROOT/'eng/unit-execution-results.py').is_file() else ROOT.parent/'sharplink-protocol-lifetime-diagnostic-20261105'
if len(sys.argv)==3 and sys.argv[1]=='--reviewed-base':BASE=Path(sys.argv[2]);sys.argv[1:]=[]
def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path);mod=importlib.util.module_from_spec(spec);spec.loader.exec_module(mod);return mod
h=load('lifetime_full_projection',ROOT/'eng/lifetime-full-results.py')
e=load('current_full_classifier',BASE/'eng/unit-execution-results.py');r=e.census
CANARY='PRIVATE_FULL_LIFETIME_CANARY_e31c02a7'
ASSEMBLY='SharpLink.UnitTests, Version=3.0.0.0, Culture=neutral, PublicKeyToken=null'

def fixture(statuses=('passed',)*16):
    ordinary={};tests=[]
    for index,status in enumerate(statuses):
        if index<14:
            source,namespace,cls,method,start,end=h.CASES[index];attribute=h.ATTRIBUTE_LINES[index]
            group=h.group_for(index);displays=list(h.expected_displays(group));display=displays[index-group['first']] if displays else method
        else:source,namespace,cls,method,attribute,end='test/SharpLink.UnitTests/Other.cs','Other','OtherClass','Other'+str(index),10+index,12+index;display=CANARY+str(index)
        uid='uid-'+str(index)
        row={'uid':uid,'displayName':display,'type':{'namespace':namespace,'typeName':cls,'methodName':method,'assemblyFullName':ASSEMBLY,
            'methodArity':0,'returnTypeFullName':'System.Threading.Tasks.Task','parameterTypeFullNames':h.PARAMETER_TYPES[index] if index<14 else []},
            'location':{'file':'/_/'+source,'lineStart':attribute,'lineEnd':end}}
        ordinary[uid]=row
        test={'id':uid,'displayName':display,'methodName':method,'className':cls,'filePath':'/_/'+source,
            'lineNumber':attribute,'endLineNumber':end,'status':status,'durationMs':1.0}
        if status not in ('passed','skipped'):test['exception']={'type':'System.Exception','message':CANARY,'stackTrace':CANARY}
        if status=='skipped':test['skipReason']=CANARY
        tests.append((namespace,cls,test))
    groups=[]
    for namespace,cls,test in tests:
        group=next((g for g in groups if (g['namespace'],g['className'])==(namespace,cls)),None)
        if group is None:group={'namespace':namespace,'className':cls,'tests':[]};groups.append(group)
        group['tests'].append(test)
    native={'schemaVersion':1,'assemblyName':'SharpLink.UnitTests','tunitVersion':'1.73.19.0','runtimeVersion':'.NET 10.0.14',
            'publicationGeneration':'a'*32,'machineName':CANARY,'timestamp':CANARY,'operatingSystem':CANARY,'totalDurationMs':16.0,'groups':groups}
    return reseal(ordinary,native)

def alltests(native):return [t for g in native['groups'] for t in g['tests']]
def junit(native):
    tests=alltests(native);statuses=[x['status'] for x in tests]
    attrs={'name':'SharpLink.UnitTests','tests':str(len(tests)),'failures':str(statuses.count('failed')),
           'errors':str(sum(x in ('error','cancelled','timedOut') for x in statuses)),'skipped':str(statuses.count('skipped')),
           'time':'0.006','timestamp':CANARY}
    root=ET.Element('testsuites',attrs);suite=ET.SubElement(root,'testsuite',{**attrs,'hostname':CANARY})
    props=ET.SubElement(suite,'properties')
    for name in ('framework','platform','runtime'):ET.SubElement(props,'property',{'name':name,'value':CANARY})
    for t in tests:
        node=ET.SubElement(suite,'testcase',{'name':e.xml_name(t['displayName']),'classname':e.xml_name(t['className']),'time':'0.001'})
        if t['status']=='skipped':ET.SubElement(node,'skipped',{'message':CANARY})
        elif t['status']!='passed':
            ex=t['exception'];child=ET.SubElement(node,h.STATUS_KIND[t['status']],{'type':ex['type'],'message':e.xml_name(ex['message'])});child.text=CANARY
    return ET.tostring(root,encoding='unicode')
def reseal(ordinary,native):
    for g in native['groups']:g['summary']=e.summary(g['tests'])
    native['summary']=e.summary(alltests(native));xml=junit(native);facts={'ownerClosed':True,'bodyExitCode':0 if all(t['status']=='passed' for t in alltests(native)) else 2}
    e.classify(native,xml,ordinary,set(),facts,[{'framework':'Microsoft.NETCore.App','version':'10.0.14'}])
    return ordinary,native,xml,facts

class Controls(unittest.TestCase):
    def project(self,data=None,**kwargs):
        ordinary,native,xml,facts=data or fixture()
        return h.project(r,native,xml,ordinary,kwargs.get('explicit',set()),facts,BASE)
    def reject(self,data,code=None):
        ordinary,native,xml,facts=data;before=copy.deepcopy(data)
        with self.assertRaises(Exception) as c:self.project(data)
        self.assertEqual(data,before)
        if code:self.assertEqual(getattr(c.exception,'code',None),code)
    def test_full_document_returns_only_fourteen_without_mutating_inputs(self):
        data=fixture();before=copy.deepcopy(data);result=self.project(data)
        self.assertEqual(data,before);self.assertEqual(len(alltests(data[1])),16);self.assertEqual(len(result['cases']),14)
        self.assertTrue(result['complete']);self.assertTrue(result['allSelectedPassed']);self.assertTrue(result['doesNotQualifyFullSuite'])
        self.assertTrue(result['parameterSetsProven']['duplex']);self.assertEqual({x['argumentIdentity']['value'] for x in result['cases'][:2]},{True,False})
        self.assertFalse(result['historicalFullRunArgumentMappingAvailable']);self.assertNotIn(CANARY,json.dumps(result))
        self.assertEqual(result['nativeFullReportCanonicalSHA256'],r.digest(data[1]))
        self.assertEqual(result['fullResultUIDSetSHA256'],r.digest(sorted(data[0])))
    def test_no_runner_import_or_process_file_write_api(self):
        text=(ROOT/'eng/lifetime-full-results.py').read_text()
        for forbidden in ('import subprocess','import importlib','write_text(', 'write_bytes(', 'run-unit-', 'unit-census.json', 'run-full-regression.py'):
            self.assertNotIn(forbidden,text)
    def test_source_pin_mutation_and_reader_failure_stop_projection(self):
        with patch.object(r,'sha',return_value='0'*64):
            with self.assertRaises(r.Rejected) as c:self.project()
            self.assertEqual(c.exception.code,'LP_SOURCE')
        original=OSError(CANARY)
        with patch.object(r,'sha',side_effect=original):
            with self.assertRaises(OSError) as c:self.project()
            self.assertIs(c.exception,original)
    def test_unrelated_suite_failure_keeps_original_global_failure(self):
        data=fixture(('passed',)*14+('error','passed'));before=copy.deepcopy(data[3]);result=self.project(data)
        self.assertTrue(result['allSelectedPassed']);self.assertFalse(data[3]['allOrdinaryPassed']);self.assertEqual(data[3],before)
        self.assertEqual(data[3]['statusCounts']['failed'],1)
    def test_selected_failures_and_all_statuses_preserve_global_counts(self):
        for status in ('failed','error','timedOut','cancelled','skipped'):
            with self.subTest(status=status):
                data=fixture((status,)+('passed',)*15);before=copy.deepcopy(data[3]);result=self.project(data)
                self.assertFalse(result['allSelectedPassed']);self.assertEqual(data[3],before)
                self.assertEqual(sum(x['status']==status for x in result['cases']),1)
    def test_missing_duplicate_foreign_explicit_or_changed_metadata_rejects(self):
        for mode in ('missing','duplicate','foreign','explicit','namespace','assembly','return','parameters','arity','unknown-type-key','three-key','uid','start186','start188','end'):
            with self.subTest(mode=mode):
                ordinary,native,xml,facts=fixture();row=ordinary['uid-0']
                if mode=='missing':del ordinary['uid-0']
                elif mode=='duplicate':ordinary['dup']=copy.deepcopy(row);ordinary['dup']['uid']='dup'
                elif mode=='foreign':row['location']['file']='/private/'+CANARY
                elif mode=='namespace':row['type']['namespace']='Other'
                elif mode=='assembly':row['type']['assemblyFullName']=CANARY
                elif mode=='return':row['type']['returnTypeFullName']='System.Void'
                elif mode=='parameters':row['type']['parameterTypeFullNames']=[]
                elif mode=='arity':row['type']['methodArity']=True
                elif mode=='unknown-type-key':row['type'][CANARY]=CANARY
                elif mode=='three-key':row['type']={k:row['type'][k] for k in ('namespace','typeName','methodName')}
                elif mode=='uid':row['uid']=CANARY
                elif mode.startswith('start'):row['location']['lineStart']=int(mode[5:])
                elif mode=='end':row['location']['lineEnd']=58
                if mode=='explicit':
                    with self.assertRaises(r.Rejected):self.project((ordinary,native,xml,facts),explicit={'uid-0'})
                else:self.reject((ordinary,native,xml,facts))
    def test_native_display_source_group_identity_mismatch_cannot_project(self):
        for key,value in [('displayName',CANARY),('filePath',CANARY),('lineNumber',24),('endLineNumber',58),('className',CANARY),('methodName',CANARY)]:
            ordinary,native,xml,facts=fixture();alltests(native)[0][key]=value;facts['nativeReportSHA256']=r.digest(native)
            self.reject((ordinary,native,xml,facts),'LP_IDENTITY')
    def test_duplicate_foreign_missing_native_uid_rejects_even_claimed_global_gate(self):
        for mode in ('duplicate','foreign','missing'):
            ordinary,native,xml,facts=fixture()
            if mode=='duplicate':native['groups'][0]['tests'].append(copy.deepcopy(alltests(native)[0]))
            elif mode=='foreign':alltests(native)[0]['id']='foreign'
            else:native['groups'][0]['tests'].pop()
            facts['nativeReportSHA256']=r.digest(native);self.reject((ordinary,native,xml,facts),'LP_IDENTITY')
    def test_incomplete_owner_closure_global_digest_or_counts_cannot_project(self):
        for key,value in [('ownerClosed',False),('executionClosureObserved',False),('executionClosureObserved',1),('observed',15),('expected',15),('missingCount',1),('nativeReportSHA256','0'*64),('resultUIDSetSHA256','0'*64),('statusCounts',{}),('retriesObserved',None),('allOrdinaryPassed',None)]:
            data=fixture();data[3][key]=value;self.reject(data,'LP_GATE')
    def test_wrong_provider_runtime_or_schema_rejects(self):
        for key,value in [('schemaVersion',2),('schemaVersion',True),('assemblyName',CANARY),('tunitVersion','1.74.0.0'),('runtimeVersion','.NET 10.0.15')]:
            data=fixture();data[1][key]=value;data[3]['nativeReportSHA256']=r.digest(data[1]);self.reject(data,'LP_IDENTITY')
    def test_unrelated_retry_does_not_modify_global_failure_selected_retry_rejects(self):
        ordinary,native,_,_=fixture();alltests(native)[14]['retryAttempt']=1;data=reseal(ordinary,native)
        self.assertFalse(data[3]['allOrdinaryPassed']);self.assertTrue(self.project(data)['allSelectedPassed'])
        ordinary,native,_,_=fixture();alltests(native)[0]['retryAttempt']=1;self.reject(reseal(ordinary,native),'LP_RETRY')
    def test_full_junit_missing_duplicate_foreign_status_or_totals_rejects(self):
        for mode in ('missing','duplicate','foreign','status','count','doctype','entity','syntax'):
            ordinary,native,xml,facts=fixture();tree=ET.fromstring(xml);suite=tree[0];case=suite.findall('testcase')[0]
            if mode=='missing':suite.remove(case)
            elif mode=='duplicate':suite.append(copy.deepcopy(case))
            elif mode=='foreign':case.set('name',CANARY)
            elif mode=='status':ET.SubElement(case,'error')
            elif mode=='count':tree.set('tests','4')
            xml=ET.tostring(tree,encoding='unicode')
            if mode=='doctype':xml='<!DOCTYPE testsuites>'+xml
            if mode=='entity':xml='<!ENTITY x "'+CANARY+'">'+xml
            if mode=='syntax':xml='<'+CANARY
            self.reject((ordinary,native,xml,facts),'LP_JUNIT')
    def test_selected_junit_exception_message_type_attributes_and_children_reject(self):
        for mode in ('message','type','attribute','nested','case-attribute'):
            ordinary,native,xml,facts=fixture(('error',)+('passed',)*15);tree=ET.fromstring(xml);case=tree[0].findall('testcase')[0];child=case[0]
            if mode in ('message','type'):child.set(mode,'different')
            elif mode=='attribute':child.set(CANARY,CANARY)
            elif mode=='nested':ET.SubElement(child,CANARY)
            else:case.set(CANARY,CANARY)
            self.reject((ordinary,native,ET.tostring(tree,encoding='unicode'),facts),'LP_JUNIT')
    def test_xml_normalization_matches_existing_writer_and_full_classifier(self):
        for text in ('plain','\0x','\u0001','\U0001f642','\ud800','a\nb\tc\rd'):
            self.assertEqual(h.xml_name(text),e.xml_name(text))
        ordinary,native,_,_=fixture(('error',)+('passed',)*15);alltests(native)[0]['exception']['message']='bad\0\U0001f642'+CANARY
        result=self.project(reseal(ordinary,native));self.assertNotIn(CANARY,json.dumps(result))
    def test_unknown_optional_canaries_never_escape(self):
        ordinary,native,_,_=fixture(('error',)+('passed',)*15);test=alltests(native)[0]
        test.update(output=CANARY,errorOutput=CANARY,categories=[CANARY],customProperties=[{'key':CANARY,'value':CANARY}],sourceRelativePath=CANARY,additionalTraceIds=[CANARY])
        test['exception']={'type':CANARY,'message':CANARY,'stackTrace':CANARY};native['artifactUrl']=CANARY
        result=self.project(reseal(ordinary,native));self.assertNotIn(CANARY,json.dumps(result))
        self.assertEqual(next(x for x in result['cases'] if x['status']=='error')['exceptions'][0]['category'],'unknown')
    def test_selected_unknown_fields_malformed_exception_and_depth_fail_closed(self):
        mutations=[lambda t:t.update({CANARY:CANARY}),lambda t:t.update(exception=[]),lambda t:t.update(exception={'type':CANARY}),lambda t:t.update(exception={'type':CANARY,'message':CANARY,'innerExceptions':[]})]
        for mutate in mutations:
            ordinary,native,xml,facts=fixture(('error',)+('passed',)*15);mutate(alltests(native)[0])
            e.classify(native,xml,ordinary,set(),facts,[{'framework':'Microsoft.NETCore.App','version':'10.0.14'}])
            self.reject((ordinary,native,xml,facts))
        ordinary,native,_,_=fixture(('error',)+('passed',)*15);ex={'type':'System.Exception','message':CANARY}
        for _ in range(16):ex={'type':'System.AggregateException','message':CANARY,'innerException':ex}
        alltests(native)[0]['exception']=ex;self.reject(reseal(ordinary,native),'LP_LIMIT')
    def test_serialized_inner_chain_does_not_claim_siblings_or_disposal_cause(self):
        ordinary,native,_,_=fixture(('error',)+('passed',)*15)
        alltests(native)[0]['exception']={'type':'System.AggregateException','message':CANARY,'innerException':{'type':'SharpLink.Runtime.PhysicalTaskFailureException','message':CANARY,'innerException':{'type':'System.TimeoutException','message':CANARY}}}
        result=self.project(reseal(ordinary,native));case=next(x for x in result['cases'] if x['status']=='error')
        self.assertEqual([x['category'] for x in case['exceptions']],['aggregate','physical-task-failure','timeout'])
        self.assertFalse(case['aggregateSiblingCausesObserved']);self.assertEqual(case['bodyVersusCleanup'],'inconclusive')
        self.assertEqual(case['exceptionEvidenceScope'],'serialized-innerException-chain-only')
    def test_failure_never_partially_publishes_or_overwrites_original_error_facts(self):
        ordinary,native,xml,facts=fixture(('passed',)*14+('error','passed'));original=copy.deepcopy(facts)
        tree=ET.fromstring(xml);tree[0].findall('testcase')[3].set(CANARY,CANARY)
        with self.assertRaises(r.Rejected):h.project(r,native,ET.tostring(tree,encoding='unicode'),ordinary,set(),facts,BASE)
        self.assertEqual(facts,original);self.assertNotIn('lifetimeDiagnostic',facts)
    def test_helper_is_silent_on_success_or_rejection(self):
        with contextlib.redirect_stdout(io.StringIO()) as out,contextlib.redirect_stderr(io.StringIO()) as err:
            self.project();data=fixture();data[3]['ownerClosed']=False
            with self.assertRaises(r.Rejected):self.project(data)
        self.assertEqual(out.getvalue(),'');self.assertEqual(err.getvalue(),'')

    def test_inner_chain_and_stack_input_limits_are_finite(self):
        for mode in ('string','stack-lines','inner-list'):
            ordinary,native,xml,facts=fixture(('error',)+('passed',)*15)
            ex=alltests(native)[0]['exception']
            if mode=='string':ex['message']='x'*(1024*1024+1)
            elif mode=='stack-lines':ex['stackTrace']='\n'.join(['x']*4097)
            else:ex['innerException']=[]
            e.classify(native,xml,ordinary,set(),facts,[{'framework':'Microsoft.NETCore.App','version':'10.0.14'}])
            self.reject((ordinary,native,xml,facts))
        ex={'type':'System.TimeoutException','message':CANARY}
        for _ in range(15):ex={'type':'System.AggregateException','message':CANARY,'innerException':ex}
        self.assertEqual(len(h.projected_exception(r,0,ex)),16)
    def test_mutated_reports_after_reconciliation_cannot_reuse_prior_facts(self):
        ordinary,native,xml,facts=fixture();alltests(native)[0]['output']=CANARY
        self.reject((ordinary,native,xml,facts),'LP_GATE')
    def test_projection_has_no_global_pass_or_exit_control(self):
        result=self.project(fixture(('error',)+('passed',)*15))
        self.assertNotIn('executionQualified',result);self.assertNotIn('bodyExitCode',result)
        self.assertNotIn('exit',result);self.assertNotIn('fullOrdinaryRegressionQualified',result)
    def test_selected_result_retry_attempts_and_invalid_exception_presence_reject(self):
        for mode in ('attempts','missing-error','pass-exception','negative-duration'):
            ordinary,native,xml,facts=fixture(('error',)+('passed',)*15)
            test=alltests(native)[0]
            if mode=='attempts':test['attempts']=[]
            elif mode=='missing-error':del test['exception']
            elif mode=='pass-exception':alltests(native)[1]['exception']={'type':'System.Exception','message':CANARY}
            else:test['durationMs']=-1
            facts['nativeReportSHA256']=r.digest(native)
            self.reject((ordinary,native,xml,facts))

    def test_all_2879_rows_mixed_status_projection_preserves_full_evidence(self):
        data=fixture(('passed',)*14+('error',)*265+('passed',)*2600)
        self.assertEqual(len(data[0]),2879);self.assertEqual(len(alltests(data[1])),2879)
        before=copy.deepcopy(data);result=self.project(data)
        self.assertEqual(data,before);self.assertEqual(len(result['cases']),14)
        self.assertTrue(result['allSelectedPassed']);self.assertFalse(data[3]['allOrdinaryPassed'])
        self.assertEqual(data[3]['statusCounts']['total'],2879)
        self.assertEqual(data[3]['statusCounts']['passed'],2614);self.assertEqual(data[3]['statusCounts']['failed'],265)
        self.assertEqual(result['fullResultUIDSetSHA256'],r.digest(sorted(data[0])))
        self.assertNotIn(CANARY,json.dumps(result));self.assertLess(len(json.dumps(result)),24000)

    def classify(self,index=0,status='error',exception=None):
        ordinary,native,_,_=fixture(tuple(status if i==index else 'passed' for i in range(16)))
        if exception is not None:alltests(native)[index]['exception']=exception
        result=self.project(reseal(ordinary,native));caseid=h.hashlib.sha256(('uid-'+str(index)).encode()).hexdigest()
        return next(x for x in result['cases'] if x['argumentIdentity']['caseID']==caseid)

    def test_exact_fourteen_source_and_parameter_proof_pins(self):
        h.verify_sources(r,BASE)
        self.assertEqual(h.SOURCE_PINS['eng/protocol-lifetime-history/argument-display-name-proof.json'],'37147528b72191820bfc81686c74f37baf6ac2f62369d6222c58697f81edd82b')
        self.assertEqual(len(h.CASES),14);self.assertEqual(len(h.GROUPS),8)
        self.assertEqual([h.group_for(i)['kind'] for i in range(14)],['response-lease']*4+['timer']*3+['prebuild']*7)
        catalog=r.read_json(BASE/'eng/unit-census.json')
        for i,(source,ns,cls,method,start,end) in enumerate(h.CASES):
            rows=[x for x in catalog['methods'] if (x['source'],x['namespace'],x['className'],x['methodName'])==(source,ns,cls,method)]
            self.assertEqual(len(rows),1);row=rows[0]
            self.assertEqual((row['attributeLine'],row['signatureLine'],row['endLine']),(h.ATTRIBUTE_LINES[i],start,end))
            self.assertEqual(row['parameters'],h.PARAMETER_TYPES[i]);self.assertEqual(row['expectedCases'],h.group_for(i)['count'])
            self.assertEqual(row['initialEligibility'],'ordinary')
        self.assertFalse(any('ProtocolV2Tests' in x or 'unit-census' in x for x in h.SOURCE_PINS))
        selected=h.select_fourteen(r,fixture()[0],set());self.assertEqual(len(selected),14)
        self.assertTrue(all(len(row['type'])==7 for _,row in selected))

    def test_complete_literal_sets_native_equality_and_no_uid_order_inference(self):
        ordinary,native,_,_=fixture();ordinary=dict(reversed(list(ordinary.items())))
        for group in native['groups']:group['tests'].reverse()
        result=self.project(reseal(ordinary,native));self.assertEqual(result['parameterSetsProven'],{'duplex':True,'stage':True})
        byid={x['argumentIdentity']['caseID']:x['argumentIdentity'] for x in result['cases']}
        for i in [0,1]+list(range(7,13)):
            expected=list(h.expected_displays(h.group_for(i)).values())[i-h.group_for(i)['first']]
            ident=byid[h.hashlib.sha256(('uid-'+str(i)).encode()).hexdigest()]
            self.assertEqual((ident['value'],ident['literalSource']['line']),expected)
        for i in [0,1]+list(range(7,13)):
            ordinary,native,xml,facts=fixture();alltests(native)[i]['displayName']=CANARY;facts['nativeReportSHA256']=r.digest(native)
            self.reject((ordinary,native,xml,facts),'LP_IDENTITY')

    def test_unknown_parameter_names_make_entire_group_opaque(self):
        for first,count,param in ((0,2,'duplex'),(7,6,'stage')):
            for mode in ('unknown','casefold','quotes','suffix','prefix','dots'):
                ordinary,native,_,_=fixture();row=ordinary['uid-'+str(first)];display=row['displayName']
                display={'unknown':CANARY,'casefold':display.lower(),'quotes':display.replace('(', '("').replace(')', '")'),'suffix':display+CANARY,'prefix':CANARY+display,'dots':display.replace('(', '(private.')}[mode]
                row['displayName']=display;alltests(native)[first]['displayName']=display
                result=self.project(reseal(ordinary,native));self.assertFalse(result['parameterSetsProven'][param])
                for case in result['cases'][first:first+count]:
                    self.assertEqual(case['argumentIdentity']['kind'],'opaque-parameter-case');self.assertIsNone(case['argumentIdentity']['value'])
                self.assertNotIn(CANARY,json.dumps(result))

    def test_missing_extra_duplicate_metadata_and_exact_attributed_parameter_spans(self):
        for index in [0,1]+list(range(7,13)):
            for mode in ('missing','extra','declaration','argument-line','parameter'):
                ordinary,native,xml,facts=fixture();uid='uid-'+str(index);row=ordinary[uid]
                if mode=='missing':del ordinary[uid]
                elif mode=='extra':ordinary['extra']={**copy.deepcopy(row),'uid':'extra'}
                elif mode=='declaration':row['location']['lineStart']=h.CASES[index][4]
                elif mode=='argument-line':row['location']['lineStart']+=1
                else:row['type']['parameterTypeFullNames']=['System.Object']
                self.reject((ordinary,native,xml,facts),'LP_SELECTION')

    def test_normalized_junit_collisions_reject_across_namespace_and_status(self):
        for kind in ('same-status','different-status','xml-normalized'):
            ordinary,native,_,_=fixture(('error',)+('passed',)*15 if kind=='different-status' else ('passed',)*16)
            target=ordinary['uid-0'];row=ordinary['uid-14']
            target_name=target['displayName']
            if kind=='xml-normalized':
                target_name='private\0'+CANARY;target['displayName']=target_name;alltests(native)[0]['displayName']=target_name
            row['displayName']=h.xml_name(target_name);row['type']['typeName']=target['type']['typeName']
            old=next(g for g in native['groups'] if any(t['id']=='uid-14' for t in g['tests']))
            test=next(t for t in old['tests'] if t['id']=='uid-14');old['tests'].remove(test)
            test['displayName']=row['displayName'];test['className']=row['type']['typeName']
            native['groups'].append({'namespace':'Other','className':row['type']['typeName'],'tests':[test]})
            self.reject(reseal(ordinary,native),'LP_JUNIT')
        for first in (0,7):
            for statuses in (('passed',)*16,tuple('error' if i==first else 'passed' for i in range(16))):
                ordinary,native,_,_=fixture(statuses);ordinary['uid-'+str(first+1)]['displayName']=ordinary['uid-'+str(first)]['displayName']
                alltests(native)[first+1]['displayName']=alltests(native)[first]['displayName']
                self.reject(reseal(ordinary,native),'LP_JUNIT')

    def test_sixtynine_exact_source_tags_and_finite_derived_messages(self):
        self.assertEqual(len(h.EXCEPTION_TAGS),69)
        for indexes,typ,message,tag,path,line in h.EXCEPTION_TAGS:
            text=(BASE/path).read_text().splitlines()[line-1]
            if message.startswith('R23 exact '):
                self.assertEqual(line,69);self.assertIn('"R23 exact " + stage + " prebuild marker"',text)
                self.assertIn(message.split()[2],('targets','buckets','providers','state','signal'))
            elif message=='Missing actual diagnostic field _error':
                self.assertIn('Missing actual diagnostic field {field}',text);self.assertIn('"_error"',(BASE/path).read_text().splitlines()[41])
            else:self.assertIn(message,text)
            out=h.projected_exception(r,indexes[0],{'type':typ,'message':message})[0]
            self.assertEqual(out['assertionTag'],tag);self.assertNotIn(message,json.dumps(out))
            for changed in (message+CANARY,CANARY+message):
                self.assertEqual(h.projected_exception(r,indexes[0],{'type':typ,'message':changed})[0]['assertionTag'],'unknown')
            outside=next(i for i in range(14) if i not in indexes)
            self.assertEqual(h.projected_exception(r,outside,{'type':typ,'message':message})[0]['assertionTag'],'unknown')

    def test_fixed_and_lambda_frames_are_source_bound_and_have_no_generated_output(self):
        for indexes,path,typ,member,signature,first,last in h.EXTRA_FRAMES:
            self.assertIn(path,h.SOURCE_PINS);self.assertLessEqual(first,last)
            good=f'   at {typ}.{member}({signature}) in /_/{path}:line {last}'
            projected=h.projected_exception(r,indexes[0],{'type':'System.AggregateException','message':CANARY,'stackTrace':good+'\n'+good+CANARY})[0]
            self.assertEqual(projected['sourceFrames'],[{'source':path,'member':member,'line':last}]);self.assertEqual(projected['unrecognizedStackLineCount'],1)
        for i in (0,2,3,7,13):
            path,ns,cls,method,start,end=h.CASES[i];signature='Fixture fixture' if i<4 else 'FailureFixture fixture'
            prefix=f'   at {ns}.{cls}.<>c__DisplayClass12_0.'
            for good in (f'{prefix}<{method}>b__0({signature}) in /_/{path}:line {start+3}',f'{prefix}<<{method}>b__0>d.MoveNext() in /_/{path}:line {start+3}'):
                negatives=[good+CANARY,CANARY+good,good.replace('12_0','1234_0'),good.replace('b__0','b__1234'),good.replace(method,'Unknown'),good.replace('/_/','/private/'),good.replace(':line '+str(start+3),':line '+str(end+1)),good.replace(cls,'UnknownClass'),good.replace('fixture','privateValue') if 'fixture' in good else good.replace('MoveNext()', 'MoveNext(privateValue)')]
                out=self.classify(i,exception={'type':'System.AggregateException','message':CANARY,'stackTrace':'\n'.join([good]+negatives)})
                self.assertEqual(out['exceptions'][0]['sourceFrames'],[{'source':path,'member':method+'-body-lambda','line':start+3}])
                self.assertEqual(out['exceptions'][0]['unrecognizedStackLineCount'],len(negatives));self.assertEqual(out['bodyVersusCleanup'],'inconclusive')
                self.assertNotIn(CANARY,json.dumps(out));self.assertNotIn('DisplayClass',json.dumps(out))

    def test_frames_have_fixed_sixtyfour_cap_and_unknown_type_private_arrays_fail_closed(self):
        i=4;path,ns,cls,method,start,end=h.CASES[i];frame=f'   at {ns}.{cls}.{method}() in /_/{path}:line 201'
        row=self.classify(i,exception={'type':'System.TimeoutException','message':CANARY,'stackTrace':'\n'.join([frame]*70)})['exceptions'][0]
        self.assertEqual((len(row['sourceFrames']),row['omittedRecognizedStackLineCount'],row['unrecognizedStackLineCount']),(64,6,0))
        for typ,category in (('SharpLink.Client.SharpLinkClient+ContractRouteFailure','contract-route-failure'),('SharpLink.Client.SharpLinkClient.ContractRouteFailure','unknown')):
            out=self.classify(7,exception={'type':typ,'message':CANARY});self.assertEqual(out['exceptions'][0]['category'],category);self.assertFalse(out['privateRouteFailurePayloadObserved']);self.assertNotIn(CANARY,json.dumps(out))
        for key in ('InnerExceptions','innerExceptions','ErrorAt','StageAt','errors','stages'):
            with self.assertRaises(r.Rejected):self.classify(7,exception={'type':'SharpLink.Client.SharpLinkClient+ContractRouteFailure','message':CANARY,key:[CANARY]})

    def test_rejecting_last_selected_case_never_returns_partial_fourteen(self):
        data=fixture(('passed',)*14+('error','passed'));before=copy.deepcopy(data)
        ordinary,native,xml,facts=data;tree=ET.fromstring(xml);tree[0].findall('testcase')[13].set(CANARY,CANARY)
        with self.assertRaises(r.Rejected):h.project(r,native,ET.tostring(tree,encoding='unicode'),ordinary,set(),facts,BASE)
        self.assertEqual(data,before);self.assertEqual(facts['statusCounts']['failed'],1)

if __name__=='__main__':unittest.main()
