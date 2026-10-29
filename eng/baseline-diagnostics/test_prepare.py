"""Offline baseline build/fixture contracts; no CLR, collector or target execution."""
import ast
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile
import prepare
from common import SOURCE, TREE, CONTRACT, ROOT


class PreparationTests(unittest.TestCase):
    def test_fixed_contract(self):
        prepare.verify_contract()
        for key, value in [('source','0'*40), ('api',4), ('itemCount',1), ('workSeconds',6), ('variants',['control'])]:
            with patch.dict(prepare.CONTRACT, {key:value}), self.assertRaises(ValueError):
                prepare.verify_contract()

    def test_exact_clean_source_tree(self):
        with patch.object(prepare, 'capture', side_effect=[SOURCE,TREE,'']):
            prepare.verify_source(Path('/fake/source'))
        for values in [('0'*40,), (SOURCE,'0'*40), (SOURCE,TREE,' M src/x.cs')]:
            with patch.object(prepare,'capture',side_effect=values), self.assertRaises(ValueError):
                prepare.verify_source(Path('/fake/source'))

    def test_package_own_source_generator(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for name in prepare.PRODUCTS:
                with zipfile.ZipFile(root/f'sharplink.{name.lower()}.3.0.0.nupkg','w') as z:
                    z.writestr(f'{name}.nuspec',f'<package><metadata><id>SharpLink.{name}</id><version>3.0.0</version><repository commit="{SOURCE}" /></metadata></package>')
                    z.writestr(f'lib/net10.0/SharpLink.{name}.dll',name.encode())
                    if name == 'Sdk': z.writestr('analyzers/dotnet/cs/SharpLink.Generator.dll',b'own-generator')
            records = prepare.materialize(root,root/'baseline','3.0.0',SOURCE)
            self.assertEqual(set(prepare.PRODUCTS),set(records))
            self.assertEqual(b'own-generator',(root/'baseline/SharpLink.Generator.dll').read_bytes())
            with self.assertRaises(ValueError):
                prepare.materialize(root,root/'wrong','3.0.0','0'*40)

    def test_no_workload_mutation_or_forbidden_experiment(self):
        fixture=ROOT/'fixture'; program=(fixture/'probe/Program.cs').read_text()
        old=(ROOT.parent/'outbound-lease-comparison/fixture/probe/Program.cs').read_text()
        def operation(source):
            return source[source.index('async ValueTask Operation()'):source.index('// This graph inspection')]
        self.assertEqual(operation(old),operation(program))
        for forbidden in ('ColdChecks','CorrectnessAsync','rawTicks','durationTicks','operationsPerSecond','p99','Percentile('):
            self.assertNotIn(forbidden,program)
        module=(fixture/'module/Contract.cs').read_text()
        oldmodule=(ROOT.parent/'outbound-lease-comparison/fixture/module/Contract.cs').read_text()
        def download(source): return source.split('    public async IAsyncEnumerable<int> DownloadAsync',1)[1].split('    // Cold-only observer.',1)[0]
        self.assertEqual(download(oldmodule),download(module))
        self.assertNotIn('CorrectnessAsync',module)
        self.assertNotIn('Timeout.InfiniteTimeSpan',module)

    def test_fixed_boundary_handshake_and_no_window_rerun(self):
        source=(ROOT/'fixture/probe/Program.cs').read_text()
        self.assertIn('args.Length != 4',source)
        self.assertIn('TimeSpan.FromSeconds(60)',source)
        self.assertIn('GO {variant} {pid}',source)
        self.assertIn('File.Move(pendingReadyFile, readyFile, overwrite: false)',source)
        self.assertLess(source.index('Encoding.UTF8.GetBytes(json)'),source.index('var actualLoaded ='))
        self.assertLess(source.index('File.ReadAllText(goFile)'),source.index('server.StartAsync'))
        self.assertEqual(1,source.count('phaseEvents.WorkStart(')); self.assertEqual(1,source.count('phaseEvents.WorkStop('))
        self.assertEqual(2,source.count('Opcode = EventOpcode.Info'))
        self.assertLess(source.index('phaseEvents.WorkStart('),source.index('var started ='))
        self.assertLess(source.index('phaseEvents.WorkStop('),source.index('postNormalCorrectness'))
        self.assertLess(source.index('await server.DisposeAsync();'),source.index('for (var inventoryRound'))
        self.assertLess(source.index('JsonSerializer.Serialize(result'),source.index('var actualLoaded ='))
        self.assertLess(source.index('var actualLoaded ='),source.index('new FileStream(output'))
        self.assertNotIn('Console.WriteLine(json)',source)

    def test_only_single_baseline_prepare_no_target_launch(self):
        source=Path(prepare.__file__).read_text(); ast.parse(source)
        self.assertNotIn("'candidate'",source); self.assertNotIn('official-cache',source)
        self.assertIn("'-p:Arm=BASELINE'",source)
        for command in ('collect','run','test','exec'):
            self.assertNotIn(f"[dotnet, '{command}'",source)
        self.assertIn('NUGET_HTTP_CACHE_PATH',source); self.assertIn('NUGET_SCRATCH',source)



class DiagnosticResultTests(unittest.TestCase):
    """Structural positive control delegates real assembly checks, mocked only in this unit."""
    def setUp(self):
        import fixture_validation as v
        self.v = v
        self.root = Path('/synthetic/prepared'); self.runtime = Path('/synthetic/runtime/dotnet')
        self.manifest = dict(contract=CONTRACT, sourceRevision=SOURCE, sourceTree=TREE, sdk='10.0.102',
            fixturePurpose='DiagnosticOnly',status='build-only',toolSourceHashes={'tool':'hash'},
            arms={'baseline':{'snapshot':{'PhysicalTcpProbe.dll':'hash'}}})
        self.data = {key: '' for key in v.RESULT_FIELDS}
        self.data.update(schemaVersion=1,purpose='DiagnosticOnly',phase='diagnostic-work',variant='trace',pid=123,
            arm='baseline-3d76a3a8',sourceRevision=SOURCE,expectedSource=SOURCE,apiVersion=5,mode='static-intercepted',
            itemCount=100,workers=1,warmupSeconds=3,workSeconds=20,transport='tcp-loopback',connectionPoolMin=1,
            connectionPoolMax=1,readyConnections=1,requestTimeout='disabled',heartbeatIntervalSeconds=3600,
            heartbeatTimeoutSeconds=7200,serverInterceptors=0,clientInterceptors=1,port=1234,runtimeVersion='10.0.14',
            architecture='X64',processorCount=2,processorAffinity=3,tieredCompilation='default',tieredPgo='default',
            readyToRun='default',serverGc=False,shutdownCompleted=True,disposeCompleted=True,sameVersionServerClient=True,
            interceptorObserved=True,validationFailures=0,binaryHashes={'PhysicalTcpProbe.dll':'hash'},
            generatedManifest=dict(arm='baseline-3d76a3a8',sourceRevision=SOURCE,api=5,protocol=2,
                generator='3.0.0',abi='sharplink-3.0-api5-owner-bound-unsafe-blit-v1',rpcAssemblyHash='1'*32),
            normalCorrectness={key:True for key in v.NORMAL_CHECKS},postNormalCorrectness={key:True for key in v.NORMAL_CHECKS},
            channelType='SharpLink.Client.SharpLinkClient',streamType='Type+LogicalInvocationAsyncEnumerable`1',
            wrapperTypes=['Type+LogicalInvocationAsyncEnumerable`1','InterceptedAsyncEnumerable','PooledAsyncStreamDispatcher'],
            logicalPathProof=dict(clientActivityListeners=False,clientCallsEnabled=False,telemetryWrapperObserved=False,
                completionOwnedObserved=False,generalLogicalObserved=True,physicalDispatcherObserved=True,observedBeforeWarmup=True),
            markerStartTick=10,workStartTick=11,workEndTick=2011,markerStopTick=2012,elapsedTicks=2000,
            stopwatchFrequency=100,operations=50,cpuTicks=100,cpuTickUnit='100ns',allocatedBytes=200,gc0=0,gc1=0,gc2=0,
            loadedAssemblies=[],frameworkAssemblies=[],loadedAssemblyCount=0,
            frameworkLocations=[str(self.runtime.parent/'shared/Microsoft.NETCore.App/10.0.14/System.Private.CoreLib.dll'),
                str(self.runtime.parent/'shared/Microsoft.AspNetCore.App/10.0.14/Microsoft.AspNetCore.dll')])
        for key, ordinal in [('beforeWorkDrain',1),('afterWorkDrain',2)]:
            self.data[key]=dict(logicalInvocations=0,activeClientCalls=0,activeClientStreams=0,pendingCalls=0,
                activeServerCalls=0,normalStarted=ordinal,normalCompleted=ordinal,normalActive=0)

    def check(self, data=None):
        with patch.object(self.v,'source_inventory',return_value={'tool':'hash'}), \
             patch.object(self.v,'inventory',return_value={'PhysicalTcpProbe.dll':'hash'}), \
             patch.object(self.v.old_identity,'validate_loaded') as loaded, \
             patch.object(self.v.old_identity,'validate_framework_assemblies') as framework:
            result=self.v.validate_result(data or self.data,'trace',self.root,self.manifest,self.runtime,{}, {},[0,1],123)
            loaded.assert_called_once(); framework.assert_called_once()
            return result

    def test_structural_positive_calls_both_identity_validators(self):
        self.assertIs(self.data,self.check())

    def test_forbidden_performance_fields_and_wrong_identity_rejected(self):
        import copy
        for key,value in [('operationsPerSecond',10),('pid',124),('apiVersion',4),('sourceRevision','0'*40),
            ('variant','control'),('itemCount',1),('warmupSeconds',0),('workSeconds',6),('processorAffinity',1)]:
            data=copy.deepcopy(self.data); data[key]=value
            with self.subTest(key=key), self.assertRaises(ValueError): self.check(data)

    def test_truncated_window_bad_metrics_or_missing_post_health_rejected(self):
        import copy
        for key,value in [('elapsedTicks',1999),('workEndTick',2000),('markerStopTick',1900),
            ('cpuTicks',-1),('allocatedBytes',True),('operations',0),('gc0',-1),('cpuTickUnit','seconds')]:
            data=copy.deepcopy(self.data); data[key]=value
            with self.subTest(key=key), self.assertRaises(ValueError): self.check(data)
        data=copy.deepcopy(self.data); data['afterWorkDrain']['normalCompleted']=1
        with self.assertRaises(ValueError): self.check(data)
        data=copy.deepcopy(self.data); data['postNormalCorrectness']['physicalDrain']=False
        with self.assertRaises(ValueError): self.check(data)

    def test_identity_validator_failure_not_suppressed(self):
        with patch.object(self.v,'source_inventory',return_value={'tool':'hash'}), \
             patch.object(self.v,'inventory',return_value={'PhysicalTcpProbe.dll':'hash'}), \
             patch.object(self.v.old_identity,'validate_loaded',side_effect=ValueError('identity-denied')):
            with self.assertRaisesRegex(ValueError,'identity-denied'):
                self.v.validate_result(self.data,'trace',self.root,self.manifest,self.runtime,{}, {},[0,1],123)

if __name__=='__main__': unittest.main()
