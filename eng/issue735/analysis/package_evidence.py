#!/usr/bin/env python3
"""Package this research's evidence directory without changing it or uploading it.

inventory: cheap, explicitly unverified size/classification preview.
pack: hash every input, validate ZIPs, byte-compare omitted copies, write bounded
      ZIP_STORED parts, then independently read/verify their reconstruction.
verify / restore: consume the reconstruction manifest; never extract ZIP paths.

Only --input is scanned. Repository checkouts are not copied. Existing source
snapshots inside evidence remain evidence. Signed download URLs are removed only
from delivered JSON copies; original input hashes and redaction counts remain.
Unreadable original ZIPs are preserved verbatim and never supply omitted files.
Internal symlinks are recorded, not followed; restore relocates them inside the
new evidence root. Outside-root links and special files cause a hard failure.
"""

import argparse
from collections import defaultdict
from contextlib import contextmanager
from datetime import datetime, timezone
import hashlib
import html
import io
import json
import lzma
import os
from pathlib import Path, PurePosixPath
import re
import stat
import sys
import tempfile
from urllib.parse import parse_qsl, urlsplit
import zipfile
import zlib

CHUNK = 1024 * 1024
SCHEMA = 'sharplink-issue735-evidence-package-v1'
V1 = '38164814650'
V2 = '38165237528'
URL = re.compile(r'https?://[^\s<>"\'`\\]+')
SIGNATURES = {'sig', 'signature', 'token', 'jwt', 'x-amz-signature', 'x-goog-signature'}
REDACTED = '[REDACTED_TEMPORARY_DOWNLOAD_URL]'


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def hash_stream(stream):
    h, size = hashlib.sha256(), 0
    for block in iter(lambda: stream.read(CHUNK), b''):
        h.update(block)
        size += len(block)
    return size, h.hexdigest()


def file_hash(path):
    with path.open('rb') as stream:
        return hash_stream(stream)[1]


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True, ensure_ascii=False) + '\n', encoding='utf-8')


def safe_logical(name):
    p = PurePosixPath(name)
    require(name and not p.is_absolute() and '..' not in p.parts and '\\' not in name
            and '\x00' not in name and p.as_posix() == name, 'Unsafe logical path')
    return name


def fingerprint(st):
    return (st.st_dev, st.st_ino, st.st_mode, st.st_size, st.st_mtime_ns)


def scan(root):
    """Never follow a symlink, including symlinks to directory fixtures."""
    records = []
    for parent, dirs, files in os.walk(root, followlinks=False):
        for name in sorted(dirs + files):
            p = Path(parent) / name
            logical = safe_logical(p.relative_to(root).as_posix())
            st = p.lstat()
            r = dict(path=logical, mode=stat.S_IMODE(st.st_mode), mtimeNs=st.st_mtime_ns,
                     _fingerprint=fingerprint(st))
            if stat.S_ISLNK(st.st_mode):
                target = p.resolve(strict=False)
                require(target.is_relative_to(root), 'Outside-root symlink: ' + logical)
                target_text = os.readlink(p)
                r.update(kind='symlink', originalTarget=target_text, bytes=len(os.fsencode(target_text)),
                         sha256=digest(os.fsencode(target_text)), targetLogicalPath=target.relative_to(root).as_posix())
                if name in dirs:
                    dirs.remove(name)
            elif stat.S_ISREG(st.st_mode):
                r.update(kind='file', bytes=st.st_size)
            elif stat.S_ISDIR(st.st_mode):
                r.update(kind='directory')
            else:
                raise ValueError('Unsupported special file: ' + logical)
            records.append(r)
    return sorted(records, key=lambda r: r['path'])


@contextmanager
def checked_open(root, record):
    p = root / record['path']
    require(p.resolve().is_relative_to(root), 'Input escaped root: ' + record['path'])
    fd = os.open(p, os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0))
    with os.fdopen(fd, 'rb') as stream:
        require(fingerprint(os.fstat(stream.fileno())) == record['_fingerprint'],
                'Input changed before read: ' + record['path'])
        yield stream
        require(fingerprint(os.fstat(stream.fileno())) == record['_fingerprint']
                and fingerprint(p.lstat()) == record['_fingerprint'],
                'Input changed during read: ' + record['path'])


