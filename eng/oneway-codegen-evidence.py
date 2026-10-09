#!/usr/bin/env python3
"""Parse raw JIT output or collect Linux NativeAOT symbol/disassembly evidence."""
import argparse
import hashlib
import json
import pathlib
import re
import subprocess


JIT_HEADER = re.compile(r"^; Assembly listing for method (.+)$", re.MULTILINE)
JIT_SIZE = re.compile(r"^; Total bytes of code (\d+)", re.MULTILINE)
JIT_BRANCH = re.compile(r"^\s+(j(?!mp\b)[a-z]+|loop[a-z]*|b\.[a-z]+|cbz|cbnz|tbz|tbnz)\s", re.MULTILINE)


def parse_jit(text):
    headers = list(JIT_HEADER.finditer(text))
    result = []
    for index, header in enumerate(headers):
        block = text[header.start():headers[index + 1].start() if index + 1 < len(headers) else len(text)]
        size = JIT_SIZE.search(block)
        result.append({"method": header.group(1), "nativeBytes": int(size.group(1)) if size else None,
                       "conditionalBranchInstructions": len(JIT_BRANCH.findall(block)),
                       "complete": size is not None,
                       "coreMoveNext": "InvokeOneWay" in header.group(1) and "MoveNext" in header.group(1)})
    return result


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def command(args):
    return subprocess.run(args, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True).stdout


def collect_aot(binary, symbols, raw):
    raw.mkdir(parents=True, exist_ok=True)
    nm = command(["nm", "-S", "--defined-only", "--demangle", str(symbols)])
    sections = command(["size", "-A", str(binary)])
    (raw / "symbols.txt").write_text(nm)
    (raw / "sections.txt").write_text(sections)
    entries = []
    for line in nm.splitlines():
        match = re.match(r"^([0-9a-fA-F]+)\s+([0-9a-fA-F]+)\s+([tTwW])\s+(.+)$", line)
        if not match or "OneWay" not in match[4]:
            continue
        address, size = int(match[1], 16), int(match[2], 16)
        if size == 0:
            continue
        disassembly = command(["objdump", "-d", "--no-show-raw-insn", f"--start-address={address}",
                               f"--stop-address={address + size}", str(binary)])
        raw_name = f"{address:x}-{size:x}.asm"
        (raw / raw_name).write_text(disassembly)
        opcodes = re.findall(r"^\s*[0-9a-f]+:\s+([a-z][a-z0-9.]*)\b", disassembly, re.MULTILINE)
        branches = sum(bool(re.fullmatch(r"j(?!mp$)[a-z]+|loop[a-z]*|b\.[a-z]+|cbz|cbnz|tbz|tbnz", op)) for op in opcodes)
        entries.append({"symbol": match[4], "address": hex(address), "nativeBytes": size,
                        "conditionalBranchInstructions": branches, "rawDisassembly": raw_name,
                        "coreMoveNext": "InvokeOneWay" in match[4] and "MoveNext" in match[4]})
    if not entries:
        raise SystemExit("No OneWay text symbols found. Publish with -p:StripSymbols=false, or pass --symbols <binary.dbg>. Do not treat this as zero code size.")
    unique = {(entry["address"], entry["nativeBytes"]) for entry in entries}
    return {"binary": str(binary), "binarySha256": sha(binary), "binaryFileBytes": binary.stat().st_size,
            "symbolFile": str(symbols), "symbolFileSha256": sha(symbols),
            "methods": entries, "uniqueOneWayNativeBytes": sum(size for _, size in unique),
            "sections": sections, "rawDirectory": str(raw)}


def self_test():
    sample = """; Assembly listing for method A:<InvokeOneWayCoreAsync>d__1:MoveNext():this (FullOpts)
       je SHORT G_M000_IG01
       jmp G_M000_IG02
; Total bytes of code 123
; Assembly listing for method B:InvokeOneWayAsync():this (FullOpts)
       jne G_M000_IG02
; Total bytes of code 21
"""
    parsed = parse_jit(sample)
    assert len(parsed) == 2 and parsed[0]["nativeBytes"] == 123
    assert parsed[0]["conditionalBranchInstructions"] == 1 and parsed[0]["coreMoveNext"]
    assert not parsed[1]["coreMoveNext"]
    assert parse_jit("; Assembly listing for method incomplete\n")[0]["nativeBytes"] is None
    print("OneWay codegen parser self-tests passed.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-test", action="store_true")
    source = parser.add_mutually_exclusive_group()
    source.add_argument("--jit-log", type=pathlib.Path)
    source.add_argument("--native-binary", type=pathlib.Path)
    parser.add_argument("--symbols", type=pathlib.Path)
    parser.add_argument("--raw-dir", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path)
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return
    if not args.output or not (args.jit_log or args.native_binary):
        parser.error("--output and either --jit-log or --native-binary are required")
    if args.jit_log:
        methods = parse_jit(args.jit_log.read_text())
        if not methods:
            raise SystemExit("No JIT method listings found; verify JitDisasm filter and exercise the method.")
        result = {"kind": "jit", "source": str(args.jit_log), "sourceSha256": sha(args.jit_log), "methods": methods,
                  "coreMoveNextCount": sum(method["coreMoveNext"] for method in methods),
                  "complete": all(method["complete"] for method in methods)}
    else:
        if not args.raw_dir:
            parser.error("NativeAOT collection requires --raw-dir")
        result = {"kind": "nativeaot", **collect_aot(args.native_binary, args.symbols or args.native_binary, args.raw_dir)}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(f"Wrote {len(result['methods'])} native method records to {args.output}")


if __name__ == "__main__":
    main()
