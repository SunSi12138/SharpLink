#!/usr/bin/env python3
"""Verify fresh source/TLS/native coverage and retain costs, without equality budgets."""
import argparse,hashlib,json,re,statistics
from pathlib import Path
from prologue_metrics import metrics,normal
from prepare_arms import profile_settings
ARMS=['baseline','candidate-final'];MODES={'fullopts':['FullOpts'],'tier0':['Tier0'],'tier1':['Tier1'],'pgo':['Instrumented Tier0','Tier1']}
SHAPE_TARGETS=['InvokeUnaryCoreAsync','OnRegistered','ReleaseCallAdmissionReservation','InvokeClientStreamingCoreAsync','RunGeneratedClientStreamsAsync','StartServerStreamingInvokerAsync','StartDuplexStreamingInvokerAsync','InvokeOneWayStreamingCoreAsync','PrepareGeneratedServerStreamAsync','CheckHealthAsync']
RETRY_TARGETS=['InvokeUnaryRetryAttemptAsync','InvokeUnaryWithRetryAsync']
ASYNC=set(['InvokeUnaryWithRetryAsync','InvokeClientStreamingCoreAsync','RunGeneratedClientStreamsAsync','StartServerStreamingInvokerAsync','StartDuplexStreamingInvokerAsync','InvokeOneWayStreamingCoreAsync','CheckHealthAsync'])
def digest(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def canon(obj):return re.sub(r'd__\d+','d__N',json.dumps(obj,sort_keys=True))
def native_rows(campaign,kind):
    rows=[]
    for arm in ARMS:
        for mode in MODES:
            path=campaign/arm/(mode+'.asm')
            if not path.exists():continue
            for body in path.read_text().split('; Assembly listing for method ')[1:]:
                label=body.splitlines()[0]
                target=next((t for t in SHAPE_TARGETS+RETRY_TARGETS if t in label),None)
                if target is None:continue
                if target in ASYNC:
                    if ':MoveNext(' not in label:continue
                elif not (':'+target+'(' in label or ':'+target+'[' in label):continue
                complete=body.count('; Total bytes of code ')==1
                rows.append({'SourceGroup':campaign.name,'Kind':kind,'Arm':arm,'Mode':mode,'Tier':label.rsplit('(',1)[-1].rstrip(')'),'Target':target,'Label':normal(label),'CompleteNativeBody':complete,'FooterCount':body.count('; Total bytes of code '),'Bytes':int(re.search(r'Total bytes of code (\d+)',body)[1]) if complete else None,'Prologue':metrics(body),'BodySha256':hashlib.sha256(body.encode()).hexdigest(),'Source':str(path)})
    return rows

def validate_network_sample(sample,kind,runtime):
    assert sample['RuntimeBinding']['RuntimeVersion']==runtime
    n=sample['Network']
    for phase in ['Before','PreUpdate','Resumed','After']:
        cohort=n[phase];assert cohort['ClientReady']==cohort['ServerPhysical']==cohort['AuthenticationCalls']==2
        assert all(cohort[k] for k in ['SameConnectionObjects','SameSessionObjects','AllReady','AuthenticatedTls'])
        assert cohort['TlsProtocols']==['Tls13','Tls13']
    assert n['RpcCalls']==30000 and n['HeldProxyAcrossPhases']
    assert n['MaintenanceEnabled']==(kind=='shapes')
    if kind=='shapes':
        assert n['UpdateStage']=='Succeeded' and n['GenerationAdvanced'] and n['Result']==31 and n['OneWayCallsObserved']==600
        assert len(n['ShapeMeasurements'])==30
        for shape in ['client-stream','server-stream','duplex-stream','one-way-client-stream','health']:
            assert len([s for s in n['ShapeMeasurements'] if s['Name']==shape])==6
    return n

def verify_registration_companion(proof,primary,samples,runtime):
    assert proof['CaptureKind']=='registration-pgo-companion'
    assert proof['Filter']=='SharpLink.Client.PendingRequestTable:OnRegistered'
    assert set(proof['Modes'])=={'pgo'} and proof['Modes']['pgo']==primary['Modes']['pgo']
    assert proof['Iterations']==primary['Iterations']==5000
    assert proof['ProbeFiles']==primary['ProbeFiles']
    assert set(proof['Arms'])==set(ARMS) and set(samples)==set(ARMS)
    for arm in ARMS:
        binding=proof['Arms'][arm];original=primary['Arms'][arm]
        for key in ['Binary','Sources','Binaries','AuxiliaryBinaries','RuntimeBinding','RuntimeFiles']:
            assert binding[key]==original[key],f'Companion differs from its primary {arm} {key}'
        assert binding['Exits']=={'metadata':0,'pgo':0}
        assert binding['RuntimeBinding']['RuntimeVersion']==runtime
        assert samples[arm]['RuntimeBinding']==binding['RuntimeBinding']
        validate_network_sample(samples[arm],'shapes',runtime)
    return {'Provenance':proof,'Samples':samples,'Launches':2,
            'Scope':'Predeclared same-shape paired PGO native companion. All allocation windows retained separately; not replacement performance samples.'}

def require_native_pair(rows,kind,target,mode,tier):
    pair={arm:[row for row in rows if row['Kind']==kind and row['Arm']==arm and row['Target']==target and row['Mode']==mode and row['Tier']==tier and row['CompleteNativeBody']] for arm in ARMS}
    assert all(pair.values()),f'Missing complete native coverage: {target} {mode} {tier}. Inlining or missing output is not zero cost.'
    return pair

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--root',type=Path,default=Path(__file__).resolve().parent);p.add_argument('--group',required=True);p.add_argument('--shape-group');p.add_argument('--retry-group');p.add_argument('--extra-native-group',action='append',default=[]);p.add_argument('--registration-native-group');p.add_argument('--source-manifest',type=Path);p.add_argument('--expected-runtime',required=True);p.add_argument('--expected-head',required=True);p.add_argument('--output',type=Path);a=p.parse_args()
    root=a.root.resolve();out=a.output or root/('summary-'+a.group+'.json');source=json.loads((a.source_manifest or root/'source-provenance.json').read_text())
    _,expected_trees=profile_settings(source.get('Profile','historical'))
    report={'Status':'Started','ExpectedCandidateCommit':a.expected_head,'ExpectedSourceTrees':expected_trees,'ExpectedRuntime':a.expected_runtime,'SourceProvenance':source,'Launches':0,'Checks':[],'NativeRows':[],'NativeCoverage':[],'AllocationWindows':[],'RegistrationNativeCompanions':[],'Limits':'No elapsed-time measurements, throughput-equivalence budget or native-equality gate. Whole-process allocations include transport, scheduling and overlapping one-way server work. PGO promotion is accelerated; all samples and native variants are retained.'}
    def save():out.parent.mkdir(parents=True,exist_ok=True);out.write_text(json.dumps(report,indent=2)+'\n')
    save()
    try:
        assert source['Status']=='SourceSnapshotsVerified'
        for kind in ['shapes','retry']:
            group=(a.shape_group if kind=='shapes' else a.retry_group) or a.group
            campaign=root/kind/'evidence'/group;proof=json.loads((campaign/'provenance.json').read_text());metadata={}
            assert set(proof['Modes'])==set(MODES),'Missing mode'
            sdk=json.loads((root/kind/'evidence/build-toolchain.json').read_text());assert sdk['SDK']==sdk['MSBuildProperties']['NETCoreSdkVersion']=='10.0.102'
            for path,item in sdk['Files'].items():assert digest(Path(path))==item['Sha256'],path
            for arm in ARMS:
                expected=source['Arms'][kind][arm];assert expected['SourceTree']==expected_trees[arm]
                if arm=='candidate-final':assert expected['Commit']==a.expected_head
                binding=proof['Arms'][arm];assert binding['Sources']=={k:v for k,v in expected['Files'].items() if k.startswith('src/')}
                assert set(binding['Exits'])==set(MODES)|{'metadata'} and all(code==0 for code in binding['Exits'].values())
                m=json.loads((campaign/arm/'metadata.json').read_text());metadata[arm]=m
                assert m['RuntimeBinding']['RuntimeVersion']==a.expected_runtime and m['RuntimeBinding']['Architecture']=='X64'
                assert binding['RuntimeBinding']==m['RuntimeBinding']
                for path,item in binding['RuntimeFiles'].items():assert digest(Path(path))==item['Sha256'],path
                for name,sha in binding['Binaries'].items():assert digest(Path(binding['Binary']).parent/name)==sha,name
                for name,sha in binding['AuxiliaryBinaries'].items():assert digest(Path(binding['Binary']).parent.parent.parent/name)==sha,name
                for mode in MODES:
                    sample=json.loads((campaign/arm/(mode+'.json')).read_text());report['Launches']+=1
                    n=validate_network_sample(sample,kind,a.expected_runtime)
                    if kind=='shapes':
                        for shape in ['client-stream','server-stream','duplex-stream','one-way-client-stream','health']:
                            samples=[s['ProcessManagedAllocatedBytes'] for s in n['ShapeMeasurements'] if s['Name']==shape];assert len(samples)==6
                            report['AllocationWindows'].append({'Arm':arm,'Mode':mode,'Shape':shape,'CallsPerBlock':100,'ProcessManagedBytes':samples,'Median':statistics.median(samples),'Min':min(samples),'Max':max(samples)})
            assert metadata['baseline']['Classes']==metadata['candidate-final']['Classes'];assert len(metadata['baseline']['Classes'])==9
            names=['TryReserveCallAdmission','TryReserveOwnCallAdmission','EnsureReconnectLoop','GetReadyConnection','EnsureExpansion']
            selections={arm:{x['Key']:x for x in m['Methods'] if '::' in x['Key'] and any(name in x['Key'] for name in names)} for arm,m in metadata.items()}
            assert len(selections['baseline'])==len(selections['candidate-final'])==10 and canon(selections['baseline'])==canon(selections['candidate-final'])
            for arm,m in metadata.items():
                state=next(x for x in m['StateMachines'] if '<InvokeClientStreamingCoreAsync>' in x['Type']);assert len(state['Fields'])==17 and state['Bytes']['Bytes']==256
                if kind=='shapes':assert len(m['GeneratedClientStreamingLayouts'])==9 and all(x['StateBytes']==272 and x['BoxAllocationBytes']==344 for x in m['GeneratedClientStreamingLayouts'])
            report['Checks'].append({'Kind':kind,'EqualOrdinaryLayouts':9,'EqualSelectionILAndLocals':10,'Metadata':metadata,'Provenance':proof,'BuildToolchain':sdk})
            report['NativeRows']+=native_rows(campaign,kind)
        report['PrimaryNativeGaps']=[]
        for kind,targets in [('shapes',SHAPE_TARGETS),('retry',RETRY_TARGETS)]:
            for target in targets:
                for mode,tiers in MODES.items():
                    for tier in tiers:
                        missing=[arm for arm in ARMS if not any(row['Kind']==kind and row['Arm']==arm and row['Target']==target and row['Mode']==mode and row['Tier']==tier and row['CompleteNativeBody'] for row in report['NativeRows'])]
                        if missing: report['PrimaryNativeGaps'].append({'Kind':kind,'Target':target,'Mode':mode,'Tier':tier,'MissingArms':missing})
        save()
        # Optional local supplements retain original broad fragments and bind their source.
        for group in a.extra_native_group:
            campaign=root/'shapes/evidence'/group;proof=json.loads((campaign/'provenance.json').read_text())
            for arm in ARMS:
                assert proof['Arms'][arm]['Sources']=={k:v for k,v in source['Arms']['shapes'][arm]['Files'].items() if k.startswith('src/')}
                assert proof['Arms'][arm]['RuntimeBinding']['RuntimeVersion']==a.expected_runtime
            report['NativeRows']+=native_rows(campaign,'shapes')
        if a.registration_native_group:
            campaign=root/'shapes/evidence'/a.registration_native_group
            proof=json.loads((campaign/'provenance.json').read_text())
            primary=next(x['Provenance'] for x in report['Checks'] if x['Kind']=='shapes')
            samples={arm:json.loads((campaign/arm/'pgo.json').read_text()) for arm in ARMS}
            report['RegistrationNativeCompanions'].append(verify_registration_companion(proof,primary,samples,a.expected_runtime))
            companion_rows=native_rows(campaign,'shapes')
            # A companion is not a retry-until-green escape: both sides must contain the
            # required target tier even if the primary happened to have complete output.
            report['NativeRows']+=companion_rows
            require_native_pair(companion_rows,'shapes','OnRegistered','pgo','Tier1')
        for kind,targets in [('shapes',SHAPE_TARGETS),('retry',RETRY_TARGETS)]:
            for target in targets:
                for mode,tiers in MODES.items():
                    for tier in tiers:
                        pair=require_native_pair(report['NativeRows'],kind,target,mode,tier)
                        report['NativeCoverage'].append({'Target':target,'Mode':mode,'Tier':tier,'Versions':pair})
        assert report['Launches']==16 and len(report['NativeCoverage'])==60
        report['Status']='Verified';save();print(f"Verified {report['Launches']} launches, 60 paired tier rows; retained {len(report['AllocationWindows'])} allocation groups.")
    except Exception as exc:
        report['Status']='Failed';report['Error']=str(exc);save();raise
if __name__=='__main__':main()
