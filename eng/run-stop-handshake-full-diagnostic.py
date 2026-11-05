#!/usr/bin/env python3
"""Unchanged full ordinary Release run with post-execution Stop/handshake evidence only."""
from collections import Counter
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import time

sys.dont_write_bytecode=True
ROOT=Path(__file__).resolve().parents[1]
SOURCE_TREES = {'src': 'c3a94c896418a5d5ec62646461f17c67dc89bc96',
                'test': '47527763f1de7aa592f94120a346d843456d6cf1'}
RUNTIMES = [{'framework': 'Microsoft.AspNetCore.App', 'version': '10.0.14'},
            {'framework': 'Microsoft.NETCore.App', 'version': '10.0.14'}]
CODES = frozenset(('FR_HELPERS', 'FR_SOURCE', 'FR_SDK', 'FR_RUNTIME', 'FR_PROJECTION', 'FR_CANDIDATE')) | frozenset(('TP_SOURCE', 'TP_GATE', 'TP_SELECTION', 'TP_IDENTITY', 'TP_SHAPE', 'TP_LIMIT', 'TP_EXCEPTION', 'TP_RETRY', 'TP_JUNIT')) | frozenset(('LP_SOURCE', 'LP_GATE', 'LP_SELECTION', 'LP_IDENTITY', 'LP_SHAPE', 'LP_LIMIT', 'LP_EXCEPTION', 'LP_RETRY', 'LP_JUNIT')) | frozenset(('FR_PARENT', 'HP_EXCEPTION', 'HP_GATE', 'HP_IDENTITY', 'HP_JUNIT', 'HP_LIMIT', 'HP_RETRY', 'HP_SELECTION', 'HP_SHAPE', 'HP_SOURCE', 'SJ_GATE', 'SJ_IDENTITY', 'SJ_JUNIT', 'SJ_LIMIT', 'SJ_SOURCE'))
HELPERS = {'.github/workflows/stop-handshake-full-diagnostic.yml': 'b7a0a9fa120825d0cb3df9bbfba49ee5e325ff94812fd284ec9a0f54f4d69e30',
 'eng/fixture-migration-baseline/parser-handshake-diagnostic-result.json': 'f2f09025a1cc611049c32ec83ad6be2ef2cd8a6351efb9191588a02d761424e1',
 'eng/fixture-migration-baseline/src/SharpLink.Runtime/OwnedFrame.cs.txt': '71948e8baa3494c086d368454460f0422bc635095f91234ea859a3b5eb4bf3d5',
 'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Protocol/ProtocolV2ErrorCompatibilityTests.cs.txt': 'c2ab7b602ecc465b366775b8565df52b37a21b159259e188b0f7b0fdcb43f7c8',
 'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs.txt': 'b6f8d1b1130a10ee446e63a00b525f37e81a74c1846371b86363225e4a1cf3c8',
 'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs.txt': 'd2f827e0f45adae15e78c9db4d9c468de1d247c34eb60ed5e01296b59b584e73',
 'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Protocol/ResponseCompressionPreferenceProtocolTests.cs.txt': 'f903e42a339e9e07e0dab9cf4d496de1e0266017cd800362a800666a08172970',
 'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Runtime/SendPumpProgressIsolationTests.cs.txt': '67ffae89508dc0aba7f25f968bec4eb6fa91f997c9acc32784e9defc96a536a9',
 'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Runtime/Stack13FrameClearTests.cs.txt': '3b4d5efdf3aba44ee1abc34199b517df615c768ec9721d5bc6ddc3c1b7076a3e',
 'eng/fixture-migration-baseline/unit-census.json': '2c34614a0bc918c48b860e795f3f331d0243065f05f0c9530e6aa0d430faeb66',
 'eng/full-regression-history/original-ordinary-result.json': 'dae32c0724ad78dca30234d7d109e45e56566efe900aa854b6461edb4a50f214',
 'eng/full-regression-history/twenty-three-case-result.json': '363a314514e08f19abee140e4ea9c2e692e9836aaeebf02d317036b012fc07af',
 'eng/lifetime-full-results.py': '754ff4e17ca43b1fc2984d0617275e9bce7a3cef8051a2aab387290b00bbea84',
 'eng/package-smoke-process.py': 'ba9f6ae43784ac752acdbfee1af60f33361ba76398a558948da721994449dda3',
 'eng/protocol-lifetime-history/argument-display-name-proof.json': '37147528b72191820bfc81686c74f37baf6ac2f62369d6222c58697f81edd82b',
 'eng/protocol70-cap-full-history/seventy-case-success.json': '37c91f17b81d2e0d9aaadcbdde1daa48536f8da7b6051bd38fd88131c49703d4',
 'eng/protocol70-history/ProtocolV2Tests.cs.txt': '00745a416645a7c5e83a934e47900f75a2c656ab6b072ac9029e8aa3b8aeeb85',
 'eng/protocol70-history/ResponseCompressionPreferenceProtocolTests.cs.txt': 'f903e42a339e9e07e0dab9cf4d496de1e0266017cd800362a800666a08172970',
 'eng/protocol70-history/command-cap-failure-result.json': '2854e98a54900ea77fc7f4c889d4cfdf79029c97a710cfbc95a28cbc9e40a0cb',
 'eng/protocol70-history/full-ordinary-result.json': '073a199d62f9add8222789e3f4fec936377e64e2897cd5145497f046d0c3c8d6',
 'eng/protocol70-history/parent-unit-census.json': '3d05292b01e9ec133f10fad420593895e122469eb82528d6190ac2e9a8dcde12',
 'eng/protocol70-history/twenty-one-case-diagnostic-result.json': 'e6916265942d76dd66761a46d979014603c6f51035ec085687bb7b3af43fb5d4',
 'eng/pure-negotiation-history/diagnostic-negotiator-preimage.cs.txt': 'd40c522f8bc8805860c9a29288109221545c69c0d598ef5ce6c206efe13e6f57',
 'eng/pure-negotiation-history/full-ordinary-result.json': '8b5c5a42210d118055655b75e456cf65f114b9e88fb63debbd8b122c55997e56',
 'eng/pure-negotiation-history/sixteen-case-diagnostic-result.json': '57700796654908f5d3abb3064c04c746384253bad0668f5b4e9dab387373a55a',
 'eng/rebase-three-fixture-census.py': '4a7f520ae1e6a23c3ee30533b6dee75202e4c71c551c3ecde4529015a2416b86',
 'eng/retirement-results.py': '6774c8bdd8bd8b3f618c01aee34e429d8a3b8218cdb169e752cd81ab6a9e79eb',
 'eng/route59-results.py': 'eb95aaf6a6799b0c216ee3687d71e5e1aeb5651867d515e3a4db16561bfcda8b',
 'eng/run-protocol70-cap-full-regression.py': '5ec508b706ea8d1e51782ad58072dd3367e6b9b42c1068da267b96abae2c027b',
 'eng/run-unit-census.py': 'a445781ba97a6c6a963faa9ba4d7e0bb890a96df6395e482ddb3948f8d12e4bf',
 'eng/run-unit-execution.py': 'ede7580fa1a9e37fba16322ef0158a9db8905bc6983c217c8bdad6eb5b784acf',
 'eng/runtime-handshake-full-results.py': '37c909bae20156d028456c26a1059701aa91022e2dd385196d2167520706b2ba',
 'eng/stop-handshake-full-history/parent-full-result.json': 'edb49ee8dd2ac847f9bf6b6fe001efc101068961b66c920afe7b2f4c1d3b3565',
 'eng/stop-junit-results.py': '1f1f9d76f7312ab56e87d7b77297bb9293076012cfd5744614789e83feac2fe7',
 'eng/stream-negotiation-history/boolean-display-name-proof.json': '1917416924ccc1f506b7922aff4872d8b7bac20bfa2147da12347129c4cfed6e',
 'eng/stream-negotiation-history/current-ordinary-result.json': 'b53cffd3018fdcd0cced7ea119d3a1268d8d96363f3645343bb14566953285fc',
 'eng/tests/test_lifetime_full_results.py': '7131cfb5708a3e9ba0352a8d3d4713f2265d828af295532327390b635160954e',
 'eng/tests/test_protocol70_cap_full_regression.py': '3d02b56fc09bbe21292b10f5478039f1e327bb3890ce9ec4ebaf323cc6821224',
 'eng/tests/test_runtime_handshake_full_results.py': '5d29640c60d248ab0574bc0c7bb9a71ad487f38b45183f6a0fe4b42707221876',
 'eng/tests/test_stop_handshake_full_diagnostic.py': '6d112d064a6703d1c2ee880176f02015ec9f632c016d005c44237e950b122efd',
 'eng/tests/test_stop_junit_results.py': '55c2601019e7b13d6cd3cb3e820d7ca51f0a61b3e0ab419067f6d180cfafd02a',
 'eng/tests/test_three_fixture_census.py': '22052ba5191443201727f48dd69b7d744447537864f10e0a06b527f543682e47',
 'eng/tests/test_timed_stream_full_results.py': 'eee2e402c75a89ba1157c1d13284064fb3d2394cc344dfdf567a57b4dacf8ca1',
 'eng/tests/test_unit_census.py': 'd12fbbc8e3634fac209aac866eab101d2938d4531ea27e04b9732fb1f7922da5',
 'eng/tests/test_unit_execution.py': 'c9fbae133b363f6ee0150036adfaba80528a3371ba73a5d6140423399672fa19',
 'eng/three-fixture-census-rebase.json': '76760142bcd923121ebe7cbce1a04304d18d8b58ec064543660494a2cec6a0a1',
 'eng/timed-stream-full-results.py': '1b1c0c9d56d240dc981d0e24ceedbebc1a13c1d138e6203bd9d67fdd8ad674ef',
 'eng/unit-census-results.py': 'cbf6ce2096576dfe027b8e196144e9cba57860ad99be48c1f89d7ba73a71414e',
 'eng/unit-census.json': 'f2164dcc06d4ca0d5aeb91a5e19cbf3d67e25321d9346948d7ccaf10d19f632c',
 'eng/unit-execution-results.py': '5e0bc05cd7c5eaf7ba84a98ecf40cc5e8c7fd02c71c64cbb7911b8cbf8ac3528'}
