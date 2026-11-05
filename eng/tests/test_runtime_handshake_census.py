"""Current four-body proof plus unchanged historical rebase controls on exact parent view."""
import copy
import importlib.util
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch
sys.dont_write_bytecode=True
ROOT=Path(__file__).resolve().parents[2]
def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m
g=load('current_runtime_handshake_rebase',ROOT/'eng/rebase-runtime-handshake-census.py')
# No monkeypatch of historical validation. Reconstruct its original input tree,
# verify it, then run its exact original controls against that sealed preimage.
_parent_owner=tempfile.TemporaryDirectory(prefix='runtime-handshake-parent-audit-')
PARENT_ROOT=Path(_parent_owner.name)/'parent';os.chmod(Path(_parent_owner.name),0o700)
shutil.copytree(ROOT,PARENT_ROOT,ignore=shutil.ignore_patterns('CANDIDATE-*','__pycache__','.git','obj','bin'))
(PARENT_ROOT/g.SOURCE).write_bytes((ROOT/g.PREIMAGE).read_bytes())
(PARENT_ROOT/'eng/unit-census.json').write_bytes((ROOT/g.PARENT_CATALOG).read_bytes())
assert g.sha((PARENT_ROOT/'eng/unit-census.json').read_bytes())==g.PARENT_CATALOG_SHA256
assert g.tree_id(PARENT_ROOT/'test')==g.PARENT_TEST_TREE
assert g.tree_id(PARENT_ROOT/'src')==g.PRODUCT_TREE
old=load('unchanged_parent_rebase_controls',PARENT_ROOT/'eng/tests/test_three_fixture_census.py')
HistoricalControls=old.Controls

