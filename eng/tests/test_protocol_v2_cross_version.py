import copy
import errno
import importlib.util
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import Mock, patch
import zipfile

SPEC = importlib.util.spec_from_file_location("wire_gate", Path(__file__).parents[1] / "verify-protocol-v2-cross-version.py")
gate = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gate)


class WireGateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.addCleanup(self.temp.cleanup)

    def frame(self, kind=4, payload=b"", magic=0x89):
        return struct.pack("<BiBBQ", magic, len(payload), kind, 0, 1) + payload

    def test_fragmented_and_coalesced_frames(self):
        recorder = gate.FrameRecorder("client-to-server")
        data = self.frame(payload=b"123") + self.frame(7, b"ab")
        for byte in data:
            recorder.feed(bytes([byte]))
        recorder.finish()
        self.assertEqual([f["type"] for f in recorder.frames], [4, 7])

    def test_bad_magic_fails(self):
        with self.assertRaisesRegex(ValueError, "Invalid frame"):
            gate.FrameRecorder("x").feed(self.frame(magic=0))

    def test_negative_length_fails(self):
        with self.assertRaises(ValueError):
            gate.FrameRecorder("x").feed(struct.pack("<BiBBQ", 0x89, -1, 4, 0, 1))

    def test_excessive_length_fails(self):
        with self.assertRaises(ValueError):
            gate.FrameRecorder("x").feed(struct.pack("<BiBBQ", 0x89, 64 * 1024 * 1024 + 1, 4, 0, 1))

    def test_truncated_frame_fails(self):
        recorder = gate.FrameRecorder("x")
        recorder.feed(self.frame(payload=b"x")[:-1])
        with self.assertRaisesRegex(ValueError, "Truncated"):
            recorder.finish()

    def test_complete_eof_already_disconnected_is_only_a_notice(self):
        connection = Mock()
        connection.shutdown.side_effect = OSError(errno.ENOTCONN, "already closed")
        self.assertTrue(gate.shutdown_write_after_eof(connection))

    def test_other_shutdown_errors_are_not_ignored(self):
        for code in (errno.ECONNRESET, errno.EPIPE, errno.EBADF):
            with self.subTest(errno=code):
                connection = Mock()
                connection.shutdown.side_effect = OSError(code, "not a benign half-close")
                with self.assertRaises(OSError):
                    gate.shutdown_write_after_eof(connection)

    def test_partial_frames_and_primary_failure_are_retained(self):
        process = Mock()
        process.poll.return_value = None
        process.returncode = -9
        relay = Mock(port=123)
        relay.errors, relay.notices, relay.recorders = ["truncated"], [], []
        relay.finish.side_effect = ValueError("secondary relay error")
        relay.snapshot.return_value = [{"direction": "client-to-server", "type": 0}]
        def start(*args, **kwargs):
            kwargs["stdout"].write("SERVER_READY 321\n")
            kwargs["stdout"].flush()
            return process
        peers = {"a": {"dll": "a.dll"}, "b": {"dll": "b.dll"}}
        with patch.object(gate.subprocess, "Popen", side_effect=start), \
             patch.object(gate, "Relay", return_value=relay), \
             patch.object(gate, "run", side_effect=ValueError("primary client failure")):
            with self.assertRaisesRegex(ValueError, "primary client failure"):
                gate.run_case("a", "b", "reject", peers, self.root, "unused-dotnet")
        prefix = self.root / "a-server_b-client_reject"
        self.assertEqual(json.loads(prefix.with_suffix(".frames.json").read_text()), relay.snapshot.return_value)
        controller = json.loads(prefix.with_suffix(".controller.json").read_text())
        self.assertIn("primary client failure", controller["primary_error"])
        self.assertIn("secondary relay error", controller["relay_error"])
        self.assertEqual(controller["server_exit"], -9)
        self.assertIn("EXIT_CODE -9", prefix.with_suffix(".server.log").read_text())

    def package(self, version="2.0.3", commit=gate.PUBLISHED_COMMIT, package="SharpLink.Sdk"):
        path = self.root / "package.nupkg"
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr("SharpLink.Sdk.nuspec", f'<package><metadata><id>{package}</id><version>{version}</version><repository commit="{commit}" /></metadata></package>')
            archive.writestr("lib/net10.0/SharpLink.Sdk.dll", b"runtime")
            archive.writestr("analyzers/dotnet/cs/SharpLink.Generator.dll", b"matching generator")
        return path

    def test_real_package_identity_shape(self):
        record = gate.package_record(self.package(), "SharpLink.Sdk", "2.0.3", gate.PUBLISHED_COMMIT)
        self.assertEqual(record["generator_sha256"], gate.sha(b"matching generator"))

    def test_current_renamed_published_fails(self):
        with self.assertRaisesRegex(ValueError, "Wrong package identity"):
            gate.package_record(self.package(version="3.0.0"), "SharpLink.Sdk", "2.0.3", gate.PUBLISHED_COMMIT)

    def test_wrong_source_commit_fails(self):
        with self.assertRaisesRegex(ValueError, "source commit"):
            gate.package_record(self.package(commit="f" * 40), "SharpLink.Sdk", "2.0.3", gate.PUBLISHED_COMMIT)

    def test_wrong_package_id_fails(self):
        with self.assertRaisesRegex(ValueError, "package identity"):
            gate.package_record(self.package(package="SharpLink.Client"), "SharpLink.Sdk", "2.0.3", gate.PUBLISHED_COMMIT)

    def identity(self):
        records = {p: {"dll_sha256": "bytes", "source_commit": "commit"} for p in gate.PACKAGES}
        identity = {"api": 4, "protocol": 2, "minor": 4, "minimumMinor": 4, "generator": "2.0.3",
                    "abi": "api4", "rpcAssemblyHash": "abc", "contractIds": [1],
                    "packages": [{"name": p, "version": "2.0.3.0", "sha256": "bytes", "informationalVersion": "2.0.3+commit", "path": str(self.root / (p + ".dll"))} for p in gate.PACKAGES]}
        return identity, records

    def test_loaded_package_identity(self):
        identity, records = self.identity()
        gate.validate_identity(identity, records, "2.0.3", 4, self.root)

    def test_loaded_current_renamed_published_fails(self):
        identity, records = self.identity()
        identity["packages"][0]["version"] = "3.0.0.0"
        with self.assertRaisesRegex(ValueError, "assembly version"):
            gate.validate_identity(identity, records, "2.0.3", 4, self.root)

    def test_loaded_bytes_mismatch_fails(self):
        identity, records = self.identity()
        identity["packages"][0]["sha256"] = "other"
        with self.assertRaisesRegex(ValueError, "DLL differs"):
            gate.validate_identity(identity, records, "2.0.3", 4, self.root)

    def test_loaded_source_commit_mismatch_fails(self):
        identity, records = self.identity()
        identity["packages"][0]["informationalVersion"] = "2.0.3+wrong"
        with self.assertRaisesRegex(ValueError, "source commit"):
            gate.validate_identity(identity, records, "2.0.3", 4, self.root)

    def test_wrong_loaded_path_fails(self):
        identity, records = self.identity()
        identity["packages"][0]["path"] = "/other/SharpLink.Sdk.dll"
        with self.assertRaisesRegex(ValueError, "isolated peer"):
            gate.validate_identity(identity, records, "2.0.3", 4, self.root)

    def test_wrong_generator_fails(self):
        identity, records = self.identity()
        identity["generator"] = "3.0.0"
        with self.assertRaisesRegex(ValueError, "generator version"):
            gate.validate_identity(identity, records, "2.0.3", 4, self.root)

    def relationships(self):
        current = {"identity": {"api": 5, "abi": "api5", "rpcAssemblyHash": "base", "contractIds": [1]}}
        published, changed = copy.deepcopy(current), copy.deepcopy(current)
        published["identity"].update(api=4, abi="api4")
        changed["identity"]["rpcAssemblyHash"] = "changed"
        return dict(current=current, published=published, incompatible=changed)

    def test_matching_wire_different_abi_is_valid(self):
        gate.validate_peer_relationships(self.relationships())

    def test_same_current_builds_are_not_cross_version(self):
        peers = self.relationships()
        peers["published"] = copy.deepcopy(peers["current"])
        with self.assertRaisesRegex(ValueError, "distinct generated ABI"):
            gate.validate_peer_relationships(peers)

    def test_unexpected_cross_version_hash_difference_not_accepted(self):
        peers = self.relationships()
        peers["published"]["identity"]["rpcAssemblyHash"] = "unexpected"
        with self.assertRaisesRegex(ValueError, "do not relabel"):
            gate.validate_peer_relationships(peers)

    def test_missing_contract_is_not_hash_negative(self):
        peers = self.relationships()
        peers["incompatible"]["identity"]["contractIds"] = [2]
        with self.assertRaisesRegex(ValueError, "missing contract"):
            gate.validate_peer_relationships(peers)

    def case(self, expectation="reject"):
        frames = [{"direction": "server-to-client", "type": 1, "flags": 0},
                  {"direction": "server-to-client", "type": 13, "flags": 0}]
        client, server = {"rpcAssemblyHash": "aaa"}, {"rpcAssemblyHash": "bbb"}
        text = 'CLIENT_CONNECTED\nREJECTION ' + json.dumps({"code": "FailedPrecondition", "message": "local aaa remote bbb"}) + '\nCLIENT_REJECTION_PASS\n'
        server_text = "SERVER_REJECTION_PASS\n"
        if expectation == "positive":
            frames += [{"direction": "client-to-server", "type": 4, "flags": 64 if n == 4 else 0} for n in range(5)]
            server = client
            text, server_text = "CLIENT_CONNECTED\nCLIENT_PASS\n", "SERVER_PASS\n"
        return [expectation, text, server_text, frames, client, server]

    def test_explicit_rejection_passes(self):
        gate.validate_case(*self.case())

    def test_timeout_is_not_rejection(self):
        args = self.case()
        args[1] = "CLIENT_FAIL TimeoutException\n"
        with self.assertRaises(ValueError):
            gate.validate_case(*args)

    def test_connection_failure_is_not_rejection(self):
        args = self.case()
        args[1] = args[1].replace("FailedPrecondition", "ConnectionClosed")
        with self.assertRaisesRegex(ValueError, "rejection kind"):
            gate.validate_case(*args)

    def test_missing_manifest_fails(self):
        args = self.case()
        args[3] = args[3][:1]
        with self.assertRaisesRegex(ValueError, "contract manifest"):
            gate.validate_case(*args)

    def test_request_after_rejection_fails(self):
        args = self.case()
        args[3].append({"direction": "client-to-server", "type": 4, "flags": 0})
        with self.assertRaisesRegex(ValueError, "business Request"):
            gate.validate_case(*args)

    def test_wrong_diagnostic_hash_fails(self):
        args = self.case()
        args[1] = args[1].replace("bbb", "ccc")
        with self.assertRaisesRegex(ValueError, "actual peer wire hashes"):
            gate.validate_case(*args)

    def test_five_shape_positive(self):
        gate.validate_case(*self.case("positive"))

    def test_missing_call_shape_fails(self):
        args = self.case("positive")
        args[3].pop()
        with self.assertRaisesRegex(ValueError, "five RPC shapes"):
            gate.validate_case(*args)

    def test_narrow_ci_uses_exact_head_and_preserves_failure_evidence(self):
        root = Path(__file__).parents[2]
        workflow = (root / ".github/workflows/protocol-v2-process-gate.yml").read_text()
        release = (root / ".github/workflows/release-gate.yml").read_text()
        self.assertIn("branches: [dev, main]", workflow)
        self.assertIn("ready_for_review", workflow)
        self.assertIn("github.event.pull_request.draft == false", workflow)
        self.assertIn("ref: ${{ github.event.pull_request.head.sha || github.sha }}", workflow)
        self.assertIn("for name in Abstractions Runtime Client Server Sdk", workflow)
        self.assertNotIn("Sharplink.slnx", workflow)
        self.assertNotIn("tags:", workflow)
        for text in (workflow, release):
            self.assertIn("./eng/verify-protocol-v2-cross-version.sh", text)
            self.assertIn("test_protocol_v2_cross_version.py", text)
            self.assertIn("path: artifacts/protocol-v2-cross-version/attempt-*/evidence", text)
            self.assertIn("if: always()", text)

    def test_missing_positive_control_fails(self):
        results = [dict(server=s, client=c, expectation=e, status="passed") for s, c, e in gate.MATRIX]
        gate.validate_matrix(results)
        with self.assertRaisesRegex(ValueError, "Incomplete"):
            gate.validate_matrix(results[1:])


if __name__ == "__main__":
    unittest.main()
