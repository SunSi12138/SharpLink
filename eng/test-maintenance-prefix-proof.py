#!/usr/bin/env python3
"""Conservative source review triggers for the no-queued-admission FIFO proof.

This does not execute a quiescence protocol or prove physical ownership by itself.
Do not regenerate hashes merely to make CI green: changes require re-auditing the
corresponding ownership argument in doc/maintenance-reader-prefix-proof.md.
"""

import hashlib
import json
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "eng/maintenance-prefix-proof.json"


def reviewed_bytes(entry):
    source = (ROOT / entry["path"]).read_bytes()
    start = entry.get("start")
    if start is None:
        return source
    text = source.decode("utf-8")
    if text.count(start) != 1:
        raise AssertionError(f"Expected exactly one method anchor: {start!r}")
    begin = text.index(start)
    indent = len(start) - len(start.lstrip())
    # These audited methods use the repository's ordinary brace indentation.
    # An ambiguous/change-of-format extraction fails closed via the digest.
    end = text.index("\n" + " " * indent + "}", begin)
    return text[begin:end + indent + 3].encode("utf-8")


class MaintenancePrefixProofTests(unittest.TestCase):
    def test_reviewed_ownership_sources_have_not_changed(self):
        manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
        self.assertEqual(manifest["version"], 1)
        self.assertEqual(len(manifest["reviewed_source"]), 20)
        for entry in manifest["reviewed_source"]:
            with self.subTest(purpose=entry["purpose"], path=entry["path"]):
                actual = hashlib.sha256(reviewed_bytes(entry)).hexdigest()
                self.assertEqual(
                    actual,
                    entry["sha256"],
                    "The no-queue reader-prefix ownership proof must be reviewed "
                    "before updating this source pin.",
                )

    def test_reader_dispatch_remains_inline_before_the_next_frame(self):
        source = (ROOT / "src/SharpLink.Server/SharpLinkServer.RequestLoop.cs").read_text()
        begin = source.index("case ProtocolV2FrameType.Request:")
        end = source.index("case ProtocolV2FrameType.Cancel:", begin)
        branch = source[begin:end]
        self.assertIn("_ = DispatchRequestAsync(", branch)
        self.assertNotIn("Task.Run", branch)
        self.assertNotIn("await ", branch)


if __name__ == "__main__":
    unittest.main()
