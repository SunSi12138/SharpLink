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

    def test_only_four_pins_and_six_bodies_change(self):
        self.assertEqual({k for k in self.frozen['sourcePins'] if self.frozen['sourcePins'][k] != self.candidate['sourcePins'][k]}, set(g.CANDIDATE_PINS))
        changed = {(old['source'], old['methodName']) for old, new in zip(self.frozen['methods'], self.candidate['methods'])
                   if old['sourceProvenance']['spanSHA256'] != new['sourceProvenance']['spanSHA256']}
        self.assertEqual(changed, g.CHANGED_METHODS)
        self.assertEqual(len(g.CANDIDATE_PINS), 4)
        self.assertEqual(self.report['unchangedMethodBodyCount'], 2457)
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

    def test_changed_target_signature_and_original_span_corruption_rejected(self):
        method = next(m for m in self.frozen['methods'] if (m['source'], m['methodName']) in g.CHANGED_METHODS)
        old = (ROOT / g.BASELINE / (method['source'] + '.txt')).read_text(); new = (ROOT / method['source']).read_text()
        with self.assertRaises(ValueError): g.relocate(old, new.replace(method['sourceProvenance']['signature'], 'public void Renamed()'), method)
        bad = copy.deepcopy(method); bad['sourceProvenance']['endOffset'] -= 1
        with self.assertRaises(ValueError): g.relocate(old, new, bad)


if __name__ == '__main__': unittest.main()
