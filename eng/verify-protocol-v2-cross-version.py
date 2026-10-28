#!/usr/bin/env python3
"""Real published-package/current-package process gate; never equate CLR ABI with wire identity."""
import argparse
import errno
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import struct
import subprocess
import tempfile
import threading
import time
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parent.parent
FIXTURE = ROOT / "test/fixtures/protocol-v2-cross-version"
ASSEMBLY = "SharpLink.ProtocolV2CrossVersion"
PUBLISHED_VERSION = "2.0.3"
PUBLISHED_COMMIT = "4f9295ffb37ce68985051c11bfd9afc0e23166f3"
NUGET = "https://api.nuget.org/v3/index.json"
PACKAGES = {"SharpLink." + name for name in ("Abstractions", "Runtime", "Client", "Server", "Sdk")}
# Every negative has a real same-version, same-source positive control.
MATRIX = (
    ("current", "current", "positive"),
    ("published", "published", "positive"),
    ("incompatible", "incompatible", "positive"),
    ("published", "current", "positive"),
    ("current", "published", "positive"),
    ("incompatible", "current", "reject"),
    ("current", "incompatible", "reject"),
    ("incompatible", "published", "reject"),
    ("published", "incompatible", "reject"),
)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def git(*args):
    return subprocess.check_output(["git", "-C", str(ROOT), *args], text=True).strip()


def run(command, log, env=None, timeout=180):
    with log.open("w", encoding="utf-8") as stream:
        stream.write(json.dumps({"command": command, "started": datetime.now(timezone.utc).isoformat()}) + "\n")
        stream.flush()
        try:
            result = subprocess.run(command, stdout=stream, stderr=subprocess.STDOUT, env=env, timeout=timeout)
        except subprocess.TimeoutExpired:
            stream.write("\nCONTROLLER_TIMEOUT (not a compatibility rejection)\n")
            raise
        stream.write(f"\nEXIT_CODE {result.returncode}\n")
    require(result.returncode == 0, f"Command failed ({result.returncode}); see {log}")


def package_record(path, package, version, commit):
    with zipfile.ZipFile(path) as archive:
        nuspecs = [name for name in archive.namelist() if name.lower().endswith(".nuspec")]
        require(nuspecs == [package + ".nuspec"], f"Ambiguous nuspec in {path}")
        metadata = ET.fromstring(archive.read(nuspecs[0])).find("{*}metadata")
        require(metadata is not None, f"Missing metadata: {path}")
        require((metadata.findtext("{*}id"), metadata.findtext("{*}version")) == (package, version),
                f"Wrong package identity: {path}")
        repository = metadata.find("{*}repository")
        require(repository is not None and repository.get("commit") == commit, f"Wrong package source commit: {path}")
        dll_path = f"lib/net10.0/{package}.dll"
        require(archive.namelist().count(dll_path) == 1, f"Missing/ambiguous runtime DLL: {path}")
        record = {"id": package, "version": version, "source_commit": commit,
                  "package_sha256": sha(path.read_bytes()), "dll_sha256": sha(archive.read(dll_path))}
        if package == "SharpLink.Sdk":
            record["generator_sha256"] = sha(archive.read("analyzers/dotnet/cs/SharpLink.Generator.dll"))
        return record


def read_identity(log):
    lines = [line.removeprefix("IDENTITY ") for line in log.read_text().splitlines() if line.startswith("IDENTITY ")]
    require(len(lines) == 1, f"Expected exactly one loaded-identity record: {log}")
    return json.loads(lines[0])


def validate_identity(identity, records, version, api, bin_dir):
    require(identity["api"] == api and identity["protocol"] == 2, "Wrong generated API/protocol")
    require(identity["minor"] == identity["minimumMinor"] == 4, "Unexpected protocol-minor contract")
    require(identity["generator"].split("+")[0] == version, "Wrong matching generator version")
    require(identity["abi"] and identity["rpcAssemblyHash"] and len(identity["contractIds"]) == 1,
            "Missing generated ABI/wire identity")
    loaded = identity["packages"]
    require(len(loaded) == len(PACKAGES) and {p["name"] for p in loaded} == PACKAGES, "Wrong loaded package closure")
    for package in loaded:
        require(package["version"] == version + ".0", "Wrong loaded assembly version")
        record = records[package["name"]]
        require(package["sha256"] == record["dll_sha256"], "Loaded DLL differs from exact NuGet package")
        require(package["informationalVersion"].endswith("+" + record["source_commit"]),
                "Loaded DLL informational version does not identify package source commit")
        require(Path(package["path"]).resolve() == (bin_dir / (package["name"] + ".dll")).resolve(),
                "Loaded DLL did not come from isolated peer output")


