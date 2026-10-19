#!/usr/bin/env python3
"""Formal paired runs for the exact bridge candidate already checked by CI."""
import argparse
import importlib
import json
from pathlib import Path
import subprocess
import sys

EXPERIMENT = "5487a21f020e16c2b036b439b8d7efd896267422"
BASE = "6cfb09e7dd6b68695f92d28f726340bc8eb39e1d"
ORIGINAL = "1a54993d3550911f5979a7b4d3f9f5f63ee86a64"
CANDIDATE = "8fc84d486c242d67fe05d4cf9962661d8351c779"
TREE = "1bd11d1d56ef951428440ee5cede21af94d1c80e"


def git(root, *args):
    return subprocess.check_output(["git","-C",str(root),*args],text=True).strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("candidate","comparator","experiment","output"):
        parser.add_argument("--"+key,type=Path,required=True)
    parser.add_argument("--comparison",choices=("original-dev","old-static"),required=True)
    args = parser.parse_args()
    candidate,comparator,experiment,out = (getattr(args,n).resolve() for n in ("candidate","comparator","experiment","output"))
    if out.exists() and any(out.iterdir()):
        raise ValueError("Never overwrite or merge experiment results")
    out.mkdir(parents=True,exist_ok=True)
    if git(experiment,"rev-parse","HEAD") != EXPERIMENT or git(experiment,"status","--porcelain"):
        raise ValueError("Not the validated clean experiment driver")
    sys.path.insert(0,str(experiment/"eng/issue775"))
    bridge = importlib.import_module("bridge")
    identity = bridge.apply_candidate(candidate,experiment,out)
    if identity["localCandidateSha"] != CANDIDATE or identity["tree"] != TREE:
        raise ValueError("Not the exact candidate validated before timing")
    expected = ORIGINAL if args.comparison == "original-dev" else BASE
    if git(comparator,"rev-parse","HEAD") != expected or git(comparator,"status","--porcelain"):
        raise ValueError("Wrong or dirty comparator")
    identity.update(comparison=args.comparison,comparator=expected,experimentDriver=EXPERIMENT,
                    performanceDriver=git(Path(__file__).resolve().parents[2],"rev-parse","HEAD"),
                    diagnosticOverlaysInstalled=False)
    (out/"performance-identity.json").write_text(json.dumps(identity,indent=2)+"\n")
    # Existing workloads, timing, full raw arrays and canonical allocation policy stay unchanged.
    if args.comparison == "original-dev":
        subprocess.run(["python3",str(candidate/"eng/issue775/run.py"),"--base-root",str(comparator),
            "--head-root",str(candidate),"--expected-head",CANDIDATE,"--output",str(out/"measurements")],check=True)
    else:
        runner = importlib.import_module("boundary_pair")
        runner.COMMITS = {"old":BASE,"candidate":CANDIDATE}
        sys.argv = [str(Path(__file__)),"--old",str(comparator),"--candidate",str(candidate),"--output",str(out/"measurements")]
        runner.main()


if __name__ == "__main__": main()
