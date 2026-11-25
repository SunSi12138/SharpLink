#!/usr/bin/env python3
"""Plan one D6 steady cell and grade its existing reports; never start workloads."""

import argparse
import importlib.util
import json
import math
from pathlib import Path
import statistics


_spec = importlib.util.spec_from_file_location("matrix", Path(__file__).resolve().parents[1] / "performance-matrix.py")
matrix = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(matrix)
RECORDER, SCHEMA = "worker-local-shared-capacity-v4", 1
METRICS = {"p50_us": 0.03, "p99_us": 0.05, "cpu_us_per_op": 0.03}
AB_PAIRS = ("PN", "NP", "NP", "PN") * 2  # ABBA, BAAB, ABBA, BAAB
HOST_FIELDS = ("OperatingSystem", "OsArchitecture", "ProcessArchitecture", "Runtime", "ProcessorCount", "ServerGc", "GcLatencyMode")


def sample_plan(transport, concurrency, operation, *, oneway_rate=0):
    matrix.require(oneway_rate == 0 or (operation == "oneway32" and oneway_rate == 80_000),
                   "unregistered OneWay offered rate")
    extra = {"oneway32-rate": oneway_rate} if oneway_rate else {}
    precision = dict(METRICS, ops_per_s=.03) if oneway_rate else METRICS
    return dict(
        transport=transport, concurrency=concurrency, operation=operation, minimum_completed=100_000,
        oneway_rate=oneway_rate,
        # Request an extra second at each edge; never give timer jitter a waiver.
        arguments={"mode": "local", "transport": transport, "operation": operation, "concurrency": concurrency,
                   "payload-size": 32, "profile": "balanced", "min-connections": 1, "max-connections": 1,
                   "payload-pattern": "compressible", "request-timeout": "default", "admission": "disabled",
                   "maximum-recorded-operations": 30_000_000,
                   "recording": "formal", "warmup": 11, "duration": 21, "drain-timeout": 5, "metrics-port": 0,
                   "heartbeat-interval": 120, "heartbeat-check-interval": 120, "heartbeat-timeout": 600, **extra},
        aa=["PP"] * 4, ab=list(AB_PAIRS), aa_maximum_deviation=precision)


def sample_names(phase, orders):
    return [[(arm, f"{phase}-{index:02}-{pos}-{arm}.json") for pos, arm in enumerate(order, 1)]
            for index, order in enumerate(orders, 1)]


