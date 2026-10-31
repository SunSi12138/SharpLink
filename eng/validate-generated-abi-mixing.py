#!/usr/bin/env python3
"""Fail-closed validation for negative generated ABI package-mixing fixtures."""

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

EXPECTED = {
    "new-generator-old-abstractions": {
        "name": "INewGeneratorOldAbstractionsService",
        "sdk": "[$(SharpLinkCurrentVersion)]",
        "abstractions": "[1.1.1]",
    },
    "old-generator-new-abstractions": {
        "name": "IOldGeneratorNewAbstractionsService",
        "sdk": "[1.1.1]",
        "abstractions": "[$(SharpLinkCurrentVersion)]",
    },
}

# Restore rejections are only valid when NuGet diagnoses the pinned SharpLink
# dependency graph, not when a file, network feed or an unrelated package fails.
RESTORE_ERROR = re.compile(
    r"\berror\s+(?:NU1605|NU1107)\b[^:\n]*:\s*[^\n]*"
    r"SharpLink\.(?:Sdk|Abstractions)\b", re.IGNORECASE
)
# Compiler rejects are only accepted from generated code mentioning current or
# legacy generated ABI surface. Unrelated CS0246 in fixture source must fail.
ABI_MEMBER = re.compile(
    r"\b(?:IRpcStub|IRpcGeneratedServerBridge|IRpcChannel|IRpcContractChannel|IRpcCodecProvider|"
    r"SharpLinkGeneratedContractDescriptor|SharpLinkGeneratedManifestVersions|"
    r"IRpcGeneratedCodecFactory|ISharpLinkGeneratedAssemblyManifest|"
    r"InvokeUnaryAsync|InvokeOneWayAsync|InvokeClientStreamingAsync|"
    r"InvokeServerStreamingAsync|InvokeDuplexStreamingAsync|"
    r"SharpLinkGeneratedAssemblyManifestAttribute)\b"
)
BUILD_ERROR = re.compile(r"\berror\s+CS\d{4}:", re.IGNORECASE)
GENERATED_CONTEXT = re.compile(
    r"(?:\.g\.cs|\.generated\.cs|SharpLink\.Generator[/\\]|SharpLink\.Generated)",
    re.IGNORECASE
)


def validate_fixture(project: Path, name: str) -> None:
    if name not in EXPECTED:
        raise ValueError(f"unknown ABI mixing fixture {name}")
    if not project.is_file():
        raise ValueError(f"missing ABI fixture project: {project}")
    source = project.parent / "MixedContract.cs"
    if not source.is_file():
        raise ValueError(f"missing ABI fixture source: {source}")

    expected = EXPECTED[name]
    root = ET.parse(project).getroot()
    references = {
        item.get("Include"): item.get("Version")
        for item in root.findall(".//PackageReference")
    }
    wanted = {
        "SharpLink.Sdk": expected["sdk"],
        "SharpLink.Abstractions": expected["abstractions"],
    }
    if references != wanted:
        raise ValueError(f"{name}: expected exact package graph {wanted}, found {references}")
    actual_source = source.read_text(encoding="utf-8")
    for token in ("[RpcContract]", "[RpcService]", expected["name"], "ValueTask<int> AddAsync"):
        if token not in actual_source:
            raise ValueError(f"{source}: required ABI test input {token!r} is missing")


def is_expected_rejection(phase: str, log: str) -> bool:
    if phase == "restore":
        return bool(RESTORE_ERROR.search(log))
    if phase == "build":
        return any(
            BUILD_ERROR.search(line)
            and ABI_MEMBER.search(line)
            and GENERATED_CONTEXT.search(line)
            for line in log.splitlines()
        )
    raise ValueError(f"unknown phase {phase}")


def make_positive(project: Path, destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    text = project.read_text(encoding="utf-8")
    if text.count('Version="[1.1.1]"') != 1:
        raise ValueError(f"{project}: expected one published version pin")
    (destination / project.name).write_text(
        text.replace('Version="[1.1.1]"', 'Version="[$(SharpLinkCurrentVersion)]"'),
        encoding="utf-8",
    )
    (destination / "MixedContract.cs").write_bytes(
        (project.parent / "MixedContract.cs").read_bytes()
    )


def main() -> int:
    try:
        command = sys.argv[1]
        if command == "fixture" and len(sys.argv) == 4:
            validate_fixture(Path(sys.argv[2]), sys.argv[3])
        elif command == "positive" and len(sys.argv) == 4:
            make_positive(Path(sys.argv[2]), Path(sys.argv[3]))
        elif command == "classify" and len(sys.argv) == 4:
            phase, log_path = sys.argv[2], Path(sys.argv[3])
            if not is_expected_rejection(phase, log_path.read_text(encoding="utf-8", errors="replace")):
                raise ValueError(
                    f"{log_path}: no phase-specific SharpLink package/ABI diagnostic "
                    f"from a generated source location"
                )
        else:
            raise ValueError("usage: validator {fixture project name|positive project dir|classify phase log}")
    except (IndexError, ValueError, OSError, ET.ParseError) as error:
        print(f"ABI mixing gate: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
