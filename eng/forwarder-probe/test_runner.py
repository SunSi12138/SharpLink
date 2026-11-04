#!/usr/bin/env python3
"""Synthetic runner validation; no production target execution."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('forwarders_runner', Path(__file__).with_name('run.py'))
r = importlib.util.module_from_spec(spec); spec.loader.exec_module(r)

class RunnerTests(unittest.TestCase):
    def test_cleanup_never_echoes_raw(self):
        value = r.cleanup_classification(dict(cleanupErrors=['Owned processes remained after command exit/deadline','secret raw text'],remainingOwned={'123':['secret arguments']}))
        self.assertEqual(value, dict(ownedProcessesAfterExitCount=1,otherCleanupErrorCount=1,remainingOwnedCount=1))
        self.assertNotIn('secret',json.dumps(value))

    def test_cli_missing_and_invalid_source_fail_without_raw(self):
        for args in [[],['/nonexistent-source']]:
            p=subprocess.run([sys.executable,r.__file__,*args],capture_output=True,text=True,timeout=15)
            self.assertEqual(1,p.returncode)
            self.assertIn('PACKAGE_FORWARDERS_FAIL category=',p.stdout)
            self.assertNotIn('PACKAGE_FORWARDERS_PASS',p.stdout)
            self.assertEqual('',p.stderr)
            self.assertNotIn('Traceback',p.stdout)

    def test_graph_rejects_escape(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); folder=root/'src'/'X';folder.mkdir(parents=True)
            path=folder/'X.csproj';path.write_text('<Project/>')
            r.check_graph(root,['X'])
            path.write_text('<Project><ItemGroup><ProjectReference Include="../Other/Other.csproj"/></ItemGroup></Project>')
            with self.assertRaises(ValueError):r.check_graph(root,['X'])

    def test_self_test_exact_manifest(self):
        import copy
        manifest=json.loads((Path(r.__file__).parent/'probe/self-test-expected.json').read_text())
        summary=dict(schemaVersion=1,mode='synthetic-self-test',success=True,code='Pass',passed=len(manifest),evidenceScope='ManufacturedMetadataFixturesNotHistoricalConsumer',tests=[dict(name=n,expectedCode=c,passed=True) for n,c in manifest.items()])
        r.validate_self_tests(summary)
        for key,value in [('name','unknown-name'),('expectedCode','WrongCode'),('passed',False)]:
            bad=copy.deepcopy(summary);bad['tests'][0][key]=value
            with self.assertRaises(ValueError):r.validate_self_tests(bad)
        bad=copy.deepcopy(summary);bad['tests']=bad['tests'][:-1];bad['passed']-=1
        with self.assertRaises(ValueError):r.validate_self_tests(bad)
        bad=copy.deepcopy(summary);bad['tests'][0]=bad['tests'][1]
        with self.assertRaises(ValueError):r.validate_self_tests(bad)

    def test_probe_schema_and_false_assertions(self):
        import copy
        hashes={'SharpLink.Sdk':'a'*64,'SharpLink.Abstractions':'b'*64}
        summary=dict(schemaVersion=1,mode='packaged-sdk-forwarding',success=True,code='Pass',sourceCommit=r.EXPECTED['sourceCommit'],evidenceScope='ExplicitSdkQualifiedForwardingOnly',historical203AbiProven=False,ordinaryPackageReferenceConsumerProven=False,expectedForwarderCount=20,runtimeVersion='10.0.14')
        for key,name in [('sdk','SharpLink.Sdk'),('abstractions','SharpLink.Abstractions')]:
            summary[key]=dict(sha256=hashes[name],fullIdentity=name+', Version=3.0.0.0, Culture=neutral, PublicKeyToken=null',mvid='11111111-1111-1111-1111-111111111111',informationalVersion='3.0.0+'+r.EXPECTED['sourceCommit'])
        summary['runtime']=dict(metadataExact20=True,freshBoundedCollectibleContext=True,loadedOriginsVerified=True,contextAssemblyCount=2,types=[dict(name=n,assemblyGetTypeSameAsDirect=True,getForwardedTypesSameAsDirect=True,sdkQualifiedTypeGetTypeSameAsDirect=True) for n in r.EXPECTED['forwardedNames']])
        r.validate_probe(summary,hashes)
        for key,value in [('success',False),('success',1),('expectedForwarderCount',19),('sourceCommit','old'),('historical203AbiProven',True),('runtimeVersion','9.0.0')]:
            bad=copy.deepcopy(summary);bad[key]=value
            with self.assertRaises(ValueError):r.validate_probe(bad,hashes)
        for field,value in [('sha256','c'*64),('fullIdentity','Other'),('mvid','00000000-0000-0000-0000-000000000000')]:
            bad=copy.deepcopy(summary);bad['sdk'][field]=value
            with self.assertRaises(ValueError):r.validate_probe(bad,hashes)
        bad=copy.deepcopy(summary);bad['runtime']['types'][0]['sdkQualifiedTypeGetTypeSameAsDirect']=False
        with self.assertRaises(ValueError):r.validate_probe(bad,hashes)
        bad=copy.deepcopy(summary);bad['runtime']['types'][0]=bad['runtime']['types'][1]
        with self.assertRaises(ValueError):r.validate_probe(bad,hashes)
        bad=copy.deepcopy(summary);bad['rawLog']='private'
        with self.assertRaises(ValueError):r.validate_probe(bad,hashes)

    def test_read_json_fail_closed(self):
        with tempfile.TemporaryDirectory() as td:
            out=Path(td)/'stdout';err=Path(td)/'stderr';err.write_bytes(b'');out.write_text('{"ok":true}')
            self.assertEqual(dict(ok=True),r.read_json(out,err))
            err.write_text('private diagnostic')
            with self.assertRaises(ValueError):r.read_json(out,err)
            err.write_bytes(b'');out.write_text('x'*24001)
            with self.assertRaises(ValueError):r.read_json(out,err)
            out.write_text('invalid')
            with self.assertRaises(ValueError):r.read_json(out,err)

if __name__=='__main__':unittest.main()