def scrub_text(text):
    count = 0

    def replace(match):
        nonlocal count
        raw = html.unescape(match.group(0))
        parsed = urlsplit(raw)
        keys = {k.lower() for k, _ in parse_qsl(parsed.query)}
        host = (parsed.hostname or '').lower()
        temporary = bool(keys & SIGNATURES) or (
            host.endswith(('.blob.core.windows.net', '.oaiusercontent.com'))
            and bool(keys & {'se', 'ske', 'skt'}))
        if temporary:
            count += 1
            return REDACTED
        return match.group(0)

    return URL.sub(replace, text), count


def scrub_json(data):
    """Recursively handle tool wrappers, including JSON serialized inside strings."""
    if not any(marker in data.lower() for marker in (b'sig=', b'signature=', b'token=', b'jwt=', b'\\u003f')):
        return data, 0

    def walk(value, depth=0):
        require(depth < 100, 'Download receipt nesting exceeds supported depth')
        if isinstance(value, dict):
            out, total = {}, 0
            for k, v in value.items():
                out[k], n = walk(v, depth + 1)
                total += n
            return out, total
        if isinstance(value, list):
            out, total = [], 0
            for v in value:
                item, n = walk(v, depth + 1)
                out.append(item)
                total += n
            return out, total
        if isinstance(value, str):
            if value.lstrip().startswith(('{', '[')):
                try:
                    nested = json.loads(value)
                except (ValueError, RecursionError):
                    pass
                else:
                    converted, n = walk(nested, depth + 1)
                    if n:
                        return json.dumps(converted, ensure_ascii=False), n
            return scrub_text(value)
        return value, 0

    text = data.decode('utf-8-sig')
    try:
        parsed = json.loads(text)
    except (ValueError, RecursionError):
        changed, count = scrub_text(text)
        return (changed.encode('utf-8'), count) if count else (data, 0)
    changed, count = walk(parsed)
    return ((json.dumps(changed, indent=2, sort_keys=True, ensure_ascii=False) + '\n').encode('utf-8'), count) if count else (data, 0)


def b2_runs(records, explicit):
    found = set(explicit)
    require(all(re.fullmatch(r'\d+', r) for r in found), '--b2-run expects a numeric run ID')
    for r in records:
        parts = PurePosixPath(r['path']).parts
        if len(parts) > 2 and parts[0] == 'new' and parts[1].isdigit() and any('b2' in s.lower() for s in parts[2:]):
            found.add(parts[1])
    return sorted(found)


def category(path, runs):
    parts = PurePosixPath(path).parts
    if parts[0] in {'historical', 'frameworks', 'root'}:
        return 'history-frameworks-root'
    if V1 in path or 'd-v1-' in path or 'independent-v1' in path or 'recomputed-v1' in path:
        return 'd-v1'
    if V2 in path or 'd-v2-' in path or 'independent-v2' in path or 'recomputed-v2' in path:
        return 'd-v2'
    if parts[0].startswith('b2') or (len(parts) > 1 and parts[0] == 'new' and parts[1] in runs):
        return 'b2'
    return 'correctness-model-footprint'


def input_output(input_path, output_path):
    root = input_path.resolve(strict=True)
    require(root.is_dir(), '--input must be an evidence directory')
    output = output_path.resolve(strict=False)
    require(not output.is_relative_to(root) and not root.is_relative_to(output),
            'Output must be a new directory outside the input tree')
    require(not output.exists(), 'Refusing to overwrite existing output directory')
    output.mkdir(parents=True)
    return root, output


