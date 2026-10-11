#!/usr/bin/env python3
"""Issue #801: fail-closed source ablation and ABBA result aggregation.

The stripped files exist ONLY inside detached GitHub Actions worktrees and are
not acceptable changes for the production branch or for release validation.
"""
import csv
import hashlib
import json
import pathlib
import re
import statistics
import subprocess
import sys
from datetime import datetime, timezone

SERVER = "src/SharpLink.Server/SharpLinkServer.AdmissionProgram.cs"
CLIENT = "src/SharpLink.Client/SharpLinkClient.cs"
CLIENT_PARTIALS = [
    "src/SharpLink.Client/SharpLinkClient.Lifecycle.cs",
    "src/SharpLink.Client/SharpLinkClient.SessionRefresh.cs",
    "src/SharpLink.Client/SharpLinkClient.StaticCluster.cs",
    "src/SharpLink.Client/SharpLinkClient.DynamicCluster.cs",
    "src/SharpLink.Client/SharpLinkClient.StaticCluster.SessionRefresh.cs",
    "src/SharpLink.Client/SharpLinkClient.DynamicCluster.SessionRefresh.cs",
]
FILES = [SERVER, CLIENT] + CLIENT_PARTIALS


def exact(source, needle, replacement="", expected=1):
    if source.count(needle) != expected:
        raise RuntimeError("Ablation source drift: expected %s occurrences of %r, got %s" %
                           (expected, needle[:100], source.count(needle)))
    return source.replace(needle, replacement)


