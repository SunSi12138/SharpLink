#!/usr/bin/env python3
"""Run the unchanged formal runners for one frozen, patch-addressed candidate."""
import argparse
import hashlib
import importlib
import json
from pathlib import Path
import subprocess
import sys

EXPERIMENT = "f82fa8685ae6da3d8d8d2df77b96fb5cc4b9d987"
BASE = "454f51e913684853c1caa2221bc92c94e6ad1789"
ORIGINAL = "1a54993d3550911f5979a7b4d3f9f5f63ee86a64"
# Filled only from the successful same-source correctness/codegen artifact before publication.
CANDIDATE = "f4ef71e255f880659862d0e7c663e50891281228"
LAYOUT_SHA = "29af909c6cfb4819f3455cd103fdcd0cfb62824fc915c6e726810b7f81ec007a"


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("candidate", "comparator", "experiment", "output"):
        parser.add_argument("--"+key, type=Path, required=True)
    parser.add_argument("--comparison", choices=("original-dev", "old-static"), required=True)
    args = parser.parse_args()
    candidate, comparator, experiment, out = (getattr(args,n).resolve() for n in ("candidate","comparator","experiment","output"))
    if out.exists() and any(out.iterdir()):
        raise ValueError("No merging, overwriting, or selecting an earlier run")
    out.mkdir(parents=True, exist_ok=True)
    if git(experiment,"rev-parse","HEAD") != EXPERIMENT or git(experiment,"status","--porcelain"):
        raise ValueError("Wrong or dirty experiment driver")
    helper = experiment/"eng/issue775/control_site_layout.py"
    if hashlib.sha256(helper.read_bytes()).hexdigest() != LAYOUT_SHA:
        raise ValueError("Layout transform changed")
    sys.path.insert(0,str(experiment/"eng/issue775"))
    control = importlib.import_module("control_site")
    identity = control.apply_candidate(candidate, experiment, out)
    if identity["localCandidateSha"] != CANDIDATE:
        raise ValueError("Not the candidate validated before this performance experiment")
    expected_old = ORIGINAL if args.comparison == "original-dev" else BASE
    if git(comparator,"rev-parse","HEAD") != expected_old or git(comparator,"status","--porcelain"):
        raise ValueError("Wrong or dirty comparator")
    identity.update(comparison=args.comparison, comparator=expected_old, experimentDriver=EXPERIMENT,
                    performanceDriver=git(Path(__file__).resolve().parents[2],"rev-parse","HEAD"),
                    stage2Authorized=False, measurementsIncludeDiagnosticOverlays=False)
    (out/"performance-identity.json").write_text(json.dumps(identity,indent=2)+"\n")
    # No diagnostic test/shim is installed. Both formal runners reject dirty checkouts,
    # record workload hashes, preserve all failed slots, and run the original budgets.
    results = out/"measurements"
    if args.comparison == "original-dev":
        subprocess.run(["python3",str(candidate/"eng/issue775/run.py"),"--base-root",str(comparator),
            "--head-root",str(candidate),"--expected-head",CANDIDATE,"--output",str(results)],check=True)
    else:
        runner = importlib.import_module("boundary_pair")
        runner.COMMITS = {"old":BASE,"candidate":CANDIDATE}
        sys.argv = [str(Path(__file__)),"--old",str(comparator),"--candidate",str(candidate),"--output",str(results)]
        runner.main()


if __name__ == "__main__":
    main()
