#!/usr/bin/env python3
"""Run the ordinary contract-binding inventories and exact old-Stub compile rejection."""

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys


ROOT = Path(__file__).resolve().parents[1]
FIXTURES = ROOT / "test/SharpLink.BindingTests"
MODES = ("", "group", "receipt", "facts", "streams", "local", "oneway", "routing")
PROJECTS = ("Probe/SharpLink.BindingTests.csproj", "GroupPluginChanged/SharpLink.GroupPluginChanged.csproj",
            "GroupPluginUnrelated/SharpLink.GroupPluginUnrelated.csproj", "PreviousAbi/SharpLink.PreviousAbi.csproj")
OLD_MEMBERS = {"TryResolveMethod", "InvokeNoReturnAsync", "InvokeNoReturnCancellableAsync",
               "InvokeAsync", "InvokeCancellableAsync"}


def run(command, log, env, timeout=300, expected_failure=False):
    with log.open("w", encoding="utf-8") as output:
        output.write(json.dumps([str(item) for item in command]) + "\n")
        output.flush()
        result = subprocess.run(command, cwd=ROOT, stdout=output, stderr=subprocess.STDOUT,
                                env=env, timeout=timeout, check=False)
    if result.returncode and not expected_failure:
        print(log.read_text(encoding="utf-8", errors="replace")[-12000:], file=sys.stderr)
        raise RuntimeError(f"Failed ({result.returncode}); retained {log}")
    return result.returncode


def verify_old_stub(code, text):
    errors = set(re.findall(r"error (CS\d+): ([^\r\n]+)", text))
    members = {member for _, message in errors for member in re.findall(r"IRpcStub\.(\w+)\(", message)}
    if code == 0 or len(errors) != 5 or {code for code, _ in errors} != {"CS0535"} or members != OLD_MEMBERS:
        raise RuntimeError(f"Old Stub must fail on exactly the five changed IRpcStub members: {errors}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=ROOT / "artifacts/contract-binding")
    args = parser.parse_args()
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    reports = out / "reports"
    reports.mkdir(exist_ok=True)
    source = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    env = {**os.environ, "SHARPLINK_TEST_SOURCE": source, "DOTNET_CLI_UI_LANGUAGE": "en"}

    for project in PROJECTS:
        run(["dotnet", "build", str(FIXTURES / project), "-c", "Release"],
            out / f"build-{project.split('/')[0]}.log", env)
    negative_log = out / "old-stub-negative.log"
    code = run(["dotnet", "build", str(FIXTURES / "OldStubConsumer/SharpLink.OldStubConsumer.csproj"),
                "-c", "Release"], negative_log, env, expected_failure=True)
    verify_old_stub(code, negative_log.read_text(encoding="utf-8", errors="replace"))
    run([sys.executable, str(FIXTURES / "verify-facts-source.py")], out / "facts-source.log", env)

    probe = FIXTURES / "Probe/bin/Release/net10.0/SharpLink.BindingTests.dll"
    for transport in ("tcp", "sharedmemory"):
        for mode in MODES:
            name = f"{mode + '-' if mode else ''}{transport}"
            report = ROOT / f"artifacts/775-slice/{name}.json"
            report.unlink(missing_ok=True)  # Never accept a stale report after a failed process.
            arguments = [mode, transport] if mode else [transport]
            run(["dotnet", str(probe), *arguments], out / f"{name}.log", env)
            shutil.copyfile(report, reports / report.name)
            print(f"Completed {name}; validating its frozen inventory below.", flush=True)
    for mode in MODES:
        verifier = f"verify-{mode}.py" if mode else "verify.py"
        run([sys.executable, str(FIXTURES / verifier), str(reports), source],
            out / f"{mode or 'baseline'}-verification.log", env)
    print(f"PASS all 16 exact inventories and old-Stub rejection; source={source}; evidence={out}")


if __name__ == "__main__":
    main()
