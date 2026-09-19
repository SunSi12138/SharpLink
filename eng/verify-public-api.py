#!/usr/bin/env python3
"""Compare the API of the actual shipping packages with the reviewed 2.0 surface."""

import argparse
import difflib
from pathlib import Path
import subprocess
import tempfile
import zipfile

PACKAGES = (
    "SharpLink.Abstractions",
    "SharpLink.Runtime",
    "SharpLink.Client",
    "SharpLink.Server",
    "SharpLink.Hosting",
    "SharpLink.Sdk",
    "SharpLink.Serializer.SharpPack",
    "SharpLink.Compression.Zstd",
)


def main():
    root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("packages", type=Path)
    parser.add_argument("--version", default="2.0.0")
    parser.add_argument("--baseline", type=Path, default=root / "eng/public-api/2.0.0")
    parser.add_argument("--output", type=Path, default=root / "artifacts/public-api/current")
    parser.add_argument("--update", action="store_true", help="Write a candidate baseline for explicit review")
    options = parser.parse_args()
    options.output.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="sharplink-public-api-") as temporary:
        assemblies = Path(temporary)
        for package in PACKAGES:
            with zipfile.ZipFile(options.packages / f"{package}.{options.version}.nupkg") as archive:
                expected = f"lib/net10.0/{package}.dll"
                matches = [name for name in archive.namelist() if name == expected]
                if len(matches) != 1:
                    raise ValueError(f"{package} must contain exactly one {expected}")
                (assemblies / f"{package}.dll").write_bytes(archive.read(expected))

        subprocess.run(
            ["dotnet", str(root / "eng/SharpLink.PublicApi/bin/Release/net10.0/SharpLink.PublicApi.dll"),
             str(assemblies), str(options.output)],
            check=True,
        )

    expected_files = {f"{package}.api.txt" for package in PACKAGES}
    actual_files = {path.name for path in options.output.glob("*.api.txt")}
    if actual_files != expected_files:
        raise ValueError(f"Generated API inventory mismatch: {actual_files ^ expected_files}")
    if options.update:
        options.baseline.mkdir(parents=True, exist_ok=True)
        for name in sorted(expected_files):
            (options.baseline / name).write_bytes((options.output / name).read_bytes())
        print("Wrote eight candidate API baselines; review the diff before committing.")
        return

    baseline_files = {path.name for path in options.baseline.glob("*.api.txt")}
    if baseline_files != expected_files:
        raise ValueError(f"Reviewed API inventory mismatch: {baseline_files ^ expected_files}")
    differences = []
    for name in sorted(expected_files):
        expected = (options.baseline / name).read_text(encoding="utf-8")
        actual = (options.output / name).read_text(encoding="utf-8")
        if expected != actual:
            differences.extend(difflib.unified_diff(
                expected.splitlines(keepends=True), actual.splitlines(keepends=True),
                fromfile=f"reviewed/{name}", tofile=f"packed/{name}"))
    if differences:
        diff = options.output / "public-api.diff"
        diff.write_text("".join(differences), encoding="utf-8")
        raise ValueError(f"Unreviewed public API changes; inspect {diff}")
    print("Verified the complete public/protected API, including SDK type forwards, of all eight packages.")


if __name__ == "__main__":
    main()