CANDIDATE_SUCCESS_PATH = 'eng/protocol70-cap-full-history/seventy-case-success.json'
# Exact immutable independently audited70-case success; never a full-suite qualification.
CANDIDATE_SUCCESS_SHA256 = '37c91f17b81d2e0d9aaadcbdde1daa48536f8da7b6051bd38fd88131c49703d4'
CANDIDATE_JOB_ID = 127125515372
CANDIDATE_HEAD = '8903e45945ad6ccea525de329c624d6dd0b6f14a'
CANDIDATE_TREE = '237c680bf4c0b6f75ff85466ff4832c7826a52a6'

PARENT_HEAD = '5b5c776c65d3ea320920a00e5dba91372678e55e'
PARENT_TREE = '5c439bd98a0df87b333ace04cd77ad2df2ff2eea'
PARENT_RESULT_PATH = 'eng/stop-handshake-full-history/parent-full-result.json'
PARENT_RESULT_SHA256 = 'edb49ee8dd2ac847f9bf6b6fe001efc101068961b66c920afe7b2f4c1d3b3565'


def verify_parent_history(r):
    # A failed full result is immutable historical evidence, never pass authority.
    path=ROOT/PARENT_RESULT_PATH
    r.require(not path.is_symlink() and r.sha(path)==PARENT_RESULT_SHA256 and
              HELPERS.get(PARENT_RESULT_PATH)==PARENT_RESULT_SHA256,'FR_PARENT')
    value=r.read_json(path);summary=value.get('summary',{});execution=summary.get('execution',{})
    r.require(value.get('runId')==41997112845 and value.get('jobId')==127142884454 and
              value.get('head')==PARENT_HEAD and value.get('tree')==PARENT_TREE and
              value.get('conclusion')=='failure' and value.get('rawReportsRetainedInRepository') is False and
              value.get('artifacts')=={'artifacts':[]},'FR_PARENT')
    r.require(summary.get('scope')=='protocol70-candidate-linux-release-full-ordinary-regression' and
              summary.get('sourceTrees')==SOURCE_TREES and
              summary.get('candidateCatalogSHA256')==HELPERS['eng/unit-census.json'] and
              summary.get('identity',{}).get('head')==PARENT_HEAD and
              summary.get('identity',{}).get('tree')==PARENT_TREE and
              summary.get('fullOrdinaryRegressionQualified') is False and
              summary.get('executionQualified') is False and summary.get('exit')==1 and
              summary.get('preparationQualifiedAtHandoff') is True and summary.get('evidenceDurable') is True and
              summary.get('maximumParallelTestsOption')==8 and summary.get('expectedExplicitMetadataCount')==5 and
              summary.get('rejections',{}).get('codes')==['UE_RESULTS'],'FR_PARENT')
    r.require(execution.get('observed')==2879 and execution.get('expected')==2879 and
              execution.get('ownerClosed') is True and execution.get('executionClosureObserved') is True and
              execution.get('bodyExitCode')==2 and execution.get('missingCount')==0 and
              execution.get('statusCounts')=={'cancelled':0,'failed':261,'flaky':0,'passed':2618,'skipped':0,'timedOut':0,'total':2879} and
              execution.get('allOrdinaryPassed') is False and execution.get('retriesObserved') is False,'FR_PARENT')
    for key,count,passed in (('timedStreamDiagnostic',4,True),('lifetimeDiagnostic',14,False)):
        projection=execution.get(key,{})
        r.require(projection.get('complete') is True and projection.get('allSelectedPassed') is passed and
                  len(projection.get('cases',[]))==count and projection.get('doesNotQualifyFullSuite') is True,'FR_PARENT')
    stages=value.get('successfulStageRows')
    r.require(type(stages) is list and len(stages)==33 and len({row.get('stage') for row in stages})==33 and
              all(row.get('exit')==0 and row.get('owned')=='joined' for row in stages) and
              stages[-1].get('stage')=='protocol70-cap-full-python-controls','FR_PARENT')
    return value


