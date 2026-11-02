#!/usr/bin/env python3
"""Source-only preparation in an independently supplied exact-main checkout.
No fetch, restore, build, SDK selection, test execution, or public writes.
Requires a clean disposable checkout. Never point this at the WIP checkout.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

BASE = "9d1635c8988870ee295098fe9e65cb49924013d2"
COORDINATOR = "src/SharpLink.Server/ServerLifecycleCoordinator.cs"
BEFORE_BLOB = "472ab7117d4b79d01151db1b745af2ff4498d859"
AFTER_SHA = "5986242bcbb20fd7cbf0b98644dc5af50462122eea9575f5335dc2fee2b22f05"
PACKAGES_BLOB = "8106159e925660c1f03f9394538b8a04814557e7"
PROJECT = "test/SharpLink.TerminalSignal.Focused"
FILES = ("SharpLink.TerminalSignal.Focused.csproj", "FocusedGlobalUsings.cs", "ServerTerminalSignalObservationTests.cs")


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def git(root, *args):
    # GIT_NO_LAZY_FETCH blocks implicit promisor fetching; no network operation here.
    import os
    env = dict(os.environ, GIT_NO_LAZY_FETCH="1", GIT_TERMINAL_PROMPT="0")
    p = subprocess.run(["git", "-C", str(root), *args], env=env, text=True,
                       capture_output=True, timeout=20, check=False)
    require(p.returncode == 0, "Local git operation failed: " + args[0])
    return p.stdout.strip()


def blob(path):
    b = path.read_bytes()
    return hashlib.sha1(b"blob " + str(len(b)).encode() + b"\0" + b).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--checkout", type=Path, required=True)
    parser.add_argument("--variant", choices=("baseline", "fixed"), required=True)
    parser.add_argument("--manifest-sha256", required=True)
    args = parser.parse_args()
    payload = Path(__file__).resolve().parent
    manifest_bytes = (payload / "SOURCE.json").read_bytes()
    require(hashlib.sha256(manifest_bytes).hexdigest() == args.manifest_sha256,
            "Payload manifest identity mismatch")
    manifest = json.loads(manifest_bytes)
    for name, expected in manifest["files"].items():
        require(Path(name).name == name, "Nonlocal payload path")
        file = payload / name
        require(not file.is_symlink() and file.is_file(), "Missing or symlink payload")
        require(hashlib.sha256(file.read_bytes()).hexdigest() == expected, "Payload hash mismatch: " + name)
    root = args.checkout.resolve(strict=True)
    require(Path(git(root, "rev-parse", "--show-toplevel")).resolve() == root, "Not checkout root")
    require(git(root, "rev-parse", "HEAD") == BASE, "Checkout is not exact main base")
    require(git(root, "status", "--porcelain", "--untracked-files=all") == "", "Checkout must be clean")
    for path in (root / COORDINATOR, root / "Directory.Packages.props", root / "test"):
        require(not path.is_symlink(), "Symlink input forbidden")
    require(blob(root / COORDINATOR) == BEFORE_BLOB, "Coordinator preimage mismatch")
    require(blob(root / "Directory.Packages.props") == PACKAGES_BLOB, "Main TUnit pin/preimage mismatch")
    target = root / PROJECT
    require(not target.exists() and not target.is_symlink(), "Focused destination must be fresh")
    if args.variant == "fixed":
        git(root, "apply", "--check", str(payload / "terminal-signal.patch"))
        git(root, "apply", str(payload / "terminal-signal.patch"))
        require(hashlib.sha256((root / COORDINATOR).read_bytes()).hexdigest() == AFTER_SHA,
                "Fixed coordinator postimage mismatch")
    target.mkdir()
    for name in FILES:
        (target / name).write_bytes((payload / name).read_bytes())
    changed = git(root, "diff", "--name-only").splitlines()
    require(changed == ([COORDINATOR] if args.variant == "fixed" else []), "Unexpected production mutation")
    require(blob(root / "Directory.Packages.props") == PACKAGES_BLOB, "Package pin changed")
    expected_untracked = sorted(PROJECT + "/" + name for name in FILES)
    require(sorted(git(root, "ls-files", "--others", "--exclude-standard").splitlines()) == expected_untracked,
            "Unexpected untracked source")
    print(json.dumps({"stage": "source-prepared", "base": BASE, "variant": args.variant,
                      "payload": args.manifest_sha256, "tunit": "1.73.19",
                      "productionChanges": changed, "executedTests": False}))


if __name__ == "__main__":
    main()
