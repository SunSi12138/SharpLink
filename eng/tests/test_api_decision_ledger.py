"""Focused counterexamples for exact decision coverage and evidence binding."""

import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('api_decision_ledger', ROOT / 'eng/verify-api-decision-ledger.py')
LEDGER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(LEDGER)


class DecisionLedgerTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        (self.root / 'evidence.cs').write_text('// inspected declaration\n')
        self.targets = {
            'P': {'project': 'src/P/P.csproj', 'framework': 'net10.0', 'assembly': 'P'},
            'G': {'project': 'src/G/G.csproj', 'framework': 'netstandard2.0', 'assembly': 'G'},
        }
        self.symbols = []

        def symbol(target, doc, kind, visible, nonsealed=False, parent=None):
            t = self.targets[target]
            semantic = target + '|' + doc
            identity = t['project'] + '@' + t['framework'] + '|' + semantic
            self.symbols.append({**t, 'id': identity, 'semanticId': semantic, 'kind': kind,
                                 'externallyVisible': visible, 'nonSealedClass': nonsealed,
                                 'containingSymbol': target + '|T:' + parent if parent else None})
            return identity

        self.base = symbol('P', 'T:N.Base', 'NamedType', True, True)
        self.method = symbol('P', 'M:N.Base.Read', 'Method', True, parent='N.Base')
        self.property = symbol('P', 'P:N.Base.Value', 'Property', True, parent='N.Base')
        self.getter = symbol('P', 'M:N.Base.get_Value', 'Method', True, parent='N.Base')
        self.derived = symbol('P', 'T:N.Derived', 'NamedType', False)
        self.hidden = symbol('P', 'M:N.Base.Secret', 'Method', False, parent='N.Base')
        self.internal = symbol('P', 'T:N.InternalBase', 'NamedType', False, True)
        self.internal_derived = symbol('P', 'T:N.InternalDerived', 'NamedType', False)
        self.generated = symbol('G', 'T:N.Generated', 'NamedType', True)
        self.generated_method = symbol('G', 'M:N.Generated.Factory', 'Method', True, parent='N.Generated')
        self.coverage = {
            'complete': True, 'failures': [], 'scope': 'production-declarations',
            'hostOs': 'linux', 'hostArchitecture': 'x86_64', 'configuration': 'Release',
            'expectedTargets': list(self.targets.values()),
            'actualTargets': [{**t, 'complete': True} for t in self.targets.values()],
            'symbolCountByTarget': {'src/P/P.csproj@net10.0': 8, 'src/G/G.csproj@netstandard2.0': 2},
        }
        self.provenance = {'source': {'commit': 'source-commit', 'tree': 'source-tree',
                                      'status': '', 'untrackedFilesSha256': {}}}
        self.decisions = {'decisions': [{'symbolId': identity, 'classification': 'needs-decision'} for identity in
                                      (self.base, self.method, self.property, self.getter, self.internal,
                                       self.generated, self.generated_method)]}
        self.relations = [
            {'source': self.derived, 'kind': 'base', 'target': 'P|T:N.Base'},
            {'source': self.internal_derived, 'kind': 'base', 'target': 'P|T:N.InternalBase'},
            # This is a generic/container dependency, NOT a subclass.
            {'source': self.generated, 'kind': 'base-type-dependency', 'target': 'P|T:N.Base'},
        ]

        def row(target, name, decision):
            return {'target': target, 'type': 'T:' + name, 'decision': decision, 'role': 'spi',
                    'rationale': 'External serialization contract' if target == 'G' else 'Existing base hierarchy',
                    'evidence': ['evidence.cs:1'], 'memberDecision': 'keep-public', 'memberExceptions': []}

        base = row('P', 'N.Base', 'keep-inheritable')
        base['directDerivedTypes'] = [self.derived]
        base['memberExceptions'] = [{'member': 'M:Read', 'decision': 'keep-public',
                                    'rationale': 'Standalone reader entry point', 'evidence': ['evidence.cs:1']}]
        internal = row('P', 'N.InternalBase', 'keep-inheritable')
        internal['directDerivedTypes'] = [self.internal_derived]
        self.ledger = {'schemaVersion': 1, 'status': 'proposal-not-approved', 'targets': self.targets,
                       'inventory': {'sourceCommit': 'source-commit', 'sourceTree': 'source-tree',
                                     'hostOs': 'linux', 'hostArchitecture': 'x86_64', 'configuration': 'Release',
                                     'declarations': 10, 'decisionIds': 7},
                       'roles': {'spi': 'Preserve public SPI and actual implementation inheritance'},
                       'types': [base, internal, row('G', 'N.Generated', 'keep-public')]}
        self.identities = {'schemaVersion': 1, 'types': [
            {'target': 'P', 'type': 'T:N.Base', 'members': ['M:Read', 'P:Value', 'M:get_Value'], 'exceptionMembers': ['M:Read']},
            {'target': 'P', 'type': 'T:N.InternalBase', 'members': []},
            {'target': 'G', 'type': 'T:N.Generated', 'members': ['M:Factory']},
        ]}

    def validate(self):
        return LEDGER.validate(self.ledger, self.identities, self.coverage, self.provenance,
                               self.symbols, self.decisions, self.relations, self.root)

    def test_exact_member_accessor_generated_and_internal_base_coverage(self):
        self.assertEqual(self.validate(), {
            'status': 'proposal-not-approved', 'targets': 2, 'declarations': 10, 'types': 3,
            'decisionIds': 7, 'memberExceptions': 1,
            'decisions': {'keep-inheritable': 2, 'keep-public': 5},
        })

    def test_missing_or_count_preserving_substituted_member_fails(self):
        for members in (['M:Read', 'P:Value'], ['M:Read', 'P:Value', 'M:Other']):
            with self.subTest(members=members):
                self.identities['types'][0]['members'] = members
                with self.assertRaises(LEDGER.LedgerError):
                    self.validate()

    def test_missing_type_and_duplicate_type_or_member_fail(self):
        original = copy.deepcopy(self.identities)
        variants = [original['types'][:-1], original['types'] + [original['types'][0]]]
        duplicate_member = copy.deepcopy(original['types'])
        duplicate_member[0]['members'].append('M:Read')
        variants.append(duplicate_member)
        for rows in variants:
            with self.subTest(rows=rows):
                self.identities['types'] = rows
                with self.assertRaises(LEDGER.LedgerError):
                    self.validate()

    def test_inventory_decisions_cannot_hide_a_required_symbol(self):
        self.decisions['decisions'] = [x for x in self.decisions['decisions'] if x['symbolId'] != self.getter]
        with self.assertRaisesRegex(LEDGER.LedgerError, 'needs-decision coverage'):
            self.validate()

    def test_unknown_invisible_and_wrong_container_members_fail(self):
        for member, message in [('M:Unknown', 'Unknown/non-required'), ('M:Secret', 'Unknown/non-required'),
                                ('=M:N.InternalBase.Missing', 'Unknown/non-required')]:
            with self.subTest(member=member):
                self.identities['types'][0]['members'][-1] = member
                with self.assertRaisesRegex(LEDGER.LedgerError, message):
                    self.validate()
        self.identities['types'][0]['members'][-1] = 'M:get_Value'
        self.identities['types'][1]['members'] = ['=M:N.Base.Read']
        # Process the wrong container first so duplicate detection cannot mask the contract.
        self.ledger['types'].reverse()
        with self.assertRaisesRegex(LEDGER.LedgerError, 'Wrong containing type'):
            self.validate()

    def test_full_id_escape_supports_nonstandard_roslyn_container_without_prefix_guessing(self):
        self.identities['types'][0]['members'][0] = '=M:N.ExtensionContainer.Read'
        self.identities['types'][0]['exceptionMembers'] = ['=M:N.ExtensionContainer.Read']
        self.ledger['types'][0]['memberExceptions'][0]['member'] = '=M:N.ExtensionContainer.Read'
        for s in self.symbols:
            if s['id'] == self.method:
                s['semanticId'] = 'P|M:N.ExtensionContainer.Read'
                s['id'] = self.method.replace('M:N.Base.Read', 'M:N.ExtensionContainer.Read')
        for d in self.decisions['decisions']:
            if d['symbolId'] == self.method:
                d['symbolId'] = self.method.replace('M:N.Base.Read', 'M:N.ExtensionContainer.Read')
        self.assertEqual(self.validate()['decisionIds'], 7)

    def test_dependency_edge_cannot_justify_inheritance(self):
        self.ledger['types'][0]['directDerivedTypes'].append(self.generated)
        with self.assertRaisesRegex(LEDGER.LedgerError, 'Direct inheritance evidence'):
            self.validate()
        self.ledger['types'][0]['directDerivedTypes'] = []
        self.relations[0]['kind'] = 'base-type-dependency'
        with self.assertRaisesRegex(LEDGER.LedgerError, 'No direct-derived evidence'):
            self.validate()

    def test_orphan_duplicate_and_unexplained_member_exceptions_fail(self):
        original = copy.deepcopy(self.ledger['types'][0]['memberExceptions'])
        for exceptions in ([], [{**original[0], 'member': 'M:Absent'}], original * 2,
                           [{**original[0], 'rationale': ''}], [{**original[0], 'evidence': []}]):
            with self.subTest(exceptions=exceptions):
                self.ledger['types'][0]['memberExceptions'] = exceptions
                with self.assertRaises(LEDGER.LedgerError):
                    self.validate()

    def test_incomplete_missing_duplicate_unexpected_targets_fail(self):
        original = copy.deepcopy(self.coverage)
        cases = []
        bad = copy.deepcopy(original); bad['complete'] = False; cases.append(bad)
        bad = copy.deepcopy(original); bad['actualTargets'] = bad['actualTargets'][:1]; cases.append(bad)
        bad = copy.deepcopy(original); bad['actualTargets'].append(bad['actualTargets'][0]); cases.append(bad)
        bad = copy.deepcopy(original); bad['actualTargets'][0]['complete'] = False; cases.append(bad)
        bad = copy.deepcopy(original); bad['actualTargets'][0]['framework'] = 'net11.0'; cases.append(bad)
        for case in cases:
            with self.subTest(coverage=case):
                self.coverage = case
                with self.assertRaises(LEDGER.LedgerError):
                    self.validate()

    def test_changed_source_tree_dirty_source_or_host_cannot_reuse_proposal(self):
        for field, value in [('tree', 'new-tree'), ('commit', 'other-commit'), ('status', ' M Source.cs'),
                             ('untrackedFilesSha256', {'New.cs': 'not-empty'})]:
            with self.subTest(field=field):
                old = self.provenance['source'][field]
                self.provenance['source'][field] = value
                with self.assertRaises(LEDGER.LedgerError):
                    self.validate()
                self.provenance['source'][field] = old
        self.coverage['hostOs'] = 'darwin'
        with self.assertRaisesRegex(LEDGER.LedgerError, 'profile differs'):
            self.validate()

    def test_invalid_rationale_evidence_role_or_approval_fails(self):
        row = self.ledger['types'][0]
        for field, value in [('rationale', ''), ('role', 'unknown'), ('evidence', ['missing.cs']),
                             ('evidence', ['evidence.cs:2']), ('evidence', ['../evidence.cs'])]:
            with self.subTest(field=field, value=value):
                old = row[field]; row[field] = value
                with self.assertRaises(LEDGER.LedgerError):
                    self.validate()
                row[field] = old
        self.ledger['status'] = 'approved'
        with self.assertRaisesRegex(LEDGER.LedgerError, 'cannot grant approval'):
            self.validate()

    def test_duplicate_json_keys_rejected_instead_of_silently_overridden(self):
        with self.assertRaisesRegex(LEDGER.LedgerError, 'Duplicate JSON key'):
            json.loads('{"decision":"needs-decision","decision":"keep-public"}',
                       object_pairs_hook=LEDGER.object_without_duplicates)

    def test_symbol_duplicates_inconsistent_identity_and_counts_fail(self):
        original = copy.deepcopy(self.symbols)
        self.symbols.append(self.symbols[0])
        with self.assertRaisesRegex(LEDGER.LedgerError, 'Duplicate inventory symbol'):
            self.validate()
        self.symbols = original
        self.symbols[0]['semanticId'] = 'P|T:Other'
        with self.assertRaisesRegex(LEDGER.LedgerError, 'Inconsistent symbol identity'):
            self.validate()
        self.symbols[0]['semanticId'] = 'P|T:N.Base'
        self.coverage['symbolCountByTarget']['src/P/P.csproj@net10.0'] = 7
        with self.assertRaisesRegex(LEDGER.LedgerError, 'Per-target declaration counts differ'):
            self.validate()


if __name__ == '__main__':
    unittest.main()