def project_stop_junit(results,native,junit,ordinary,explicit,facts,source_root):
    r=results.census
    for name in ('eng/stop-junit-results.py','eng/lifetime-full-results.py'):
        path=ROOT/name
        r.require(not path.is_symlink() and r.sha(path)==HELPERS[name],'FR_HELPERS')
    helper=load('current_full_stop_junit_projection',ROOT/'eng/stop-junit-results.py')
    lifetime=load('current_full_stop_lifetime_dependency',ROOT/'eng/lifetime-full-results.py')
    return helper.project(r,lifetime,native,junit,ordinary,explicit,facts,source_root)


def project_runtime_handshake(results,native,junit,ordinary,explicit,facts,source_root):
    path=ROOT/'eng/runtime-handshake-full-results.py'
    results.census.require(not path.is_symlink() and results.census.sha(path)==HELPERS['eng/runtime-handshake-full-results.py'],'FR_HELPERS')
    helper=load('current_full_runtime_handshake_projection',path)
    return helper.project(results.census,native,junit,ordinary,explicit,facts,source_root)


def verify_candidate_success(r):
    r.require(type(CANDIDATE_SUCCESS_SHA256) is str and len(CANDIDATE_SUCCESS_SHA256)==64 and
              type(CANDIDATE_JOB_ID) is int, 'FR_CANDIDATE')
    path=ROOT/CANDIDATE_SUCCESS_PATH
    r.require(not path.is_symlink() and r.sha(path)==CANDIDATE_SUCCESS_SHA256 and
              HELPERS.get(CANDIDATE_SUCCESS_PATH)==CANDIDATE_SUCCESS_SHA256, 'FR_CANDIDATE')
    result=r.read_json(path);summary=result.get('summary',{})
    r.require(result.get('runId')==41991798281 and result.get('jobId')==CANDIDATE_JOB_ID and
              result.get('head')==CANDIDATE_HEAD and result.get('tree')==CANDIDATE_TREE and
              result.get('conclusion')=='success', 'FR_CANDIDATE')
    r.require(summary.get('scope')=='seventy-ordinary-protocol-fixture-cap-repair-validation' and
              all(summary.get(key) is True for key in ('candidateValidationComplete','candidateValidationQualified',
                  'protocolSliceComplete','protocolSliceQualified','infrastructureQualified','allSelectedPassed',
                  'evidenceDurable','preparationQualifiedAtHandoff')) and
              summary.get('fullSuiteQualified') is False and summary.get('errorOccurrences')==0 and
              summary.get('observedNonpassingCaseCount')==0 and summary.get('exit')==0 and
              summary.get('requiredSelectedCaseCount')==70 and summary.get('requiredSdk')=='10.0.114' and
              summary.get('requiredRuntime')=='10.0.14' and summary.get('plannedCommandCount')==103 and
              summary.get('expectedOrdinaryMetadataCount')==2879 and summary.get('expectedExplicitMetadataCount')==5 and
              summary.get('candidateCatalogSHA256')==HELPERS['eng/unit-census.json'] and
              summary.get('sourceTrees')==SOURCE_TREES, 'FR_CANDIDATE')
    catalog=r.read_json(ROOT/'eng/unit-census.json')
    methods=sorted((m for m in catalog['methods'] if m['namespace']=='SharpLink.UnitTests.Protocol'),
                   key=lambda m:(m['source'],m['className'],m['methodName']))
    r.require(len(methods)==70 and all(m['expectedCases']==1 and m['parameters']==[] and
              m['initialEligibility']=='ordinary' and 'Release' in m['configurations'] for m in methods), 'FR_CANDIDATE')
    cases=summary.get('cases')
    r.require(type(cases) is list and len(cases)==70, 'FR_CANDIDATE')
    seen=set()
    for index,(row,method) in enumerate(zip(cases,methods)):
        r.require(row.get('case')==index+1 and row.get('source')==method['source'] and
                  row.get('method')==method['namespace']+'.'+method['className']+'.'+method['methodName'] and
                  row.get('selectedPassed') is True and row.get('status')=='passed' and
                  row.get('ownerClosed') is True and row.get('bodyExitCode')==0 and row.get('exceptions')==[], 'FR_CANDIDATE')
        identity=row.get('argumentIdentity',{});uid=identity.get('caseID')
        r.require(type(uid) is str and len(uid)==64 and all(ch in '0123456789abcdef' for ch in uid) and uid not in seen and
                  identity=={'caseID':uid,'kind':'parameterless','parameter':None,'value':None,'limitation':'none'}, 'FR_CANDIDATE')
        seen.add(uid)
    stages=result.get('successfulStageRows')
    r.require(type(stages) is list and len(stages)==103 and all(row.get('exit')==0 and row.get('owned')=='joined' for row in stages) and
              len({row.get('stage') for row in stages})==103 and
              [row.get('stage') for row in stages[32:]]==['protocol70-python-controls']+['protocol70-case-'+str(i) for i in range(1,71)], 'FR_CANDIDATE')
    return result


