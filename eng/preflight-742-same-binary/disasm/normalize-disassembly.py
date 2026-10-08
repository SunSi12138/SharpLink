#!/usr/bin/env python3
"""Compare the two selected NoPGO methods; normalize only identified addresses.

Usage: normalize-disassembly.py OFF.txt ON.txt OUTPUT_PREFIX
This is deliberately strict. New instruction differences fail; review any new
address form instead of extending the normalizer without checking its meaning.
"""
import hashlib
import json
import pathlib
import re
import sys

off_path, on_path, prefix = map(pathlib.Path, sys.argv[1:])
off = off_path.read_text().splitlines(keepends=True)
on = on_path.read_text().splitlines(keepends=True)
assert len(off) == len(on), "different disassembly line counts"
assert sum("; Assembly listing for method" in line for line in off) == 2
assert any("QuiescentCompletionDiagnostic:IsEnabled" in line for line in off)
assert any("StreamManager:CompleteStream" in line for line in off)
assert sum("; No PGO data" in line for line in off) == 2
changes = []
normalized = []
for i, (left, right) in enumerate(zip(off, on)):
    if left == right:
        normalized.append(left)
        continue
    # Explicit relocation of the diagnostic flag's storage.
    if "cmp      dword ptr [(reloc " in left:
        pattern = r"(?<=reloc )0x[0-9a-fA-F]+"
    # Runtime type handle used by the following exact-type comparison.
    elif (re.fullmatch(r"       mov      rsi, 0x[0-9a-fA-F]{12}\n", left)
          and off[i + 1] == on[i + 1] == "       cmp      qword ptr [rdi], rsi\n"):
        pattern = r"0x[0-9a-fA-F]{12}"
    # Address of a GC static handle, immediately dereferenced.
    elif (re.fullmatch(r"       mov      rdi, 0x[0-9a-fA-F]{12}\n", left)
          and off[i + 1] == on[i + 1] == "       mov      rdi, gword ptr [rdi]\n"):
        pattern = r"0x[0-9a-fA-F]{12}"
    else:
        raise AssertionError(f"non-address difference at line {i + 1}: {left!r} / {right!r}")
    nleft, nright = (re.sub(pattern, "<address>", line) for line in (left, right))
    assert nleft == nright, f"instruction difference at line {i + 1}"
    changes.append({"line": i + 1, "off": left.rstrip(), "on": right.rstrip()})
    normalized.append(nleft)

result = "".join(normalized)
prefix.with_suffix(".txt").write_text(result)
report = {
    "same_instruction_shape": True,
    "scope": "Two selected methods, FullOpts/NoPGO only; no tiered-PGO equivalence claim",
    "normalizations": changes,
    "off_sha256": hashlib.sha256(off_path.read_bytes()).hexdigest(),
    "on_sha256": hashlib.sha256(on_path.read_bytes()).hexdigest(),
    "normalized_sha256": hashlib.sha256(result.encode()).hexdigest(),
}
prefix.with_suffix(".json").write_text(json.dumps(report, indent=2) + "\n")
print(json.dumps(report))
