"""Runtime and CPU checks specific to the controlled profile, not legacy local runs."""
from pathlib import Path

SDK = "10.0.102"
RUNTIME = "10.0.11"


def parse_cpu_list(value):
    result = set()
    for part in value.split(","):
        if "-" in part:
            start, end = map(int, part.split("-"))
            result.update(range(start, end + 1))
        else:
            result.add(int(part))
    return result


def validate_metadata(data, manifest):
    contract = manifest["runtimeContract"]
    root = Path(contract["privateRoot"]).resolve()
    core = Path(data["coreFrameworkLocation"]).resolve()
    expected_core = root / "shared" / "Microsoft.NETCore.App" / RUNTIME / "System.Private.CoreLib.dll"
    if data["runtimeVersion"] != RUNTIME or core != expected_core:
        raise ValueError(f"Runtime escaped exact private .NET {RUNTIME}: {core}")
    if Path(data["processPath"]).resolve() != root / "dotnet":
        raise ValueError("Executing host is not the private dotnet host")
    selected = set(contract["selectedCpus"])
    if parse_cpu_list(data["allowedCpuList"]) != selected or data["processorCount"] != len(selected):
        raise ValueError("Effective process CPU affinity/count differs from frozen topology selection")
    if data["architecture"] != "X64" or data["serverGC"]:
        raise ValueError("Controlled profile requires x64 and default workstation GC")
    if data["extraPerformanceEnvironment"] or any(value is not None for value in data["performanceEnvironment"].values()):
        raise ValueError("Unexpected tiering/PGO/GC/processor override")