def project_timed_stream(results,native,junit,ordinary,explicit,facts,source_root):
    path=ROOT/'eng/timed-stream-full-results.py'
    results.census.require(not path.is_symlink() and results.census.sha(path)==HELPERS['eng/timed-stream-full-results.py'], 'FR_HELPERS')
    helper=load('current_full_timed_projection',path)
    return helper.project(results.census,native,junit,ordinary,explicit,facts,source_root)


def project_lifetime(results,native,junit,ordinary,explicit,facts,source_root):
    path=ROOT/'eng/lifetime-full-results.py'
    results.census.require(not path.is_symlink() and results.census.sha(path)==HELPERS['eng/lifetime-full-results.py'], 'FR_HELPERS')
    helper=load('current_full_lifetime_projection',path)
    return helper.project(results.census,native,junit,ordinary,explicit,facts,source_root)


# Top-level exact type vocabulary only; no exception text or stack is projected.
EXCEPTION_CATEGORIES = {
    'SharpLink.Runtime.SharpLinkProtocolViolationException': 'protocol-violation',
    'System.ArgumentException': 'argument',
    'System.ArgumentNullException': 'argument-null',
    'System.ArgumentOutOfRangeException': 'argument-range',
    'System.InvalidOperationException': 'invalid-operation',
    'TUnit.Assertions.Exceptions.AssertionException': 'assertion',
    'System.Exception': 'exception',
    'System.AggregateException': 'aggregate',
}


def verify_helpers():
    for name, expected in HELPERS.items():
        path = ROOT / name
        if path.is_symlink() or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
            raise RuntimeError('FR_HELPERS')


def verify_source(r, root):
    catalog = r.read_json(root / 'eng/unit-census.json')
    r.require(r.sha(root / 'eng/unit-census.json') == HELPERS['eng/unit-census.json'], 'FR_SOURCE')
    for name, expected in catalog['sourcePins'].items():
        r.require(r.sha(root / name) == expected, 'FR_SOURCE')


def verify_isolated_sdk(lane, census):
    r = lane.r
    dotnet = Path(census.shutil.which('dotnet', path=lane.env['PATH']) or '').resolve()
    root = dotnet.parent; temporary = os.environ.get('RUNNER_TEMP')
    r.require(type(temporary) is str and Path(temporary).is_absolute() and
              root == Path(temporary) / 'stop-handshake-full-dotnet' and dotnet.name == 'dotnet' and dotnet.is_file(), 'FR_SDK')
    r.legacy.directory_identity_strict(root)
    for name in ('DOTNET_ROOT', 'DOTNET_ROOT_X64'):
        if name in lane.env: r.require(Path(lane.env[name]) == root, 'FR_SDK')
    r.require(lane.env.get('DOTNET_MULTILEVEL_LOOKUP') == '0', 'FR_SDK')


