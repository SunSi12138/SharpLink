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
    # ABBA+BAAB reverses the second quartet to reduce one-way runner drift.
    rounds = [(1, "base"), (2, "stripped"), (3, "stripped"), (4, "base"),
              (5, "stripped"), (6, "base"), (7, "base"), (8, "stripped")]
    rows = []
    for index, variant in rounds:
        path = out / ("round-%02d-%s" % (index, variant)) / "results"
        reports = list(path.glob("*-report-full*.json"))
        if len(reports) != 4:
            raise RuntimeError("%s: expected four different BDN JSON reports; found %d" %
                               (path, len(reports)))
        for report in reports:
            benchmarks = json.loads(report.read_text()).get("Benchmarks") or []
            if len(benchmarks) != 1:
                raise RuntimeError("%s: expected exactly one benchmark method" % report)
            bench = benchmarks[0]
            stats = bench.get("Statistics") or {}
            ns = stats.get("Mean")
            allocated = (bench.get("Memory") or {}).get("BytesAllocatedPerOperation")
            if not isinstance(ns, (float, int)) or not 0 < ns < 1.0e15:
                raise RuntimeError("%s: missing/invalid positive ns/op" % report)
            if not isinstance(allocated, (float, int)) or not 0 <= allocated < 1.0e15:
                raise RuntimeError("%s: missing/invalid B/op" % report)
            rows.append({
                "round": index,
                "variant": variant,
                "method": bench["Type"] + "." + bench["Method"],
                "meanNsOp": ns,
                "stdDevNsOp": stats.get("StandardDeviation"),
                "allocatedBytesOp": allocated,
                "report": str(report.relative_to(out)),
            })

    methods = sorted({r["method"] for r in rows})
    expected_classes = [
        "Issue801AdmissionAblationBenchmarks.CaptureAndRelease",
        "Issue801ClientAblationBenchmarks.BuildAndDisposeDefaultAsync",
        "Issue801WireRpcAblationBenchmarks.AddViaRealTcpAsync",
        "Issue801ReconnectAblationBenchmarks.ForceTransportLossAndReconnectAsync",
    ]
    if len(rows) != 32 or len(methods) != 4:
        raise RuntimeError("Incomplete eight rounds by four benchmarks")
    if sorted(method.split("SharpLink.Benchmarks.")[-1] for method in methods) != sorted(expected_classes):
        raise RuntimeError("Benchmark method set unexpectedly changed: " + repr(methods))
    with (out / "measurements.csv").open("w", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)

    lines = [
        "# Issue #801: same-runner source-ablation ABBA + BAAB replication",
        "",
        "Both production variants compiled from the exact same dev SHA. The stripped variant is temporary and non-shippable.",
        "Four **independent BDN processes per variant**, five/four warmup iterations for original/new benchmarks.",
        "New workload: real TCP Protocol v2 RPC. New reconnect workload: close an established TCP transport,",
        "wait for the fixed-client reconnect owner to negotiate a replacement, then complete an RPC.",
        "",
        "| Method | Baseline median ns/op | Ablated median ns/op | Baseline median B/op | Ablated median B/op | Baseline run span ns | Ablated run span ns |",
        "| --- | ---: | ---: | ---: | ---: | ---: | ---: |",
    ]
    comparable = []
    for method in methods:
        groups = {}
        for variant in ("base", "stripped"):
            matches = [r for r in rows if r["method"] == method and r["variant"] == variant]
            if len(matches) != 4:
                raise RuntimeError("Missing four repetitions: %s / %s" % (method, variant))
            ns_values = [r["meanNsOp"] for r in matches]
            group = {
                "timeNsMedian": statistics.median(ns_values),
                "timeNsMin": min(ns_values),
                "timeNsMax": max(ns_values),
                "bytesMedian": statistics.median(r["allocatedBytesOp"] for r in matches),
                "rounds": [r["round"] for r in matches],
            }
            groups[variant] = group
        a, b = groups["base"], groups["stripped"]
        spans_overlap = max(a["timeNsMin"], b["timeNsMin"]) <= min(a["timeNsMax"], b["timeNsMax"])
        comparable.append({
            "method": method,
            "baseline": a,
            "stripped": b,
            "medianDeltaNs": b["timeNsMedian"] - a["timeNsMedian"],
            "medianDeltaPct": (b["timeNsMedian"] / a["timeNsMedian"] - 1) * 100,
            "spansOverlap": spans_overlap,
        })
        lines.append("| %s | %.3f | %.3f | %.2f | %.2f | %.2f..%.2f | %.2f..%.2f |" %
                     (method, a["timeNsMedian"], b["timeNsMedian"],
                      a["bytesMedian"], b["bytesMedian"],
                      a["timeNsMin"], a["timeNsMax"],
                      b["timeNsMin"], b["timeNsMax"]))

    lines.extend([
        "",
        "Each median uses four BDN process means, **not** a robust cross-machine confidence interval.",
        "Run spans are simple ranges of four process means; overlapping spans weaken attribution.",
        "Client reconnection timing includes a real fixed 2-ms policy delay, TCP establishment,",
        "Protocol v2 handshake, OS scheduling, and a post-recovery RPC. It does NOT isolate one hook check.",
        "Do not infer production throughput, request latency, or object-size savings without additional",
        "controlled runners, assembly layout inspections, and preserved deterministic race tests.",
        "",
        "Experimental hook removal is NOT compatible with the existing hook-driven race tests.",
        "Never cherry-pick the stripped worktree into dev. See ablation-manifest.json and raw BDN results.",
    ])
    (out / "summary.md").write_text("\n".join(lines) + "\n")
    (out / "summary.json").write_text(json.dumps({
        "completedUtc": datetime.now(timezone.utc).isoformat(),
        "observations": rows,
        "comparisons": comparable,
        "methodology": "Eight same-host rounds, ABBA followed by BAAB; four BDN processes per source variant",
        "limits": "No causal guarantee or full regression evidence; stripped source is experimental only",
    }, indent=2))
    print((out / "summary.md").read_text())

if __name__ == "__main__":
    if len(sys.argv) == 5 and sys.argv[1] == "apply":
        apply(sys.argv[2], sys.argv[3], sys.argv[4])
    elif len(sys.argv) == 3 and sys.argv[1] == "summarize":
        summarize(sys.argv[2])
    else:
        raise SystemExit("Usage: issue-801-ablation.py apply ROOT SHA MANIFEST | summarize OUTPUT_DIR")
