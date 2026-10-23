"""Real MSBuild/Roslyn fixtures and coverage counterfactuals for the inventory tool."""

import importlib.util
import hashlib
import json
import re
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("run_api_inventory", ROOT / "eng/run-api-inventory.py")
INVENTORY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(INVENTORY)


def read_rows(path):
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]


class TargetCoverageTests(unittest.TestCase):
    def test_untracked_source_edits_change_provenance(self):
        with tempfile.TemporaryDirectory(prefix="inventory-git-") as directory:
            root = Path(directory)
            subprocess.run(["git", "init", "--quiet", str(root)], check=True)
            source = root / "New.cs"
            source.write_text("internal class Before;", encoding="utf-8")
            before = INVENTORY.git_provenance(root)
            source.write_text("public class After;", encoding="utf-8")
            after = INVENTORY.git_provenance(root)
            self.assertEqual(before["status"], after["status"])
            self.assertNotEqual(before["untrackedFilesSha256"], after["untrackedFilesSha256"])

    def test_missing_failed_duplicate_and_unexpected_targets_fail(self):
        expected = [{"project": "P.csproj", "framework": framework} for framework in ("net10.0", "net10.0-windows")]
        good = [{**target, "complete": True} for target in expected]
        self.assertEqual(INVENTORY.coverage_errors(expected, good), [])
        for actual in (good[:1], good + good[:1], [good[0], {**good[1], "complete": False}],
                       good + [{"project": "Other.csproj", "framework": "net10.0", "complete": True}]):
            with self.subTest(actual=actual):
                self.assertNotEqual(INVENTORY.coverage_errors(expected, actual), [])


class LoggerGeneratedComparisonTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        artifacts = ROOT / "artifacts/api-inventory-fixtures"
        artifacts.mkdir(parents=True, exist_ok=True)
        cls.directory = Path(tempfile.mkdtemp(prefix="logger-", dir=artifacts))
        cls.source = cls.directory / "source"
        shutil.copytree(ROOT / "eng/api-inventory/fixtures", cls.source)
        shutil.copy2(ROOT / "global.json", cls.source / "global.json")
        cls.report = cls.directory / "report"
        result = subprocess.run([sys.executable, str(ROOT / "eng/run-api-inventory.py"), "--root", str(cls.source),
                                 "--coverage-config", str(cls.source / "logger-scope.json"), "--output", str(cls.report)],
                                cwd=ROOT, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=300)
        if result.returncode:
            raise AssertionError(f"Real LoggerMessage fixture failed: {result.stdout} {cls.report / 'coverage.json'}")
        cls.target_path = next(cls.report.glob("targets/*/target.json"))
        cls.target = json.loads(cls.target_path.read_text(encoding="utf-8"))
        analyzer = next(item for item in cls.target["analyzers"] if item["identity"].startswith("Microsoft.Extensions.Logging.Generators,"))
        cls.analyzer = (cls.source / analyzer["path"]).resolve()
        cls.logical_path = "generated/Microsoft.Extensions.Logging.Generators/Microsoft.Extensions.Logging.Generators.LoggerMessageGenerator/LoggerMessage.g.cs"
        original = cls.target_path.parent / ("build-" + cls.logical_path)
        cls.compiler = original.read_bytes().decode("utf-8-sig")
        # Independently split the two real emitted namespace blocks. No comparer
        # or canonicalizer output is used to construct the expected permutation.
        parts = cls.compiler.split("namespace Inventory.LogProbe")
        if len(parts) != 3:
            raise AssertionError("Expected two real LoggerMessage namespace blocks")
        cls.prefix = parts[0]
        cls.blocks, tails = [], []
        for part in parts[1:]:
            block = "namespace Inventory.LogProbe" + part
            end = block.rfind("}") + 1
            cls.blocks.append(block[:end])
            tails.append(block[end:])
        cls.separator, cls.suffix = tails
        cls.reordered = cls.prefix + cls.blocks[1] + cls.separator + cls.blocks[0] + cls.suffix
        print(f"Real LoggerMessage comparison evidence retained at {cls.directory}", flush=True)

    def compare(self, compiler, workspace, logical_path=None):
        first, second = self.directory / "probe-compiler.cs", self.directory / "probe-workspace.cs"
        first.write_bytes(compiler.encode("utf-8"))
        second.write_bytes(workspace.encode("utf-8"))
        result = subprocess.run(["dotnet", str(INVENTORY.TOOL), "--compare-generated", str(self.analyzer),
                                 logical_path or self.logical_path, str(first), str(second)],
                                cwd=ROOT, text=True, capture_output=True, timeout=30)
        report = json.loads(result.stdout)
        self.assertEqual(result.returncode, 0 if report["equivalent"] else 1, result.stderr)
        return report

    def pair(self, transform):
        blocks = [transform(block) for block in self.blocks]
        return (self.prefix + blocks[0] + self.separator + blocks[1] + self.suffix,
                self.prefix + blocks[1] + self.separator + blocks[0] + self.suffix)

    def test_real_editorbrowsable_blocks_allow_only_whole_class_permutation(self):
        self.assertIn("global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never)", self.compiler)
        result = self.compare(self.compiler, self.reordered)
        self.assertTrue(result["equivalent"])
        self.assertEqual(result["matchKind"], "logger-independent-namespace-order")
        self.assertNotEqual(result["compilerSha256"], result["workspaceSha256"])
        self.assertEqual(result["compilerSha256"], hashlib.sha256(self.compiler.encode()).hexdigest())
        self.assertEqual(result["generatorBinding"]["assemblySha256"], hashlib.sha256(self.analyzer.read_bytes()).hexdigest())
        self.assertEqual(set(result["compilerTypeOrder"]), {"Inventory.LogProbe.First`0", "Inventory.LogProbe.Second`0"})
        self.assertEqual(result["compilerTypeOrder"], list(reversed(result["workspaceTypeOrder"])))
        self.assertEqual(self.compare(self.compiler, self.compiler)["matchKind"], "exact")

    def test_changed_body_member_order_or_header_rejects_permutation(self):
        fields = re.findall(r"(?m)^([ \t]*(?:public|private|internal) readonly [^\r\n]+;)\r?$", self.reordered)
        self.assertGreaterEqual(len(fields), 2)
        swapped_fields = self.reordered.replace(fields[0], "FIELD_ORDER_SENTINEL", 1).replace(fields[1], fields[0], 1).replace("FIELD_ORDER_SENTINEL", fields[1], 1)
        changed_body = self.reordered.replace("new global::Microsoft.Extensions.Logging.EventId(1", "new global::Microsoft.Extensions.Logging.EventId(999", 1)
        self.assertNotEqual(changed_body, self.reordered)
        for changed in (swapped_fields, changed_body, "// different header\n" + self.reordered):
            with self.subTest(change=changed[:40]):
                self.assertFalse(self.compare(self.compiler, changed)["equivalent"])

    def test_same_type_partial_blocks_and_wrong_generator_hint_rejects_permutation(self):
        compiler, workspace = self.pair(lambda block: block.replace("partial class Second", "partial class First"))
        self.assertIn("Duplicate type identity", self.compare(compiler, workspace)["rejection"])
        for path in (self.logical_path.replace("LoggerMessage.g.cs", "Other.g.cs"),
                     self.logical_path.replace("Microsoft.Extensions.Logging.Generators.LoggerMessageGenerator", "Unrelated.Generator")):
            with self.subTest(path=path):
                self.assertFalse(self.compare(self.compiler, self.reordered, path)["equivalent"])

    def test_other_analyzer_cannot_borrow_logger_ordering_exception(self):
        first, second = self.directory / "wrong-binding-compiler.cs", self.directory / "wrong-binding-workspace.cs"
        first.write_bytes(self.compiler.encode())
        second.write_bytes(self.reordered.encode())
        result = subprocess.run(["dotnet", str(INVENTORY.TOOL), "--compare-generated", str(INVENTORY.TOOL),
                                 self.logical_path, str(first), str(second)], cwd=ROOT, text=True, capture_output=True, timeout=30)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("TypeLoadException", result.stderr)

    def test_forbidden_attributes_directives_and_namespace_imports_reject_even_when_identical(self):
        def add_member(block, member):
            namespace_end = block.rfind("}")
            class_end = block.rfind("}", 0, namespace_end)
            return block[:class_end] + member + block[class_end:]
        for attribute in ("global::System.Runtime.CompilerServices.ModuleInitializerAttribute", "MI", "global::System.ObsoleteAttribute"):
            with self.subTest(attribute=attribute):
                pair = self.pair(lambda block: add_member(block, f"\n[{attribute}]\ninternal static void ForbiddenInitializer() {{ }}\n"))
                result = self.compare(*pair)
                self.assertFalse(result["equivalent"])
                self.assertIn("Unsupported attribute", result["rejection"])
        pair = self.pair(lambda block: block.replace("EditorBrowsableState.Never", "EditorBrowsableState.Advanced"))
        self.assertFalse(self.compare(*pair)["equivalent"])
        for insertion in ("#pragma warning disable CS0168", "#nullable disable", "using Alias = global::System.Int32;", "extern alias Other;"):
            with self.subTest(insertion=insertion):
                pair = self.pair(lambda block: block.replace("{", "{\n" + insertion + "\n", 1))
                self.assertFalse(self.compare(*pair)["equivalent"])

    def test_generated_locations_bind_to_saved_workspace_text(self):
        docs = {document["path"]: document for document in self.target["documents"] if document["kind"] == "source-generated"}
        rows = read_rows(self.report / "symbols.jsonl")
        for path, document in docs.items():
            snapshot = self.target_path.parent / document["snapshotPath"]
            text = snapshot.read_bytes().decode("utf-8")
            self.assertEqual(document["sha256"], hashlib.sha256(text.encode()).hexdigest())
            declarations = [location for row in rows for location in row["declarations"] if location["path"] == path]
            self.assertGreater(len(declarations), 1)
            for location in declarations:
                fragment = text[location["start"]:location["start"] + location["length"]]
                self.assertEqual(location["sha256"], hashlib.sha256(fragment.encode()).hexdigest())


class RoslynInventoryFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        artifacts = ROOT / "artifacts/api-inventory-fixtures"
        artifacts.mkdir(parents=True, exist_ok=True)
        cls.directory = Path(tempfile.mkdtemp(prefix="run-", dir=artifacts))
        cls.source = cls.directory / "source"
        shutil.copytree(ROOT / "eng/api-inventory/fixtures", cls.source)
        shutil.copy2(ROOT / "global.json", cls.source / "global.json")
        cls.report = cls.directory / "success"
        result = cls.run_inventory(cls.report)
        if result.returncode:
            raise AssertionError(f"Fixture inventory failed: {result.stdout}\n{cls.report / 'coverage.json'}")
        cls.symbols = read_rows(cls.report / "symbols.jsonl")
        cls.relations = read_rows(cls.report / "relations.jsonl")
        print(f"Inventory fixture evidence retained at {cls.directory}", flush=True)

    @classmethod
    def run_inventory(cls, output):
        return subprocess.run([sys.executable, str(ROOT / "eng/run-api-inventory.py"), "--root", str(cls.source),
                               "--coverage-config", str(cls.source / "scope.json"), "--output", str(output)],
                              cwd=ROOT, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=600)

    def one(self, documentation_id, assembly="Fixture.Consumer", framework="net10.0"):
        matches = [row for row in self.symbols if row["semanticId"] == assembly + "|" + documentation_id and row["framework"] == framework]
        self.assertEqual(len(matches), 1, documentation_id)
        return matches[0]

    def test_nested_accessibility_and_protected_scopes_are_distinct(self):
        nested = self.one("T:Fixture.InternalContainer.PublicNested", "Fixture.Base")
        self.assertEqual((nested["declaredAccessibility"], nested["effectiveAccessibility"], nested["externallyVisible"]),
                         ("Public", "Internal", False))
        restricted = self.one("M:Fixture.Base.Restricted", "Fixture.Base")
        self.assertEqual(restricted["effectiveAccessibility"], "ProtectedAndInternal")
        self.assertFalse(restricted["externallyVisible"])
        family = self.one("M:Fixture.Base.FamilyOrAssembly", "Fixture.Base")
        self.assertEqual(family["effectiveAccessibility"], "ProtectedOrInternal")
        self.assertTrue(family["externallyVisible"])
        protected = self.one("T:Fixture.Base.ProtectedNested", "Fixture.Base")
        self.assertEqual(protected["effectiveAccessibility"], "Protected")

    def test_partial_implicit_and_accessor_rows_are_not_conflated(self):
        packet = self.one("T:Fixture.Packet")
        self.assertEqual(len(packet["declarations"]), 2)
        self.assertTrue(packet["details"]["isRecord"])
        self.assertEqual(packet["origin"], "explicit-source")
        implicit = [row for row in self.symbols if row["containingSymbol"] == "Fixture.Consumer|T:Fixture.Packet" and row["isImplicit"]]
        self.assertTrue(any(row["name"] == "Equals" and row["origin"] == "compiler-implicit" for row in implicit))
        setter = self.one("M:Fixture.Packet.set_Description(System.String)")
        self.assertEqual(setter["associatedSymbol"], "Fixture.Consumer|P:Fixture.Packet.Description")
        self.assertTrue(self.one("P:Fixture.Packet.Description")["details"]["isInitOnly"])

    def test_generated_internal_nested_and_signatures_are_in_compilation(self):
        generated = self.one("T:Fixture.Generated.InternalGenerated")
        self.assertEqual((generated["origin"], generated["effectiveAccessibility"]), ("source-generated", "Internal"))
        self.assertTrue(generated["hasGeneratedSource"])
        nested = self.one("T:Fixture.Generated.Outer.PublicNested")
        self.assertEqual(nested["effectiveAccessibility"], "Public")
        self.assertTrue(all(location["generated"] for location in nested["declarations"]))
        value = self.one("P:Fixture.Generated.Outer.PublicNested.Value")
        self.assertEqual(value["details"]["type"], "int")
        targets = [json.loads(path.read_text(encoding="utf-8")) for path in self.report.glob("targets/*Consumer*/target.json")]
        self.assertEqual(len(targets), 2)
        for target in targets:
            generated_docs = [doc for doc in target["documents"] if doc["kind"] == "source-generated"]
            self.assertEqual([(doc["hintName"], len(doc["sha256"])) for doc in generated_docs], [("Fixture.g.cs", 64)])

    def test_all_evaluated_tfms_and_conditional_members_are_recorded(self):
        coverage = json.loads((self.report / "coverage.json").read_text(encoding="utf-8"))
        self.assertEqual(coverage["scope"], "synthetic-fixture")
        self.assertEqual(len(coverage["expectedTargets"]), 4)
        self.assertTrue(coverage["complete"])
        self.one("M:Fixture.Derived.WindowsOnly", framework="net10.0-windows")
        self.assertFalse(any(row["semanticId"] == "Fixture.Consumer|M:Fixture.Derived.WindowsOnly" and row["framework"] == "net10.0" for row in self.symbols))
        self.assertLess(coverage["unionIdentityCountIgnoringTfm"], len(self.symbols))

    def test_cross_assembly_relations_arity_and_linked_sources_keep_identity(self):
        derived = self.one("T:Fixture.Derived")
        self.assertTrue(any(edge["source"] == derived["id"] and edge["kind"] == "base" and
                            edge["target"] == "Fixture.Base|T:Fixture.Base" for edge in self.relations))
        self.assertTrue(any(edge["kind"] == "internals-visible-to" and edge["target"] == "Fixture.Consumer" for edge in self.relations))
        self.assertTrue(any(edge["kind"] == "explicit-interface-implementation" and edge["target"] == "Fixture.Base|M:Fixture.IContract.Invoke" for edge in self.relations))
        plain = self.one("T:Fixture.SharedName", "Fixture.Base")
        generic = self.one("T:Fixture.SharedName`1", "Fixture.Base")
        self.assertNotEqual(plain["id"], generic["id"])
        linked = [self.one("T:Fixture.Shared.LinkedMarker", assembly) for assembly in ("Fixture.Base", "Fixture.Consumer")]
        self.assertNotEqual(linked[0]["semanticId"], linked[1]["semanticId"])
        self.assertEqual([row["declarations"][0]["path"] for row in linked], ["Shared.cs", "Shared.cs"])

    def test_signature_shapes_and_extension_blocks_are_preserved(self):
        types = self.one("T:Fixture.Signatures`1")
        self.assertTrue(types["details"]["typeParameters"][0]["hasReferenceTypeConstraint"])
        self.assertTrue(types["details"]["typeParameters"][0]["hasConstructorConstraint"])
        label = self.one("P:Fixture.Signatures`1.Label")
        self.assertTrue(label["details"]["isRequired"])
        callback = self.one("F:Fixture.Signatures`1.Callback")
        self.assertIn("delegate* unmanaged[Cdecl]", callback["details"]["type"])
        self.assertTrue(any(row["details"].get("isExtension") for row in self.symbols))

    def test_type_arguments_and_containers_are_not_inheritance_edges(self):
        derived = self.one("T:Fixture.NestedGenericDerived")
        edges = [edge for edge in self.relations if edge["source"] == derived["id"]]
        self.assertEqual({edge["target"] for edge in edges if edge["kind"] == "base"},
                         {"Fixture.Base|T:Fixture.Container.NestedBase`1"})
        self.assertEqual({edge["target"] for edge in edges if edge["kind"] == "interface"},
                         {"Fixture.Base|T:Fixture.Container.INestedContract`1"})
        for kind in ("base-type-dependency", "interface-type-dependency"):
            self.assertEqual({edge["target"] for edge in edges if edge["kind"] == kind},
                             {"Fixture.Base|T:Fixture.Container", "Fixture.Base|T:Fixture.TypeArgument"})

    def test_reports_are_deterministic_for_unchanged_inputs(self):
        repeated = self.directory / "repeated"
        result = self.run_inventory(repeated)
        self.assertEqual(result.returncode, 0, result.stdout)
        for filename in ("symbols.jsonl", "relations.jsonl", "coverage.json", "decisions.json", "inventory.md"):
            self.assertEqual((self.report / filename).read_bytes(), (repeated / filename).read_bytes(), filename)
        originals = {path.parent.name: path.read_bytes() for path in self.report.glob("targets/*/target.json")}
        self.assertEqual(originals, {path.parent.name: path.read_bytes() for path in repeated.glob("targets/*/target.json")})

    def test_z_generator_failure_is_nonzero_and_reported(self):
        mode = self.source / "src/Consumer/mode.txt"
        original = mode.read_bytes()
        try:
            mode.write_text("FAIL\n", encoding="utf-8")
            failed = self.directory / "generator-failure"
            result = self.run_inventory(failed)
            self.assertNotEqual(result.returncode, 0)
            coverage = json.loads((failed / "coverage.json").read_text(encoding="utf-8"))
            self.assertFalse(coverage["complete"])
            consumers = [target for target in coverage["actualTargets"] if target["project"] == "src/Consumer/Consumer.csproj"]
            self.assertEqual(len(consumers), 2)
            self.assertTrue(all(not target["complete"] for target in consumers))
            for target in consumers:
                log = (failed / target["directory"] / "build.log").read_text(encoding="utf-8")
                self.assertIn("CS8785", log)
                self.assertIn("Intentional inventory generator counterfactual", log)
            self.assertTrue((failed / "provenance.json").is_file())
        finally:
            mode.write_bytes(original)


if __name__ == "__main__":
    unittest.main()
