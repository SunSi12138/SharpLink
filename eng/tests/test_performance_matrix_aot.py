"""Exercises the actual performance matrix shell with an explicit no-load dotnet shim."""
import json
import os
import pathlib
import subprocess
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / "run-performance-matrix.sh"
DOTNET = r'''#!/usr/bin/env python3
import json, os, pathlib, sys
args = sys.argv[1:]
command = args[0]
marker = pathlib.Path(os.environ["MATRIX_MARKER"]).read_text().strip()
events = pathlib.Path(os.environ["MATRIX_EVENTS"])
def log(kind, extra=None):
    with events.open("a") as f:
        f.write(json.dumps({"kind": kind, "marker": marker, "extra": extra}) + "\n")
def report(argv):
    if "--json-output" in argv:
        path = pathlib.Path(argv[argv.index("--json-output") + 1])
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps({"source_marker": marker}))
if command == "build":
    log("build")
elif command == "run":
    log("jit")
    report(args)
elif command == "publish":
    project = args[1]
    name = pathlib.Path(project).name
    output = pathlib.Path(args[args.index("-o") + 1])
    log("publish", name)
    if os.environ.get("MATRIX_FAIL_PUBLISH") == "1":
        sys.exit(17)
    output.mkdir(parents=True, exist_ok=True)
    exe = output / (name + (".exe" if os.environ.get("MATRIX_WIN") == "1" else ""))
    exe.write_text('#!/usr/bin/env python3\n'
        'import json,os,pathlib,sys\n'
        'with open(os.environ["MATRIX_EVENTS"],"a") as log:\n'
        ' log.write(json.dumps({"kind":"aot","marker":' + repr(marker) +
        ',"extra":' + repr(name) + '})+"\\n")\n'
        'argv=sys.argv[1:]\n'
        'if "--json-output" in argv:\n'
        ' path=pathlib.Path(argv[argv.index("--json-output")+1]);'
        ' path.parent.mkdir(parents=True,exist_ok=True);'
        ' path.write_text(json.dumps({"source_marker":' + repr(marker) + '}))\n')
    exe.chmod(0o755)
else:
    raise SystemExit("unexpected shim command: " + repr(args))
'''
UNAME = """#!/usr/bin/env python3
import os, sys
if "-m" in sys.argv:
    print(os.environ.get("MATRIX_ARCH", "x86_64"))
elif os.environ.get("MATRIX_WIN") == "1":
    print("MINGW64_NT-10.0")
else:
    print(os.environ.get("MATRIX_OS", "Linux"))
"""


class AotMatrixInvocations(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = pathlib.Path(self.tmp.name)
        (self.root / "test/SharpLink.LoadTest").mkdir(parents=True)
        (self.root / "test/SharpLink.StreamLoadTest").mkdir(parents=True)
        bindir = self.root / "bin"
        bindir.mkdir()
        for name, script in (("dotnet", DOTNET), ("uname", UNAME)):
            path = bindir / name
            path.write_text(script)
            path.chmod(0o755)
        self.marker = self.root / "marker.txt"
        self.events_file = self.root / "events.jsonl"
        self.output = self.root / "output"
        self.env = dict(os.environ, PATH=f'{bindir}:{os.environ["PATH"]}',
            MATRIX_MARKER=str(self.marker), MATRIX_EVENTS=str(self.events_file),
            SHARPLINK_MATRIX_ROOT=str(self.root), SHARPLINK_MATRIX_OUTPUT=str(self.output),
            SHARPLINK_MATRIX_RUNTIMES="jit,aot", SHARPLINK_MATRIX_REPETITIONS="2",
            SHARPLINK_MATRIX_WORKLOADS="unary,streams", SHARPLINK_MATRIX_TRANSPORTS="anonymous",
            SHARPLINK_MATRIX_PROFILES="balanced", SHARPLINK_MATRIX_PAYLOADS="0",
            SHARPLINK_MATRIX_CONCURRENCY="1")

    def run_matrix(self, marker, **env):
        self.marker.write_text(marker)
        # The macOS CI job sets /bin/bash explicitly to exercise Apple's
        # system Bash 3.2, not an optional Homebrew Bash 4+ binary.
        shell = self.env.get("SHARPLINK_MATRIX_SHELL", "bash")
        return subprocess.run([shell, str(SCRIPT)], env=dict(self.env, **env),
            capture_output=True, text=True)

    def events(self):
        return [json.loads(row) for row in self.events_file.read_text().splitlines()]

    def test_reusing_output_across_invocations_republishes_each_project(self):
        first = self.run_matrix("A")
        self.assertEqual(first.returncode, 0, first.stderr)
        count = len(self.events())
        self.assertEqual(sum(e["kind"] == "publish" for e in self.events()), 2)
        second = self.run_matrix("B")
        self.assertEqual(second.returncode, 0, second.stderr)
        fresh = self.events()[count:]
        self.assertEqual(sum(e["kind"] == "publish" for e in fresh), 2)
        self.assertTrue(all(e["marker"] == "B" for e in fresh if e["kind"] == "aot"))
        self.assertEqual(sum(e["kind"] == "aot" for e in fresh), 4)
        self.assertEqual(sum(e["kind"] == "jit" for e in fresh), 4)

    def test_jit_only_never_publishes(self):
        result = self.run_matrix("J", SHARPLINK_MATRIX_RUNTIMES="jit")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(any(e["kind"] == "publish" for e in self.events()))

    def test_failed_publish_never_runs_stale_binary(self):
        self.assertEqual(self.run_matrix("A").returncode, 0)
        old_count = len(self.events())
        result = self.run_matrix("B", MATRIX_FAIL_PUBLISH="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any(e["kind"] == "aot" for e in self.events()[old_count:]))

    def test_macos_jit_only_works_without_bash_four_features(self):
        result = self.run_matrix(
            "MacJit", SHARPLINK_MATRIX_RUNTIMES="jit",
            MATRIX_OS="Darwin", MATRIX_ARCH="arm64")
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.events()
        self.assertEqual(sum(e["kind"] == "build" for e in calls), 2)
        self.assertEqual(sum(e["kind"] == "jit" for e in calls), 4)
        self.assertEqual(sum(e["kind"] == "publish" for e in calls), 0)

    def test_macos_aot_republishes_each_project_once_per_invocation(self):
        first = self.run_matrix(
            "MacA", SHARPLINK_MATRIX_RUNTIMES="aot",
            MATRIX_OS="Darwin", MATRIX_ARCH="arm64")
        self.assertEqual(first.returncode, 0, first.stderr)
        self.assertEqual(sum(e["kind"] == "publish" for e in self.events()), 2)
        before = len(self.events())
        second = self.run_matrix(
            "MacB", SHARPLINK_MATRIX_RUNTIMES="aot",
            MATRIX_OS="Darwin", MATRIX_ARCH="arm64")
        self.assertEqual(second.returncode, 0, second.stderr)
        fresh = self.events()[before:]
        self.assertEqual(sum(e["kind"] == "publish" for e in fresh), 2)
        aot = [e for e in fresh if e["kind"] == "aot"]
        self.assertEqual(len(aot), 4)
        self.assertTrue(all(e["marker"] == "MacB" for e in aot))

    def test_windows_exe_suffix_is_respected(self):
        result = self.run_matrix("W", MATRIX_WIN="1")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(sum(e["kind"] == "publish" for e in self.events()), 2)
        self.assertTrue((self.output / "aot/SharpLink.LoadTest/SharpLink.LoadTest.exe").is_file())


if __name__ == "__main__":
    unittest.main()
