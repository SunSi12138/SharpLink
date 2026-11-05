"""Python-only controls for prospective text observations. Never executes .NET."""
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
sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT if (ROOT/'eng/unit-execution-results.py').is_file() else ROOT.parent/'sharplink-protocol70-cap-full-regression-20261105'
if len(sys.argv) == 3 and sys.argv[1] == '--reviewed-base':
    BASE = Path(sys.argv[2]); sys.argv[1:] = []
def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    return module
h = load('stop_junit_projection', ROOT/'eng/stop-junit-results.py')
f = load('stop_junit_fixture_support', BASE/'eng/tests/test_lifetime_full_results.py')
lifetime, e, r = f.h, f.e, f.r
CANARY = 'PRIVATE_STOP_JUNIT_SENTINEL_3f7e29d8'
EXPLICIT = {'explicit-'+str(i) for i in range(5)}
PRODUCTION_ORDINARY_COUNT = h.FULL_ORDINARY_COUNT

def fixture(statuses=('error', 'error'), text=None, count=16):
    ordinary, native, xml, facts = f.fixture(tuple(statuses) + ('passed',)*(count-3) + ('error',))
    return with_text((ordinary, native, xml, facts), text if text is not None else 'System.AggregateException: '+CANARY)

def with_text(data, text):
    ordinary, native, xml, facts = data
    tree = ET.fromstring(xml)
    for case in tree[0].findall('testcase')[:2]:
        if len(case): case[0].text = text
    xml = ET.tostring(tree, encoding='unicode')
    e.classify(native, xml, ordinary, EXPLICIT, facts, [{'framework':'Microsoft.NETCore.App', 'version':'10.0.14'}])
    return ordinary, native, xml, facts

def frame(index=4, line=None):
    span = h.FRAME_SPANS[index]; types, member, signature = h._RULES[index]
    member = member or '<CleanupAsync>g__ObserveStop|20_2'
    return '   at '+types[0]+'.'+member+'('+signature+') in /_/'+span['source']+':line '+str(line or span['start'])