def validate_peer_relationships(peers):
    current, published, changed = [peers[name]["identity"] for name in ("current", "published", "incompatible")]
    require(current["abi"] != published["abi"] and current["api"] != published["api"],
            "Not real distinct generated ABI generations")
    require(current["rpcAssemblyHash"] == published["rpcAssemblyHash"],
            "Unchanged contract unexpectedly differs across generators: investigate contract; do not relabel as expected rejection")
    require(current["rpcAssemblyHash"] != changed["rpcAssemblyHash"], "Negative wire fixture did not change hash")
    require(current["contractIds"] == published["contractIds"] == changed["contractIds"],
            "Negative must exercise mismatched hash, not a missing contract")


def build_peer(name, version, commit, feed, work, evidence, dotnet):
    peer = work / name
    peer.mkdir()
    cache = peer / "packages"
    project = peer / (ASSEMBLY + ".csproj")
    for source in FIXTURE.iterdir():
        if source.suffix in (".cs", ".csproj"):
            shutil.copy2(source, peer / source.name)
    # Prevent inherited repository build/version/package settings from disguising a published consumer.
    (peer / "Directory.Build.props").write_text("<Project />\n")
    (peer / "Directory.Build.targets").write_text("<Project />\n")
    (peer / "Directory.Packages.props").write_text("<Project />\n")
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    if feed:
        ET.SubElement(sources, "add", key="candidate", value=str(feed))
    ET.SubElement(sources, "add", key="nuget.org", value=NUGET)
    mapping = ET.SubElement(config, "packageSourceMapping")
    if feed:
        ET.SubElement(ET.SubElement(mapping, "packageSource", key="candidate"), "package", pattern="SharpLink.*")
    ET.SubElement(ET.SubElement(mapping, "packageSource", key="nuget.org"), "package", pattern="*")
    config_path = peer / "NuGet.config"
    ET.ElementTree(config).write(config_path, encoding="utf-8", xml_declaration=True)
    shutil.copy2(config_path, evidence / f"{name}.NuGet.config")
    env = dict(os.environ, NUGET_PACKAGES=str(cache), NUGET_HTTP_CACHE_PATH=str(peer / "http-cache"))
    properties = [f"-p:SharpLinkVersion={version}", f"-p:IncompatibleWireContract={str(name == 'incompatible').lower()}"]
    run([dotnet, "restore", str(project), "--force", "--no-cache", "--disable-parallel", "--configfile", str(config_path),
         "-m:1", "-nodeReuse:false", *properties], evidence / f"{name}.restore.log", env, timeout=600)
    assets_path = peer / "obj/project.assets.json"
    assets = json.loads(assets_path.read_text())
    require(all(item["type"] == "package" for item in assets["libraries"].values()), "Project reference in isolated consumer")
    sharp = {key.split("/")[0]: (key, item) for key, item in assets["libraries"].items() if key.startswith("SharpLink.")}
    require(set(sharp) == PACKAGES, f"Unexpected SharpLink package closure: {list(sharp)}")
    records = {}
    for package, (key, library) in sharp.items():
        require(key == f"{package}/{version}", f"Wrong resolved package: {key}")
        directory = cache / library["path"]
        metadata = json.loads((directory / ".nupkg.metadata").read_text())
        source = metadata["source"]
        require(source == (str(feed) if feed else NUGET), f"Wrong NuGet source for {key}: {source}")
        record = package_record(directory / f"{package.lower()}.{version}.nupkg", package, version, commit)
        record["source"] = source
        if feed:
            require(record["package_sha256"] == sha((feed / f"{package}.{version}.nupkg").read_bytes()),
                    "Candidate package not identical to local input")
        records[package] = record
    shutil.copy2(assets_path, evidence / f"{name}.assets.json")
    run([dotnet, "build", str(project), "--no-restore", "-c", "Release", "-m:1", "-nodeReuse:false",
         "-p:UseSharedCompilation=false", *properties], evidence / f"{name}.build.log", env)
    analyzers = (peer / "obj/analyzers.txt").read_text().splitlines()
    generator = cache / f"sharplink.sdk/{version}/analyzers/dotnet/cs/SharpLink.Generator.dll"
    require([Path(path).resolve() for path in analyzers if Path(path).name == "SharpLink.Generator.dll"] == [generator.resolve()],
            "Compiler did not use exactly the matching packaged generator")
    require(sha(generator.read_bytes()) == records["SharpLink.Sdk"]["generator_sha256"], "Wrong analyzer bytes")
    shutil.copy2(peer / "obj/analyzers.txt", evidence / f"{name}.analyzers.txt")
    shutil.copytree(peer / "obj/generated", evidence / f"{name}.generated")
    bin_dir = peer / "bin/Release/net10.0"
    dll = bin_dir / (ASSEMBLY + ".dll")
    log = evidence / f"{name}.identity.log"
    run([dotnet, str(dll), "identity"], log, env)
    identity = read_identity(log)
    validate_identity(identity, records, version, 4 if name == "published" else 5, bin_dir)
    result = {"version": version, "source_commit": commit, "packages": records,
              "identity": identity, "dll": str(dll), "fixture_sha256": sha(dll.read_bytes()), "bin_dir": str(bin_dir)}
    write_json(evidence / f"{name}.provenance.json", result)
    return result