def inventory(args):
    root, output = input_output(args.input, args.output)
    rows = scan(root)
    runs = b2_runs(rows, args.b2_run)
    groups = defaultdict(lambda: dict(files=0, sourceBytes=0, originalZips=0, links=0))
    zips = []
    for r in rows:
        c = category(r['path'], runs)
        if r['kind'] == 'file':
            groups[c]['files'] += 1
            groups[c]['sourceBytes'] += r['bytes']
            if r['path'].lower().endswith('.zip'):
                groups[c]['originalZips'] += 1
                zips.append(dict(path=r['path'], bytes=r['bytes'], category=c,
                                 validation='NotPerformedInInventory'))
        elif r['kind'] == 'symlink':
            groups[c]['links'] += 1
    result = dict(schema=SCHEMA, mode='inventory-only', inputRoot=str(root),
                  inventoryUtc=datetime.now(timezone.utc).isoformat(), scriptSha256=file_hash(Path(__file__)),
                  categories=dict(groups), detectedB2Runs=runs, originalZips=zips,
                  directories=sum(r['kind'] == 'directory' for r in rows),
                  temporaryDownloadReceiptCandidates=[r['path'] for r in rows if r['kind'] == 'file'
                      and r['path'].lower().endswith('.json') and 'download' in Path(r['path']).name.lower()],
                  limitations=['No content hashes, ZIP CRC checks, deduplication, or part-size prediction have run.',
                               'All original ZIPs, including incomplete files, remain inputs for final pack.',
                               'Rerun pack after evidence writers have finished; new or changed inputs abort a pack.'])
    write_json(output / 'inventory.json', result)
    print(json.dumps(dict(mode='inventory-only', files=sum(g['files'] for g in groups.values()),
                         bytes=sum(g['sourceBytes'] for g in groups.values()), originalZips=len(zips),
                         links=sum(g['links'] for g in groups.values()), output=str(output))))


def inspect_files(root, records):
    for r in records:
        if r['kind'] != 'file':
            continue
        with checked_open(root, r) as stream:
            if r['path'].lower().endswith('.json'):
                data = stream.read()
                r['sha256'] = digest(data)
                replacement, count = scrub_json(data)
                if count:
                    r.update(_replacement=replacement, redaction=dict(policy='temporary-signed-download-url', occurrences=count))
            else:
                size, r['sha256'] = hash_stream(stream)
                require(size == r['bytes'], 'Input size changed: ' + r['path'])
        r['deliveredBytes'] = len(r['_replacement']) if '_replacement' in r else r['bytes']
        r['deliveredSha256'] = digest(r['_replacement']) if '_replacement' in r else r['sha256']


def inspect_zips(root, records):
    index, reports = defaultdict(list), []
    for r in records:
        if r['kind'] != 'file' or not r['path'].lower().endswith('.zip'):
            continue
        refs = []
        report = dict(path=r['path'], bytes=r['bytes'], sha256=r['sha256'])
        try:
            with checked_open(root, r) as stream, zipfile.ZipFile(stream) as archive:
                for number, entry in enumerate(archive.infolist()):
                    if entry.is_dir():
                        continue
                    with archive.open(entry) as member:
                        if entry.filename.lower().endswith('.json'):
                            data = member.read()
                            size, sha = len(data), digest(data)
                            _, redactions = scrub_json(data)
                            require(not redactions, 'Signed download receipt inside an original ZIP; cannot both redact and preserve it verbatim: '
                                    + r['path'] + ' entry index ' + str(number))
                        else:
                            size, sha = hash_stream(member)
                    require(size == entry.file_size, 'ZIP entry length mismatch: ' + r['path'])
                    refs.append(dict(zipLogicalPath=r['path'], zipEntryName=entry.filename,
                                     zipEntryIndex=number, zipCrc32=entry.CRC, bytes=size, sha256=sha))
        except (zipfile.BadZipFile, EOFError, NotImplementedError, RuntimeError,
                OSError, zlib.error, lzma.LZMAError) as exc:
            report.update(status='preserved-unreadable-not-a-reconstruction-source', errorType=type(exc).__name__)
        else:
            report.update(status='all-entries-read-and-crc-verified', fileEntries=len(refs))
            for ref in refs:
                index[(ref['bytes'], ref['sha256'])].append(ref)
        reports.append(report)
    return index, reports