class Controls(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.parent=json.loads((ROOT/g.PARENT_CATALOG).read_bytes())
        cls.old=(ROOT/g.PREIMAGE).read_bytes();cls.new=(ROOT/g.SOURCE).read_bytes()
        cls.manifest=json.loads((ROOT/g.MANIFEST).read_bytes())
        cls.candidate,cls.report=g.generate(ROOT)
    def test_new_catalog_and_proof_reproduce_exactly(self):
        self.assertEqual(g.render(self.candidate),(ROOT/'eng/unit-census.json').read_bytes())
        self.assertEqual(g.render(self.report),(ROOT/'eng/runtime-handshake-census-rebase.json').read_bytes())
        self.assertEqual(self.report['candidateCatalogSHA256'],g.sha(g.render(self.candidate)))
        self.assertNotEqual(g.sha(g.render(self.candidate)),g.PARENT_CATALOG_SHA256)
    def test_all_identity_attributes_return_parameters_eligibility_cases_preserved(self):
        variable={'attributeLine','signatureLine','endLine','sourceProvenance'}
        provenance={'testAttributeLine','startOffset','endOffset','spanSHA256'}
        self.assertEqual(len(self.candidate['methods']),2463)
        for before,after in zip(self.parent['methods'],self.candidate['methods']):
            self.assertEqual({k:v for k,v in before.items() if k not in variable},{k:v for k,v in after.items() if k not in variable})
            self.assertEqual({k:v for k,v in before['sourceProvenance'].items() if k not in provenance},{k:v for k,v in after['sourceProvenance'].items() if k not in provenance})
        for kind,count in (('ordinary',2879),('explicit',5)):
            self.assertEqual(sum(m['expectedCases'] for m in self.candidate['methods'] if m['initialEligibility']==kind and 'Release' in m['configurations']),count)
    def test_old_derivation_and_all_older_top_level_provenance_are_unchanged(self):
        self.assertEqual(self.parent['sourceDerivation'],self.candidate['sourceDerivation'])
        for key,value in self.parent.items():
            if key not in ('methods','sourcePins'):self.assertEqual(value,self.candidate[key])
        self.assertEqual(self.report['candidateExecution'],'not run');self.assertFalse(self.report['fullSuiteQualified'])
        self.assertEqual(self.report['historicalDiagnostic']['errors'],261)
        self.assertFalse(self.report['historicalDiagnostic']['qualified'])
    def test_exact_one_pin_four_bodies_all_other_methods_identical(self):
        self.assertEqual({p for p in self.parent['sourcePins'] if self.parent['sourcePins'][p]!=self.candidate['sourcePins'][p]},{g.SOURCE})
        changed=[]
        for before,after in zip(self.parent['methods'],self.candidate['methods']):
            if before['sourceProvenance']['spanSHA256']!=after['sourceProvenance']['spanSHA256']:changed.append(before['methodName'])
            if before['source']!=g.SOURCE:self.assertEqual(before,after)
        self.assertEqual(set(changed),g.METHODS);self.assertEqual(len(changed),4)
        self.assertEqual(len(self.report['methodRecordsInEditedFile']),10)
        self.assertTrue(self.report['allOtherParentTestBytesUnchanged']);self.assertTrue(self.report['nonMethodBytesUnchanged'])
    def test_body_manifest_identity_span_digest_and_count_mutations_rejected(self):
        for mutate in (lambda x:x['methods'].pop(),lambda x:x['methods'].append(copy.deepcopy(x['methods'][0])),
                       lambda x:x['methods'][0].update(method='Foreign'),lambda x:x['methods'][0]['new'].update(byteEndExclusive=3050),
                       lambda x:x['methods'][0]['old'].update(sha256='0'*64),lambda x:x['methods'][0].update(identity='Foreign'),
                       lambda x:x['methods'][0].update(parameters=['bool']),lambda x:x['methods'][0].update(discoveredCases=2),
                       lambda x:x['methods'][0]['new'].update(signatureLine=13),lambda x:x.update(baseHead='0'*40)):
            value=copy.deepcopy(self.manifest);mutate(value)
            with self.assertRaises(ValueError):g.derive(self.parent,self.old,self.new,value)
    def test_unapproved_source_helper_signature_attribute_or_fifth_body_changes_rejected(self):
        for before,after in ((b'private static NegotiatedSessionOptions CreateOptions(',b'private static NegotiatedSessionOptions OtherOptions('),
                             (b'[Test]',b'[Explicit]'),(b'public async Task HandshakeCompletionShouldPublishOneCompleteImmutableSnapshot()',b'public async Task Renamed()'),
                             (b'HandshakeCompletionAfterTerminalShouldBeRejected',b'WrongExcludedMethod')):
            self.assertIn(before,self.new)
            bad=self.new.replace(before,after,1)
            with self.assertRaises(ValueError):g.derive(self.parent,self.old,bad,self.manifest)
        with self.assertRaises(ValueError):g.derive(self.parent,self.old+b'\n',self.new,self.manifest)
    def test_exact_other_bytes_reconstruction_remains_enforced_after_pin_substitution(self):
        # Independently exercise the structural proof after admitting synthetic
        # hashes. A shared-helper byte cannot hide inside an allowed body delta.
        bad=self.new+b'\n';manifest=copy.deepcopy(self.manifest);manifest.update(newFileSHA256=g.sha(bad),newFileBytes=len(bad))
        with patch.object(g,'NEW_SOURCE_SHA256',g.sha(bad)):
            with self.assertRaisesRegex(ValueError,'RH_ALL_OTHER_BYTES'):g.derive(self.parent,self.old,bad,manifest)
    def test_every_current_source_span_and_new_test_tree_is_exact(self):
        for method in self.candidate['methods']:g.text_span((ROOT/method['source']).read_text(),method)
        self.assertEqual(g.tree_id(ROOT/'test'),g.CANDIDATE_TEST_TREE)
        self.assertEqual(g.tree_id(ROOT/'test',{g.SOURCE.removeprefix('test/'):self.old}),g.PARENT_TEST_TREE)
        self.assertEqual(g.tree_id(ROOT/'src'),g.PRODUCT_TREE)
    def test_each_new_and_original_history_pin_is_fail_closed(self):
        read=Path.read_bytes
        for name in g.HISTORY_PINS:
            target=ROOT/name
            def changed(path,target=target):return read(path)+(b'\n' if path==target else b'')
            with patch.object(Path,'read_bytes',changed):
                with self.assertRaises(ValueError):g.generate(ROOT)
    def test_product_or_other_test_changes_are_rejected(self):
        actual=g.tree_id
        for domain in ('src','test'):
            def changed(root,*args,domain=domain,**kwargs):return '0'*40 if root==ROOT/domain else actual(root,*args,**kwargs)
            with patch.object(g,'tree_id',changed):
                with self.assertRaises(ValueError):g.generate(ROOT)
    def test_historical_controls_use_exact_private_parent_only(self):
        self.assertEqual(os.stat(Path(_parent_owner.name)).st_mode&0o777,0o700)
        self.assertNotEqual(PARENT_ROOT,ROOT)
        self.assertEqual((PARENT_ROOT/g.SOURCE).read_bytes(),self.old)
        self.assertEqual((PARENT_ROOT/'eng/unit-census.json').read_bytes(),(ROOT/g.PARENT_CATALOG).read_bytes())
        self.assertEqual((PARENT_ROOT/'eng/tests/test_three_fixture_census.py').read_bytes(),(ROOT/'eng/tests/test_three_fixture_census.py').read_bytes())
        self.assertEqual((PARENT_ROOT/'eng/rebase-three-fixture-census.py').read_bytes(),(ROOT/'eng/rebase-three-fixture-census.py').read_bytes())

if __name__=='__main__':unittest.main()