def apply(root, expected_sha, manifest):
    root = pathlib.Path(root).resolve()
    if not (root / ".git").is_file():
        raise RuntimeError("Refuse to ablate main checkout; required: detached Git worktree")
    actual = subprocess.check_output(
        ["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
    if actual != expected_sha:
        raise RuntimeError("Refuse unpinned revision: %s != %s" % (actual, expected_sha))
    before = {p: (root / p).read_text() for p in FILES}
    edits = {}
    server = before[SERVER]
    server = exact(server, "    private static Action<SharpLinkServer, long, AdmissionProgram>? s_afterAdmissionPublicationReadForTests;\n")
    server = exact(server, "    private static Action<SharpLinkServer, long, AdmissionProgram?>? s_afterAdmissionCaptureForTests;\n")
    start = "    /// <summary>\n    /// Deterministic stale-read probe."
    finish = "    /// <summary>\n    /// Deterministic control-plane probe."
    if server.count(start) != 1 or server.count(finish) != 1:
        raise RuntimeError("Server diagnostic accessor boundaries changed")
    a, b = server.index(start), server.index(finish)
    if a >= b:
        raise RuntimeError("Incorrect accessor order")
    section = server[a:b]
    if not all(x in section for x in ["AfterAdmissionPublicationReadForTests",
                                     "AfterAdmissionCaptureForTests"]):
        raise RuntimeError("Unexpected accessor section contents")
    server = server[:a] + server[b:]
    for statement in [
        "Volatile.Read(ref s_afterAdmissionCaptureForTests)?.Invoke(this, requestId, null);",
        "Volatile.Read(ref s_afterAdmissionCaptureForTests)?.Invoke(this, requestId, publication);",
        "Volatile.Read(ref s_afterAdmissionPublicationReadForTests)?.Invoke(this, requestId, publication);",
    ]:
        regex = re.compile(r"(?m)^[ \t]*" + re.escape(statement) + r"\n")
        server, count = regex.subn("", server)
        if count != 1:
            raise RuntimeError("Expected exactly one production hook read: " + statement)
    if ("s_afterAdmissionCaptureForTests" in server or
            "s_afterAdmissionPublicationReadForTests" in server):
        raise RuntimeError("Server still references a removed field")
    edits[SERVER] = server

    client = before[CLIENT]
    client = exact(client,
        "    private readonly Func<CancellationToken, ValueTask>? _beforeReadyPublicationTestHook;\n")
    client = exact(client,
        "        _beforeReadyPublicationTestHook = composition.BeforeReadyPublicationTestHook;\n")
    if "_beforeReadyPublicationTestHook" in client:
        raise RuntimeError("Removed Client field still referenced")
    edits[CLIENT] = client

    pattern = re.compile(
        r"(?m)^[ \t]*if \((?:_client\.)?_beforeReadyPublicationTestHook is not null\)\n"
        r"[ \t]*await (?:_client\.)?_beforeReadyPublicationTestHook"
        r"\(attemptCts\.Token\)\.ConfigureAwait\(false\);\n"
    )
    for path in CLIENT_PARTIALS:
        new, count = pattern.subn("", before[path])
        if count != 1 or "_beforeReadyPublicationTestHook" in new:
            raise RuntimeError("Expected one specific Client readiness hook in " + path)
        edits[path] = new

    for path, updated in edits.items():
        (root / path).write_text(updated)
    changed = subprocess.check_output(
        ["git", "-C", str(root), "diff", "--name-only", "--", "src/"],
        text=True).splitlines()
    if sorted(changed) != sorted(FILES):
        raise RuntimeError("Unexpected modified production paths: " + repr(changed))
    diff = subprocess.check_output(["git", "-C", str(root), "diff", "--stat"], text=True)
    manifest = pathlib.Path(manifest)
    manifest.parent.mkdir(parents=True, exist_ok=True)
    manifest.write_text(json.dumps({
        "pinnedDevSha": actual,
        "experimentalOnlyNeverMerge": True,
        "actualTouchedPaths": changed,
        "server": "Remove 2 static test delegate fields/accessors plus 3 request-capture loads",
        "client": "Remove 1 nullable client test delegate field/assignment plus 6 handshake guards",
        "intent": "Compare physical removal of default-null test state, not normal-vs-installed hooks",
        "files": [{"path": p,
                   "beforeSha256": hashlib.sha256(before[p].encode()).hexdigest(),
                   "afterSha256": hashlib.sha256(edits[p].encode()).hexdigest()}
                  for p in FILES],
        "diffStat": diff,
    }, indent=2))
    print(diff)
    print("Source ablation confined to %d temporary worktree files" % len(changed))


def summarize(out):
    out = pathlib.Path(out)
    rounds = [(1, "base"), (2, "stripped"), (3, "stripped"), (4, "base")]
    rows = []
    for index, variant in rounds:
        path = out / ("round-%02d-%s" % (index, variant)) / "results"
        reports = list(path.glob("*-report-full*.json"))
        if len(reports) != 2:
            raise RuntimeError("%s: expected two BDN JSON reports; found %d" %
                               (path, len(reports)))
        for report in reports:
            benchmarks = json.loads(report.read_text()).get("Benchmarks") or []
            if len(benchmarks) != 1:
                raise RuntimeError("%s: expected one method per report" % report)
            bench = benchmarks[0]
            ns = (bench.get("Statistics") or {}).get("Mean")
            allocated = (bench.get("Memory") or {}).get("BytesAllocatedPerOperation")
            if not isinstance(ns, (float, int)) or ns <= 0:
                raise RuntimeError("%s: invalid ns/op" % report)
            if not isinstance(allocated, (float, int)) or allocated < 0:
                raise RuntimeError("%s: missing allocated bytes/op" % report)
            rows.append({
                "round": index, "variant": variant,
                "method": bench["Type"] + "." + bench["Method"],
                "meanNsOp": ns, "allocatedBytesOp": allocated,
                "report": str(report.relative_to(out)),
            })
    methods = sorted({r["method"] for r in rows})
    if len(rows) != 8 or len(methods) != 2:
        raise RuntimeError("ABBA 4 rounds x 2 methods were not completed")
    with (out / "measurements.csv").open("w", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    lines = [
        "# Issue 801: pinned-dev source-level A/B (ABBA on one CI runner)",
        "",
        "Both variants compile independently; the stripped version is experimental and must never be merged.",
        "These are exploratory numbers: only two independent BDN runs per variant.",
        "",
        "| Method | Base median ns/op | Ablated median ns/op | Base median B/op | Ablated median B/op |",
        "| --- | ---: | ---: | ---: | ---: |",
    ]
    for method in methods:
        groups = {}
        for variant in ("base", "stripped"):
            matches = [r for r in rows if r["method"] == method and r["variant"] == variant]
            if len(matches) != 2:
                raise RuntimeError("Missing pair: %s / %s" % (method, variant))
            groups[variant] = {
                "ns": statistics.median(r["meanNsOp"] for r in matches),
                "bytes": statistics.median(r["allocatedBytesOp"] for r in matches),
            }
        a, b = groups["base"], groups["stripped"]
        lines.append("| %s | %.3f | %.3f | %.2f | %.2f |" %
                     (method, a["ns"], b["ns"], a["bytes"], b["bytes"]))
    lines.extend([
        "",
        "No confidence interval is inferred from two runs per variant.",
        "Do not equate measured differences with a production performance guarantee.",
        "All raw BDN statistics, CPU info, exact SHA and source diff are attached.",
    ])
    (out / "summary.md").write_text("\n".join(lines) + "\n")
    (out / "summary.json").write_text(json.dumps({
        "completedUtc": datetime.now(timezone.utc).isoformat(),
        "observations": rows,
        "methodology": "ABBA source ablation with two independent BDN runs per variant",
    }, indent=2))
    print((out / "summary.md").read_text())


if __name__ == "__main__":
    if len(sys.argv) == 5 and sys.argv[1] == "apply":
        apply(sys.argv[2], sys.argv[3], sys.argv[4])
    elif len(sys.argv) == 3 and sys.argv[1] == "summarize":
        summarize(sys.argv[2])
    else:
        raise SystemExit("Usage: issue-801-ablation.py apply ROOT SHA MANIFEST | summarize OUTPUT_DIR")
