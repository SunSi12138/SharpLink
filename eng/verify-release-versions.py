#!/usr/bin/env python3
"""Enforce the reviewed version calibration against the reviewed published release boundary."""

import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

def verify_transition(entry, observed):
    before, after, policy = entry["published"], entry["current"], entry["policy"]
    drafts = entry.get("reservedDraftVersions", [])
    if not isinstance(drafts, list) or any(type(value) is not int for value in drafts):
        raise ValueError(f"{entry['name']}: reservedDraftVersions must be a list of integers")

    if policy in ("increment", "initial"):
        # Never reuse a wire/ABI identity assigned to an incompatible draft.
        # The only way to skip a value is to list every intervening draft.
        if policy == "increment":
            first = before + 1 if before is not None else None
        else:
            first = 1 if before is None else None
        valid = (first is not None and after == first + len(drafts)
                 and all(value == first + index for index, value in enumerate(drafts)))
    else:
        valid = not drafts and (
            (policy == "unchanged" and before is not None and after == before)
            or (policy == "floor" and before is None and after == observed.get("Protocol minor")))
    if not valid:
        raise ValueError(f"Invalid release calibration for {entry['name']}: {policy}")


def verify(root):
    manifest = json.loads((root / "eng/release-versions.json").read_text(encoding="utf-8"))
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
        verify_transition(entry, observed)
        observed[entry["name"]] = entry["current"]
    print(f"Verified {len(observed)} declarations against released {manifest['publishedBaseline']['version']} "
          "and the reviewed draft reservations.")


if __name__ == "__main__":
    verify(Path(__file__).resolve().parent.parent)