class FrameRecorder:
    """Observe only metadata, forwarding bytes unchanged. Protocol v2 header is 15 bytes."""
    def __init__(self, direction):
        self.direction, self.buffer, self.frames = direction, bytearray(), []

    def feed(self, data):
        self.buffer.extend(data)
        while len(self.buffer) >= 15:
            magic, length, kind, flags, request_id = struct.unpack_from("<BiBBQ", self.buffer)
            require(magic == 0x89 and 0 <= length <= 64 * 1024 * 1024, "Invalid frame observed by relay")
            if len(self.buffer) < 15 + length:
                return
            self.frames.append({"direction": self.direction, "type": kind, "flags": flags,
                                "request_id": request_id, "payload_bytes": length})
            del self.buffer[:15 + length]

    def finish(self):
        require(not self.buffer, "Truncated wire frame in relay")


def shutdown_write_after_eof(connection):
    """Only after a complete EOF: the opposite peer may already have closed its socket."""
    try:
        connection.shutdown(socket.SHUT_WR)
        return False
    except OSError as error:
        if error.errno != errno.ENOTCONN:
            raise
        return True


class Relay:
    def __init__(self, server_port):
        self.listener = socket.socket()
        self.listener.bind(("127.0.0.1", 0))
        self.listener.listen(1)
        self.listener.settimeout(15)
        self.port = self.listener.getsockname()[1]
        self.server_port, self.errors, self.recorders, self.connections = server_port, [], [], []
        self.notices = []
        self.thread = threading.Thread(target=self.forward, daemon=True)
        self.thread.start()

    def forward(self):
        try:
            client, _ = self.listener.accept()
            server = socket.create_connection(("127.0.0.1", self.server_port), timeout=15)
            self.connections = [client, server]
            def copy(source, destination, direction):
                recorder = FrameRecorder(direction)
                self.recorders.append(recorder)
                try:
                    source.settimeout(20)
                    while data := source.recv(65536):
                        recorder.feed(data)
                        destination.sendall(data)
                    recorder.finish()
                    if shutdown_write_after_eof(destination):
                        self.notices.append(f"{direction}: opposite socket already disconnected after complete EOF")
                except Exception as error:
                    self.errors.append(str(error))
            other = threading.Thread(target=copy, args=(server, client, "server-to-client"), daemon=True)
            other.start()
            copy(client, server, "client-to-server")
            other.join(25)
            require(not other.is_alive(), "Relay response thread did not stop")
        except Exception as error:
            self.errors.append(str(error))
        finally:
            for connection in self.connections:
                connection.close()
            self.listener.close()

    def snapshot(self):
        return [frame for recorder in self.recorders for frame in recorder.frames]

    def finish(self):
        self.thread.join(30)
        if self.thread.is_alive():
            self.errors.append("Relay exceeded completion deadline")
            self.listener.close()
            for connection in self.connections:
                connection.close()
            self.thread.join(2)
        require(not self.thread.is_alive(), "Relay did not stop")
        require(not self.errors, f"Wire observation failed: {self.errors}")
        return self.snapshot()