def lane_type(census):
    class FullRegressionLane(census.Lane):
        def run(self, label, argv, cwd=None, seconds=20):
            output = super().run(label, argv, cwd, seconds)
            if label == 'clone-head':
                for name, expected in SOURCE_TREES.items():
                    self.r.require(self.git('full-regression-' + name + '-tree', 'rev-parse', 'HEAD:' + name, cwd=self.source) == expected, 'FR_SOURCE')
                verify_source(self.r, self.source)
            if label == 'sdk-version':
                self.r.require(self.r.stable_read(output)[0].decode().strip() == '10.0.114', 'FR_SDK')
            if label == 'sdk-runtimes':
                dotnet = Path(argv[0]); text = self.r.stable_read(output)[0].decode()
                expected = {f"{row['framework']} {row['version']} [{dotnet.parent / 'shared' / row['framework']}]" for row in RUNTIMES}
                self.r.require(set(text.splitlines()) == expected and len(text.splitlines()) == 2, 'FR_RUNTIME')
            return output
    return FullRegressionLane


def project_exception_categories(results, native, ordinary, facts):
    # Called only after complete native/JUnit identity and status reconciliation.
    r = results.census
    r.require(facts.get('executionClosureObserved') is True and facts.get('observed') == len(ordinary), 'FR_PROJECTION')
    categories = Counter(); methods = Counter(); seen = set()
    for group in native['groups']:
        for test in group['tests']:
            uid = test['id']; r.require(uid in ordinary and uid not in seen, 'FR_PROJECTION'); seen.add(uid)
            if test['status'] == 'passed': continue
            r.require(test['status'] in results.STATUS, 'FR_PROJECTION')
            exception = test.get('exception'); typ = exception.get('type') if type(exception) is dict else None
            category = EXCEPTION_CATEGORIES.get(typ, 'unknown') if type(typ) is str else 'unknown'
            row = ordinary[uid]; meta = row['type']; loc = row['location']
            categories[category] += 1
            methods[(loc['file'][3:], meta['namespace'], meta['typeName'], meta['methodName'], test['status'], category)] += 1
    r.require(seen == set(ordinary), 'FR_PROJECTION')
    facts['exceptionCategoryCounts'] = [{'category': category, 'count': count} for category, count in sorted(categories.items())]
    facts['methodExceptionCategoryCounts'] = [dict(zip(('source', 'namespace', 'className', 'methodName', 'status', 'category'), key), count=count)
                                            for key, count in sorted(methods.items())]


# Strong process-lifetime root, including when every durable write fails.
ACTIVE_RECORD={'lane':None,'work':None,'errors':[]}


def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path);obj=importlib.util.module_from_spec(spec);spec.loader.exec_module(obj);return obj


def bootstrap_persist(work,errors):
    # Only used before the reviewed census module is available. Do not format
    # exceptions: retain graph, original primitive args and exact traceback.
    if work is None:return False
    for attempt in range(2):
        try:
            nodes=[];refs={};queue=[]
            def ref(error):
                if id(error) not in refs:refs[id(error)]=len(nodes);nodes.append(None);queue.append(error)
                return refs[id(error)]
            roots=[ref(error) for error in errors];index=0
            while index<len(queue):
                error=queue[index];index+=1;frames=[];tb=error.__traceback__
                while tb is not None:
                    frames.append({'file':tb.tb_frame.f_code.co_filename,'function':tb.tb_frame.f_code.co_name,'line':tb.tb_lineno,'instruction':tb.tb_lasti});tb=tb.tb_next
                node={'type':type(error).__module__+'.'+type(error).__qualname__,'args':[v if type(v) in (str,int,float,bool,type(None)) else {'unrenderedType':type(v).__qualname__} for v in error.args],
                    'traceback':frames,'cause':ref(error.__cause__) if error.__cause__ is not None else None,
                    'context':ref(error.__context__) if error.__context__ is not None else None,'suppressContext':error.__suppress_context__}
                if isinstance(error,BaseExceptionGroup):node['members']=[ref(e) for e in error.exceptions]
                if hasattr(error,'causes'):node['customCauses']=[ref(e) for e in error.causes]
                if hasattr(error,'outcome'):node['originalOwnerReceipt']=error.outcome
                nodes[refs[id(error)]]=node
            data=json.dumps({'orderedRootOccurrences':roots,'nodes':nodes},ensure_ascii=True,allow_nan=False).encode()
            directory=os.open(work,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)
            try:
                fd=os.open('bootstrap-'+str(attempt)+'.json',os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600,dir_fd=directory)
                with os.fdopen(fd,'wb') as stream:stream.write(data);stream.flush();os.fsync(stream.fileno())
                os.fsync(directory)
            finally:os.close(directory)
            return True
        except BaseException as diagnostic:errors.append(diagnostic)
    return False


def prepare_sdk_metadata(lane,sdkroot):
    # Fresh pinned SDKs create this empty directory during Build. Prepare only
    # that exact absent directory, then seal it independently before any SDK
    # command. Neither workload markers nor arbitrary preexisting content pass.
    r=lane.r;sdkroot=Path(sdkroot)
    parent_identity=r.legacy.directory_identity_strict(sdkroot)
    metadata=sdkroot/'metadata'
    r.require(not os.path.lexists(metadata),'UE_SDK_METADATA')
    metadata.mkdir(mode=0o755)
    r.legacy.verify_directory(sdkroot,parent_identity)
    identity=r.legacy.directory_identity_strict(metadata)
    r.legacy.verify_directory(metadata,identity,empty=True)
    lane.trees[metadata]=r.snapshot(metadata);lane.seal_domains[metadata]=('sdk',None)
    return metadata


