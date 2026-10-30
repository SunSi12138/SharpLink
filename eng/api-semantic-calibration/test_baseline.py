#!/usr/bin/env python3
"""Offline security controls: synthetic ZIPs and mocked HTTP, never network/.NET."""
import contextlib
import hashlib
import io
import json
from pathlib import Path
import stat
import struct
import tempfile
import unittest
from unittest.mock import Mock, patch
import urllib.error
import warnings
import zipfile

import baseline as b


def metadata():
    return {'id': 12515194592, 'name': 'api-inventory-39986953281-1',
            'expired': False, 'size_in_bytes': b.ZIP_SIZE,
            'digest': 'sha256:' + b.ZIP_SHA,
            'workflow_run': {'id': 39986953281, 'repository_id': 1155708874,
                             'head_repository_id': 1155708874,
                             'head_sha': '87aeaf66cba5f804b8a33d312b9878143adbc9dd'}}


class Response(io.BytesIO):
    def __init__(self, body, status=200):
        super().__init__(body)
        self.status = status


class MetadataTests(unittest.TestCase):
    def test_exact_metadata(self):
        b.validate_metadata(metadata())

    def test_metadata_mutations_rejected(self):
        changes = [('id', 1), ('id', True), ('id', float(b.ARTIFACT_ID)),
                   ('name', 'api-inventory-latest'), ('expired', True),
                   ('expired', 0), ('expired', None), ('size_in_bytes', b.ZIP_SIZE+1),
                   ('size_in_bytes', float(b.ZIP_SIZE)), ('digest', 'sha256:'+'0'*64)]
        for key, value in changes:
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                b.validate_metadata({**metadata(), key: value})

    def test_workflow_identity_mutations_rejected(self):
        changes = [('id', 1), ('id', True), ('id', float(b.RUN_ID)),
                   ('repository_id', 1), ('repository_id', float(b.REPO_ID)),
                   ('head_repository_id', 1), ('head_repository_id', float(b.REPO_ID)),
                   ('head_sha', '0'*40)]
        for key, value in changes:
            altered = metadata()
            altered['workflow_run'][key] = value
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                b.validate_metadata(altered)

    def test_missing_metadata_rejected(self):
        for key in metadata():
            altered = metadata()
            del altered[key]
            with self.subTest(key=key), self.assertRaises((ValueError, KeyError, TypeError)):
                b.validate_metadata(altered)


class ResponseTests(unittest.TestCase):
    def test_empty_exact_and_over_limit(self):
        self.assertEqual(b.read_response(io.BytesIO(), 0), b'')
        self.assertEqual(b.read_response(io.BytesIO(b'abcd'), 4), b'abcd')
        for data, limit in [(b'a', 0), (b'abcde', 4), (b'a'*65537, 65536)]:
            with self.subTest(limit=limit), self.assertRaisesRegex(ValueError, 'limit'):
                b.read_response(io.BytesIO(data), limit)

    def test_stream_stops_after_first_excess_byte(self):
        response = Mock()
        response.read.side_effect = [b'ab', b'cd', b'e', AssertionError('over-read')]
        with self.assertRaises(ValueError):
            b.read_response(response, 4)
        self.assertEqual(response.read.call_count, 3)
        self.assertEqual(response.read.call_args.args, (1,))


class ZipTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.archive = self.root/'archive.zip'
        self.destination = self.root/'extracted'

    def make_archive(self, extras=(), count=315):
        with warnings.catch_warnings():
            warnings.simplefilter('ignore', UserWarning)
            with zipfile.ZipFile(self.archive, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
                for i in range(count):
                    archive.writestr(f'{b.PREFIX}file-{i:03}.json', b'{}')
                for name, contents in extras:
                    archive.writestr(name, contents)
        return self.archive.read_bytes()

    @contextlib.contextmanager
    def pinned(self, **limits):
        data = self.archive.read_bytes()
        with patch.multiple(b, ZIP_SIZE=len(data), ZIP_SHA=hashlib.sha256(data).hexdigest(), **limits):
            yield

    def reject(self, pattern=None):
        with self.pinned(), self.assertRaisesRegex(ValueError, pattern or '.'):
            b.safe_extract(self.archive, self.destination)
        self.assertFalse(self.destination.exists(), 'manifest rejection must precede extraction')

    def test_good_archive_extracts_only_fixed_subtree(self):
        self.make_archive([('api-inventory/base/ignored.json', b'no'), ('logs/build.txt', b'no')])
        with self.pinned():
            self.assertEqual(b.safe_extract(self.archive, self.destination), self.destination)
        files = list(self.destination.rglob('*'))
        self.assertEqual(len(files), 315)
        self.assertTrue(all(p.read_bytes() == b'{}' for p in files))
        self.assertFalse((self.root/'logs').exists())

    def test_digest_and_size_checked_before_opening_zip(self):
        data = self.make_archive()
        for changes in [dict(ZIP_SIZE=len(data)+1, ZIP_SHA=hashlib.sha256(data).hexdigest()),
                        dict(ZIP_SIZE=len(data), ZIP_SHA='0'*64)]:
            with self.subTest(changes=changes), patch.multiple(b, **changes), patch.object(b.zipfile, 'ZipFile') as constructor:
                with self.assertRaisesRegex(ValueError, 'artifact bytes mismatch'):
                    b.safe_extract(self.archive, self.destination)
                constructor.assert_not_called()
        self.assertFalse(self.destination.exists())

    def test_unsafe_paths_anywhere_in_archive_rejected(self):
        names = ['../escape', '/absolute', 'api-inventory/pr/../escape',
                 'api-inventory/pr/./file', 'api-inventory//pr/file',
                 'api-inventory/pr\\evil', 'C:/evil', 'api-inventory/pr/C:evil',
                 'outside/../../escape', '//server/share', 'api-inventory/pr/'+'x'*1024]
        for name in names:
            with self.subTest(name=name):
                self.make_archive([(name, b'x')])
                self.reject('path')

    def test_nul_in_original_zip_name_rejected(self):
        self.make_archive([('outside/truncatedXhidden', b'x')])
        # ZipInfo.filename truncates at NUL, while orig_filename retains it.
        data = self.archive.read_bytes().replace(b'outside/truncatedXhidden', b'outside/truncated\x00hidden')
        self.archive.write_bytes(data)
        self.reject('path')

    def test_duplicate_files_and_directory_aliases_rejected(self):
        for extras in [[(b.PREFIX+'file-000.json', b'duplicate')],
                       [('outside/', b''), ('outside', b'file')]]:
            with self.subTest(extras=extras):
                self.make_archive(extras)
                self.reject('duplicate')

    def test_file_parent_collision_rejected(self):
        self.make_archive([('outside', b'x'), ('outside/child', b'x')])
        self.reject('collision')

    def test_symlink_and_special_files_rejected(self):
        for mode in [stat.S_IFLNK, stat.S_IFIFO, stat.S_IFSOCK, stat.S_IFCHR, stat.S_IFBLK]:
            with self.subTest(mode=mode):
                entry = zipfile.ZipInfo('outside/special')
                entry.create_system = 3
                entry.external_attr = (mode | 0o777) << 16
                self.make_archive([(entry, b'../../escape')])
                self.reject('special')

    def test_encrypted_and_unsupported_members_rejected(self):
        for kind in ['encrypted', 'unsupported']:
            with self.subTest(kind=kind):
                data = bytearray(self.make_archive())
                # Set matching local + central fields; these tests exercise our preflight,
                # not a fixture upload or a network-provided corrupt archive.
                for signature, offset in [(b'PK\x03\x04', 6), (b'PK\x01\x02', 8)]:
                    position = data.index(signature)
                    if kind == 'encrypted':
                        current = struct.unpack_from('<H', data, position+offset)[0]
                        struct.pack_into('<H', data, position+offset, current | 1)
                    else:
                        struct.pack_into('<H', data, position+offset+2, 99)
                self.archive.write_bytes(data)
                self.reject('unsupported')

    def test_member_count_and_expansion_limits_rejected(self):
        self.make_archive()
        for limits, message in [(dict(MAX_ENTRIES=314), 'entry limit'),
                                (dict(MAX_ENTRY=1), 'member limit'),
                                (dict(MAX_UNPACKED=629), 'expansion limit'),
                                (dict(MAX_DOWNLOAD=1), 'invalid artifact file')]:
            with self.subTest(limits=limits), self.pinned(**limits), self.assertRaisesRegex(ValueError, message):
                b.safe_extract(self.archive, self.destination)
            self.assertFalse(self.destination.exists())

    def test_missing_or_extra_selected_member_rejected(self):
        for count in [0, 314, 316]:
            with self.subTest(count=count):
                self.make_archive(count=count)
                self.reject()

    def test_existing_directory_file_or_dangling_symlink_rejected(self):
        self.make_archive()
        for kind in ['directory', 'file', 'symlink']:
            if kind == 'directory':
                self.destination.mkdir()
            elif kind == 'file':
                self.destination.write_text('preserve')
            else:
                self.destination.symlink_to(self.root/'absent')
            with self.subTest(kind=kind), self.pinned(), self.assertRaisesRegex(ValueError, 'destination exists'):
                b.safe_extract(self.archive, self.destination)
            if kind == 'directory':
                self.destination.rmdir()
            else:
                self.destination.unlink()

    def test_archive_symlink_rejected(self):
        self.make_archive()
        link = self.root/'link.zip'
        link.symlink_to(self.archive)
        with self.pinned(), self.assertRaisesRegex(ValueError, 'invalid artifact file'):
            b.safe_extract(link, self.destination)


class FetchTests(unittest.TestCase):
    token = 'local-test-secret-never-send'
    location = 'https://test.blob.core.windows.net/artifact?signature=local'

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.destination = Path(self.temporary.name)/'archive.zip'
        self.body = b'offline mock archive'
        self.pins = patch.multiple(b, ZIP_SIZE=len(self.body), ZIP_SHA=hashlib.sha256(self.body).hexdigest())
        self.pins.start()
        self.addCleanup(self.pins.stop)
        self.opener = Mock()
        self.builder = patch.object(b.urllib.request, 'build_opener', return_value=self.opener)
        self.builder.start()
        self.addCleanup(self.builder.stop)

    def sequence(self, location=None, code=302, body=None, value=None):
        response = Response(json.dumps(metadata() if value is None else value).encode())
        error = urllib.error.HTTPError('https://api.github.com/fixed/zip', code, 'mock',
                                      {'Location': self.location if location is None else location}, io.BytesIO())
        self.opener.open.side_effect = [response, error, Response(self.body if body is None else body)]

    def test_exact_gets_and_no_storage_authorization(self):
        self.sequence()
        b.fetch_archive(self.destination, self.token)
        self.assertEqual(self.destination.read_bytes(), self.body)
        requests = [call.args[0] for call in self.opener.open.call_args_list]
        endpoint = 'https://api.github.com/repos/SunSi12138/SharpLink/actions/artifacts/12515194592'
        self.assertEqual([request.full_url for request in requests], [endpoint, endpoint+'/zip', self.location])
        self.assertTrue(all(request.get_method() == 'GET' for request in requests))
        for request in requests[:2]:
            self.assertEqual(request.get_header('Authorization'), 'Bearer '+self.token)
        self.assertEqual(dict(requests[2].header_items()), {'User-agent': 'SharpLink-fixed-api-calibration'})

    def test_redirect_handler_does_not_follow_automatically(self):
        self.assertIsNone(b.NoRedirect().redirect_request(None, None, 302, '', {}, self.location))

    def test_invalid_tokens_rejected_without_requests(self):
        for token in [None, '', ' ', 'x\ny', 'x\ry', 1]:
            with self.subTest(token=token), self.assertRaises(ValueError):
                b.fetch_archive(self.destination, token)
        self.opener.open.assert_not_called()

    def test_existing_destination_rejected_without_requests(self):
        self.destination.write_bytes(b'preserve')
        with self.assertRaises(ValueError):
            b.fetch_archive(self.destination, self.token)
        self.opener.open.assert_not_called()
        self.assertEqual(self.destination.read_bytes(), b'preserve')

    def test_invalid_storage_urls_rejected_before_third_request(self):
        urls = ['http://test.blob.core.windows.net/file', 'https://evil.invalid/file',
                'https://test.blob.core.windows.net.evil.invalid/file',
                'https://evilblob.core.windows.net/file', 'https://blob.core.windows.net/file',
                'https://user@test.blob.core.windows.net/file',
                'https://user:password@test.blob.core.windows.net/file',
                'https://test.blob.core.windows.net:444/file',
                'https://test.blob.core.windows.net/file#fragment', '//test.blob.core.windows.net/file', '']
        for url in urls:
            self.opener.reset_mock()
            self.sequence(location=url)
            with self.subTest(url=url), self.assertRaises(ValueError):
                b.fetch_archive(self.destination, self.token)
            self.assertEqual(self.opener.open.call_count, 2)
            self.assertFalse(self.destination.exists())

    def test_non_302_download_status_rejected(self):
        for code in [301, 303, 307, 308, 401, 403, 404, 410, 500]:
            self.opener.reset_mock()
            self.sequence(code=code)
            with self.subTest(code=code), self.assertRaises(ValueError):
                b.fetch_archive(self.destination, self.token)
            self.assertEqual(self.opener.open.call_count, 2)
            self.assertFalse(self.destination.exists())

    def test_unexpected_direct_archive_body_rejected(self):
        self.opener.open.side_effect = [Response(json.dumps(metadata()).encode()), Response(self.body)]
        with self.assertRaisesRegex(ValueError, 'expected artifact redirect'):
            b.fetch_archive(self.destination, self.token)
        self.assertFalse(self.destination.exists())

    def test_bad_metadata_prevents_archive_request(self):
        value = metadata()
        value['expired'] = True
        self.sequence(value=value)
        with self.assertRaises(ValueError):
            b.fetch_archive(self.destination, self.token)
        self.assertEqual(self.opener.open.call_count, 1)
        self.assertFalse(self.destination.exists())

    def test_unavailable_metadata_or_storage_never_written(self):
        self.opener.open.side_effect = [Response(b'{}', status=403)]
        with self.assertRaisesRegex(ValueError, 'metadata unavailable'):
            b.fetch_archive(self.destination, self.token)
        self.assertEqual(self.opener.open.call_count, 1)
        self.assertFalse(self.destination.exists())
        self.opener.reset_mock()
        self.sequence()
        responses = list(self.opener.open.side_effect)
        responses[2] = Response(self.body, status=404)
        self.opener.open.side_effect = responses
        with self.assertRaisesRegex(ValueError, 'storage unavailable'):
            b.fetch_archive(self.destination, self.token)
        self.assertFalse(self.destination.exists())

    def test_metadata_response_cap_prevents_archive_request(self):
        self.opener.open.side_effect = [Response(b' '*65537)]
        with self.assertRaisesRegex(ValueError, 'limit'):
            b.fetch_archive(self.destination, self.token)
        self.assertEqual(self.opener.open.call_count, 1)
        self.assertFalse(self.destination.exists())

    def test_duplicate_metadata_key_rejected(self):
        raw = json.dumps(metadata())[:-1]+', "id": 12515194592}'
        self.opener.open.side_effect = [Response(raw.encode())]
        with self.assertRaisesRegex(ValueError, 'duplicate'):
            b.fetch_archive(self.destination, self.token)
        self.assertEqual(self.opener.open.call_count, 1)

    def test_corrupt_short_oversized_archive_never_written(self):
        for body in [b'x'*len(self.body), self.body[:-1], self.body+b'x']:
            self.sequence(body=body)
            with self.subTest(body=body), self.assertRaises(ValueError):
                b.fetch_archive(self.destination, self.token)
            self.assertFalse(self.destination.exists())
        self.sequence(body=self.body)
        with patch.object(b, 'MAX_DOWNLOAD', len(self.body)-1), self.assertRaisesRegex(ValueError, 'limit'):
            b.fetch_archive(self.destination, self.token)
        self.assertFalse(self.destination.exists())

    def test_second_redirect_never_followed(self):
        self.sequence()
        responses = list(self.opener.open.side_effect)
        responses[2] = urllib.error.HTTPError(self.location, 302, 'mock', {'Location': 'https://evil.invalid'}, io.BytesIO())
        self.opener.open.side_effect = responses
        with self.assertRaises(urllib.error.HTTPError):
            b.fetch_archive(self.destination, self.token)
        self.assertEqual(self.opener.open.call_count, 3)
        self.assertFalse(self.destination.exists())


class RebuildGuardTests(unittest.TestCase):
    def test_baseline_source_exact_clean_identity(self):
        with patch.object(b.subprocess, 'check_output', side_effect=[b.BASE_SOURCE+'\n', b.BASE_TREE+'\n', '']):
            b.verify_baseline_source(Path('offline-source'))

    def test_baseline_source_wrong_or_dirty_rejected(self):
        answers = [('wrong',), (b.BASE_SOURCE, 'wrong'),
                   (b.BASE_SOURCE, b.BASE_TREE, ' M production.cs'),
                   (b.BASE_SOURCE, b.BASE_TREE, '?? untracked')]
        for sequence in answers:
            with self.subTest(sequence=sequence), patch.object(b.subprocess, 'check_output', side_effect=sequence), self.assertRaises(ValueError):
                b.verify_baseline_source(Path('offline-source'))

    def test_rebuild_checks_source_before_reading_inputs(self):
        with patch.object(b, 'verify_baseline_source', side_effect=ValueError('source')), patch.object(b.compare, 'read') as read:
            with self.assertRaisesRegex(ValueError, 'source'):
                b.rebuild(Path('inventory'), Path('source'))
            read.assert_not_called()

    def test_rebuild_manifest_count_checked_before_inventory(self):
        with patch.object(b, 'verify_baseline_source'), patch.object(b.compare, 'read', return_value={}), \
             patch.object(b.compare, 'verify_reports', return_value={'outputsSha256': {}}), \
             patch.object(b.compare, 'validate_inventory') as validate:
            with self.assertRaisesRegex(ValueError, 'manifest count'):
                b.rebuild(Path('inventory'), Path('source'))
            validate.assert_not_called()

    def test_rebuild_rejects_escaping_helper_and_control_paths(self):
        for field in ['toolSourceSha256', 'sourceControlFilesSha256']:
            for name in ['/absolute', '../escape', 'eng/../../escape']:
                provenance = {'outputsSha256': {str(i): '0'*64 for i in range(314)},
                              'toolSourceSha256': {}, 'sourceControlFilesSha256': {}}
                provenance[field] = {name: '0'*64}
                with self.subTest(field=field, name=name), \
                     patch.object(b, 'verify_baseline_source'), \
                     patch.object(b.compare, 'read', return_value={}), \
                     patch.object(b.compare, 'verify_reports', return_value=provenance), \
                     patch.object(b.compare, 'validate_inventory') as validate:
                    with self.assertRaisesRegex(ValueError, 'invalid baseline source path'):
                        b.rebuild(Path('inventory'), Path('source'))
                    validate.assert_not_called()

    def test_rebuild_rejects_changed_helper_or_control(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary)
            (source/'control.txt').write_bytes(b'changed')
            for field in ['toolSourceSha256', 'sourceControlFilesSha256']:
                provenance = {'outputsSha256': {str(i): '0'*64 for i in range(314)},
                              'toolSourceSha256': {}, 'sourceControlFilesSha256': {}}
                provenance[field] = {'control.txt': hashlib.sha256(b'original').hexdigest()}
                with self.subTest(field=field), patch.object(b, 'verify_baseline_source'), \
                     patch.object(b.compare, 'read', return_value={}), \
                     patch.object(b.compare, 'verify_reports', return_value=provenance), \
                     patch.object(b.compare, 'validate_inventory') as validate:
                    with self.assertRaisesRegex(ValueError, 'helper or control changed'):
                        b.rebuild(Path('inventory'), source)
                    validate.assert_not_called()
            self.assertEqual([p.name for p in source.iterdir()], ['control.txt'])

    @contextlib.contextmanager
    def synthetic_rebuild(self, **pin_changes):
        # All preceding trust/ledger checks are mocked only to isolate the two
        # final independent pins. No production identity mapping is embedded.
        snapshot = {'synthetic-only': {'symbolSha256': '1'*64, 'relationSha256': []}}
        canonical = lambda value: json.dumps(value, sort_keys=True, ensure_ascii=False,
                                             separators=(',', ':'), allow_nan=False).encode('utf-8')
        required_sha = hashlib.sha256(canonical(snapshot)).hexdigest()
        target = {'project': 'synthetic.csproj', 'framework': 'synthetic', 'assembly': 'Synthetic'}
        metadata = {'source': 'synthetic-source', 'tree': 'synthetic-tree', 'sdk': 'synthetic-sdk'}
        fixture = {'schemaVersion': 1,
                   'projection': 'all-symbol-fields-except-declarations-locations+incident-relations-v1',
                   'historical': {**metadata, 'runId': 39986953281, 'artifactId': 12515194592},
                   'targets': [target], 'toolSourceSha256': {}, 'required': snapshot,
                   'requiredSha256': required_sha, 'assemblyRelationSha256': [],
                   'sourceControlFilesSha256': {}}
        expected = canonical(fixture)+b'\n'
        pins = {'REQUIRED_SHA': required_sha, 'FIXTURE_SHA': hashlib.sha256(expected).hexdigest(),
                **pin_changes}
        provenance = {'outputsSha256': {str(i): '0'*64 for i in range(314)},
                      'toolSourceSha256': {}, 'sourceControlFilesSha256': {}}
        module = Mock()
        module.validate.return_value = {'status': 'proposal-not-approved', 'declarations': 18713,
                                        'decisionIds': 3397, 'types': 302, 'memberExceptions': 5}
        specification = Mock()
        ledger = {'targets': {'synthetic': target}}
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            inventory, source = root/'inventory', root/'source'
            inventory.mkdir()
            source.mkdir()
            with patch.object(b, 'verify_baseline_source'), \
                 patch.object(b.compare, 'read', side_effect=[ledger, {}, {}, {}]), \
                 patch.object(b.compare, 'rows', return_value=[]), \
                 patch.object(b.compare, 'verify_reports', return_value=provenance), \
                 patch.object(b.compare, 'validate_inventory', return_value=(snapshot, {}, {}, metadata)), \
                 patch.object(b.compare, 'validate_fixture') as validate_fixture, \
                 patch.object(b.importlib.util, 'spec_from_file_location', return_value=specification), \
                 patch.object(b.importlib.util, 'module_from_spec', return_value=module), \
                 patch.multiple(b, **pins):
                yield inventory, source, expected, validate_fixture
                module.validate.assert_called_once()
                validate_fixture.assert_called_once_with(fixture)
                self.assertEqual(list(inventory.iterdir()), [])
                self.assertEqual(list(source.iterdir()), [])

    def test_rebuild_matching_independent_pins_returns_canonical_bytes_without_writes(self):
        with self.synthetic_rebuild() as (inventory, source, expected, _):
            self.assertEqual(b.rebuild(inventory, source), expected)

    def test_rebuild_rejects_independent_required_digest_pin(self):
        with self.synthetic_rebuild(REQUIRED_SHA='0'*64) as (inventory, source, _, _):
            with self.assertRaisesRegex(ValueError, 'frozen baseline digest mismatch'):
                b.rebuild(inventory, source)

    def test_rebuild_rejects_independent_fixture_digest_pin(self):
        with self.synthetic_rebuild(FIXTURE_SHA='0'*64) as (inventory, source, _, _):
            with self.assertRaisesRegex(ValueError, 'frozen baseline digest mismatch'):
                b.rebuild(inventory, source)

    def test_cli_failure_hides_sensitive_error_and_writes_no_fixture(self):
        with tempfile.TemporaryDirectory() as directory:
            workspace = Path(directory)/'work'
            output = io.StringIO()
            with patch('sys.argv', ['baseline.py', '--workspace', str(workspace), '--source-root', directory]), \
                 patch.object(b, 'fetch_archive', side_effect=ValueError('credential=SECRET signed-url=PRIVATE')), \
                 contextlib.redirect_stdout(output):
                self.assertEqual(b.main(), 2)
            result = json.loads(output.getvalue().removeprefix('API_BASELINE '))
            self.assertEqual(result, {'status': 'INCOMPLETE', 'errorType': 'ValueError'})
            self.assertEqual(list(workspace.iterdir()), [])


if __name__ == '__main__':
    unittest.main()