def find_reference(root, record, index, records_by_path, runs):
    if '_replacement' in record or record['path'].lower().endswith('.zip'):
        return None
    candidates = index.get((record['bytes'], record['sha256']), [])
    parts = PurePosixPath(record['path']).parts

    def rank(ref):
        zip_parts = PurePosixPath(ref['zipLogicalPath']).parts
        common = 0
        for a, b in zip(parts, zip_parts):
            if a != b:
                break
            common += 1
        return (-int(category(ref['zipLogicalPath'], runs) == category(record['path'], runs)),
                -common, ref['zipLogicalPath'], ref['zipEntryIndex'])

    for ref in sorted(candidates, key=rank):
        zr = records_by_path[ref['zipLogicalPath']]
        with (checked_open(root, zr) as zstream, zipfile.ZipFile(zstream) as z,
              checked_open(root, record) as source):
            entry = z.infolist()[ref['zipEntryIndex']]
            equal = True
            with z.open(entry) as member:
                while True:
                    a, b = source.read(CHUNK), member.read(CHUNK)
                    if a != b:
                        equal = False
                        break
                    if not a:
                        break
            if equal:
                return dict(kind='original-zip-entry', **ref, verified='full-byte-comparison')
    return None


class Parts:
    """Use stored members so the exact final size has a simple conservative bound."""

    def __init__(self, output, limit):
        self.output, self.limit = output, limit
        self.archive = None
        self.group = None
        self.used = 22
        self.sequence = defaultdict(int)
        self.completed = []

    def close(self, check_size=True):
        if self.archive is None:
            return
        self.archive.close()
        p = self.output / self.name
        if check_size:
            require(p.stat().st_size == self.used and self.used <= self.limit, 'Archive part exceeded the declared size bound')
        self.completed.append(dict(name=self.name, category=self.group, bytes=p.stat().st_size, sha256=file_hash(p)))
        self.archive = None

    def start(self, group):
        self.close()
        self.group = group
        self.sequence[group] += 1
        self.name = f'evidence-{group}-part{self.sequence[group]:03d}.zip'
        self.archive = zipfile.ZipFile(self.output / self.name, 'x', compression=zipfile.ZIP_STORED, allowZip64=False)
        self.used = 22

    def add(self, stream, logical, total, expected_hash, group, mode):
        if self.archive is None or self.group != group:
            self.start(group)
        members, offset, h = [], 0, hashlib.sha256()
        while offset < total or not members:
            name = 'payload/' + logical + f'.piece-{len(members):06d}'
            overhead = 76 + 2 * len(name.encode('utf-8'))
            if self.used + overhead >= self.limit or len(self.archive.filelist) >= 60000:
                self.start(group)
            available = self.limit - self.used - overhead
            require(available > 0, 'Part bound too small for a logical path')
            size = min(total - offset, available)
            info = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = (stat.S_IFREG | (mode & 0o777)) << 16
            info.file_size = size
            ch = hashlib.sha256()
            with self.archive.open(info, 'w', force_zip64=False) as out:
                left = size
                while left:
                    block = stream.read(min(CHUNK, left))
                    require(bool(block), 'Source ended before declared length: ' + logical)
                    out.write(block)
                    ch.update(block)
                    h.update(block)
                    left -= len(block)
            members.append(dict(archive=self.name, entry=name, offset=offset, bytes=size, sha256=ch.hexdigest()))
            offset += size
            self.used += overhead + size
        require(not stream.read(1) and h.hexdigest() == expected_hash, 'Source changed while packaging: ' + logical)
        return dict(kind='archive-members', members=members)


def public_record(record):
    return {k: v for k, v in record.items() if not k.startswith('_')}


