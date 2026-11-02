"""Source-only consistency guard; not generated ABI/runtime acceptance evidence."""

from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
DOCS = (
    "migration.md",
    "unsafe-blit-3.0-migration.md",
    "generated-method-facts-3.0-migration.md",
    "rpc-channel-3.0-migration.md",
    "3.0-api-surface-audit-decisions.md",
)


class MigrationBoundaryDocsTests(unittest.TestCase):
    def test_current_boundary_matches_authoritative_manifest_constants(self):
        source = (ROOT / "src/SharpLink.Abstractions/SharpLinkGeneratedManifestVersions.cs").read_text()
        api = re.search(r"public const int Api = (\d+);", source).group(1)
        protocol = re.search(r"public const int Protocol = (\d+);", source).group(1)
        abi = re.search(r'public const string AbiIdentity = "([^"]+)";', source).group(1)
        expected = f"Current generated boundary: API **{api}**, ABI `{abi}`, Protocol **{protocol}**."
        for name in DOCS:
            with self.subTest(document=name):
                text = (ROOT / "doc" / name).read_text()
                blocks = re.findall(
                    r"<!-- current-generated-boundary:start -->\n(.*?)\n<!-- current-generated-boundary:end -->",
                    text, re.DOTALL,
                )
                self.assertEqual(blocks, [expected])

    def test_current_migration_separates_historical_protocol_and_abi(self):
        text = (ROOT / "doc/migration.md").read_text()
        current, historical = text.split("## 历史参考：1.1.1 → 2.0", 1)
        self.assertIn("IRpcContractChannel", current)
        self.assertIn("CreateProxy<TContract>", current)
        self.assertIn("PrepareAsync", current)
        self.assertNotIn("3.0 仍使用", current)
        self.assertNotIn("sharplink-3.0-api5-owner-bound-unsafe-blit-v1", current)
        self.assertIn("Generated ABI（API 4）", historical)
        self.assertIn("Protocol v2 minor", historical)


if __name__ == "__main__":
    unittest.main()
