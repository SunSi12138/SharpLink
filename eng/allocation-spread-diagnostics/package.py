#!/usr/bin/env python3
"""Retain every evidence byte; split the archive into independently downloadable parts."""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import shutil
import tarfile

PART_BYTES = 16 * 1024 * 1024
UPLOAD_SLOTS = 8


def digest(path):
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(chunk)
    return result.hexdigest()


def package(source, output):
    source, output = Path(source).resolve(), Path(output).resolve()
    if not source.is_dir() or output == source or source in output.parents:
        raise ValueError("Output must be outside the existing source evidence directory")
    output.mkdir(parents=True, exist_ok=False)
    archive = output / "evidence.tar.gz"
    with archive.open("wb") as raw, gzip.GzipFile(fileobj=raw, mode="wb", filename="", mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode="w|") as tar:
            for path in sorted(source.rglob("*")):
                if path.is_file():
                    if path.is_symlink():
                        raise ValueError("Evidence must not contain symlinks")
                    info = tar.gettarinfo(str(path), str(path.relative_to(source)))
                    info.mtime = 0
                    info.uid = info.gid = 0
                    info.uname = info.gname = ""
                    with path.open("rb") as stream:
                        tar.addfile(info, stream)
    parts = []
    with archive.open("rb") as stream:
        while block := stream.read(PART_BYTES):
            part = output / f"part-{len(parts):02}.bin"
            part.write_bytes(block)
            parts.append({"file": part.name, "bytes": len(block), "sha256": digest(part)})
    index = {"schemaVersion": 1, "completeTransfer": len(parts) <= UPLOAD_SLOTS,
             "archiveSha256": digest(archive), "archiveBytes": archive.stat().st_size,
             "partBytes": PART_BYTES, "uploadSlots": UPLOAD_SLOTS, "parts": parts,
             "overflow": "No bytes discarded. If parts exceed upload slots, the full archive is uploaded separately; tool-based transfer is incomplete."}
    (output / "index.json").write_text(json.dumps(index, indent=2) + "\n", encoding="utf-8")
    for name in ("summary.json", "identity.json", "files.json", "execution-index.json"):
        if (source / name).is_file():
            shutil.copyfile(source / name, output / name)
    if not index["completeTransfer"]:
        (output / "overflow.txt").write_text("Full evidence retained; bounded-part transfer incomplete.\n", encoding="utf-8")
        raise ValueError("Evidence exceeds artifact upload slots; full archive retained for overflow upload")
    return index


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    package(args.source, args.output)