def pack(args):
    root, output = input_output(args.input, args.output)
    require(1 <= args.part_mib <= 249, '--part-mib must be 1..249; default 240 MiB')
    marker = output / 'INCOMPLETE'
    marker.write_text('Packaging or verification has not completed.\n')
    records = scan(root)
    frozen = {r['path']: r['_fingerprint'] for r in records}
    runs = b2_runs(records, args.b2_run)
    print(json.dumps(dict(stage='hash-inputs', entries=len(records))))
    inspect_files(root, records)
    index, zip_reports = inspect_zips(root, records)
    by_path = {r['path']: r for r in records}
    print(json.dumps(dict(stage='validate-zip-and-compare', originalZips=len(zip_reports))))
    for r in records:
        r['category'] = category(r['path'], runs)
        if r['kind'] == 'file':
            r['_reference'] = find_reference(root, r, index, by_path, runs)
    parts = Parts(output, args.part_mib * CHUNK)
    try:
        for r in sorted(records, key=lambda r: (r['category'], r['path'])):
            if r['kind'] != 'file':
                continue
            if r['_reference'] is not None:
                r['storage'] = r['_reference']
            elif '_replacement' in r:
                with io.BytesIO(r['_replacement']) as stream:
                    r['storage'] = parts.add(stream, r['path'], r['deliveredBytes'], r['deliveredSha256'], r['category'], r['mode'])
            else:
                with checked_open(root, r) as stream:
                    r['storage'] = parts.add(stream, r['path'], r['deliveredBytes'], r['deliveredSha256'], r['category'], r['mode'])
    except BaseException:
        try:
            parts.close(check_size=False)
        except Exception:
            pass
        raise
    else:
        parts.close()
    require({r['path']: r['_fingerprint'] for r in scan(root)} == frozen, 'Input tree changed during packaging; output remains INCOMPLETE')
    manifest = dict(schema=SCHEMA, createdUtc=datetime.now(timezone.utc).isoformat(), sourceRoot=str(root),
                    packager=dict(repositoryPath='eng/issue735/analysis/package_evidence.py', sha256=file_hash(Path(__file__))),
                    inputPolicy='Only the supplied evidence tree. No sibling repositories or Library files.',
                    partLimitBytes=args.part_mib * CHUNK, detectedB2Runs=runs, archives=parts.completed,
                    records=[public_record(r) for r in records], originalZips=zip_reports,
                    redactionPolicy='Signed download URLs in JSON delivery copies only; raw input files are never modified. Original ZIP bytes are never rewritten.',
                    symlinkPolicy='Original target text is recorded; restoration uses a relative link to targetLogicalPath within the restored root.',
                    missingOrUnprovenCopiesPolicy='Store the complete original file. Deduplication is optional and requires full-byte comparison.',
                    incompleteZipPolicy='Preserved as ordinary original bytes; never used as a reconstruction source.')
    write_json(output / 'manifest.json', manifest)
    proof = verify(output / 'manifest.json', allow_incomplete=True)
    write_json(output / 'verification.json', proof)
    require({r['path']: r['_fingerprint'] for r in scan(root)} == frozen, 'Input tree changed during verification; output remains INCOMPLETE')
    (output / 'README.txt').write_text(
        'SharpLink Issue #735 evidence package\n\n'
        'Keep manifest.json and every evidence-*.zip part together. Verify SHA256SUMS first.\n'
        'The repository owns eng/issue735/analysis/package_evidence.py; its SHA256 is in manifest.json.\n'
        'Verify: python3 package_evidence.py verify --manifest /path/to/manifest.json\n'
        'Restore: python3 package_evidence.py restore --manifest /path/to/manifest.json --output /new/evidence\n\n'
        'Every logical file is listed. Only byte-identical extracted copies may reference an original ZIP entry.\n'
        'Unreadable original ZIPs remain present; an unreadable historical ZIP is not a package verification failure.\n'
        'Temporary signed download receipts are explicitly redacted in delivery copies. Original input SHA256 values remain recorded.\n'
        'Internal symlinks are relocated into the restored root. No source evidence was modified or uploaded.\n', encoding='utf-8')
    outputs = [dict(name=p.name, bytes=p.stat().st_size, sha256=file_hash(p),
                    category='package-metadata' if p.suffix != '.zip' else next(a['category'] for a in parts.completed if a['name'] == p.name))
               for p in sorted(output.iterdir()) if p.is_file() and p.name != 'INCOMPLETE']
    write_json(output / 'package-index.json', dict(schema=SCHEMA, outputs=outputs))
    (output / 'SHA256SUMS').write_text(''.join(file_hash(p) + '  ' + p.name + '\n' for p in sorted(output.iterdir()) if p.is_file() and p.name not in {'SHA256SUMS', 'INCOMPLETE'}), encoding='ascii')
    marker.unlink()
    print(json.dumps(dict(stage='complete-verified', files=proof['filesVerified'], parts=len(parts.completed),
                         outputBytes=sum(p.stat().st_size for p in output.iterdir() if p.is_file()), output=str(output))))