def validate_case(expectation, client_text, server_text, frames, client_identity, server_identity):
    require("CLIENT_CONNECTED" in client_text.splitlines(), "Handshake did not succeed")
    responses = [f for f in frames if f["direction"] == "server-to-client" and f["type"] == 1]
    require(len(responses) == 1 and not responses[0]["flags"] & 1, "Missing successful wire handshake")
    require(any(f["direction"] == "server-to-client" and f["type"] == 13 for f in frames), "No wire contract manifest")
    requests = [f for f in frames if f["direction"] == "client-to-server" and f["type"] == 4]
    if expectation == "positive":
        require("CLIENT_PASS" in client_text.splitlines() and "SERVER_PASS" in server_text.splitlines(), "Missing round-trip pass")
        require(len(requests) == 5 and sum(bool(f["flags"] & 64) for f in requests) == 1,
                "Expected all five RPC shapes, including one OneWay Request")
        require(client_identity["rpcAssemblyHash"] == server_identity["rpcAssemblyHash"], "Positive wire hashes differ")
    else:
        require("CLIENT_REJECTION_PASS" in client_text.splitlines() and "SERVER_REJECTION_PASS" in server_text.splitlines(),
                "Missing explicit hash rejection or zero-invocation proof")
        rejection = [json.loads(line.removeprefix("REJECTION ")) for line in client_text.splitlines() if line.startswith("REJECTION ")]
        require(len(rejection) == 1 and rejection[0]["code"] == "FailedPrecondition", "Wrong rejection kind")
        require(not requests, "Rejected contract emitted a business Request")
        require(client_identity["rpcAssemblyHash"] != server_identity["rpcAssemblyHash"], "Negative wire hashes equal")
        for identity in (client_identity, server_identity):
            require(identity["rpcAssemblyHash"] in rejection[0]["message"], "Rejection did not identify actual peer wire hashes")


def run_case(server_name, client_name, expectation, peers, evidence, dotnet):
    name = f"{server_name}-server_{client_name}-client_{expectation}"
    server, client = peers[server_name], peers[client_name]
    server_log, client_log = evidence / f"{name}.server.log", evidence / f"{name}.client.log"
    relay, primary_error = None, None
    command = [dotnet, server["dll"], "server", expectation]
    with server_log.open("w") as stream:
        stream.write(json.dumps({"command": command, "started": datetime.now(timezone.utc).isoformat()}) + "\n")
        stream.flush()
        process = subprocess.Popen(command, stdin=subprocess.PIPE,
                                   stdout=stream, stderr=subprocess.STDOUT, text=True)
        try:
            deadline, port = time.monotonic() + 15, None
            while time.monotonic() < deadline and process.poll() is None:
                ready = [line for line in server_log.read_text().splitlines() if line.startswith("SERVER_READY ")]
                if ready:
                    port = int(ready[0].split()[1])
                    break
                time.sleep(0.05)
            require(port is not None, f"Server failed readiness: {server_log}")
            relay = Relay(port)
            run([dotnet, client["dll"], "client", str(relay.port), expectation], client_log, timeout=25)
            process.communicate("STOP\n", timeout=15)
            require(process.returncode == 0, f"Server failed: {server_log}")
        except BaseException as error:
            primary_error = f"{type(error).__name__}: {error}"
            raise
        finally:
            if process.poll() is None:
                process.kill()
                process.wait()
            stream.write(f"\nEXIT_CODE {process.returncode}\n")
            relay_error = None
            if relay:
                try:
                    relay.finish()
                except Exception as error:
                    relay_error = f"{type(error).__name__}: {error}"
                # Always retain partial observations, even malformed/truncated/timeout frames.
                frames = relay.snapshot()
                write_json(evidence / f"{name}.frames.json", frames)
            write_json(evidence / f"{name}.controller.json", {
                "server_command": command, "server_exit": process.returncode,
                "primary_error": primary_error, "relay_error": relay_error,
                "relay_errors": relay.errors if relay else [],
                "relay_notices": relay.notices if relay else [],
                "pending_frame_bytes": [len(r.buffer) for r in relay.recorders] if relay else [],
            })
            if relay_error and primary_error is None:
                raise ValueError(relay_error)
    for peer, log in ((server, server_log), (client, client_log)):
        identity = read_identity(log)
        validate_identity(identity, peer["packages"], peer["version"], peer["identity"]["api"], Path(peer["bin_dir"]))
        require(identity == peer["identity"], "Process identity differs from recorded peer")
    validate_case(expectation, client_log.read_text(), server_log.read_text(), frames, client["identity"], server["identity"])
    return {"name": name, "server": server_name, "client": client_name, "expectation": expectation,
            "status": "passed", "server_exit": process.returncode, "client_exit": 0, "frames": len(frames)}