def read_sample(path, arm, source, cell, hosts):
    report = matrix.read_json(path)
    matrix.require(report["Workload"] == "SharpLink.LoadTest" and report["SchemaVersion"] == SCHEMA, "report schema/workload mismatch")
    matrix.require(report["SourceCommit"] == source, f"{arm} report source differs from the frozen arm")
    matrix.require(report["Runtime"].startswith(".NET 10."), "the steady comparison uses .NET 10")
    cfg = report["Configuration"]
    for key, value in {
        "Mode": 0, "Transport": matrix.TRANSPORTS[cell["transport"]], "Operation": cell["operation"],
        "ConcurrencyConfig": [cell["concurrency"]], "PayloadSize": 32, "PerformanceProfile": 0, "RecordingMode": 1,
        "MinConnections": 1, "MaxConnections": 1, "WarmupSeconds": 11, "DurationSeconds": 21, "DrainTimeoutSeconds": 5,
        "MetricsPort": 0, "DetailedSharedMemoryEvidence": False, "TailObserver": False,
        "PayloadPattern": "compressible", "RequestTimeoutMode": "default", "AdmissionMode": "disabled",
        "MaximumRecordedOperations": 30_000_000,
        "UseStaticEndpoints": False, "UseDynamicResolver": False, "HeartbeatIntervalSeconds": 120,
        "HeartbeatCheckIntervalSeconds": 120, "HeartbeatTimeoutSeconds": 600,
    }.items():
        matrix.require(cfg[key] == value, f"{path.name}: unexpected {key}")
    matrix.require(cfg.get("MaxSendQueueBytes") is None, "study uses the profile-default queue")
    one_way_rate = cell.get("oneway_rate", 0)
    matrix.require(cfg.get("OneWay32Rate", 0) == one_way_rate, "OneWay offered rate differs from the frozen workload")
    stage, = report["Results"]
    matrix.validate_result(stage, {"recorderVersion": RECORDER})
    matrix.require((stage["Operation"], stage["Concurrency"]) == (cell["operation"], cell["concurrency"]), "wrong cell")
    matrix.require(stage["Success"] >= cell["minimum_completed"] and stage["MeasurementDurationSeconds"] >= 20 and
                   stage["WarmupDurationSeconds"] >= 10, "insufficient completions or measured time")
    matrix.require(stage["SendQueueBackpressureRetries"] == 0, "unexpected backpressure retry")
    if one_way_rate:
        matrix.require(one_way_rate * .80 <= stage["Qps"] <= one_way_rate * 1.03,
                       "measured OneWay offered load is outside the frozen range")
    if cell["operation"] == "oneway32":
        matrix.require(stage["OneWay32BusinessCompleted"] == stage["Success"], "remote OneWay work did not match local acceptance")
        evidence = stage["OneWay32SettledEvidence"]
        matrix.number(stage["OneWay32ConfirmationSeconds"], "OneWay settlement")
    else:
        evidence = stage["Evidence"]
    identity = tuple(report[field] for field in HOST_FIELDS) + (stage["StopwatchFrequency"],)
    matrix.require(all(x not in ("", "unknown", None) for x in identity), "missing report environment")
    hosts.add(identity)
    matrix.require(len(hosts) == 1, "report runtime/host settings differ")
    n = stage["Success"]
    return dict(file=path.name, arm=arm, completed=n, measured_s=stage["MeasurementDurationSeconds"],
                p50_us=matrix.number(stage["P50Us"], "P50", positive=True),
                p99_us=matrix.number(stage["P99Us"], "P99", positive=True),
                p999_us=matrix.number(stage["P999Us"], "P99.9", positive=True),
                ops_per_s=matrix.number(stage["Qps"], "ops/s", positive=True),
                cpu_us_per_op=matrix.number(evidence["CpuMilliseconds"], "CPU", positive=True) * 1000 / n,
                managed_b_per_op=matrix.number(evidence["AllocatedBytes"], "managed bytes") / n,
                gen0=evidence["Gen0Collections"], remote_completed=stage.get("OneWay32BusinessCompleted"),
                confirmation_s=stage.get("OneWay32ConfirmationSeconds"))


def evaluate_aa(pairs, precision=METRICS):
    deviations = {}
    for key in precision:
        deviations[key] = max(max(p[key], q[key]) / min(p[key], q[key]) - 1.0 for p, q in pairs)
    # Compare with the pre-registered limit, allowing only arithmetic roundoff.
    passed = all(deviations[k] <= limit or math.isclose(deviations[k], limit, abs_tol=1e-12)
                 for k, limit in precision.items())
    return dict(status="AA_PASS" if passed else "INCONCLUSIVE", maximum_relative_deviation=deviations)


def interval(values):
    matrix.require(len(values) == 8, "exactly eight independent ordered pairs are required")
    mean = statistics.mean(values)
    half = 2.364625 * statistics.stdev(values) / math.sqrt(len(values))  # two-sided 95%, t(7)
    return mean, mean - half, mean + half


def evaluate_ab(pairs, noise, critical=("p99_us", "cpu_us_per_op"), *,
                extras=("p999_us", "ops_per_s"), compare_allocations=True):
    log_stats = {}
    for key in (*noise, *extras):
        logs = [math.log(n[key] / p[key]) for p, n in pairs]
        mean, lower, upper = interval(logs)
        log_stats[key] = dict(log_ratios=logs, ratio=math.exp(mean), ci95_ratio=[math.exp(lower), math.exp(upper)])
    regressions = [k for k in critical if
                   log_stats[k]["ci95_ratio"][0] > 1 and log_stats[k]["ratio"] - 1 > noise[k]]
    inconclusive = [k for k in critical if log_stats[k]["ci95_ratio"][1] > 1]
    allocation = {}
    if compare_allocations:
        allocations = [n["managed_b_per_op"] - p["managed_b_per_op"] for p, n in pairs]
        delta, low, high = interval(allocations)
        if low > 0:
            regressions.append("managed_b_per_op")
        if high > 0:
            inconclusive.append("managed_b_per_op")
        allocation = dict(managed_b_per_op=dict(paired_delta=allocations, mean_delta=delta, ci95_delta=[low, high]))
    return dict(status="REGRESSION" if regressions else "INCONCLUSIVE" if inconclusive else "SUPPORTS_NEXT_STAGE",
                regressions=regressions, unqualified=inconclusive, paired=log_stats, **allocation)


