#!/usr/bin/env python3
"""Reproduce lost binding facts in persistent decode, with the same live-transport overlay."""
import argparse
import json
import os
from pathlib import Path
import re
import shutil

from run import BENCHMARK, COMMON_INPUTS, PROJECT, digest, execute, git, save

OLD = "2763bb4276a6f715e9dfd4827450e7285f919363"
MODES = ("sync-small", "sync-large", "persistent-large")
SOURCE = "eng/issue775/PersistentBindingChecks.cs"


def validate(result, old):
    if result.get("Schema") != 1 or result.get("Magic") != 183:
        raise ValueError("Wrong schema or experimental dialect")
    cases = result["Cases"]
    expected_keys = {(t, m, s) for t in ("tcp", "sharedmemory") for m in (False, True) for s in MODES}
    if len(cases) != 12 or {(r["Transport"], r["Metadata"], r["Mode"]) for r in cases} != expected_keys:
        raise ValueError("Missing, duplicated or substituted case")
    for row in cases:
        persistent = row["Mode"] == "persistent-large"
        expected = not (old and persistent)
        if row["PayloadBytes"] != (8192 if row["Mode"] == "sync-small" else 2 * 1024 * 1024):
            raise ValueError("Wrong payload boundary")
        if row["DeadlineEnabled"] != persistent or row["WorkerStarts"] != int(persistent):
            raise ValueError("The persistent/synchronous decode path was not actually exercised")
        if row["Decompressions"] != 1 or not row["BindingUnchanged"] or row["InfrastructureError"] is not None:
            raise ValueError(f"Setup/codec/binding failure is not this bug: {row}")
        if row["Passed"] != expected or row["BusinessCalls"] != int(expected):
            raise ValueError(f"Unexpected business outcome: {row}")
        if expected:
            if not all(row[k] for k in ("PayloadMatched", "ContextMatched", "HealthyAfter", "Drained")) or row["CallError"] is not None or row["RecoveryError"] is not None:
                raise ValueError("Successful path failed context, recovery or ownership checks")
        elif row["PayloadMatched"] or row["ContextMatched"] or not row["CallError"]:
            raise ValueError("Old failure did not reproduce missing method facts")
    if result["Passed"] != (not old):
        raise ValueError("Wrong aggregate result")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--old", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--expected-candidate", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    roots = {"old": args.old.resolve(), "candidate": args.candidate.resolve()}
    commits = {"old": OLD, "candidate": args.expected_candidate}
    out = args.output.resolve()
    if out.exists() and any(out.iterdir()):
        raise ValueError("Do not overwrite evidence")
    out.mkdir(parents=True, exist_ok=True)
    for arm, root in roots.items():
        if not re.fullmatch(r"[0-9a-f]{40}", commits[arm]) or git(root, "rev-parse", "HEAD") != commits[arm] or git(root, "status", "--porcelain"):
            raise ValueError("Wrong or dirty source")
    inputs = {}
    for path in COMMON_INPUTS:
        if (roots["old"] / path).read_bytes() != (roots["candidate"] / path).read_bytes():
            raise ValueError(f"Unrelated workload or policy change: {path}")
        inputs[path] = digest(roots["old"] / path)
    test = (roots["candidate"] / SOURCE).read_bytes()
    results, failures = {}, []
    save(out / "manifest.json", {"commits": commits, "commonInputSha256": inputs,
        "testSha256": digest(roots["candidate"] / SOURCE), "casesPerArm": 12, "performanceEvidence": False})
    for arm, root in roots.items():
        target = root / "test/SharpLink.Benchmarks/PersistentBindingChecks.cs"
        target.write_bytes(test)
        program = root / "test/SharpLink.Benchmarks/Program.cs"
        anchor = "    public static async Task Main(string[] args)\n    {\n"
        text = program.read_text()
        if text.count(anchor) != 1:
            raise ValueError("Non-unique overlay anchor")
        program.write_text(text.replace(anchor, anchor + '#if SHARPLINK_STATIC_BINDING_PROTOTYPE\n        if (args.Length == 2 && args[0] == "--persistent-binding")\n        {\n            await PersistentBindingChecks.RunAsync(args[1]);\n            return;\n        }\n#endif\n'))
        directory = out / arm
        directory.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(target, directory / target.name)
        (directory / "overlay.patch").write_text(git(root, "diff") + "\n")
        if git(root, "diff", "--", "src", "Directory.Build.props", "eng/perf"):
            raise ValueError("Test overlay must not modify production or policy")
        env = os.environ | {"SharpLinkStaticBindingPrototype": "true", "SHARPLINK_EXPECTED_MAGIC": "183",
                            "SHARPLINK_BENCHMARK_SHA": commits[arm]}
        if not execute(["dotnet", "build", PROJECT, "-c", "Release", "-v", "minimal"], root, directory / "build", env):
            failures.append(f"{arm}: build failure is not bug reproduction")
            continue
        success = execute(["dotnet", BENCHMARK, "--persistent-binding", str(directory / "result.json")],
            root, directory, env, 180, expected_failure=(arm == "old"))
        try:
            if not success:
                raise ValueError("Unexpected process exit")
            result = json.loads((directory / "result.json").read_text())
            validate(result, arm == "old")
            results[arm] = result
        except Exception as error:
            failures.append(f"{arm}: {error}")
    save(out / "execution-failures.json", failures)
    save(out / "summary.json", {"commits": commits, "oldBugReproduced": "old" in results,
        "candidatePassed": "candidate" in results, "performanceEvidence": False, "results": results})
    if failures:
        raise RuntimeError("; ".join(failures))


if __name__ == "__main__":
    main()
