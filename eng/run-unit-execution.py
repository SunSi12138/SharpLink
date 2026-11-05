#!/usr/bin/env python3
"""Same-build census followed by one unfiltered Linux Release ordinary run."""
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import time

sys.dont_write_bytecode=True
ROOT=Path(__file__).resolve().parents[1]
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
    certificate={'identity':lane.identity,'metadataQualified':True,'ordinaryCount':len(ordinary),'explicitCount':len(explicit),
        'canonicalLedgerSHA256':r.digest(first['actual']),'preparationDeadlineMonotonic':lane.stop}
    r.write_json(lane.evidence,'preparation.json',certificate);lane.extend_evidence(['preparation.json'])
    lane.check()
    r.require(time.monotonic()<lane.stop,'UE_PREPARATION')
    return ordinary,explicit


def run_body(lane,results,ordinary,explicit,overall_stop):
    r=lane.r;lane.execution_facts={};lane.body_passed=False;lane.report=None
    # Only reached after the complete original deadline-bound preparation gate.
    lane.stop=min(overall_stop,time.monotonic()+3000)
    lane.run('execution-python-controls',[sys.executable,'-B',lane.source/'eng/tests/test_unit_execution.py'],lane.source,30)
    r.require(lane.sequence==31,'UE_COMMANDS')
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
            if not lane.body_passed:raise r.Rejected('UE_RESULTS')
        except BaseException as error:lane.record(error)
    try:
        lane.check();r.require(lane.sequence==32,'UE_COMMANDS')
    except BaseException as error:lane.record(error)


def rejection_codes(census,results,errors):
    allowed=results.CODES|results.census.PUBLIC_REJECTION_CODES
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
    stage=lane.stage if lane.stage in ('ordinary-execution','execution-python-controls') else 'preparation'
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
        summary={'schemaVersion':1,'scope':'linux-release-ordinary-execution','identity':lane.identity,
            'executionQualified':qualified,'preparationQualifiedAtHandoff':bool(getattr(lane,'preparation_complete',False)),
            'evidenceDurable':lane.durable,'errorOccurrences':len(lane.errors),'rejections':rejection_codes(census,results,lane.errors),
            'execution':getattr(lane,'execution_facts',{}),'explicitRequested':False,'maximumParallelTestsOption':8,
            'exit':0 if qualified else 1}
        r.write_json(lane.evidence,'execution-summary.json',summary);lane.extend_evidence(['execution-summary.json'])
        lane.check()
        rendered=json.dumps(summary,sort_keys=True,allow_nan=False)
        r.require(time.monotonic()<lane.stop,'UE_BUDGET')
        print(rendered,flush=True)
        r.require(time.monotonic()<lane.stop,'UE_BUDGET')
        return 0 if qualified else 1
    except BaseException as error:
        lane.record(error)
        try:os.write(2,b'UNIT_EXECUTION_FAILED_SUMMARY\n')
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
        t0=time.monotonic();work=Path(tempfile.mkdtemp(prefix='slue-',dir='/tmp'));ACTIVE_RECORD['work']=work;os.chmod(work,0o700)
        census=load('execution_census_runner',ROOT/'eng/run-unit-census.py')
        results=load('execution_results',ROOT/'eng/unit-execution-results.py')
        r=results.census;owner=load('execution_owner',ROOT/'eng/package-smoke-process.py');owner.enable_subreaper()
        lane=census.Lane(work,r,owner);ACTIVE_RECORD['lane']=lane
        errors=lane.errors;ACTIVE_RECORD['errors']=errors;main.last_errors=errors
        lane.preparation_complete=False
        # Preserve the original3600 preparation bound, including bootstrap elapsed.
        lane.stop=min(lane.stop,t0+3600)
        try:
            ordinary,explicit=prepare(lane,census,results);lane.preparation_complete=True
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
        try:os.write(2,b'UNIT_EXECUTION_FAILED_BOOTSTRAP\n')
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
        try:os.write(2,b'UNIT_EXECUTION_FAILED_OUTERMOST\n')
        except BaseException as secondary:errors.append(secondary)
        code=1
    return code


if __name__=='__main__':
    raise SystemExit(guarded_main())