def prepare(lane,census,results):
    r=lane.r
    verify_isolated_sdk(lane,census)
    verify_helpers()
    verify_candidate_success(r)
    verify_parent_history(r)
    lane.parent_history_verified=True
    lane.candidate_success_verified=True
    r.require(r.sha(ROOT/'eng/unit-census.json')==results.CATALOG_SHA256,'UE_CATALOG')
    dotnet=Path(census.shutil.which('dotnet',path=lane.env['PATH']) or '').resolve()
    r.require(dotnet.is_file(),'UC_SDK_MISSING')
    prepare_sdk_metadata(lane,dotnet.parent)
    census.execute(lane)
    lane.check()
    first=r.read_json(lane.evidence/'ledger1'/'ledger.json')
    second=r.read_json(lane.evidence/'ledger2'/'ledger.json')
    ordinary,explicit=results.expected_ordinary(first,second)
    lane.check()
    r.require(lane.stable and lane.unfiltered and lane.durable and not lane.errors and time.monotonic()<lane.stop,'UE_PREPARATION')
    r.require(lane.sequence==32,'UE_COMMANDS')
    r.require(lane.identity['sdk']=='10.0.114' and sorted(lane.identity['installedRuntimes'],key=lambda row:row['framework'])==RUNTIMES,'FR_RUNTIME')
    certificate={'identity':lane.identity,'metadataQualified':True,'ordinaryCount':len(ordinary),'explicitCount':len(explicit),
        'canonicalLedgerSHA256':r.digest(first['actual']),'preparationDeadlineMonotonic':lane.stop}
    r.write_json(lane.evidence,'preparation.json',certificate);lane.extend_evidence(['preparation.json'])
    lane.check()
    r.require(time.monotonic()<lane.stop,'UE_PREPARATION')
    return ordinary,explicit


def run_body(lane,results,ordinary,explicit,overall_stop):
    r=lane.r;lane.execution_facts={};lane.body_passed=False;lane.report=None
    # Only reached after the complete original deadline-bound preparation gate.
    lane.check()
    now=time.monotonic()
    r.require(now<lane.stop and now<overall_stop,'UE_BUDGET')
    verify_helpers();verify_source(r,lane.source)
    lane.check()
    now=time.monotonic()
    r.require(now<lane.stop and now<overall_stop,'UE_BUDGET')
    lane.preparation_complete=True
    lane.stop=min(overall_stop,now+3000)
    # Deterministic source-tree controls use the sealed original unbuilt checkout.
    lane.run('stop-handshake-full-python-controls',[sys.executable,'-B',ROOT/'eng/tests/test_stop_handshake_full_diagnostic.py'],ROOT,60)
    r.require(lane.sequence==33,'UE_COMMANDS')
    report=lane.work/'execution-results';report.mkdir(mode=0o700);lane.report=report
    report_identity=r.legacy.directory_identity_strict(report);r.legacy.verify_directory(report,report_identity,empty=True)
    dotnet=next(p for p in lane.inputs if p.name=='dotnet' and p.parent in lane.trees)
    dll=lane.source/'test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll'
    r.require(r.sha(dll)==lane.identity['binarySHA256'],'UC_INPUT_CHANGED')
    lane.env['TUNIT_ENABLE_JUNIT_REPORTER']='1'
    argv=results.body_argv(dotnet,dll,report);results.verify_body_inputs(argv,dotnet,dll,report,lane.env)
    lane.validation_phase='ordinary-execution'
    try:lane.run('ordinary-execution',argv,lane.source,1800)
    except BaseException as error:lane.record(error)
    # Owner failures do not suppress report sealing or independent input checks.
    closed=results.owner_closed(lane.commands.get('ordinary-execution'),'ordinary-execution',argv)
    lane.execution_facts['ownerClosed']=closed
    if not closed:
        lane.record(r.Rejected('UE_OWNER'))
        # Unknown surviving writers cannot establish a closed result domain.
        return
    exit_code=lane.commands['ordinary-execution']['receipt']['exitCode']
    r.require(type(exit_code) is int and -255<=exit_code<=255,'UE_OWNER')
    lane.execution_facts['bodyExitCode']=exit_code
    sealed=False
    try:
        snapshot=results.report_snapshot(report,report_identity)
        lane.seals[report]=snapshot;lane.seal_domains[report]=('unknown',None);sealed=True
    except BaseException as error:lane.record(error)
    try:lane.check()
    except BaseException as error:lane.record(error)
    if sealed:
        try:
            lane.validation_phase='ordinary-results'
            r.require(results.PARENT_REPORTS <= set(snapshot),'UE_REPORT_MEMBERS')
            native=r.read_json(report/'session.tunit-report.json')
            junit=r.stable_read(report/'junit.xml')[0].decode('utf-8-sig')
            lane.check()
            lane.body_passed=results.classify(native,junit,ordinary,explicit,lane.execution_facts,lane.identity['installedRuntimes'])
            # Retain actual full-suite failure before independent diagnostics or their persistence.
            if not lane.body_passed:lane.record(r.Rejected('UE_RESULTS'))
            # Each pure projection is independently attempted only after complete classification.
            # Failures never erase the global outcome or another completed projection.
            try:
                project_exception_categories(results,native,ordinary,lane.execution_facts)
            except BaseException as error:
                lane.record(error)
            for key,project in (('timedStreamDiagnostic',project_timed_stream),('lifetimeDiagnostic',project_lifetime),
                                ('stopJunitDiagnostic',project_stop_junit),('runtimeHandshakeDiagnostic',project_runtime_handshake)):
                try:
                    projected=project(results,native,junit,ordinary,explicit,lane.execution_facts,lane.source)
                    lane.execution_facts[key]=projected
                except BaseException as error:
                    lane.record(error)
        except BaseException as error:lane.record(error)
    try:
        lane.check();r.require(lane.sequence==34,'UE_COMMANDS')
    except BaseException as error:lane.record(error)


