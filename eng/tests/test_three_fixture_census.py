"""Python-only reproducibility and fail-closed candidate catalog controls."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('fixture_census_rebase', ROOT / 'eng/rebase-three-fixture-census.py')
g = importlib.util.module_from_spec(spec); spec.loader.exec_module(g)


class Controls(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.frozen = json.loads((ROOT / g.BASELINE / 'unit-census.json').read_bytes())
        cls.candidate, cls.report = g.generate(ROOT)

    def test_catalog_and_delta_reproduce_byte_for_byte(self):
        self.assertEqual(g.render(self.candidate), (ROOT / 'eng/unit-census.json').read_bytes())
        self.assertEqual(g.render(self.report), (ROOT / 'eng/three-fixture-census-rebase.json').read_bytes())
        self.assertEqual(self.report['candidateCatalogSHA256'], hashlib.sha256(g.render(self.candidate)).hexdigest())

    def test_all_identities_signatures_cardinalities_eligibility_preserved(self):
        self.assertEqual(len(self.candidate['methods']), 2463)
        self.assertEqual(len(self.candidate['sourcePins']), 1599)
        changing = {'attributeLine', 'signatureLine', 'endLine', 'sourceProvenance'}
        provenance_changing = {'testAttributeLine', 'startOffset', 'endOffset', 'spanSHA256'}
        for old, new in zip(self.frozen['methods'], self.candidate['methods']):
            self.assertEqual({k: v for k, v in old.items() if k not in changing},
                             {k: v for k, v in new.items() if k not in changing})
            self.assertEqual({k: v for k, v in old['sourceProvenance'].items() if k not in provenance_changing},
                             {k: v for k, v in new['sourceProvenance'].items() if k not in provenance_changing})
        top_changing = {'methods', 'sourcePins', 'sourceCheckpoint', 'sourceTree', 'sourceDirectorySHA256', 'sourceDerivation'}
        self.assertEqual({k: v for k, v in self.frozen.items() if k not in top_changing},
                         {k: v for k, v in self.candidate.items() if k not in top_changing})

    def test_only_seven_pins_and_twenty_seven_bodies_change(self):
        self.assertEqual({k for k in self.frozen['sourcePins'] if self.frozen['sourcePins'][k] != self.candidate['sourcePins'][k]}, set(g.CANDIDATE_PINS))
        changed = {(old['source'], old['methodName']) for old, new in zip(self.frozen['methods'], self.candidate['methods'])
                   if old['sourceProvenance']['spanSHA256'] != new['sourceProvenance']['spanSHA256']}
        self.assertEqual(changed, g.CHANGED_METHODS)
        self.assertEqual(len(g.CANDIDATE_PINS), 7)
        self.assertEqual(self.report['unchangedMethodBodyCount'], 2436)
        self.assertEqual((self.report['deletedMethods'], self.report['addedMethods']), (0, 0))

    def test_every_current_method_span_is_bound(self):
        for method in self.candidate['methods']:
            text = (ROOT / method['source']).read_text()
            g.text_span(text, method)
            self.assertTrue('\n'.join(text.splitlines()[method['signatureLine'] - 1:]).lstrip().startswith(method['sourceProvenance']['signature']))

    def test_historical_provenance_is_separate_and_failure_not_relabelled(self):
        provenance = self.candidate['sourceDerivation']
        self.assertEqual(provenance['historicalCatalogProvenance'], {k: self.frozen[k] for k in ('sourceCheckpoint', 'sourceTree', 'sourceDirectorySHA256')})
        self.assertEqual(provenance['parentCommit'], g.PARENT_COMMIT)
        self.assertNotIn('candidateCommit', provenance)
        self.assertNotIn('candidateTree', provenance)
        self.assertEqual(self.report['historicalFullOrdinaryRun']['errors'], 290)
        self.assertFalse(self.report['historicalFullOrdinaryRun']['qualified'])
        self.assertFalse(self.report['fullSuiteQualified'])
        self.assertEqual(self.report['candidateExecution'], 'not run')
        self.assertEqual(self.report['historicalFirstMigrationRun'], {
            'runId': 41950562552, 'head': '4209233576c2100e0d5dc625308f49b76d500169', 'complete': True,
            'passed': 5, 'nonpassing': 1, 'qualified': False,
            'case4': {'measuredBytes': 64, 'comparatorBytes': 56, 'shapeGuardsPassed': True}})

    def test_historical_packing_identity_is_fixed_and_not_candidate_execution(self):
        self.assertEqual(self.report['historicalPackingRun'], {
            'runId': 41952557024, 'head': '3f2f1d03da65844fba6f367811545f195ccf5206',
            'complete': True, 'passed': 8, 'nonpassing': 0, 'qualified': True, 'fullSuiteQualified': False})
        self.assertEqual(self.report['candidateExecution'], 'not run')
        self.assertFalse(self.report['fullSuiteQualified'])

    def test_frozen_candidate_or_nonfixture_pin_mutation_rejected(self):
        original = Path.read_bytes
        targets = [ROOT / g.BASELINE / 'unit-census.json', ROOT / g.PROTOCOL,
                   ROOT / g.BASELINE / (g.PROTOCOL + '.txt'), ROOT / g.OWNED_FRAME,
                   ROOT / g.BASELINE / (g.OWNED_FRAME + '.txt'), ROOT / g.STACK,
                   ROOT / g.BASELINE / (g.STACK + '.txt'), ROOT / 'Directory.Build.props']
        for target in targets:
            def changed(path, target=target):
                value = original(path)
                return value + b'\n' if path == target else value
            with self.subTest(path=target.name), patch.object(Path, 'read_bytes', changed):
                with self.assertRaises(ValueError): g.generate(ROOT)

    def test_only_ownedframe_product_change_is_bound_by_original_preimage(self):
        original = (ROOT / g.BASELINE / (g.OWNED_FRAME + '.txt')).read_bytes()
        self.assertEqual(g.sha(original), self.frozen['sourcePins'][g.OWNED_FRAME])
        self.assertEqual(g.tree_id(ROOT / 'src', {g.OWNED_FRAME.removeprefix('src/'): original}), g.BASE_PRODUCT_TREE)
        self.assertNotEqual(g.tree_id(ROOT / 'src'), g.BASE_PRODUCT_TREE)
        self.assertEqual(g.tree_id(ROOT / 'src'), self.report['candidateProductTree'])
        self.assertNotIn('unchangedProductTree', self.candidate['sourceDerivation'])
        self.assertEqual(self.candidate['sourceDerivation']['frozenProductTree'], g.BASE_PRODUCT_TREE)

    def test_product_and_other_test_tree_changes_rejected(self):
        original = g.tree_id
        for name in ('src', 'test'):
            def changed(path, replacements=None, prefix='', name=name):
                if path == ROOT / name: return '0' * 40
                return original(path, replacements, prefix)
            with self.subTest(tree=name), patch.object(g, 'tree_id', changed):
                with self.assertRaises(ValueError): g.generate(ROOT)

    def test_changed_unchanged_method_slice_and_ambiguous_duplicate_rejected(self):
        method = next(m for m in self.frozen['methods'] if m['source'] == g.PROTOCOL and m['methodName'] == 'RawFrameWriterAndTokenShouldRemainInternalImplementationDetails')
        old = (ROOT / g.BASELINE / (g.PROTOCOL + '.txt')).read_text(); new = (ROOT / g.PROTOCOL).read_text()
        before = g.text_span(old, method)
        for changed in (new.replace(before, before.replace('IsFalse()', 'IsTrue()', 1)), new + '\n' + before):
            with self.assertRaises(ValueError): g.relocate(old, changed, method)

    def test_parameterized_attribute_prefix_is_preserved_exactly(self):
        method = next(m for m in self.frozen['methods'] if m['source'] == g.STACK and m['expectedCases'] == 3)
        old = (ROOT / g.BASELINE / (g.STACK + '.txt')).read_text(); new = (ROOT / g.STACK).read_text()
        rebound, changed = g.relocate(old, new, method)
        self.assertTrue(changed)
        self.assertEqual(rebound['sourceProvenance']['attributes'], method['sourceProvenance']['attributes'])
        self.assertEqual(rebound['sourceProvenance']['arguments'], method['sourceProvenance']['arguments'])
        for altered in (new.replace('[Arguments(32)]', '[Arguments(64)]'), new.replace('    [Arguments(1024)]\n', '')):
            with self.assertRaises(ValueError): g.relocate(old, altered, method)


    def test_only_mutateheader_helper_delta_is_allowed_outside_test_bodies(self):
        paths = (g.PROTOCOL, g.NEGOTIATOR, g.COMPATIBILITY)
        old = {path: (ROOT / g.BASELINE / (path + '.txt')).read_text() for path in paths}
        new = {path: (ROOT / path).read_text() for path in paths}
        delta = g.helper_delta(old, new, self.frozen['methods'], self.candidate['methods'])
        self.assertEqual(delta, self.report['changedHelper'])
        self.assertEqual(delta['oldSpanSHA256'], '73d6787ce2f764786b689254fd01a1d764b1854ebe2454d80edeb19e615cfcec')
        self.assertEqual(delta['newSpanSHA256'], '37d028dab77d986a8faa7611d3db61bab2b0e0aaaeb65c8684df71daff30f46f')
        self.assertEqual(delta['newLines'], [837, 848])
        for path, before, after in ((g.PROTOCOL, 'ProtocolV2CancelReason.UserCancellation]);\n        if (length', 'ProtocolV2CancelReason.DeadlineExceeded]);\n        if (length'),
                                     (g.NEGOTIATOR, 'throw new Exception(message);', 'throw new Exception("changed");'),
                                     (g.COMPATIBILITY, 'throw new Exception(message);', 'throw new Exception("changed");')):
            changed = dict(new); self.assertIn(before, changed[path]); changed[path] = changed[path].replace(before, after)
            with self.assertRaises(ValueError): g.helper_delta(old, changed, self.frozen['methods'], self.candidate['methods'])

    def test_new_preimages_and_diagnostic_evidence_mutations_rejected(self):
        original = Path.read_bytes
        for name in (g.NEGOTIATOR, g.COMPATIBILITY):
            for target in (ROOT / name, ROOT / g.BASELINE / (name + '.txt')):
                def changed(path): return original(path) + (b'\n' if path == target else b'')
                with patch.object(Path, 'read_bytes', changed):
                    with self.assertRaises(ValueError): g.generate(ROOT)
        target = ROOT / g.BASELINE / 'parser-handshake-diagnostic-result.json'
        def changed(path): return original(path) + (b'\n' if path == target else b'')
        with patch.object(Path, 'read_bytes', changed):
            with self.assertRaises(ValueError): g.generate(ROOT)

    def test_current_product_matches_packed_parent_and_histories_remain_distinct(self):
        self.assertEqual(self.report['candidateProductTree'], g.PARENT_PRODUCT_TREE)
        self.assertEqual(len(self.report['affectedMethods']), 59)
        self.assertEqual(self.report['changedMethodBodyCount'], 27)
        self.assertEqual(self.report['historicalThirteenCaseRun']['runId'], 41954983558)
        self.assertEqual(self.report['historicalThirteenCaseRun']['passed'], 13)
        diagnostic = self.report['historicalParserHandshakeDiagnostic']
        self.assertEqual(diagnostic['runId'], 41957258310)
        self.assertEqual((diagnostic['passed'], diagnostic['nonpassing'], diagnostic['errorOccurrences']), (4, 7, 21))
        self.assertFalse(diagnostic['qualified'])
        self.assertEqual(diagnostic['evidenceSHA256'], g.HISTORICAL_DIAGNOSTIC_SHA256)


    def test_negotiator_parent_reconstruction_has_exact_ten_bodies_and_helper_region(self):
        delta=g.negotiation_delta(ROOT,self.frozen,self.candidate)
        self.assertEqual(delta,self.report['negotiatorDelta'])
        self.assertEqual({row['method'] for row in delta['changedBodiesAgainstParent']},g.NEW_NEGOTIATOR_METHODS)
        self.assertEqual(len(delta['changedBodiesAgainstParent']),10)
        self.assertEqual(delta['preservedPriorBodies'],['ClientOfferShouldContainOnlyPolicyAndAuthenticationInputs','ImplementedCapabilitiesShouldRemainExplicitAndProtocolRecognized'])
        self.assertEqual(delta['helperBlock']['changedBodiesUnchangedSignatures'],['CreateOffer','CreatePolicy'])
        self.assertEqual(delta['helperBlock']['newDeclarations'],['MandatoryCapabilities','TestAuthority','AssertMandatoryCapabilityCannotBeOmitted','CreateExactPolicy'])
        self.assertEqual(delta['helperBlock']['newLines'],[596,684])
        self.assertTrue(delta['allOtherHistoricalParentTestBytesReconstructWithLaterProtocolPreimages'])
        parent=(ROOT/g.NEGOTIATOR_HISTORY).read_bytes();overlays=g.protocol_parent_overlays(ROOT);overlays[g.NEGOTIATOR.removeprefix('test/')]=parent
        self.assertEqual(g.tree_id(ROOT/'test',overlays),g.NEGOTIATOR_PARENT_TEST_TREE)
        self.assertEqual(delta['laterProtocolPreimages'],g.PROTOCOL_PARENT_PINS)

    def test_unapproved_negotiator_helper_body_or_signature_is_rejected(self):
        old=(ROOT/g.BASELINE/(g.NEGOTIATOR+'.txt')).read_text();new=(ROOT/g.NEGOTIATOR).read_text()
        start,end,block=g.negotiation_helper_block(new,True)
        for changed in (block.replace('supported | MandatoryCapabilities,','supported,',1),
                        block.replace('private static ProtocolV2HandshakeRequest CreateOffer(','private static ProtocolV2HandshakeRequest RenamedOffer('),
                        block.replace('Protocol3BindingLimits.Default);','default);',1)):
            self.assertNotEqual(changed,block)
            with self.assertRaises(ValueError):g.normalized_negotiator_helpers(old,new[:start]+changed+new[end:])
        self.assertEqual(g.negotiation_helper_block(old,False)[2],g.negotiation_helper_block((ROOT/g.NEGOTIATOR_HISTORY).read_text(),False)[2])

    def test_pure_history_preimage_diagnostic_and_full_evidence_are_sealed(self):
        original=Path.read_bytes
        for target_name in g.PURE_HISTORY_PINS:
            target=ROOT/target_name
            def changed(path):return original(path)+(b'\n' if path==target else b'')
            with patch.object(Path,'read_bytes',changed):
                with self.assertRaises(ValueError):g.generate(ROOT)
        self.assertEqual(self.report['historicalCurrentFullRun']['errors'],272)
        self.assertEqual(self.report['historicalSixteenCaseDiagnostic']['errors'],9)
        self.assertFalse(self.report['historicalCurrentFullRun']['qualified'])
        self.assertFalse(self.report['historicalSixteenCaseDiagnostic']['qualified'])
        self.assertEqual(self.report['historicalTimedFailureResolution'],'unresolved-no-timed-source-change')

    def test_exact_four_protocol_bodies_and_two_complete_parent_files_reconstruct(self):
        delta=g.protocol_delta(ROOT,self.candidate)
        self.assertEqual(delta,self.report['protocolDelta'])
        self.assertEqual({(row['source'],row['method']) for row in delta['changedBodiesAgainstParent']},g.NEW_PROTOCOL_METHODS)
        self.assertEqual(len(delta['methodRecordsInTwoFiles']),30)
        self.assertEqual(delta['allOtherParentMethodsByteIdentical'],2459)
        self.assertTrue(delta['nonMethodBytesUnchanged']);self.assertTrue(delta['allOtherParentTestBytesUnchanged'])
        self.assertEqual(g.tree_id(ROOT/'test',g.protocol_parent_overlays(ROOT)),g.PARENT_TEST_TREE)
        self.assertEqual(g.tree_id(ROOT/'src'),g.PARENT_PRODUCT_TREE)

    def test_protocol_parent_catalog_preimages_and_history_are_sealed(self):
        original=Path.read_bytes
        for name in g.PROTOCOL_HISTORY_PINS:
            target=ROOT/name
            def changed(path,target=target):return original(path)+(b'\n' if path==target else b'')
            with patch.object(Path,'read_bytes',changed):
                with self.assertRaises(ValueError):g.generate(ROOT)
        self.assertEqual(self.report['historicalLatestFullRun']['errors'],265)
        self.assertEqual((self.report['historicalTwentyOneCaseDiagnostic']['passed'],self.report['historicalTwentyOneCaseDiagnostic']['errors']),(17,4))
        self.assertFalse(self.report['historicalTwentyOneCaseDiagnostic']['qualified'])
        self.assertEqual(self.report['parentCommit'],'0de69b451c744182f6962d253a1749899fdc29af')

    def test_protocol_parent_scope_rejects_unapproved_helper_or_green_method_bytes(self):
        original=Path.read_text;target=ROOT/g.PROTOCOL
        for before,after in (('private static byte[] MutateHeader(', 'private static byte[] ForbiddenHelper('),
                             ('RawFrameWriterAndTokenShouldRemainInternalImplementationDetails()', 'ForbiddenMethod()')):
            actual=original(target);self.assertIn(before,actual)
            def changed(path,*args,**kwargs):
                value=original(path,*args,**kwargs)
                return value.replace(before,after,1) if path==target else value
            with patch.object(Path,'read_text',changed):
                with self.assertRaises(ValueError):g.protocol_delta(ROOT,self.candidate)

    def test_new_preference_preimage_is_original_and_only_two_pins_change_from_parent(self):
        self.assertEqual(g.sha((ROOT/g.BASELINE/(g.PREFERENCE+'.txt')).read_bytes()),self.frozen['sourcePins'][g.PREFERENCE])
        parent=json.loads((ROOT/'eng/protocol70-history/parent-unit-census.json').read_bytes())
        self.assertEqual({path for path in parent['sourcePins'] if parent['sourcePins'][path]!=self.candidate['sourcePins'][path]},set(g.PROTOCOL_PARENT_PINS))
        self.assertEqual(len(g.NEW_PROTOCOL_METHODS),4)

    def test_changed_target_signature_and_original_span_corruption_rejected(self):
        method = next(m for m in self.frozen['methods'] if (m['source'], m['methodName']) in g.CHANGED_METHODS)
        old = (ROOT / g.BASELINE / (method['source'] + '.txt')).read_text(); new = (ROOT / method['source']).read_text()
        with self.assertRaises(ValueError): g.relocate(old, new.replace(method['sourceProvenance']['signature'], 'public void Renamed()'), method)
        bad = copy.deepcopy(method); bad['sourceProvenance']['endOffset'] -= 1
        with self.assertRaises(ValueError): g.relocate(old, new, bad)


if __name__ == '__main__': unittest.main()
