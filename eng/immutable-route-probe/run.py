#!/usr/bin/env python3
"""Run the bounded #775 identity feasibility gate, not the RPC performance suite."""
from __future__ import annotations

import json
from pathlib import Path
import subprocess
import sys

BASELINE = "13928a35df4bff3cd99b14bd1df131aa3eb3bcdb"
ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "eng/immutable-route-probe/ImmutableRouteProbe.csproj"


def output(*args: str) -> str:
    return subprocess.check_output(args, cwd=ROOT, text=True).strip()


def main() -> None:
    # Don't silently compare a new descriptor or changed callback path with an old label.
    subprocess.run(["git", "diff", "--exit-code", BASELINE, "--", "src"],
                   cwd=ROOT, check=True, stdout=sys.stderr)
    if output("git", "ls-files", "--others", "--exclude-standard", "src"):
        raise RuntimeError("Untracked production source files invalidate the pinned-source gate.")
    sdk = output("dotnet", "--version")
    subprocess.run(["dotnet", "build", str(PROJECT), "-c", "Release", "--nologo"],
                   cwd=ROOT, check=True, stdout=sys.stderr)
    executable = PROJECT.parent / "bin/Release/net10.0/ImmutableRouteProbe.dll"
    result = json.loads(output("dotnet", str(executable)))
    result["sourceValidation"] = "All tracked src files match the reference baseline; no untracked src files."
    result["productionSourceBaselineVerified"] = BASELINE
    result["probeCheckoutHead"] = output("git", "rev-parse", "HEAD")
    result["probeWorkingTreeDirty"] = bool(output("git", "status", "--porcelain"))
    result["sdk"] = sdk
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
