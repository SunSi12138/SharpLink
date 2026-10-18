#!/usr/bin/env python3
"""Run identical end-to-end compression cases against the archived bug and current fix."""
import argparse
import json
import os
from pathlib import Path
import shutil

from run import BENCHMARK, PROJECT, execute, git, save

OLD = "a473f5d3a50d3b23874f8b1bb72f46e65ff939d1"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--old", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--expected-candidate", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    if output.exists() and any(output.iterdir()):
        raise ValueError("Do not overwrite evidence")
    output.mkdir(parents=True, exist_ok=True)
    roots = {"old": args.old.resolve(), "candidate": args.candidate.resolve()}
    commits = {"old": OLD, "candidate": args.expected_candidate}
    results = {}
    for arm, root in roots.items():
        if git(root, "rev-parse", "HEAD") != commits[arm] or git(root, "status", "--porcelain"):
            raise ValueError("Wrong or dirty input")
        source = roots["candidate"] / "eng/issue775/BoundaryCompressionChecks.cs"
        target = root / "test/SharpLink.Benchmarks/BoundaryCompressionChecks.cs"
        shutil.copyfile(source, target)
        program = root / "test/SharpLink.Benchmarks/Program.cs"
        anchor = "    public static async Task Main(string[] args)\n    {\n"
        text = program.read_text()
        if text.count(anchor) != 1:
            raise ValueError("Non-unique diagnostic anchor")
        text = text.replace(anchor, anchor + '#if SHARPLINK_STATIC_BINDING_PROTOTYPE\n        if (args.Length == 2 && args[0] == "--boundary-compression")\n        {\n            await BoundaryCompressionChecks.RunAsync(args[1]);\n            return;\n        }\n#endif\n')
        program.write_text(text)
        directory = output / arm
        directory.mkdir(parents=True, exist_ok=True)
        (directory / "overlay.patch").write_text(git(root, "diff") + "\n")
        shutil.copyfile(target, directory / target.name)
        env = os.environ | {"SharpLinkStaticBindingPrototype": "true", "SHARPLINK_EXPECTED_MAGIC": "183",
                            "SHARPLINK_BENCHMARK_SHA": commits[arm]}
        if not execute(["dotnet", "build", PROJECT, "-c", "Release", "-v", "minimal"], root, directory / "build", env):
            raise RuntimeError("Build failure is not bug reproduction")
        if not execute(["dotnet", BENCHMARK, "--boundary-compression", str(directory / "result.json")],
                       root, directory, env, 120, expected_failure=(arm == "old")):
            raise RuntimeError(f"Unexpected {arm} exit status")
        result = json.loads((directory / "result.json").read_text())
        cases = result["Cases"]
        if len(cases) != 8 or {(c['Transport'], c['OneWay'], c['Metadata']) for c in cases} != {
            (t, w, m) for t in ('tcp', 'sharedmemory') for w in (False, True) for m in (False, True)}:
            raise ValueError("Incomplete test matrix")
        for case in cases:
            expected = arm == "candidate" or case["Metadata"]
            if case["Passed"] != expected or case["Decompressions"] != 1 or case["BusinessEntered"] != expected:
                raise ValueError(f"Unexpected result, not the targeted bug: {arm}: {case}")
        results[arm] = result
    save(output / "summary.json", {"commits": commits, "oldBugReproduced": True,
        "candidatePassed": True, "casesPerArm": 8, "performanceEvidence": False, "results": results})


if __name__ == "__main__":
    main()