def rejection_codes(census,results,errors):
    allowed=CODES|results.CODES|results.census.PUBLIC_REJECTION_CODES
    queue=list(errors);seen=set();found=set();unknown=0
    while queue:
        error=queue.pop()
        if not isinstance(error,BaseException) or id(error) in seen:continue
        seen.add(id(error))
        if type(error) is results.census.Rejected and type(error.code) is str and error.code in allowed:found.add(error.code)
        elif not isinstance(error,BaseExceptionGroup):unknown+=1
        if isinstance(error,BaseExceptionGroup):queue.extend(error.exceptions)
        if error.__cause__ is not None:queue.append(error.__cause__)
        if error.__context__ is not None:queue.append(error.__context__)
    return {'codes':sorted(found),'unclassifiedExceptionNodes':unknown}


def terminal_diagnostic(lane,census,results):
    codes=rejection_codes(census,results,lane.errors)['codes']
    stage=lane.stage if lane.stage in ('ordinary-execution','stop-handshake-full-python-controls') else 'preparation'
    prepared='true' if getattr(lane,'preparation_complete',False) is True else 'false'
    state=lane.commands.get('ordinary-execution');closed=False
    if type(state) is dict and type(state.get('argv')) is list:closed=results.owner_closed(state,'ordinary-execution',state['argv'])
    code='null'
    if closed:
        value=state['receipt']['exitCode']
        if -255<=value<=255:code=str(value)
    return ('{"terminalDiagnostic":true,"executionQualified":false,"exit":1,"stage":"'+stage+
        '","preparationQualifiedAtHandoff":'+prepared+',"ownerClosed":'+('true' if closed else 'false')+
        ',"bodyExitCode":'+code+',"errorOccurrences":'+str(len(lane.errors))+
        ',"codes":['+','.join('"'+item+'"' for item in codes)+']}\n').encode('ascii')


