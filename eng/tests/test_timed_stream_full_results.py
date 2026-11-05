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
BASE=ROOT if (ROOT/'eng/unit-execution-results.py').is_file() else ROOT.parent/'sharplink-stream-negotiation-diagnostic-20261105'
if len(sys.argv)==3 and sys.argv[1]=='--reviewed-base':BASE=Path(sys.argv[2]);sys.argv[1:]=[]
def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path);mod=importlib.util.module_from_spec(spec);spec.loader.exec_module(mod);return mod
h=load('timed_full_projection',ROOT/'eng/timed-stream-full-results.py')
e=load('current_full_classifier',BASE/'eng/unit-execution-results.py');r=e.census
CANARY='PRIVATE_FULL_TIMED_CANARY_e31c02a7'
ASSEMBLY='SharpLink.UnitTests, Version=3.0.0.0, Culture=neutral, PublicKeyToken=null'

def fixture(statuses=('passed',)*6):
    ordinary={};tests=[]
    for index,status in enumerate(statuses):
        if index<4:
            source,namespace,cls,method,start,end=h.CASES[index];attribute=h.ATTRIBUTE_LINES[index]
            display=method+('(True)' if index==0 else '(False)') if index<2 else method
        else:source,namespace,cls,method,attribute,end='test/SharpLink.UnitTests/Other.cs','Other','OtherClass','Other'+str(index),10+index,12+index;display=CANARY+str(index)
        uid='uid-'+str(index)
        row={'uid':uid,'displayName':display,'type':{'namespace':namespace,'typeName':cls,'methodName':method,'assemblyFullName':ASSEMBLY,
            'methodArity':0,'returnTypeFullName':'System.Threading.Tasks.Task','parameterTypeFullNames':['System.Boolean'] if index<2 else []},
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
            'publicationGeneration':'a'*32,'machineName':CANARY,'timestamp':CANARY,'operatingSystem':CANARY,'totalDurationMs':6.0,'groups':groups}
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
    def test_binding_pins_match_sources_and_exact_official_proof(self):
        h.verify_sources(r,BASE)
        self.assertEqual(h.SOURCE_PINS['test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs'],'bb0bf4e489c2bc58ca070c496cc33a0681c874331b574f40005ba2e1ef14ff15')
        self.assertEqual(h.SOURCE_PINS['eng/stream-negotiation-history/boolean-display-name-proof.json'],'1917416924ccc1f506b7922aff4872d8b7bac20bfa2147da12347129c4cfed6e')
    def test_full_document_returns_only_four_without_mutating_inputs(self):
        data=fixture();before=copy.deepcopy(data);result=self.project(data)
        self.assertEqual(data,before);self.assertEqual(len(alltests(data[1])),6);self.assertEqual(len(result['cases']),4)
        self.assertTrue(result['complete']);self.assertTrue(result['allSelectedPassed']);self.assertTrue(result['doesNotQualifyFullSuite'])
        self.assertTrue(result['booleanPairProven']);self.assertEqual({x['argumentIdentity']['value'] for x in result['cases'][:2]},{True,False})
        self.assertFalse(result['historicalFullRunArgumentMappingAvailable']);self.assertNotIn(CANARY,json.dumps(result))
        self.assertEqual(result['nativeFullReportCanonicalSHA256'],r.digest(data[1]))
        self.assertEqual(result['fullResultUIDSetSHA256'],r.digest(sorted(data[0])))
    def test_real_seven_key_metadata_and_source_spans(self):
        data=fixture();selected=h.select_four(r,data[0],set());self.assertEqual(len(selected),4)
        self.assertTrue(all(len(x[1]['type'])==7 for x in selected))
        self.assertEqual([x[1]['location']['lineStart'] for x in selected],[185,185,17,54])
    def test_no_runner_import_or_process_file_write_api(self):
        text=(ROOT/'eng/timed-stream-full-results.py').read_text()
        for forbidden in ('import subprocess','import importlib','write_text(', 'write_bytes(', 'run-unit-', 'unit-census.json', 'run-full-regression.py'):
            self.assertNotIn(forbidden,text)
    def test_source_pin_mutation_and_reader_failure_stop_projection(self):
        with patch.object(r,'sha',return_value='0'*64):
            with self.assertRaises(r.Rejected) as c:self.project()
            self.assertEqual(c.exception.code,'TP_SOURCE')
        original=OSError(CANARY)
        with patch.object(r,'sha',side_effect=original):
            with self.assertRaises(OSError) as c:self.project()
            self.assertIs(c.exception,original)
    def test_unrelated_suite_failure_keeps_original_global_failure(self):
        data=fixture(('passed',)*4+('error','passed'));before=copy.deepcopy(data[3]);result=self.project(data)
        self.assertTrue(result['allSelectedPassed']);self.assertFalse(data[3]['allOrdinaryPassed']);self.assertEqual(data[3],before)
        self.assertEqual(data[3]['statusCounts']['failed'],1)
    def test_selected_failures_and_all_statuses_preserve_global_counts(self):
        for status in ('failed','error','timedOut','cancelled','skipped'):
            with self.subTest(status=status):
                data=fixture((status,)+('passed',)*5);before=copy.deepcopy(data[3]);result=self.project(data)
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
                elif mode=='end':row['location']['lineEnd']=258
                if mode=='explicit':
                    with self.assertRaises(r.Rejected):self.project((ordinary,native,xml,facts),explicit={'uid-0'})
                else:self.reject((ordinary,native,xml,facts))
    def test_unique_pair_from_discovery_not_order_or_uid(self):
        ordinary,native,xml,facts=fixture();ordinary=dict(reversed(list(ordinary.items())))
        native['groups'][0]['tests'].reverse();data=reseal(ordinary,native);result=self.project(data)
        values={x['argumentIdentity']['caseID']:x['argumentIdentity']['value'] for x in result['cases'][:2]}
        self.assertEqual(values[h.hashlib.sha256(b'uid-0').hexdigest()],True);self.assertEqual(values[h.hashlib.sha256(b'uid-1').hexdigest()],False)
    def test_unknown_pair_downgrades_both_but_duplicate_junit_identity_rejects(self):
        ordinary,native,xml,facts=fixture();ordinary['uid-1']['displayName']=CANARY;alltests(native)[1]['displayName']=CANARY
        result=self.project(reseal(ordinary,native));self.assertFalse(result['booleanPairProven'])
        self.assertTrue(all(x['argumentIdentity']['value'] is None for x in result['cases'][:2]));self.assertNotIn(CANARY,json.dumps(result))
        ordinary,native,xml,facts=fixture();ordinary['uid-1']['displayName']=ordinary['uid-0']['displayName'];alltests(native)[1]['displayName']=alltests(native)[0]['displayName']
        self.reject(reseal(ordinary,native),'TP_JUNIT')
    def test_native_display_source_group_identity_mismatch_cannot_project(self):
        for key,value in [('displayName',CANARY),('filePath',CANARY),('lineNumber',186),('endLineNumber',258),('className',CANARY),('methodName',CANARY)]:
            ordinary,native,xml,facts=fixture();alltests(native)[0][key]=value;facts['nativeReportSHA256']=r.digest(native)
            self.reject((ordinary,native,xml,facts),'TP_IDENTITY')
    def test_duplicate_foreign_missing_native_uid_rejects_even_claimed_global_gate(self):
        for mode in ('duplicate','foreign','missing'):
            ordinary,native,xml,facts=fixture()
            if mode=='duplicate':native['groups'][0]['tests'].append(copy.deepcopy(alltests(native)[0]))
            elif mode=='foreign':alltests(native)[0]['id']='foreign'
            else:native['groups'][0]['tests'].pop()
            facts['nativeReportSHA256']=r.digest(native);self.reject((ordinary,native,xml,facts),'TP_IDENTITY')
    def test_incomplete_owner_closure_global_digest_or_counts_cannot_project(self):
        for key,value in [('ownerClosed',False),('executionClosureObserved',False),('executionClosureObserved',1),('observed',5),('expected',5),('missingCount',1),('nativeReportSHA256','0'*64),('resultUIDSetSHA256','0'*64),('statusCounts',{}),('retriesObserved',None),('allOrdinaryPassed',None)]:
            data=fixture();data[3][key]=value;self.reject(data,'TP_GATE')
    def test_wrong_provider_runtime_or_schema_rejects(self):
        for key,value in [('schemaVersion',2),('schemaVersion',True),('assemblyName',CANARY),('tunitVersion','1.74.0.0'),('runtimeVersion','.NET 10.0.15')]:
            data=fixture();data[1][key]=value;data[3]['nativeReportSHA256']=r.digest(data[1]);self.reject(data,'TP_IDENTITY')
    def test_unrelated_retry_does_not_modify_global_failure_selected_retry_rejects(self):
        ordinary,native,_,_=fixture();alltests(native)[4]['retryAttempt']=1;data=reseal(ordinary,native)
        self.assertFalse(data[3]['allOrdinaryPassed']);self.assertTrue(self.project(data)['allSelectedPassed'])
        ordinary,native,_,_=fixture();alltests(native)[0]['retryAttempt']=1;self.reject(reseal(ordinary,native),'TP_RETRY')
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
            self.reject((ordinary,native,xml,facts),'TP_JUNIT')
    def test_full_report_junit_cross_namespace_collision_is_rejected(self):
        ordinary,native,xml,facts=fixture();row=ordinary['uid-4'];target=ordinary['uid-0']
        row['displayName']=target['displayName'];row['type']['typeName']=target['type']['typeName']
        old=native['groups'][1];test=old['tests'].pop(0);test['displayName']=row['displayName'];test['className']=row['type']['typeName']
        native['groups'].append({'namespace':'Other','className':row['type']['typeName'],'tests':[test]})
        self.reject(reseal(ordinary,native),'TP_JUNIT')
    def test_selected_junit_exception_message_type_attributes_and_children_reject(self):
        for mode in ('message','type','attribute','nested','case-attribute'):
            ordinary,native,xml,facts=fixture(('error',)+('passed',)*5);tree=ET.fromstring(xml);case=tree[0].findall('testcase')[0];child=case[0]
            if mode in ('message','type'):child.set(mode,'different')
            elif mode=='attribute':child.set(CANARY,CANARY)
            elif mode=='nested':ET.SubElement(child,CANARY)
            else:case.set(CANARY,CANARY)
            self.reject((ordinary,native,ET.tostring(tree,encoding='unicode'),facts),'TP_JUNIT')
    def test_xml_normalization_matches_existing_writer_and_full_classifier(self):
        for text in ('plain','\0x','\u0001','\U0001f642','\ud800','a\nb\tc\rd'):
            self.assertEqual(h.xml_name(text),e.xml_name(text))
        ordinary,native,_,_=fixture(('error',)+('passed',)*5);alltests(native)[0]['exception']['message']='bad\0\U0001f642'+CANARY
        result=self.project(reseal(ordinary,native));self.assertNotIn(CANARY,json.dumps(result))
    def test_unknown_optional_canaries_never_escape(self):
        ordinary,native,_,_=fixture(('error',)+('passed',)*5);test=alltests(native)[0]
        test.update(output=CANARY,errorOutput=CANARY,categories=[CANARY],customProperties=[{'key':CANARY,'value':CANARY}],sourceRelativePath=CANARY,additionalTraceIds=[CANARY])
        test['exception']={'type':CANARY,'message':CANARY,'stackTrace':CANARY};native['artifactUrl']=CANARY
        result=self.project(reseal(ordinary,native));self.assertNotIn(CANARY,json.dumps(result))
        self.assertEqual(next(x for x in result['cases'] if x['status']=='error')['exceptions'][0]['category'],'unknown')
    def test_selected_unknown_fields_malformed_exception_and_depth_fail_closed(self):
        mutations=[lambda t:t.update({CANARY:CANARY}),lambda t:t.update(exception=[]),lambda t:t.update(exception={'type':CANARY}),lambda t:t.update(exception={'type':CANARY,'message':CANARY,'innerExceptions':[]})]
        for mutate in mutations:
            ordinary,native,xml,facts=fixture(('error',)+('passed',)*5);mutate(alltests(native)[0])
            e.classify(native,xml,ordinary,set(),facts,[{'framework':'Microsoft.NETCore.App','version':'10.0.14'}])
            self.reject((ordinary,native,xml,facts))
        ordinary,native,_,_=fixture(('error',)+('passed',)*5);ex={'type':'System.Exception','message':CANARY}
        for _ in range(16):ex={'type':'System.AggregateException','message':CANARY,'innerException':ex}
        alltests(native)[0]['exception']=ex;self.reject(reseal(ordinary,native),'TP_LIMIT')
    def test_serialized_inner_chain_does_not_claim_siblings_or_disposal_cause(self):
        ordinary,native,_,_=fixture(('error',)+('passed',)*5)
        alltests(native)[0]['exception']={'type':'System.AggregateException','message':CANARY,'innerException':{'type':'SharpLink.Runtime.PhysicalTaskFailureException','message':CANARY,'innerException':{'type':'System.TimeoutException','message':CANARY}}}
        result=self.project(reseal(ordinary,native));case=next(x for x in result['cases'] if x['status']=='error')
        self.assertEqual([x['category'] for x in case['exceptions']],['aggregate','physical-task-failure','timeout'])
        self.assertFalse(case['aggregateSiblingCausesObserved']);self.assertEqual(case['bodyVersusDisposal'],'inconclusive')
        self.assertEqual(case['exceptionEvidenceScope'],'serialized-innerException-chain-only')
    def test_frame_allowlist_only_fixed_bound_coordinates_and_sixtyfour_limit(self):
        source,namespace,cls,method,_,_=h.CASES[0]
        frame=f'   at {namespace}.{cls}.{method}(Boolean expiresDuringEmission) in /_/{source}:line 254'
        raw='\n'.join([frame]*70+[frame+CANARY,frame.replace('/_/','/private/'+CANARY+'/'),frame.replace('254','999'),CANARY])
        ordinary,native,_,_=fixture(('error',)+('passed',)*5);alltests(native)[0]['exception']['stackTrace']=raw
        result=self.project(reseal(ordinary,native));case=next(x for x in result['cases'] if x['status']=='error');ex=case['exceptions'][0]
        self.assertEqual(len(ex['sourceFrames']),64);self.assertEqual(ex['omittedRecognizedStackLineCount'],6);self.assertEqual(ex['unrecognizedStackLineCount'],4)
        self.assertNotIn(CANARY,json.dumps(result))
    def test_source_literal_tags_and_product_cleanup_frames(self):
        for indexes,typ,message,tag,path,line in h.EXCEPTION_TAGS:
            self.assertIn(message,(BASE/path).read_text().splitlines()[line-1])
            ex=h.projected_exception(r,indexes[0],{'type':typ,'message':message})[0]
            self.assertEqual(ex['assertionTag'],tag);self.assertNotIn(message,json.dumps(ex))
            self.assertEqual(h.projected_exception(r,indexes[0],{'type':typ,'message':message+CANARY})[0]['assertionTag'],'unknown')
        raw='   at SharpLink.Client.SharpLinkClient.ThrowStopCleanupFailures(List`1 failures) in /_/src/SharpLink.Client/SharpLinkClient.cs:line 261'
        self.assertEqual(h.projected_exception(r,0,{'type':'System.AggregateException','message':CANARY,'stackTrace':raw})[0]['sourceFrames'][0]['line'],261)
    def test_failure_never_partially_publishes_or_overwrites_original_error_facts(self):
        ordinary,native,xml,facts=fixture(('passed',)*4+('error','passed'));original=copy.deepcopy(facts)
        tree=ET.fromstring(xml);tree[0].findall('testcase')[3].set(CANARY,CANARY)
        with self.assertRaises(r.Rejected):h.project(r,native,ET.tostring(tree,encoding='unicode'),ordinary,set(),facts,BASE)
        self.assertEqual(facts,original);self.assertNotIn('timedStreamDiagnostic',facts)
    def test_helper_is_silent_on_success_or_rejection(self):
        with contextlib.redirect_stdout(io.StringIO()) as out,contextlib.redirect_stderr(io.StringIO()) as err:
            self.project();data=fixture();data[3]['ownerClosed']=False
            with self.assertRaises(r.Rejected):self.project(data)
        self.assertEqual(out.getvalue(),'');self.assertEqual(err.getvalue(),'')

    def test_inner_chain_and_stack_input_limits_are_finite(self):
        for mode in ('string','stack-lines','inner-list'):
            ordinary,native,xml,facts=fixture(('error',)+('passed',)*5)
            ex=alltests(native)[0]['exception']
            if mode=='string':ex['message']='x'*(1024*1024+1)
            elif mode=='stack-lines':ex['stackTrace']='\n'.join(['x']*4097)
            else:ex['innerException']=[]
            e.classify(native,xml,ordinary,set(),facts,[{'framework':'Microsoft.NETCore.App','version':'10.0.14'}])
            self.reject((ordinary,native,xml,facts))
        ex={'type':'System.TimeoutException','message':CANARY}
        for _ in range(15):ex={'type':'System.AggregateException','message':CANARY,'innerException':ex}
        self.assertEqual(len(h.projected_exception(r,0,ex)),16)
    def test_casefold_or_suffix_boolean_names_are_not_parsed(self):
        for display in (h.CASES[0][3]+'(true)',h.CASES[0][3]+'(True)'+CANARY,CANARY+'(False)'):
            ordinary,native,_,_=fixture();ordinary['uid-0']['displayName']=display;alltests(native)[0]['displayName']=display
            result=self.project(reseal(ordinary,native));self.assertFalse(result['booleanPairProven'])
            self.assertTrue(all(x['argumentIdentity']['value'] is None for x in result['cases'][:2]));self.assertNotIn(CANARY,json.dumps(result))
    def test_mutated_reports_after_reconciliation_cannot_reuse_prior_facts(self):
        ordinary,native,xml,facts=fixture();alltests(native)[0]['output']=CANARY
        self.reject((ordinary,native,xml,facts),'TP_GATE')
    def test_projection_has_no_global_pass_or_exit_control(self):
        result=self.project(fixture(('error',)+('passed',)*5))
        self.assertNotIn('executionQualified',result);self.assertNotIn('bodyExitCode',result)
        self.assertNotIn('exit',result);self.assertNotIn('fullOrdinaryRegressionQualified',result)
    def test_selected_result_retry_attempts_and_invalid_exception_presence_reject(self):
        for mode in ('attempts','missing-error','pass-exception','negative-duration'):
            ordinary,native,xml,facts=fixture(('error',)+('passed',)*5)
            test=alltests(native)[0]
            if mode=='attempts':test['attempts']=[]
            elif mode=='missing-error':del test['exception']
            elif mode=='pass-exception':alltests(native)[1]['exception']={'type':'System.Exception','message':CANARY}
            else:test['durationMs']=-1
            facts['nativeReportSHA256']=r.digest(native)
            self.reject((ordinary,native,xml,facts))

    def test_all_2879_rows_mixed_status_projection_preserves_full_evidence(self):
        data=fixture(('passed',)*4+('error',)*272+('passed',)*2603)
        self.assertEqual(len(data[0]),2879);self.assertEqual(len(alltests(data[1])),2879)
        before=copy.deepcopy(data);result=self.project(data)
        self.assertEqual(data,before);self.assertEqual(len(result['cases']),4)
        self.assertTrue(result['allSelectedPassed']);self.assertFalse(data[3]['allOrdinaryPassed'])
        self.assertEqual(data[3]['statusCounts']['total'],2879)
        self.assertEqual(data[3]['statusCounts']['passed'],2607);self.assertEqual(data[3]['statusCounts']['failed'],272)
        self.assertEqual(result['fullResultUIDSetSHA256'],r.digest(sorted(data[0])))
        self.assertNotIn(CANARY,json.dumps(result));self.assertLess(len(json.dumps(result)),8192)

if __name__=='__main__':unittest.main()