def load_package(path, allow_incomplete):
    require(allow_incomplete or not (path.parent / 'INCOMPLETE').exists(), 'Package is marked INCOMPLETE')
    data = json.loads(path.read_text(encoding='utf-8'))
    require(data['schema'] == SCHEMA, 'Unsupported package schema')
    rows = {safe_logical(r['path']): r for r in data['records']}
    require(len(rows) == len(data['records']), 'Duplicate logical paths')
    archives = {}
    expected = defaultdict(set)
    for r in rows.values():
        if r['kind'] == 'file' and r['storage']['kind'] == 'archive-members':
            offset = 0
            for m in r['storage']['members']:
                require(m['offset'] == offset and m['bytes'] >= 0, 'Invalid reconstruction chunk offsets')
                offset += m['bytes']
                require(m['entry'] not in expected[m['archive']], 'Overlapping stored member')
                expected[m['archive']].add(m['entry'])
            require(offset == r['deliveredBytes'], 'Invalid delivered length')
    for a in data['archives']:
        name = safe_logical(a['name'])
        require('/' not in name and name not in archives, 'Invalid package part name')
        p = path.parent / name
        require(not p.is_symlink() and p.stat().st_size == a['bytes'] and file_hash(p) == a['sha256'], 'Package part identity mismatch: ' + name)
        with zipfile.ZipFile(p) as z:
            require(len(z.namelist()) == len(set(z.namelist())) and set(z.namelist()) == expected[name], 'Unexpected package member set: ' + name)
        archives[name] = p
    require(set(archives) == set(expected), 'Missing package part')
    return data, rows, archives


def copy_stored(record, archives, sink):
    total, whole = 0, hashlib.sha256()
    for member in record['storage']['members']:
        with zipfile.ZipFile(archives[member['archive']]) as z, z.open(member['entry']) as source:
            h, size = hashlib.sha256(), 0
            for block in iter(lambda: source.read(CHUNK), b''):
                sink.write(block)
                h.update(block)
                whole.update(block)
                size += len(block)
            require(size == member['bytes'] and h.hexdigest() == member['sha256'], 'Stored piece mismatch: ' + record['path'])
            total += size
    require(total == record['deliveredBytes'] and whole.hexdigest() == record['deliveredSha256'], 'Stored file mismatch: ' + record['path'])


class Discard:
    def write(self, block):
        return len(block)


@contextmanager
def sink_for(root, record):
    if root is None:
        yield Discard()
        return
    path = root / safe_logical(record['path'])
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('xb') as sink:
        yield sink
    os.chmod(path, record['mode'] & 0o777)
    os.utime(path, ns=(record['mtimeNs'], record['mtimeNs']))