class Controls(unittest.TestCase):
    def setUp(self):
        # Compact unit fixtures exercise the same complete-domain algorithm.
        # A separate unmocked-size full2879 control below covers the real gate.
        replacement = patch.object(h, 'FULL_ORDINARY_COUNT', 16)
        replacement.start(); self.addCleanup(replacement.stop)
    def project(self, data=None, explicit=EXPLICIT):
        ordinary, native, xml, facts = data or fixture()
        return h.project(r, lifetime, native, xml, ordinary, explicit, facts, BASE)
    def reject(self, data, code=None):
        before = copy.deepcopy(data)
        with self.assertRaises(r.Rejected) as caught: self.project(data)
        self.assertEqual(data, before)
        if code: self.assertEqual(caught.exception.code, code)
    def scan(self, text): return h.observe_text(r, text)
    def test_full2879_gate_exact_two_bools_and_no_input_mutations(self):
        self.assertEqual(PRODUCTION_ORDINARY_COUNT, 2879)
        data = fixture(count=2879); before = copy.deepcopy(data)
        with patch.object(h, 'FULL_ORDINARY_COUNT', PRODUCTION_ORDINARY_COUNT):
            out = self.project(data)
        self.assertEqual(data, before); self.assertEqual(len(data[0]), 2879)
        self.assertEqual([x['duplex'] for x in out['cases']], [False, True])
        self.assertTrue(out['completeCaseCoverage']); self.assertTrue(out['doesNotQualifyFullSuite'])
        self.assertFalse(data[3]['allOrdinaryPassed']); self.assertEqual(out['selectedCaseCount'], 2)
        self.assertNotIn(CANARY, json.dumps(out)); self.assertLess(len(json.dumps(out)), h.MAX_OUTPUT_BYTES)
    def test_original_unrelated_failure_and_projection_no_qualification_control(self):
        data = fixture(('passed', 'passed')); facts = copy.deepcopy(data[3]); out = self.project(data)
        self.assertEqual(data[3], facts); self.assertFalse(facts['allOrdinaryPassed'])
        for key in ('bodyExitCode','allSelectedPassed','executionQualified','fullOrdinaryRegressionQualified','exit'):
            self.assertNotIn(key, out)
    def test_pass_and_skip_cancel_timeout_never_relabel_as_exception_text(self):
        for status in ('passed','skipped','cancelled','timedOut'):
            with self.subTest(status=status):
                out = self.project(fixture((status, status)))
                for case in out['cases']:
                    self.assertIsNone(case['textObservation'])
                    self.assertEqual(case['observationAvailability'], 'not-an-exception-error-or-failure')
    def test_failure_and_error_use_distinct_global_status_and_same_text_scope(self):
        out = self.project(fixture(('failed','error')))
        self.assertEqual([x['status'] for x in out['cases']], ['failed','error'])
        self.assertTrue(all(x['textObservation']['evidenceSource']=='junit-exception-text' for x in out['cases']))
    def test_nested_aggregate_text_is_flat_lexical_evidence_only(self):
        text = '\n'.join(['System.AggregateException: '+CANARY,
            ' ---> System.AggregateException: nested', ' ---> System.InvalidOperationException: '+CANARY,
            frame(1,263), '   --- End of inner exception stack trace ---',
            ' ---> (Inner Exception #1) System.TimeoutException: '+CANARY, frame(2,273),
            ' ---> (Inner Exception #2) SharpLink.Runtime.PhysicalTaskFailureException: '+CANARY,
            frame(8,280)])
        out = self.scan(text)
        self.assertEqual([x['category'] for x in out['observations'] if 'category' in x],
                         ['aggregate','aggregate','invalid-operation','timeout','physical-task-failure'])
        self.assertFalse(out['exceptionGraphObserved']); self.assertFalse(out['aggregateSiblingCausesObserved'])
        self.assertFalse(out['caughtStopObjectObserved']); self.assertFalse(out['referenceIdentityObserved'])
        self.assertFalse(out['authenticatedExceptionTypesOrFrames']); self.assertNotIn(CANARY,json.dumps(out))
        self.assertTrue(all(not {'depth','parent','siblingIndex','exceptionID'} & x.keys() for x in out['observations']))
    def test_fake_header_frame_in_message_is_explicitly_unauthenticated(self):
        text = 'System.Exception: innocent\nSystem.TimeoutException: '+CANARY+'\n'+frame(4,205)
        out = self.scan(text)
        self.assertEqual([x['category'] for x in out['observations'] if 'category' in x], ['exception','timeout'])
        self.assertTrue(out['mayOriginateInExceptionMessageOrCustomToString'])
        self.assertEqual(out['evidenceMeaning'],'unauthenticated-lexical-observations-only')
        self.assertFalse(out['authenticatedExceptionTypesOrFrames'])
    def test_inline_mentions_arbitrary_names_paths_and_unknown_types_never_escape(self):
        text = '\n'.join(['System.Exception: mentions System.TimeoutException: '+CANARY,
            'Secret.'+CANARY+'Exception: private', '   at '+CANARY+' in /private/'+CANARY+':line 123',
            'prefix '+frame(), 'System.TimeoutExceptionEvil: '+CANARY])
        out = self.scan(text); self.assertNotIn(CANARY,json.dumps(out))
        self.assertEqual([x['category'] for x in out['observations'] if 'category' in x], ['exception','unknown','unknown'])
        self.assertEqual(out['unknownHeaderShapedLineCount'],2)
    def test_all_finite_types_have_fixed_categories_and_type_bounds(self):
        for typ, category in h.EXCEPTION_TYPES.items():
            self.assertEqual(self.scan(typ+': '+CANARY)['observations'][0]['category'],category)
        self.assertEqual(self.scan('X'*255+': private')['observations'][0]['category'],'unknown')
        self.assertEqual(self.scan('X'*256+': private')['observations'],[])
    def test_every_pinned_span_member_signature_and_boundaries(self):
        for index, span in enumerate(h.FRAME_SPANS):
            for line in (span['start'],span['end']):
                out = self.scan(frame(index,line)); self.assertEqual(len(out['observations']),1)
                observed=out['observations'][0]
                self.assertEqual((observed['tag'],observed['source'],observed['member'],observed['line']),
                    (span['tag'],span['source'],span['member'],line))
            for line in (span['start']-1,span['end']+1):
                self.assertEqual(self.scan(frame(index,line))['observations'],[])
    def test_wrong_path_type_signature_suffix_and_compiler_ordinal_are_unknown(self):
        valid=frame(2,273)
        for text in (valid+' '+CANARY, valid.replace('/_/','/tmp/'),valid.replace('Task operation','Object operation'),
                     valid.replace('ObserveStop','ObserveStopOther'),valid.replace('|20_2','|2000_2'),
                     valid.replace('RecoveryTests','RecoveryTests'+CANARY),valid.replace('Client/','Client/../Client/')):
            self.assertEqual(self.scan(text)['observations'],[])
    def test_crlf_and_empty_text_are_bounded_without_input_completeness_claims(self):
        out=self.scan('System.TimeoutException: '+CANARY+'\r\n'+frame()+'\r\n')
        self.assertEqual(len(out['observations']),2);self.assertFalse(out['scanTruncated'])
        self.assertEqual(self.scan('')['observations'],[])
        self.assertEqual(out['sourceTextCompleteness'],'not-established');self.assertFalse(out['sourceTruncationDetectable'])
        self.assertEqual(self.scan(frame()+'\rINJECTION')['observations'],[])
    def test_scan_character_boundary_does_not_interpret_partial_line(self):
        prefix='x\n' * 10
        text=prefix+'x'*(h.MAX_SCAN_CHARS-len(prefix)-len('System.TimeoutException: '))+'System.TimeoutException: '+CANARY
        out=self.scan(text);self.assertTrue(out['scanTruncated']);self.assertTrue(out['partialBoundaryLineNotInterpreted'])
        self.assertEqual(out['observations'],[])
    def test_line_limit_stops_before_late_text_and_unknown_counts_bounded(self):
        out=self.scan(('unknown line\n'*h.MAX_SCAN_LINES)+'System.TimeoutException: private')
        self.assertEqual(out['scannedLineCount'],h.MAX_SCAN_LINES);self.assertTrue(out['scanTruncated'])
        self.assertEqual(out['unrecognizedScannedLineCount'],h.MAX_SCAN_LINES);self.assertEqual(out['observations'],[])
    def test_overlong_line_not_sliced_into_false_token(self):
        out=self.scan('System.TimeoutException: '+'x'*h.MAX_LINE_CHARS+'\n'+frame())
        self.assertEqual(out['overlongLineCount'],1);self.assertTrue(out['projectionTruncated'])
        self.assertEqual(len(out['observations']),1);self.assertEqual(out['observations'][0]['kind'],'source-frame-shaped-text')
    def test_observation_limit_counts_omissions_and_bounded_final_output(self):
        text='\n'.join([frame(2,273)]*200);out=self.scan(text)
        self.assertEqual(len(out['observations']),64);self.assertEqual(out['omittedRecognizedObservationCount'],136)
        self.assertTrue(out['projectionTruncated']); result=self.project(fixture(text=text))
        self.assertLess(len(json.dumps(result,sort_keys=True,separators=(',',':')).encode()),h.MAX_OUTPUT_BYTES)
    def test_text_hard_character_byte_and_surrogate_limits_reject(self):
        for text in ('x'*(h.MAX_TEXT_CHARS+1),'é'*(h.MAX_TEXT_BYTES//2+1),None,42):
            with self.assertRaises(r.Rejected) as caught:self.scan(text)
            self.assertEqual(caught.exception.code,'SJ_LIMIT')
        with self.assertRaises(r.Rejected) as caught:self.scan('\ud800')
        self.assertEqual(caught.exception.code,'SJ_JUNIT')
    def test_source_pin_and_reader_failures_preserve_errors(self):
        h.verify_sources(r,BASE)
        with patch.object(r,'sha',return_value='0'*64):
            with self.assertRaises(r.Rejected) as caught:self.project()
            self.assertEqual(caught.exception.code,'SJ_SOURCE')
        original=OSError(CANARY)
        with patch.object(r,'sha',side_effect=original):
            with self.assertRaises(OSError) as caught:self.project()
            self.assertIs(caught.exception,original)
    def test_proof_and_reconciliation_dependency_is_exactly_pinned(self):
        self.assertEqual(h.SOURCE_PINS['eng/lifetime-full-results.py'],r.sha(BASE/'eng/lifetime-full-results.py'))
        self.assertEqual(h.SOURCE_PINS['eng/protocol-lifetime-history/argument-display-name-proof.json'],
            '37147528b72191820bfc81686c74f37baf6ac2f62369d6222c58697f81edd82b')
        self.assertEqual(len(h.FRAME_SPANS),14);self.assertFalse(h.REPORTER_PROOF['runtimeTextGrammarPinned'])
    def test_unknown_duplicate_casefold_or_incomplete_literal_sets_fail_closed(self):
        for mode in ('unknown','duplicate','lowercase','suffix'):
            data=fixture();ordinary,native,_,_=data
            value={'unknown':CANARY,'duplicate':ordinary['uid-0']['displayName'],
                   'lowercase':ordinary['uid-1']['displayName'].lower(),'suffix':ordinary['uid-1']['displayName']+CANARY}[mode]
            ordinary['uid-1']['displayName']=value; f.alltests(native)[1]['displayName']=value
            self.reject(f.reseal(ordinary,native),'SJ_IDENTITY')
    def test_uid_order_never_decides_bool_identity(self):
        ordinary,native,_,_=fixture()
        ordinary=dict(reversed(list(ordinary.items())))
        for group in native['groups']:group['tests'].reverse()
        out=self.project(f.reseal(ordinary,native))
        self.assertEqual([x['duplex'] for x in out['cases']],[False,True])
        for case in out['cases']:
            self.assertEqual(case['argumentIdentity']['literalSource']['line'],25 if case['duplex'] else 24)
    def test_missing_case_extra_case_bad_metadata_and_explicit_membership_reject(self):
        for mode in ('missing','extra','span','parameter','assembly','uid'):
            data=fixture();row=data[0]['uid-0']
            if mode=='missing':del data[0]['uid-0']
            elif mode=='extra':data[0]['extra']={**copy.deepcopy(row),'uid':'extra'}
            elif mode=='span':row['location']['lineStart']=26
            elif mode=='parameter':row['type']['parameterTypeFullNames']=['System.String']
            elif mode=='assembly':row['type']['assemblyFullName']=CANARY
            else:row['uid']=CANARY
            self.reject(data)
        with self.assertRaises(r.Rejected):self.project(explicit={'uid-0',*list(EXPLICIT)[:4]})
    def test_full_count_explicit_count_and_complete_global_gate_required(self):
        self.reject(f.fixture(('passed',)*15),'SJ_GATE')
        with self.assertRaises(r.Rejected) as caught:self.project(explicit=set())
        self.assertEqual(caught.exception.code,'SJ_GATE')
        for key,value in [('ownerClosed',False),('executionClosureObserved',False),('observed',2878),
                          ('nativeReportSHA256','0'*64),('resultUIDSetSHA256','0'*64),('statusCounts',{}),('retriesObserved',None)]:
            data=fixture();data[3][key]=value;self.reject(data,'LP_GATE')
    def test_junit_collisions_across_namespaces_and_status_fail_closed(self):
        for different_status in (False,True):
            ordinary,native,_,_=fixture(('error','error') if different_status else ('passed','passed'))
            row=ordinary['uid-14'];target=ordinary['uid-0'];row['displayName']=target['displayName'];row['type']['typeName']=target['type']['typeName']
            old=next(g for g in native['groups'] if any(t['id']=='uid-14' for t in g['tests']))
            test=next(t for t in old['tests'] if t['id']=='uid-14');old['tests'].remove(test)
            test['displayName']=row['displayName'];test['className']=row['type']['typeName']
            native['groups'].append({'namespace':'Other','className':row['type']['typeName'],'tests':[test]})
            self.reject(f.reseal(ordinary,native),'SJ_JUNIT')
    def test_junit_raw_type_message_nested_nodes_and_case_attributes_reject(self):
        for mode in ('type','message','nested','case-attribute'):
            ordinary,native,xml,facts=fixture();tree=ET.fromstring(xml);case=tree[0].findall('testcase')[0]
            if mode in ('type','message'):case[0].set(mode,CANARY)
            elif mode=='nested':ET.SubElement(case[0],CANARY)
            else:case.set(CANARY,CANARY)
            self.reject((ordinary,native,ET.tostring(tree,encoding='unicode'),facts),'SJ_JUNIT')
    def test_native_mismatch_and_selected_retry_cannot_project(self):
        data=fixture();f.alltests(data[1])[0]['displayName']=CANARY;data[3]['nativeReportSHA256']=r.digest(data[1]);self.reject(data,'LP_IDENTITY')
        ordinary,native,_,_=fixture();f.alltests(native)[0]['retryAttempt']=1
        self.reject(f.reseal(ordinary,native),'LP_RETRY')
    def test_selected_overlimit_text_rejects_atomically_without_changing_global_failure(self):
        data=fixture(text='x'*(h.MAX_TEXT_CHARS+1));self.reject(data,'SJ_LIMIT')
        self.assertFalse(data[3]['allOrdinaryPassed']);self.assertNotIn('stopJunitDiagnostic',data[3])
    def test_unknown_native_type_and_message_stay_private(self):
        ordinary,native,_,_=fixture()
        for test in f.alltests(native)[:2]:test['exception']={'type':CANARY,'message':CANARY,'stackTrace':CANARY}
        out=self.project(with_text(f.reseal(ordinary,native),'Secret.'+CANARY+': '+CANARY+'\n'+frame()))
        self.assertNotIn(CANARY,json.dumps(out));self.assertEqual(out['cases'][0]['textObservation']['unknownHeaderShapedLineCount'],1)
    def test_full_junit_missing_row_totals_doctype_or_syntax_reject(self):
        for mode in ('missing','totals','doctype','syntax'):
            ordinary,native,xml,facts=fixture();tree=ET.fromstring(xml)
            if mode=='missing':tree[0].remove(tree[0].findall('testcase')[-1])
            if mode=='totals':tree.set('tests','2')
            xml=ET.tostring(tree,encoding='unicode')
            if mode=='doctype':xml='<!DOCTYPE testsuites>'+xml
            if mode=='syntax':xml='<private'
            self.reject((ordinary,native,xml,facts),'LP_JUNIT')
    def test_wrong_runtime_provider_or_schema_cannot_project(self):
        for key,value in [('runtimeVersion','.NET 10.0.15'),('tunitVersion','1.74.0.0'),('schemaVersion',True)]:
            data=fixture();data[1][key]=value;data[3]['nativeReportSHA256']=r.digest(data[1])
            self.reject(data,'LP_IDENTITY')
    def test_upstream_partial_text_and_missing_aggregate_markers_never_claim_completeness(self):
        for text in ('System.AggregateException: partial',' ---> (Inner Exception #1) System.TimeoutException: partial',
                     'System.Exception: '+CANARY+'\n   at partial'):
            out=self.scan(text)
            self.assertFalse(out['projectionTruncated'])
            self.assertEqual(out['sourceTextCompleteness'],'not-established')
            self.assertFalse(out['sourceTruncationDetectable']);self.assertFalse(out['exceptionGraphObserved'])
    def test_silent_success_rejection_and_no_process_write_or_dynamic_import(self):
        with contextlib.redirect_stdout(io.StringIO()) as out,contextlib.redirect_stderr(io.StringIO()) as err:
            self.project();data=fixture();data[3]['ownerClosed']=False
            with self.assertRaises(r.Rejected):self.project(data)
        self.assertEqual(out.getvalue(),'');self.assertEqual(err.getvalue(),'')
        code=(ROOT/'eng/stop-junit-results.py').read_text()
        for bad in ('import subprocess','import importlib','write_text(', 'write_bytes(', 'os.system(', 'print('):self.assertNotIn(bad,code)

if __name__ == '__main__': unittest.main()
