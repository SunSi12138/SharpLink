#!/usr/bin/env python3
"""Validate the identity of both NuGet package archives, not only their filenames."""

import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from zipfile import BadZipFile, ZipFile


def metadata(archive_path: Path, package_id: str) -> tuple[str, str, str | None]:
    with ZipFile(archive_path) as archive:
        names = [name for name in archive.namelist() if name.endswith(".nuspec")]
        if names != [f"{package_id}.nuspec"]:
            raise ValueError(f"{archive_path}: expected one root {package_id}.nuspec; found {names}")
        root = ET.fromstring(archive.read(names[0]))
    meta = root.find("./{*}metadata")
    if meta is None:
        raise ValueError(f"{archive_path}: nuspec metadata is missing")

    def required(name: str) -> str:
        node = meta.find(f"./{{*}}{name}")
        if node is None or not node.text or not node.text.strip():
            raise ValueError(f"{archive_path}: nuspec {name} is missing")
        return node.text.strip()

    repository = meta.find("./{*}repository")
    commit = repository.get("commit") if repository is not None else None
    return required("id"), required("version"), commit


def verify_pair(nupkg: Path, snupkg: Path, package_id: str, version: str, commit: str) -> None:
    for path, suffix in ((nupkg, "nupkg"), (snupkg, "snupkg")):
        expected_name = f"{package_id}.{version}.{suffix}"
        if path.name != expected_name:
            raise ValueError(f"{path}: expected exact filename {expected_name}")
        actual_id, actual_version, actual_commit = metadata(path, package_id)
        if actual_id != package_id or actual_version != version:
            raise ValueError(
                f"{path}: nuspec identity {actual_id}/{actual_version} does not match {package_id}/{version}"
            )
        if suffix == "nupkg" and actual_commit != commit:
            raise ValueError(f"{path}: nuspec repository commit does not match {commit}")
        if suffix == "snupkg" and actual_commit is not None and actual_commit != commit:
            raise ValueError(f"{path}: symbol package repository commit differs from nupkg")


def main() -> int:
    if len(sys.argv) != 6:
        print("usage: verify-package-identities.py NUPKG SNUPKG ID VERSION COMMIT", file=sys.stderr)
        return 2
    try:
        verify_pair(Path(sys.argv[1]), Path(sys.argv[2]), *sys.argv[3:])
    except (ValueError, OSError, BadZipFile, ET.ParseError) as exc:
        print(f"NuGet package identity verification failed: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