def aa_decision(cell, pairs):
    required = len(cell["aa"])
    matrix.require(0 < len(pairs) <= required, "missing or extra A/A pairs")
    aa = evaluate_aa(pairs, cell["aa_maximum_deviation"])
    if len(pairs) < required:
        # The maximum pairwise deviation can only rise. A failing complete pair
        # stops this attempt; an incomplete passing prefix never qualifies.
        matrix.require(aa["status"] == "INCONCLUSIVE" and all(
            evaluate_aa([pair], cell["aa_maximum_deviation"])["status"] == "AA_PASS"
            for pair in pairs[:-1]), "incomplete A/A without a first proven threshold breach")
    return dict(scope="paired statistics; same-machine execution must be established by the runner receipt",
                cell=cell, aa=aa, aa_raw=pairs, aa_pairs_completed=len(pairs), aa_pairs_required=required)


def ab_decision(aa_result, pairs):
    cell = aa_result["cell"]
    aa = aa_result["aa"]
    matrix.require(aa["status"] == "AA_PASS" and
                   aa_result["aa_pairs_completed"] == len(cell["aa"]) and len(pairs) == len(cell["ab"]),
                   "A/B requires all planned qualifying A/A and A/B pairs")
    normalized = [(left, right) if left["arm"] == "P" else (right, left) for left, right in pairs]
    if cell.get("oneway_rate"):
        matrix.require(all(evaluate_aa([(p, n)], {"ops_per_s": cell["aa_maximum_deviation"]["ops_per_s"]})["status"] ==
                           "AA_PASS" for p, n in normalized), "paced OneWay arms did not offer the same rate")
    critical = cell.get("critical", ("p99_us", "cpu_us_per_op"))
    return dict(aa_result, ab=evaluate_ab(normalized, aa["maximum_relative_deviation"], critical,
                                        extras=cell.get("extras", ("p999_us", "ops_per_s")),
                                        compare_allocations=cell.get("compare_allocations", True)), ab_raw=pairs)


def require_report_population(root, phase, planned):
    expected = {filename for pair in planned for _, filename in pair}
    matrix.require({p.name for p in root.glob(f"{phase}-*.json")} == expected, f"{phase}: missing or extra raw sample")


def evaluate(root, cell, baseline, candidate=None, *, reader=read_sample):
    hosts = set()
    sources = {"P": baseline, "N": candidate}
    planned = sample_names("aa", cell["aa"])
    present = sum(1 for _ in root.glob("aa-*.json"))
    matrix.require(0 < present <= 2 * len(planned) and present % 2 == 0, "aa: missing or extra raw sample")

    def read(phase, pairs):
        require_report_population(root, phase, pairs)
        return [[reader(root / name, arm, sources[arm], cell, hosts) for arm, name in pair] for pair in pairs]

    result = aa_decision(cell, read("aa", planned[:present // 2]))
    if result["aa"]["status"] != "AA_PASS":
        require_report_population(root, "ab", ())
    elif candidate is not None:
        result = ab_decision(result, read("ab", sample_names("ab", cell["ab"])))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("unary32", "oneway32"))
    parser.add_argument("transport", choices=("tcp", "sharedmemory"))
    parser.add_argument("concurrency", type=int, choices=(1, 8))
    parser.add_argument("--reports", type=Path, help="omit for the dry-run plan")
    parser.add_argument("--baseline")
    parser.add_argument("--candidate")
    args = parser.parse_args()
    cell = sample_plan(args.transport, args.concurrency, args.operation)
    try:
        if args.reports:
            if not args.baseline:
                parser.error("--baseline is required with --reports")
            result = evaluate(args.reports, cell, args.baseline, args.candidate)
        else:
            result = dict(cell=cell, files=dict(aa=sample_names("aa", cell["aa"]), ab=sample_names("ab", cell["ab"])))
    except (OSError, KeyError, TypeError, ValueError) as error:
        print(json.dumps({"status": "INCONCLUSIVE", "invalid_sample": str(error)}))
        raise SystemExit(2)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
