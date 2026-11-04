"""Synthetic validator tests. No real benchmark or compatibility evidence."""

import contextlib
import copy
import io
import json
import tempfile
import unittest
from pathlib import Path

import validator as v


OLD_SOURCE = "a" * 40
CANDIDATE = "b" * 40


def fixture():
    return {
        "schemaVersion": 1,
        "targetSourceCommit": v.TARGET_SOURCE_COMMIT,
        "directions": [
            {
                "issueId": issue,
                "decision": "open",
                "sourceCommit": v.TARGET_SOURCE_COMMIT,
                "candidateCommit": None,
                "axes": {
                    axis: {
                        "state": "not-run",
                        "assessment": "Synthetic fixture: no observation asserted.",
                        "currentPass": False,
                        "evidence": [],
                    } for axis in v.AXES
                },
            } for issue in sorted(v.ISSUE_IDS)
        ],
    }


def evidence(scope="current", source=v.TARGET_SOURCE_COMMIT):
    item = {
        "url": "https://example.invalid/synthetic-evidence",
        "scope": scope,
        "currentEvidence": scope == "current",
    }
    if source is not None:
        item["sourceCommit"] = source
    return item


class ValidatorTests(unittest.TestCase):
    def setUp(self):
        self.doc = fixture()
        self.row = self.doc["directions"][0]
        self.axis = self.row["axes"]["jit"]

    def assert_error(self, code):
        self.assertIn(code, v.validate(self.doc))

    def test_complete_open_ledger_is_structurally_valid(self):
        self.assertEqual(v.validate(self.doc), [])

    def test_candidate_and_source_are_separate_identities(self):
        self.row["candidateCommit"] = CANDIDATE
        self.assertEqual(v.validate(self.doc), [])

    def test_all_decisions_are_allowed_without_inventing_acceptance_gates(self):
        for decision in v.DECISIONS:
            self.row["decision"] = decision
            self.assertEqual(v.validate(self.doc), [])

    def test_duplicate_direction_rejected_even_with_six_rows(self):
        self.doc["directions"][5] = copy.deepcopy(self.row)
        self.assert_error("duplicate_issue")
        self.assert_error("issue_set")

    def test_missing_direction(self):
        self.doc["directions"].pop()
        self.assert_error("direction_count")
        self.assert_error("issue_set")

    def test_wrong_issue_id_or_boolean_rejected(self):
        for wrong in (True, 728, "729", 729.0, [], {}):
            self.row["issueId"] = wrong
            self.assert_error("issue_id")

    def test_each_missing_axis_is_rejected(self):
        for name in v.AXES:
            doc = fixture()
            del doc["directions"][0]["axes"][name]
            self.assertIn("axes_keys", v.validate(doc))

    def test_extra_compatibility_gate_is_not_silently_accepted(self):
        self.row["axes"]["old203AbiGate"] = copy.deepcopy(self.axis)
        self.assert_error("axes_keys")

    def test_unknown_all_pass_flags_rejected(self):
        for flag in ("allPass", "performancePassed", "compatibilityPassed"):
            doc = fixture()
            doc[flag] = True
            self.assertIn("root_keys", v.validate(doc))

    def test_malformed_commit_identities(self):
        for key, error in (("sourceCommit", "source_commit"),
                           ("candidateCommit", "candidate_commit")):
            for value in ("abc123", "g" * 40, "a" * 39, 123, True, []):
                self.row[key] = value
                self.assert_error(error)
            self.row[key] = v.TARGET_SOURCE_COMMIT

    def test_target_cannot_be_rewritten_to_old_source(self):
        self.doc["targetSourceCommit"] = OLD_SOURCE
        self.assert_error("target_mismatch")

    def test_boolean_schema_version_rejected(self):
        self.doc["schemaVersion"] = True
        self.assert_error("schema_version")

    def test_missing_required_candidate_key_rejected(self):
        del self.row["candidateCommit"]
        self.assert_error("row_keys")

    def test_invalid_enums_or_non_scalar_enums_do_not_crash(self):
        for value in (True, [], {}, "passed"):
            self.row["decision"] = value
            self.axis["state"] = value
            self.assert_error("decision")
            self.assert_error("assessment_state")

    def test_assessment_required_even_for_not_applicable(self):
        self.axis["state"] = "not-applicable"
        self.axis["assessment"] = " "
        self.assert_error("assessment_text")

    def test_observation_requires_evidence(self):
        for state in ("observed", "partial"):
            self.axis["state"] = state
            self.assert_error("observation_without_evidence")

    def test_current_pass_flag_requires_real_boolean(self):
        for value in (1, 0, "true", "false", None, [], {}):
            self.axis["currentPass"] = value
            self.assert_error("current_pass_boolean")

    def test_current_evidence_flag_requires_real_boolean(self):
        entry = evidence()
        self.axis["evidence"] = [entry]
        for value in (1, 0, "true", "false", None, [], {}):
            entry["currentEvidence"] = value
            self.assert_error("current_evidence_boolean")

    def test_evidence_url_syntax(self):
        entry = evidence()
        self.axis["evidence"] = [entry]
        for url in (True, 2, None, [], "", "http://example.invalid", "file:///tmp/a",
                    "javascript:alert(1)", "https:///no-host", "https://u:p@example.invalid",
                    "https://example.invalid:bad", "https://[broken", "https://a/b\nc",
                    "https://a/has space", "https://a\\b"):
            entry["url"] = url
            self.assert_error("evidence_url")

    def test_current_evidence_requires_exact_target_commit(self):
        for source in (OLD_SOURCE, None, "short"):
            self.axis["evidence"] = [evidence(source=source)]
            self.assert_error("current_evidence_source")

    def test_historical_context_can_remain_without_current_pass(self):
        self.axis.update(state="observed", evidence=[evidence("historical", OLD_SOURCE)])
        self.assertEqual(v.validate(self.doc), [])

    def test_context_may_omit_commit_but_not_claim_current(self):
        self.axis["evidence"] = [evidence("context", None)]
        self.assertEqual(v.validate(self.doc), [])
        self.axis["evidence"][0]["currentEvidence"] = True
        self.assert_error("current_evidence_scope")
        self.assert_error("current_evidence_source")

    def test_historical_measurement_cannot_be_current_evidence(self):
        entry = evidence("historical", OLD_SOURCE)
        entry["currentEvidence"] = True
        self.axis["evidence"] = [entry]
        self.assert_error("current_evidence_scope")
        self.assert_error("current_evidence_source")

    def test_current_scope_requires_current_flag(self):
        entry = evidence()
        entry["currentEvidence"] = False
        self.axis["evidence"] = [entry]
        self.assert_error("current_evidence_scope")

    def test_historical_evidence_alone_cannot_support_current_pass(self):
        self.axis.update(state="observed", currentPass=True,
                         evidence=[evidence("historical", OLD_SOURCE)])
        self.assert_error("current_pass_without_current_evidence")

    def test_current_pass_requires_observed_state(self):
        self.axis.update(currentPass=True, evidence=[evidence()])
        for state in ("not-run", "partial", "not-applicable"):
            self.axis["state"] = state
            self.assert_error("current_pass_state")

    def test_historical_row_cannot_claim_current_pass(self):
        self.row["sourceCommit"] = OLD_SOURCE
        self.axis.update(state="observed", currentPass=True, evidence=[evidence()])
        self.assert_error("current_pass_source")

    def test_valid_current_labels_remain_unverified(self):
        self.axis.update(state="observed", currentPass=True, evidence=[evidence()])
        self.assertEqual(v.validate(self.doc), [])
        output = v.result(v.validate(self.doc))
        self.assertFalse(output["semanticTruthVerified"])
        self.assertFalse(output["evidenceContentsVerified"])
        self.assertNotIn("allPass", output)

    def test_complete_fabricated_claims_never_produce_truth_or_all_pass(self):
        # Deliberately false, complete claims demonstrate the limit of a shape check.
        # Rejecting them based on their prose would falsely imply semantic review.
        for row in self.doc["directions"]:
            row["decision"] = "qualified-go"
            row["candidateCommit"] = CANDIDATE
            for axis in row["axes"].values():
                axis.update(state="observed", currentPass=True,
                            assessment="SYNTHETIC LIE: all performance and ABI verified.",
                            evidence=[evidence()])
        self.assertEqual(v.validate(self.doc), [])
        code, output, raw = self.run_cli(json.dumps(self.doc))
        self.assertEqual(code, 0)
        self.assertEqual(output, {
            "structurallyValid": True, "semanticTruthVerified": False,
            "evidenceContentsVerified": False, "errors": [],
        })
        self.assertNotIn("SYNTHETIC", raw)
        self.assertNotIn("allPass", raw)
        self.assertNotIn("qualified-go", raw)

    def test_arbitrary_json_shapes_return_errors_without_exceptions(self):
        for value in (None, True, 2, "x", [], [1]):
            self.assertEqual(v.validate(value), ["root_shape"])
        for field in ("directions",):
            self.doc[field] = None
            self.assert_error("directions_shape")

    @staticmethod
    def run_cli(text):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as directory:
            path = Path(directory) / "secret-input-name.json"
            path.write_text(text, encoding="utf-8")
            stream = io.StringIO()
            with contextlib.redirect_stdout(stream):
                code = v.main([str(path)])
            raw = stream.getvalue()
            return code, json.loads(raw), raw

    def test_cli_malformed_json_does_not_echo_input_or_exception(self):
        code, output, raw = self.run_cli('{"secret-claim":')
        self.assertEqual(code, 1)
        self.assertEqual(output["errors"], ["malformed_json"])
        self.assertNotIn("secret", raw)

    def test_cli_rejects_duplicate_json_keys(self):
        code, output, _ = self.run_cli('{"schemaVersion":1,"schemaVersion":1}')
        self.assertEqual(code, 1)
        self.assertEqual(output["errors"], ["duplicate_json_key"])

    def test_cli_rejects_nonfinite_numbers(self):
        for value in ("NaN", "Infinity", "-Infinity"):
            code, output, _ = self.run_cli('{"x":' + value + '}')
            self.assertEqual(code, 1)
            self.assertEqual(output["errors"], ["malformed_json"])

    def test_cli_duplicate_rows_return_nonzero(self):
        self.doc["directions"][1] = copy.deepcopy(self.row)
        code, output, _ = self.run_cli(json.dumps(self.doc))
        self.assertEqual(code, 1)
        self.assertIn("duplicate_issue", output["errors"])

    def test_cli_missing_malformed_and_historical_mislabels_return_nonzero(self):
        cases = []
        missing = fixture()
        del missing["directions"][0]["axes"]["codeSize"]
        cases.append((missing, "axes_keys"))
        malformed = fixture()
        malformed["directions"][0]["sourceCommit"] = "short"
        cases.append((malformed, "source_commit"))
        mislabeled = fixture()
        mislabeled["directions"][0]["axes"]["jit"]["evidence"] = [
            evidence(source=OLD_SOURCE)
        ]
        cases.append((mislabeled, "current_evidence_source"))
        flag = fixture()
        flag["directions"][0]["axes"]["jit"]["currentPass"] = "false"
        cases.append((flag, "current_pass_boolean"))
        for doc, error in cases:
            with self.subTest(error=error):
                code, output, _ = self.run_cli(json.dumps(doc))
                self.assertEqual(code, 1)
                self.assertFalse(output["structurallyValid"])
                self.assertIn(error, output["errors"])

    def test_cli_oversized_input_is_bounded(self):
        code, output, _ = self.run_cli(" " * (v.MAX_INPUT_BYTES + 1))
        self.assertEqual(code, 1)
        self.assertEqual(output["errors"], ["input_too_large"])

    def test_field_shape_mutations_never_escape_as_exceptions(self):
        bad_values = (None, True, 1, "", [], {}, [None], {"unexpected": []})
        paths = (
            ("schemaVersion",), ("targetSourceCommit",), ("directions",),
            ("directions", 0), ("directions", 0, "issueId"),
            ("directions", 0, "decision"), ("directions", 0, "sourceCommit"),
            ("directions", 0, "candidateCommit"), ("directions", 0, "axes"),
            ("directions", 0, "axes", "jit"),
            ("directions", 0, "axes", "jit", "state"),
            ("directions", 0, "axes", "jit", "assessment"),
            ("directions", 0, "axes", "jit", "currentPass"),
            ("directions", 0, "axes", "jit", "evidence"),
            ("directions", 0, "axes", "jit", "evidence", 0),
            ("directions", 0, "axes", "jit", "evidence", 0, "scope"),
            ("directions", 0, "axes", "jit", "evidence", 0, "url"),
            ("directions", 0, "axes", "jit", "evidence", 0, "currentEvidence"),
            ("directions", 0, "axes", "jit", "evidence", 0, "sourceCommit"),
        )
        for path in paths:
            for value in bad_values:
                doc = fixture()
                doc["directions"][0]["axes"]["jit"]["evidence"] = [evidence()]
                target = doc
                for part in path[:-1]:
                    target = target[part]
                target[path[-1]] = value
                with self.subTest(path=path, value=value):
                    self.assertIsInstance(v.validate(doc), list)

    def test_cli_usage_and_unreadable_do_not_echo_paths(self):
        for argv, expected_code, error in (([], 2, "usage"),
                (["one.json", "extra-arg"], 2, "usage"),
                ([str(Path(__file__).parent / "does-not-exist-secret")], 1, "input_unreadable")):
            stream = io.StringIO()
            with contextlib.redirect_stdout(stream):
                code = v.main(argv)
            self.assertEqual(code, expected_code)
            self.assertEqual(json.loads(stream.getvalue())["errors"], [error])
            self.assertNotIn("secret", stream.getvalue())


if __name__ == "__main__":
    unittest.main()