def verify(manifest_path, output=None, allow_incomplete=False):
    data, rows, archives = load_package(manifest_path, allow_incomplete)
    if output is not None:
        output = output.resolve(strict=False)
        require(not output.exists() and not output.is_relative_to(manifest_path.parent.resolve()), 'Restore output must be new and outside the package directory')
        output.mkdir(parents=True)
    references, count = defaultdict(list), 0
    for r in rows.values():
        if r['kind'] == 'file':
            if r['storage']['kind'] == 'archive-members':
                with sink_for(output, r) as sink:
                    copy_stored(r, archives, sink)
                count += 1
            else:
                require(r['storage']['kind'] == 'original-zip-entry', 'Unknown reconstruction kind')
                references[r['storage']['zipLogicalPath']].append(r)
    for name, group in references.items():
        require(name in rows and rows[name]['kind'] == 'file' and rows[name]['storage']['kind'] == 'archive-members'
                and rows[name]['sha256'] == rows[name]['deliveredSha256'], 'ZIP reconstruction source must be preserved original bytes')
        with tempfile.TemporaryFile(dir=manifest_path.parent) as temporary:
            copy_stored(rows[name], archives, temporary)
            temporary.seek(0)
            with zipfile.ZipFile(temporary) as z:
                for r in group:
                    ref = r['storage']
                    entry = z.infolist()[ref['zipEntryIndex']]
                    require(entry.filename == ref['zipEntryName'] and entry.CRC == ref['zipCrc32'], 'ZIP entry identity mismatch')
                    h, size = hashlib.sha256(), 0
                    with z.open(entry) as source, sink_for(output, r) as sink:
                        for block in iter(lambda: source.read(CHUNK), b''):
                            sink.write(block)
                            h.update(block)
                            size += len(block)
                    require(size == r['deliveredBytes'] and h.hexdigest() == r['deliveredSha256'], 'Reconstructed file mismatch: ' + r['path'])
                    count += 1
    links = [r for r in rows.values() if r['kind'] == 'symlink']
    for r in links:
        target = r['targetLogicalPath']
        require(target == '.' or target in rows, 'Missing internal symlink target: ' + r['path'])
        if output is not None:
            dest = output / safe_logical(r['path'])
            dest.parent.mkdir(parents=True, exist_ok=True)
            os.symlink(os.path.relpath(output / target, dest.parent), dest)
    if output is not None:
        for r in reversed(list(rows.values())):
            if r['kind'] == 'directory':
                dest = output / r['path']
                dest.mkdir(parents=True, exist_ok=True)
                os.chmod(dest, r['mode'] & 0o777)
                os.utime(dest, ns=(r['mtimeNs'], r['mtimeNs']))
    return dict(schema=SCHEMA, status='verified', manifestSha256=file_hash(manifest_path),
                filesVerified=count, reconstructedFromOriginalZip=sum(map(len, references.values())),
                symlinksVerified=len(links), originalZips=len(data['originalZips']),
                preservedUnreadableZips=sum(z['status'].startswith('preserved-unreadable') for z in data['originalZips']),
                redactedFiles=sum('redaction' in r for r in rows.values()),
                byteRestorationBoundary='All delivered file bytes verified; signed receipts are intentionally sanitized. Symlink original target text is retained in the manifest and relocated on restore.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    for command in ['inventory', 'pack']:
        p = commands.add_parser(command)
        p.add_argument('--input', type=Path, required=True, help='Only this conversation evidence directory is scanned')
        p.add_argument('--output', type=Path, required=True, help='New output directory outside the input')
        p.add_argument('--b2-run', action='append', default=[], help='Additional B2 run ID; direct b2-named run content is detected automatically')
        if command == 'pack':
            p.add_argument('--part-mib', type=int, default=240)
    for command in ['verify', 'restore']:
        p = commands.add_parser(command)
        p.add_argument('--manifest', type=Path, required=True)
        if command == 'restore':
            p.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == 'inventory':
            inventory(args)
        elif args.command == 'pack':
            pack(args)
        else:
            print(json.dumps(verify(args.manifest.resolve(strict=True), getattr(args, 'output', None))))
    except Exception as exc:
        message, _ = scrub_text(str(exc))
        print(json.dumps(dict(status='failed', errorType=type(exc).__name__, message=message)), file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
