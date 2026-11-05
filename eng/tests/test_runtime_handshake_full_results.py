"""Python-only synthetic adversarial controls. Never executes .NET."""
import contextlib
import copy
import importlib.util
import io
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
sys.dont_write_bytecode=True
ROOT=Path(__file__).resolve().parents[2]
BASE=ROOT if (ROOT/'eng/unit-execution-results.py').is_file() else ROOT.parent/'sharplink-protocol70-cap-full-regression-20261105'
if len(sys.argv)==3 and sys.argv[1]=='--reviewed-base':BASE=Path(sys.argv[2]);sys.argv[1:]=[]
def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path);mod=importlib.util.module_from_spec(spec);spec.loader.exec_module(mod);return mod
h=load('runtime_handshake_full_projection',ROOT/'eng/runtime-handshake-full-results.py')
e=load('handshake_full_classifier',BASE/'eng/unit-execution-results.py');r=e.census
CANARY='PRIVATE_HANDSHAKE_CANARY_f60d04bb'
ASSEMBLY='SharpLink.UnitTests, Version=3.0.0.0, Culture=neutral, PublicKeyToken=null'

def fixture(statuses=('passed',)*4,total=4):
    assert total>=4 and len(statuses)<=total
    statuses=tuple(statuses)+('passed',)*(total-len(statuses));ordinary={};tests=[]
    for index,status in enumerate(statuses):
        if index<4:source,ns,cls,method,start,end=h.CASES[index];attribute=h.ATTRIBUTE_LINES[index];display=method
        else:source,ns,cls,method,attribute,end='test/SharpLink.UnitTests/Other.cs','Other','OtherClass','Other'+str(index),10,12;display=CANARY+str(index)
        uid='uid-'+str(index)
        ordinary[uid]={'uid':uid,'displayName':display,'type':{'namespace':ns,'typeName':cls,'methodName':method,'assemblyFullName':ASSEMBLY,
            'methodArity':0,'returnTypeFullName':'System.Threading.Tasks.Task','parameterTypeFullNames':[]},
            'location':{'file':'/_/'+source,'lineStart':attribute,'lineEnd':end}}
        test={'id':uid,'displayName':display,'methodName':method,'className':cls,'filePath':'/_/'+source,
            'lineNumber':attribute,'endLineNumber':end,'status':status,'durationMs':1.0}
        if status not in ('passed','skipped'):test['exception']={'type':'System.Exception','message':CANARY,'stackTrace':CANARY}
        if status=='skipped':test['skipReason']=CANARY
        tests.append((ns,cls,test))
    groups=[]
    for ns,cls,test in tests:
        group=next((g for g in groups if (g['namespace'],g['className'])==(ns,cls)),None)
        if group is None:group={'namespace':ns,'className':cls,'tests':[]};groups.append(group)
        group['tests'].append(test)
    native={'schemaVersion':1,'assemblyName':'SharpLink.UnitTests','tunitVersion':'1.73.19.0','runtimeVersion':'.NET 10.0.14',
            'publicationGeneration':'a'*32,'machineName':CANARY,'timestamp':CANARY,'operatingSystem':CANARY,'totalDurationMs':float(total),'groups':groups}
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
    @classmethod
    def setUpClass(cls):
        cls.full=fixture(('error','error','error','passed','error'),total=2879)
    def project(self,data=None,explicit=None):
        ordinary,native,xml,facts=data or self.full
        return h.project(r,native,xml,ordinary,set() if explicit is None else explicit,facts,BASE)
    def reject(self,data,code=None):
        before=copy.deepcopy(data)
        with self.assertRaises(Exception) as c:self.project(data)
        self.assertEqual(data,before)
        if code:self.assertEqual(getattr(c.exception,'code',None),code)
    def test_full_atomic4_preserves_global_failures_and_has_no_raw_text(self):
        before=copy.deepcopy(self.full);out=self.project()
        self.assertEqual(self.full,before);self.assertEqual(len(out['cases']),4);self.assertTrue(out['complete'])
        self.assertFalse(out['allSelectedPassed']);self.assertTrue(out['doesNotQualifyFullSuite']);self.assertEqual(out['fullOrdinaryCaseCount'],2879)
        self.assertEqual(self.full[3]['statusCounts']['failed'],4);self.assertFalse(self.full[3]['allOrdinaryPassed']);self.assertNotIn(CANARY,json.dumps(out))
        self.assertTrue(all(x['argumentIdentity']['kind']=='parameterless' for x in out['cases']))
        self.assertEqual(out['nativeFullReportCanonicalSHA256'],r.digest(self.full[1]))
        self.assertEqual(out['fullResultUIDSetSHA256'],r.digest(sorted(self.full[0])))
        for k in ('executionQualified','bodyExitCode','exit','fullOrdinaryRegressionQualified'):self.assertNotIn(k,out)
        self.assertEqual(out['cases'][3]['exceptions'],[]);self.assertFalse(out['cases'][3]['caughtValidationExceptionsObserved'])
    def test_all_four_pass_does_not_clear_unrelated_failure(self):
        data=fixture(('passed','passed','passed','passed','error'),total=2879);before=copy.deepcopy(data)
        result=self.project(data);self.assertTrue(result['allSelectedPassed']);self.assertEqual(data,before)
        self.assertFalse(data[3]['allOrdinaryPassed']);self.assertEqual(data[3]['bodyExitCode'],2);self.assertEqual(data[3]['statusCounts']['failed'],1)
    def test_junit_normalized_collision_across_namespace_rejects(self):
        for mixed in (False,True):
            ordinary,native,_,_=fixture(('error' if mixed else 'passed','passed','passed','passed'),total=2879)
            row=ordinary['uid-4'];row['displayName']=ordinary['uid-0']['displayName'];row['type']['typeName']=ordinary['uid-0']['type']['typeName']
            old=native['groups'][1];test=old['tests'].pop(0);test['displayName']=row['displayName'];test['className']=row['type']['typeName']
            native['groups'].append({'namespace':'Other','className':row['type']['typeName'],'tests':[test]})
            with self.assertRaises(r.Rejected) as c:self.project(reseal(ordinary,native))
            self.assertEqual(c.exception.code,'HP_JUNIT')
    def test_unknown_or_large_exception_shape_fails_closed(self):
        for ex in ({'type':'System.Exception'}, {'type':CANARY,'message':'x'*(1024*1024+1)},
                   {'type':CANARY,'message':CANARY,'innerException':[]}, {'type':CANARY,'message':CANARY,'innerException':None}):
            with self.assertRaises(r.Rejected):h.projected_exception(r,0,ex)
    def test_no_execution_import_write_or_unrelated_selection_dependency(self):
        text=(ROOT/'eng/runtime-handshake-full-results.py').read_text()
        for token in ('import subprocess','import importlib','write_text(', 'write_bytes(', 'run-unit-', 'unit-census.json','lifetime-full-results','select_fourteen','InterceptedResponseLifetime'):
            self.assertNotIn(token,text)
    def test_source_pins_and_exact_four_census_spans(self):
        h.verify_sources(r,BASE);catalog=r.read_json(BASE/'eng/unit-census.json')
        for i,(source,ns,cls,method,start,end) in enumerate(h.CASES):
            row=next(x for x in catalog['methods'] if (x['source'],x['namespace'],x['className'],x['methodName'])==(source,ns,cls,method))
            self.assertEqual((row['attributeLine'],row['signatureLine'],row['endLine']),(h.ATTRIBUTE_LINES[i],start,end));self.assertEqual(row['parameters'],[]);self.assertEqual(row['expectedCases'],1)
        with patch.object(r,'sha',return_value='0'*64):
            with self.assertRaises(r.Rejected) as c:self.project()
            self.assertEqual(c.exception.code,'HP_SOURCE')
        error=OSError(CANARY)
        with patch.object(r,'sha',side_effect=error):
            with self.assertRaises(OSError) as c:self.project()
            self.assertIs(c.exception,error)
    def test_selection_rejects_missing_duplicate_mutated_identity_and_explicit(self):
        for mode in ('missing','duplicate','namespace','assembly','arity','parameters','return','uid','source','attribute','end','extra-type','explicit'):
            ordinary=fixture()[0];row=ordinary['uid-0']
            if mode=='missing':del ordinary['uid-0']
            elif mode=='duplicate':ordinary['extra']={**copy.deepcopy(row),'uid':'extra'}
            elif mode=='namespace':row['type']['namespace']='Other'
            elif mode=='assembly':row['type']['assemblyFullName']=CANARY
            elif mode=='arity':row['type']['methodArity']=True
            elif mode=='parameters':row['type']['parameterTypeFullNames']=['System.Boolean']
            elif mode=='return':row['type']['returnTypeFullName']='System.Void'
            elif mode=='uid':row['uid']=CANARY
            elif mode=='source':row['location']['file']=CANARY
            elif mode=='attribute':row['location']['lineStart']=12
            elif mode=='end':row['location']['lineEnd']=63
            elif mode=='extra-type':row['type'][CANARY]=CANARY
            with self.assertRaises(r.Rejected):h.select_four(r,ordinary,{'uid-0'} if mode=='explicit' else set())
    def test_selection_uses_metadata_not_uid_order_or_display_guess(self):
        ordinary=fixture()[0];ordinary=dict(reversed(list(ordinary.items())))
        for row in ordinary.values():row['displayName']=CANARY
        selected=h.select_four(r,ordinary,set())
        self.assertEqual([uid for uid,_ in selected],['uid-0','uid-1','uid-2','uid-3'])
        self.assertNotIn(CANARY,json.dumps([h.argument_identity(x) for x in selected]))
    def test_only_full2879_after_complete_global_gate(self):
        self.reject(fixture(),'HP_GATE')
        for key,value in [('ownerClosed',False),('executionClosureObserved',False),('executionClosureObserved',1),('expected',2878),('observed',2878),('missingCount',1),('missingCount',False),('nativeReportSHA256','0'*64),('resultUIDSetSHA256','0'*64),('statusCounts',{}),('retriesObserved',None),('allOrdinaryPassed',None)]:
            data=list(self.full);data[3]={**data[3],key:value}
            with self.assertRaises(r.Rejected) as c:h.full_domains(r,data[1],data[2],data[0],data[3])
            self.assertEqual(c.exception.code,'HP_GATE')
    def test_wrong_runtime_provider_schema_and_post_gate_report_mutation_reject(self):
        for key,value in [('schemaVersion',True),('assemblyName',CANARY),('tunitVersion','1.74.0.0'),('runtimeVersion','.NET 10.0.15')]:
            native={**self.full[1],key:value};facts={**self.full[3],'nativeReportSHA256':r.digest(native)}
            with self.assertRaises(r.Rejected) as c:h.full_domains(r,native,self.full[2],self.full[0],facts)
            self.assertEqual(c.exception.code,'HP_IDENTITY')
        native={**self.full[1],'artifactUrl':CANARY}
        with self.assertRaises(r.Rejected) as c:h.full_domains(r,native,self.full[2],self.full[0],self.full[3])
        self.assertEqual(c.exception.code,'HP_GATE')
    def test_full_native_identity_domain_changes_reject(self):
        for mode in ('duplicate','foreign','missing','display','path','line','end','group'):
            native=copy.deepcopy(self.full[1]);test=alltests(native)[0]
            if mode=='duplicate':native['groups'][0]['tests'].append(copy.deepcopy(test))
            elif mode=='foreign':test['id']=CANARY
            elif mode=='missing':native['groups'][0]['tests'].pop()
            elif mode=='display':test['displayName']=CANARY
            elif mode=='path':test['filePath']=CANARY
            elif mode=='line':test['lineNumber']=True
            elif mode=='end':test['endLineNumber']+=1
            else:native['groups'][0]['namespace']=CANARY
            facts={**self.full[3],'nativeReportSHA256':r.digest(native)}
            with self.assertRaises(r.Rejected) as c:h.full_domains(r,native,self.full[2],self.full[0],facts)
            self.assertEqual(c.exception.code,'HP_IDENTITY')
    def test_full_junit_domain_and_collision_checks(self):
        for mode in ('missing','duplicate','foreign','status','count','doctype','entity','syntax'):
            tree=ET.fromstring(self.full[2]);suite=tree[0];case=suite.findall('testcase')[0]
            if mode=='missing':suite.remove(case)
            elif mode=='duplicate':suite.append(copy.deepcopy(case))
            elif mode=='foreign':case.set('name',CANARY)
            elif mode=='status':case.remove(case[0])
            elif mode=='count':tree.set('tests','4')
            xml=ET.tostring(tree,encoding='unicode')
            if mode=='doctype':xml='<!DOCTYPE testsuites>'+xml
            elif mode=='entity':xml='<!ENTITY x "private">'+xml
            elif mode=='syntax':xml='<private'
            with self.assertRaises(r.Rejected) as c:h.full_domains(r,self.full[1],xml,self.full[0],self.full[3])
            self.assertEqual(c.exception.code,'HP_JUNIT')
    def test_last_selected_junit_error_never_returns_partial_or_mutates_facts(self):
        data=list(self.full);tree=ET.fromstring(data[2]);tree[0].findall('testcase')[3].set(CANARY,CANARY);data[2]=ET.tostring(tree,encoding='unicode')
        self.reject(tuple(data),'HP_JUNIT');self.assertNotIn('runtimeHandshakeDiagnostic',data[3])
    def test_selected_junit_exception_equality(self):
        for mode in ('message','type','attribute','nested'):
            tree=ET.fromstring(self.full[2]);child=tree[0].findall('testcase')[0][0]
            if mode in ('message','type'):child.set(mode,'different')
            elif mode=='attribute':child.set(CANARY,CANARY)
            else:ET.SubElement(child,CANARY)
            with self.assertRaises(r.Rejected) as c:self.project((self.full[0],self.full[1],ET.tostring(tree,encoding='unicode'),self.full[3]))
            self.assertEqual(c.exception.code,'HP_JUNIT')
    def test_all_selected_status_shapes_and_retries(self):
        for status in h.STATUS_KIND:
            test=alltests(fixture((status,)*4)[1])[0];h.selected_test_shape(r,test)
        for mode in ('retry','attempts','missing-error','pass-exception','duration','field','siblings'):
            test=copy.deepcopy(alltests(self.full[1])[0])
            if mode=='retry':test['retryAttempt']=1
            elif mode=='attempts':test['attempts']=[]
            elif mode=='missing-error':del test['exception']
            elif mode=='pass-exception':test['status']='passed'
            elif mode=='duration':test['durationMs']=-1
            elif mode=='field':test[CANARY]=CANARY
            else:test['exception']['innerExceptions']=[]
            with self.assertRaises(r.Rejected):h.selected_test_shape(r,test)
    def test_exact_source_tags_include_mandatory_authority_and_revision(self):
        self.assertEqual(len(h.EXCEPTION_TAGS),558)
        wanted={'mandatory-capabilities-missing','authority-or-binding-limits-invalid','revision-mismatch'}
        self.assertTrue(wanted<={x[3] for x in h.EXCEPTION_TAGS})
        for indexes,typ,msg,tag,path,line in h.EXCEPTION_TAGS:
            text=(BASE/path).read_text().splitlines()[line-1]
            if msg.startswith('round '):self.assertIn('{round}',text)
            elif msg.startswith('invalid case '):self.assertIn('{index}',text)
            elif msg.startswith('Negotiated frame limit '):self.assertIn('{options.MaxFramePayloadBytes}',text)
            else:self.assertIn(msg,text)
            out=h.projected_exception(r,indexes[0],{'type':typ,'message':msg})[0]
            self.assertEqual(out['assertionTag'],tag);self.assertEqual(out['exactMessageSource'],{'source':path,'line':line});self.assertNotIn(msg,json.dumps(out))
            for bad in (msg+CANARY,CANARY+msg):self.assertEqual(h.projected_exception(r,indexes[0],{'type':typ,'message':bad})[0]['assertionTag'],'unknown')
        for i,msg in ((1,'round 100: exactly one concurrent completion must win'),(1,'round -1: exactly one concurrent completion must win'),(3,'invalid case 9: negotiation validation must use ProtocolViolation')):
            self.assertEqual(h.projected_exception(r,i,{'type':'System.Exception','message':msg})[0]['assertionTag'],'unknown')
    def test_exception_type_and_case_scope_are_exact_not_substrings(self):
        for i,msg in ((0,'round 0: exactly one concurrent completion must win'),(2,'invalid case 0: negotiation validation must use ProtocolViolation')):
            self.assertEqual(h.projected_exception(r,i,{'type':'System.Exception','message':msg})[0]['assertionTag'],'unknown')
        tag=next(x for x in h.EXCEPTION_TAGS if x[3]=='mandatory-capabilities-missing')
        out=h.projected_exception(r,0,{'type':tag[1]+CANARY,'message':tag[2]})[0]
        self.assertEqual(out['assertionTag'],'unknown');self.assertEqual(out['category'],'unknown')
    def test_rebound_frames_remove_old_lambdas_and_unused_shared_helper(self):
        path,ns,cls,*_=h.CASES[1]
        for i,line in ((1,84),(1,89),(2,202),(2,207),(3,151)):
            method=h.CASES[i][3]
            frame=f'   at {ns}.{cls}.<>c__DisplayClass1_0.<{method}>b__0() in /_/{path}:line {line}'
            self.assertEqual(h.source_frames(r,i,frame),([],1,0))
        frame=f'   at {ns}.{cls}.CreateOptions(ProtocolV2Capabilities capabilities, Int32 maxFramePayloadBytes) in /_/{path}:line 500'
        for i in (1,2):self.assertEqual(h.source_frames(r,i,frame),([],1,0))
        for value in (-1,9,100):
            msg=f'invalid case {value}: negotiation must reject the intended invalid field'
            self.assertEqual(h.projected_exception(r,3,{'type':'System.Exception','message':msg})[0]['assertionTag'],'unknown')
        for value in range(9):
            msg=f'invalid case {value}: negotiation must reject the intended invalid field'
            self.assertEqual(h.projected_exception(r,3,{'type':'System.Exception','message':msg})[0]['assertionTag'],'invalid-guard-mismatch')

    def test_fixed_frames_and_rethrow_line_are_source_bound(self):
        for indexes,path,typ,member,signature,first,last in h.EXTRA_FRAMES:
            self.assertIn(path,h.SOURCE_PINS);good=f'   at {typ}.{member}({signature}) in /_/{path}:line {last}'
            frames,unknown,omitted=h.source_frames(r,indexes[0],good+'\n'+good+CANARY)
            self.assertEqual(frames,[{'source':path,'member':member,'line':last}]);self.assertEqual((unknown,omitted),(1,0))
        line='   at SharpLink.Runtime.RpcSession.TryCompleteHandshake(NegotiatedSessionOptions options) in /_/src/SharpLink.Runtime/RpcSession.Negotiation.cs:line 28'
        self.assertEqual(h.source_frames(r,0,line)[0][0]['line'],28)
    def test_lambda_frames_bounded_and_never_publish_generated_names(self):
        for i,line,label in ((1,101,'first-completion-lambda'),(1,106,'second-completion-lambda'),(2,259,'completion-lambda'),(2,264,'termination-lambda'),(3,198,'invalid-completion-lambda')):
            path,ns,cls,method,_,_=h.CASES[i]
            good=f'   at {ns}.{cls}.<>c__DisplayClass1_0.<{method}>b__0() in /_/{path}:line {line}'
            bad=[good+CANARY,CANARY+good,good.replace('1_0','1000_0'),good.replace('b__0','b__1000'),good.replace('/_/','/private/'),good.replace(':line '+str(line),':line 450'),good.replace('b__0()','b__0(private)')]
            frames,unknown,_=h.source_frames(r,i,'\n'.join([good]+bad))
            self.assertEqual(frames,[{'source':path,'member':method+'-'+label,'line':line}]);self.assertEqual(unknown,len(bad));self.assertNotIn('DisplayClass',json.dumps(frames));self.assertNotIn(CANARY,json.dumps(frames))
    def test_stack_and_chain_limits_and_unknowns_are_explicit(self):
        path,ns,cls,method,_,_=h.CASES[0];line=f'   at {ns}.{cls}.{method}() in /_/{path}:line 42'
        frames,unknown,omitted=h.source_frames(r,0,'\n'.join([line]*70+[CANARY]));self.assertEqual((len(frames),unknown,omitted),(64,1,6))
        with self.assertRaises(r.Rejected):h.source_frames(r,0,'\n'.join(['x']*4097))
        ex={'type':CANARY,'message':CANARY,'stackTrace':CANARY}
        for _ in range(15):ex={'type':'System.AggregateException','message':CANARY,'innerException':ex}
        rows=h.projected_exception(r,0,ex);self.assertEqual(len(rows),16);self.assertEqual(rows[-1]['category'],'unknown');self.assertNotIn(CANARY,json.dumps(rows))
        with self.assertRaises(r.Rejected):h.projected_exception(r,0,{'type':'System.Exception','message':CANARY,'innerException':ex})
        for key in ('InnerExceptions','innerExceptions','errors','stages'):
            with self.assertRaises(r.Rejected):h.projected_exception(r,0,{'type':'System.Exception','message':CANARY,key:[]})
    def test_xml_writer_normalization(self):
        for value in ('plain','\0x','\u0001','\U0001f642','\ud800','a\nb\tc\rd'):self.assertEqual(h.xml_name(value),e.xml_name(value))
    def test_unknown_selected_payload_never_escapes(self):
        ordinary,native,_,_=fixture(("error","passed","passed","passed"),total=2879);test=alltests(native)[0]
        test.update(output=CANARY,errorOutput=CANARY,categories=[CANARY],customProperties=[{'key':CANARY,'value':CANARY}],sourceRelativePath=CANARY,additionalTraceIds=[CANARY])
        test['exception']={'type':CANARY,'message':CANARY,'stackTrace':CANARY};native['artifactUrl']=CANARY
        out=self.project(reseal(ordinary,native));self.assertNotIn(CANARY,json.dumps(out));self.assertEqual(out['cases'][0]['exceptions'][0]['assertionTag'],'unknown')
    def test_silent_rejection_success_no_caught_control_claim_or_causality(self):
        with contextlib.redirect_stdout(io.StringIO()) as out,contextlib.redirect_stderr(io.StringIO()) as err:
            result=self.project()
            with self.assertRaises(r.Rejected):self.project(fixture())
        self.assertEqual(out.getvalue(),'');self.assertEqual(err.getvalue(),'')
        for case in result['cases']:
            self.assertFalse(case['aggregateSiblingCausesObserved']);self.assertFalse(case['caughtValidationExceptionsObserved']);self.assertEqual(case['internalIterationEvidence'],'not-serialized')
            self.assertEqual(case['bodyVersusCleanup'],'not-applicable' if case['status']=='passed' else 'inconclusive')

if __name__=='__main__':unittest.main()
