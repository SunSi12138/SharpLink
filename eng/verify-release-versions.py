#!/usr/bin/env python3
"""Enforce the reviewed version calibration against the published 1.1.1 boundary."""

import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parent.parent
manifest = json.loads((root / "eng/release-versions.json").read_text())
if manifest["schemaVersion"] != 1:
    raise ValueError("Unknown version-calibration manifest schema")
version = ET.parse(root / "Directory.Build.props").findtext(".//VersionPrefix")
if version != manifest["release"]:
    raise ValueError(f"Release version {version} needs an explicitly reviewed calibration manifest")

observed = {}
for entry in manifest["versions"]:
    matches = re.findall(entry["pattern"], (root / entry["path"]).read_text(encoding="utf-8-sig"))
    if len(matches) != 1 or int(matches[0]) != entry["current"]:
        raise ValueError(f"{entry['name']}: expected {entry['current']}, found {matches}")
    before, after, policy = entry["published"], entry["current"], entry["policy"]
    valid = {
        "increment": before is not None and after == before + 1,
        "unchanged": before is not None and after == before,
        "initial": before is None and after == 1,
        "floor": before is None and after == observed.get("Protocol minor"),
    }
    if not valid.get(policy, False):
        raise ValueError(f"Invalid release calibration for {entry['name']}: {policy}")
    observed[entry["name"]] = after
print(f"Verified {len(observed)} version declarations against released 1.1.1; no development-only increments.")
