#!/usr/bin/env python3
"""Bounded official dotnet-trace capture, separate from untraced timing runs."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--trace-tool", required=True, help="installed, pinned dotnet-trace executable")
    parser.add_argument("--output", required=True, help=".nettrace path")
    parser.add_argument("--seconds", type=int, default=120)
    parser.add_argument("--max-mib", type=int, default=256)
    parser.add_argument("command", nargs=argparse.REMAINDER, help="-- dotnet harness.dll arguments...")
    args = parser.parse_args()
    command = args.command[1:] if args.command and args.command[0] == "--" else args.command
    if not command or not 1 <= args.seconds <= 300 or not 1 <= args.max_mib <= 1024:
        parser.error("provide a command, seconds 1..300, and max-mib 1..1024")
    output = Path(args.output).resolve()
    if output.exists():
        parser.error("output exists; use a fresh per-process output filename")
    output.parent.mkdir(parents=True, exist_ok=True)
    version = subprocess.run([args.trace_tool, "--version"], capture_output=True, text=True, check=True).stdout.strip()
    duration = f"00:{args.seconds // 60:02d}:{args.seconds % 60:02d}"
    cmd = [args.trace_tool, "collect", "--profile", "gc-verbose", "--providers", "SharpLink-Issue739:0xffffffffffffffff:5", "--buffersize", "256", "--duration", duration, "--output", str(output), "--"] + command
    start = time.monotonic()
    utc_start = datetime.datetime.now(datetime.timezone.utc).isoformat()
    abort = None
    with open(str(output) + ".collector.log", "w") as log:
        process = subprocess.Popen(cmd, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        while process.poll() is None:
            if time.monotonic() - start > args.seconds + 60:
                abort = "wall-time bound exceeded (includes 60s drain allowance)"
            elif output.exists() and output.stat().st_size > args.max_mib * 1024 * 1024:
                abort = "trace-size bound exceeded"
            if abort:
                os.killpg(process.pid, signal.SIGINT)
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()
                break
            time.sleep(0.25)
    meta = {
        "schemaVersion": 1, "tool": "dotnet-trace", "toolVersion": version, "command": cmd,
        "providerConfiguration": {"profile": "gc-verbose", "additionalProviders": "SharpLink-Issue739:0xffffffffffffffff:5", "bufferMiB": 256},
        "launchMode": "dotnet-trace child process; runtime startup suspended until EventPipe configured",
        "startReadyStatus": "verify Start marker present in decoded trace; startup launch requested",
        "stopDrainStatus": "tool returned success; full drain not independently proven" if process.returncode == 0 and abort is None else "failed-or-aborted",
        "utcStart": utc_start, "utcEnd": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "elapsedWallSeconds": time.monotonic() - start, "exitCode": process.returncode,
        "abortReason": abort, "maxMiB": args.max_mib, "maxRecordingSeconds": args.seconds,
        "targetRuntimeCommand": command,
        "traceSha256": hashlib.file_digest(open(output, "rb"), "sha256").hexdigest() if output.exists() else None,
        "tracedTimingIsDiagnosticOnly": True,
    }
    Path(str(output) + ".capture.json").write_text(json.dumps(meta, indent=2) + "\n")
    if abort or process.returncode != 0:
        raise SystemExit(f"Capture failed: {abort or process.returncode}; retain log and partial trace as failure evidence")


if __name__ == "__main__":
    main()