def finish(lane,census,results):
    r=lane.r
    try:lane.check()
    except BaseException as error:lane.record(error)
    qualified=bool(getattr(lane,'body_passed',False) and not lane.errors and lane.durable and time.monotonic()<lane.stop)
    try:
        summary={'schemaVersion':1,'scope':'unchanged-linux-release-full-ordinary-stop-handshake-diagnostic','identity':lane.identity,
            'candidateCatalogSHA256':HELPERS['eng/unit-census.json'],'sourceTrees':dict(SOURCE_TREES),
            'sourceUnchangedFromHead':PARENT_HEAD,'candidateParentHead':PARENT_HEAD,'candidateParentTree':PARENT_TREE,
            'diagnosticOnly':True,'plannedCommandCount':34,'commandCap':64,
            'parentFullFailure':{'runId':41997112845,'jobId':127142884454,'head':PARENT_HEAD,'tree':PARENT_TREE,
                'ordinary':2879,'passed':2618,'errors':261,'qualified':False,
                'historyBindingVerified':getattr(lane,'parent_history_verified',False),'evidenceSHA256':PARENT_RESULT_SHA256},
            'requiredSdk':'10.0.114','requiredRuntime':'10.0.14',
            'expectedOrdinaryMetadataCount':2879,'expectedExplicitMetadataCount':5,
            'fullOrdinaryRegressionQualified':qualified,
            'historicalFullOrdinaryRun':{'runId':41940224644,'jobId':126958823032,
                'head':'dcc3edbb2bb4057343b9a32a31f7bed39e47c4ad','ordinary':2879,'passed':2589,'errors':290,
                'qualified':False,'evidenceSHA256':HELPERS['eng/full-regression-history/original-ordinary-result.json']},
            'previousCurrentFullRun':{'runId':41962521900,'head':'72d728bf226ef3d5c9a5ca4d547f798810147880',
                'ordinary':2879,'passed':2607,'errors':272,'qualified':False,
                'evidenceSHA256':HELPERS['eng/pure-negotiation-history/full-ordinary-result.json']},
            'historicalSixteenCaseDiagnostic':{'runId':41966659630,'head':'aecea7afdfdd426a1bbbd5cf840bed945ef62e7b',
                'passed':7,'errors':9,'qualified':False,'infrastructureQualified':True,
                'evidenceSHA256':HELPERS['eng/pure-negotiation-history/sixteen-case-diagnostic-result.json']},
            'historicalTimedFailureResolution':'unresolved-no-timed-source-change',
            'latestHistoricalFullRun':{'runId':41970862705,'head':'1f5315ca23753258c002ebd6bed294f0226af603',
                'ordinary':2879,'passed':2614,'errors':265,'qualified':False,
                'evidenceSHA256':HELPERS['eng/protocol70-history/full-ordinary-result.json']},
            'historicalCommandCapFailure':{'runId':41988031636,'head':'9784e1b08dbd8d6eebc60072bf1fa99ece1f4682',
                'passed':31,'completedCases':31,'successfulStageCount':64,'observedNonpassingCaseCount':0,
                'complete':False,'qualified':False,'infrastructureQualified':False,
                'evidenceSHA256':HELPERS['eng/protocol70-history/command-cap-failure-result.json']},
            'historicalTwentyOneCaseDiagnostic':{'runId':41976164524,'head':'0de69b451c744182f6962d253a1749899fdc29af',
                'passed':17,'errors':4,'complete':True,'infrastructureQualified':True,'qualified':False,
                'evidenceSHA256':HELPERS['eng/protocol70-history/twenty-one-case-diagnostic-result.json']},
            'historicalLifetimeFailuresResolution':'unresolved-despite-isolated-pass',
            'candidateSeventyCaseRun':{'runId':41991798281,'jobId':CANDIDATE_JOB_ID,'head':CANDIDATE_HEAD,
                'requiredPassed':70,'qualificationVerified':getattr(lane,'candidate_success_verified',False),
                'fullSuiteQualified':False,'evidenceSHA256':CANDIDATE_SUCCESS_SHA256},
            'historicalTwentyThreeCaseRun':{'runId':41959478271,'jobId':127022142585,
                'head':'8dba79eacbb18744cc6ccc01ad107bb460d574a4','passed':23,'nonpassing':0,'qualified':True,
                'fullSuiteQualified':False,'evidenceSHA256':HELPERS['eng/full-regression-history/twenty-three-case-result.json']},
            'executionQualified':qualified,'preparationQualifiedAtHandoff':bool(getattr(lane,'preparation_complete',False)),
            'evidenceDurable':lane.durable,'errorOccurrences':len(lane.errors),'rejections':rejection_codes(census,results,lane.errors),
            'execution':getattr(lane,'execution_facts',{}),'explicitRequested':False,'maximumParallelTestsOption':8,
            'exit':0 if qualified else 1}
        r.write_json(lane.evidence,'stop-handshake-full-diagnostic-summary.json',summary);lane.extend_evidence(['stop-handshake-full-diagnostic-summary.json'])
        lane.check()
        rendered=json.dumps(summary,sort_keys=True,allow_nan=False)
        r.require(time.monotonic()<lane.stop,'UE_BUDGET')
        print(rendered,flush=True)
        r.require(time.monotonic()<lane.stop,'UE_BUDGET')
        return 0 if qualified else 1
    except BaseException as error:
        lane.record(error)
        try:os.write(2,b'FULL_REGRESSION_FAILED_SUMMARY\n')
        except BaseException as diagnostic:lane.record(diagnostic)
        try:os.write(2,terminal_diagnostic(lane,census,results))
        except BaseException as diagnostic:lane.record(diagnostic)
        # Retain the original census's allowlisted stage/seal diagnostics too.
        # This fail-only record cannot grant qualification or disclose raw paths.
        try:os.write(2,census.terminal_diagnostic(lane,'guard'))
        except BaseException as diagnostic:lane.record(diagnostic)
        return 1


def main():
    global ACTIVE_RECORD
    lane=None;work=None;errors=[];census=None
    ACTIVE_RECORD={'lane':None,'work':None,'errors':errors}
    main.last_errors=errors
    try:
        t0=time.monotonic();work=Path(tempfile.mkdtemp(prefix='sl-stop-handshake-full-',dir='/tmp'));ACTIVE_RECORD['work']=work;os.chmod(work,0o700)
        verify_helpers()
        census=load('execution_census_runner',ROOT/'eng/run-unit-census.py')
        results=load('execution_results',ROOT/'eng/unit-execution-results.py')
        r=results.census;owner=load('execution_owner',ROOT/'eng/package-smoke-process.py');owner.enable_subreaper()
        lane=lane_type(census)(work,r,owner);ACTIVE_RECORD['lane']=lane
        errors=lane.errors;ACTIVE_RECORD['errors']=errors;main.last_errors=errors
        lane.preparation_complete=False
        # Preserve the original3600 preparation bound, including bootstrap elapsed.
        lane.stop=min(lane.stop,t0+3600)
        try:
            ordinary,explicit=prepare(lane,census,results)
            run_body(lane,results,ordinary,explicit,t0+6600)
        except BaseException as error:lane.record(error)
        return finish(lane,census,results)
    except BaseException as error:
        if lane is not None:lane.record(error)
        else:
            errors.append(error)
            if census is not None and work is not None:census.persist(work,errors,999)
            else:bootstrap_persist(work,errors)
        main.last_errors=errors
        try:os.write(2,b'FULL_REGRESSION_FAILED_BOOTSTRAP\n')
        except BaseException as secondary:
            errors.append(secondary)
            if census is not None and work is not None:census.persist(work,errors,1000)
            else:bootstrap_persist(work,errors)
        return 1


def guarded_main():
    try:code=main()
    except BaseException as error:
        # Never replace the original occurrences or detach their lane/receipts.
        errors=ACTIVE_RECORD['errors'];errors.append(error);main.last_errors=errors
        try:os.write(2,b'FULL_REGRESSION_FAILED_OUTERMOST\n')
        except BaseException as secondary:errors.append(secondary)
        code=1
    return code


if __name__=='__main__':
    raise SystemExit(guarded_main())
