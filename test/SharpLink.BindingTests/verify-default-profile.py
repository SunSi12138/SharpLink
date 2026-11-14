"""Prove the ordinary compilation's exact tokens still equal the prior slice source."""
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from eng.tests.test_admitted_dispatch_parity import exact_tokens

BASE = "2360a0ca5b782095f02d976ca303fc7890bb9871"


def ordinary(source):
    output, stack = [], [True]
    for line in source.splitlines():
        directive = line.strip()
        if directive in ("#if SHARPLINK_775_STATIC_SLICE", "#if !SHARPLINK_775_STATIC_SLICE"):
            stack.append(directive.startswith("#if !"))
        elif len(stack) > 1 and directive == "#else":
            stack[-1] = not stack[-1]
        elif len(stack) > 1 and directive == "#endif":
            stack.pop()
        elif all(stack):
            output.append(line)
    assert len(stack) == 1
    return "\n".join(output)


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT).decode("utf-8")


baseline = set(git("ls-tree", "-r", "--name-only", BASE).splitlines())
changed = git("diff", "--name-only", BASE, "HEAD", "--", "src").splitlines()
for name in changed:
    old = git("show", f"{BASE}:{name}") if name in baseline else ""
    new = (ROOT / name).read_text(encoding="utf-8")
    assert exact_tokens(ordinary(old)) == exact_tokens(ordinary(new)), name
for name in ("Directory.Build.props", "Directory.Packages.props", "global.json"):
    assert git("show", f"{BASE}:{name}").splitlines() == (ROOT / name).read_text(encoding="utf-8").splitlines(), name
print(f"PASS ordinary source tokens: {len(changed)} files; unchanged build inputs; baseline {BASE}")
print("Prior Unit evidence remains Linux 2212/2212; Windows 2211/2212 with unresolved Stop failure. NOT a passed gate.")