def validate_matrix(results):
    require([(r["server"], r["client"], r["expectation"]) for r in results] == list(MATRIX), "Incomplete/duplicate/reordered matrix")
    require(all(r["status"] == "passed" for r in results), "Matrix contains a failure")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packages", type=Path, default=ROOT / "artifacts/nuget")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/protocol-v2-cross-version")
    parser.add_argument("--candidate-commit", default=None, help="Exact package source SHA; defaults to HEAD")
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    attempt = Path(tempfile.mkdtemp(prefix="attempt-", dir=args.output.resolve()))
    evidence, work = attempt / "evidence", attempt / "work"
    evidence.mkdir(); work.mkdir()
    report = {"status": "failed", "started": datetime.now(timezone.utc).isoformat(), "cases": [], "peers": {}}
    print(f"Wire gate evidence: {evidence}", flush=True)
    try:
        head = git("rev-parse", "HEAD")
        candidate = git("rev-parse", args.candidate_commit or "HEAD")
        # Tool-only commits can consume exact earlier packages, never silently different product sources.
        require(not git("diff", candidate, "--", "src", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.config", "global.json"),
                "Package candidate differs from checkout product/build inputs")
        version = ET.parse(ROOT / "Directory.Build.props").findtext(".//VersionPrefix")
        require(version != PUBLISHED_VERSION, "Candidate must be a distinct version")
        report.update(tool_commit=head, candidate_commit=candidate, published_commit=PUBLISHED_COMMIT,
                      candidate_version=version, published_version=PUBLISHED_VERSION,
                      checkout_diff=git("diff", "--stat"), expected_cases=len(MATRIX))
        run([args.dotnet, "--info"], evidence / "dotnet-info.log")
        feed = args.packages.resolve()
        for name, peer_version, commit, source in (("current", version, candidate, feed),
                ("published", PUBLISHED_VERSION, PUBLISHED_COMMIT, None), ("incompatible", version, candidate, feed)):
            report["peers"][name] = build_peer(name, peer_version, commit, source, work, evidence, args.dotnet)
            write_json(evidence / "report.json", report)
        validate_peer_relationships(report["peers"])
        for server, client, expectation in MATRIX:
            report["active_case"] = {"server": server, "client": client, "expectation": expectation}
            write_json(evidence / "report.json", report)
            report["cases"].append(run_case(server, client, expectation, report["peers"], evidence, args.dotnet))
            write_json(evidence / "report.json", report)
        validate_matrix(report["cases"])
        report.pop("active_case", None)
        report["status"] = "passed"
        print(f"WIRE_GATE_PASS: {len(MATRIX)} process pairs (5 five-shape round trips, 4 explicit hash rejections).")
        return 0
    except Exception as error:
        report["error"] = f"{type(error).__name__}: {error}"
        print(f"WIRE_GATE_FAIL: {report['error']}", flush=True)
        return 1
    finally:
        report["finished"] = datetime.now(timezone.utc).isoformat()
        write_json(evidence / "report.json", report)


if __name__ == "__main__":
    raise SystemExit(main())
