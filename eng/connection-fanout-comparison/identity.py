"""Exact framework14 execution closure, restored from O203C1 c1b4d73.

Copied private12 reference DLLs remain verified snapshot inputs; those four
assemblies actually bind to the higher fixed private shared-framework14 closure.
No arbitrary framework directories, dependency upgrades or product swaps are allowed.
"""
import hashlib
import io
import re
import tarfile
from pathlib import Path

FRAMEWORK_PRODUCT_VERSION = '10.0.14+0427b9abbead3fa64c6655eb4290c537397131fa'
FRAMEWORK = {
    'Microsoft.Extensions.Logging.Abstractions': ('c014eba79da8764b050bee1f1f70db623dd5026e855b5bc7bf463b10913643b2', '9c0203ae-8da7-43b6-ba77-11ebeca53df9'),
    'Microsoft.Extensions.DependencyInjection': ('12a361d88bf9c0ef4f4efe388415aa3820bb15c115002be4fd6f219fab527170', '7b80704b-1f41-48f7-9b28-da04ae40188e'),
    'Microsoft.Extensions.DependencyInjection.Abstractions': ('8bc15b2a240737a54c253711054d140db8995055d0209d98f9148e04daec27f4', 'e74460a9-5d22-43dd-b598-66467a162477'),
    'System.Threading.RateLimiting': ('6d7816840be1cc3e03693c99e5949e4bf6c4833ce79a0a1c3dd6e925032201c3', '0798f765-10d2-4f7a-aa9e-9a7639dc46b2'),
}


IDENTITY_CODES = frozenset({'framework_policy', 'framework_disk_hash', 'loaded_closure',
                            'loaded_basename', 'framework_loaded_identity', 'own_arm_identity',
                            'product_source', 'identity_record', 'archive_integrity', 'archive_entry',
                            'framework_archive_disk', 'framework_archive_loaded'})


class IdentityError(ValueError):
    def __init__(self, code, detail):
        if code not in IDENTITY_CODES:
            raise ValueError('Unknown identity failure code')
        self.code = code
        super().__init__(detail)



def _canonical_path(value):
    if type(value) is not str or not value or not Path(value).is_absolute():
        return False
    path = Path(value)
    try:
        return value == str(path.resolve(strict=False))
    except (OSError, RuntimeError, ValueError):
        return False


def _require_record(record, fields):
    if (type(record) is not dict or set(record) != set(fields) or
            any(type(record[key]) is not str or not record[key] for key in fields) or
            not _canonical_path(record['location'])):
        raise IdentityError('identity_record', 'Malformed or noncanonical assembly record')
    if 'mvid' in fields and re.fullmatch(r'[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}', record['mvid']) is None:
        raise IdentityError('identity_record', 'Malformed assembly module identity')


def framework_policy(runtime):
    directory = runtime.parent / 'shared/Microsoft.AspNetCore.App/10.0.14'
    policy = {}
    for name, (sha, mvid) in FRAMEWORK.items():
        path = directory / (name + '.dll')
        if not _canonical_path(str(path)) or hashlib.sha256(path.read_bytes()).hexdigest() != sha:
            raise IdentityError('framework_disk_hash', f'Private framework14 hash mismatch {name}')
        policy[name] = dict(path=str(path), sha256=sha, mvid=mvid, productVersion=FRAMEWORK_PRODUCT_VERSION)
    return policy


def validate_policy(policy, runtime):
    if type(policy) is not dict or set(policy) != set(FRAMEWORK):
        raise IdentityError('framework_policy', 'Unexpected framework dependency policy closure')
    for name, (sha, mvid) in FRAMEWORK.items():
        expected = dict(path=str(runtime.parent / 'shared/Microsoft.AspNetCore.App/10.0.14' / (name + '.dll')),
                        sha256=sha, mvid=mvid, productVersion=FRAMEWORK_PRODUCT_VERSION)
        if policy[name] != expected:
            raise IdentityError('framework_policy', f'Policy differs from fixed framework14 origin: {name}')


def validate_loaded(loaded, arm, prepared, snapshot, source, policy, runtime):
    validate_policy(policy, runtime)
    if type(loaded) is not list:
        raise IdentityError('identity_record', 'Loaded assemblies must be a record list')
    for record in loaded:
        _require_record(record, ('name','location','sha256','mvid','productVersion'))
    required = {'SharpLink.Abstractions', 'SharpLink.Client', 'SharpLink.Runtime', 'SharpLink.Server',
                'PhysicalTcpProbe', 'ScalarStreamModule'} | set(FRAMEWORK)
    names = [a['name'] for a in loaded]
    allowed = required | {'SharpLink.Sdk'}
    if len(names) != len(set(names)) or not required <= set(names) or not set(names) <= allowed:
        raise IdentityError('loaded_closure', 'Missing, duplicate or undeclared executed identity')
    for assembly in loaded:
        name = assembly['name']
        if Path(assembly['location']).name != name + '.dll':
            raise IdentityError('loaded_basename', 'Loaded path basename does not match assembly identity')
        if name in FRAMEWORK:
            expected = policy[name]
            if (assembly['location'], assembly['sha256'], assembly['mvid'], assembly['productVersion']) != (
                    expected['path'], expected['sha256'], expected['mvid'], expected['productVersion']):
                raise IdentityError('framework_loaded_identity', f'Framework identity differs from fixed private14 policy: {name}')
        else:
            path = Path(assembly['location'])
            if path.parent != prepared / arm or assembly['sha256'] != snapshot.get(path.name) or not assembly['mvid']:
                raise IdentityError('own_arm_identity', f'Product/fixture escaped own-arm snapshot: {name}')
            if name.startswith('SharpLink.') and assembly['productVersion'].split('+')[-1] != source:
                raise IdentityError('product_source', 'Wrong product source identity')


ARCHIVE_SHA512 = {
    'dotnet.tar.gz': '634326028645f38731c8c4e71a68356e4ca0d6b6607139a234ab760687f742063edaf33a1f20dda615742d2dc8300ca2ff857619d51fbb38090383b71334af89',
    'aspnetcore.tar.gz': 'dd024fd2616926a555b75cbf7a836f6dce998199f0596d9d75676dca2b9d59c8ee8673c6ac23473c0f1a4bdd59eb5cd02fe9ab097eeaf2dd8c8bd117c85e7031',
}
_FRAMEWORK_DLL = re.compile(r'shared/(Microsoft\.NETCore\.App|Microsoft\.AspNetCore\.App)/10\.0\.14/[A-Za-z0-9_.-]+\.dll')
_REQUIRED_FRAMEWORK_RECORDS = {
    'System.Private.CoreLib': 'shared/Microsoft.NETCore.App/10.0.14/System.Private.CoreLib.dll',
    'Microsoft.AspNetCore': 'shared/Microsoft.AspNetCore.App/10.0.14/Microsoft.AspNetCore.dll',
}


def _archive_framework_entries(payload, policy):
    """Parse only; never extract archive paths. Caller first verifies immutable payload SHA512."""
    try:
        with tarfile.open(fileobj=io.BytesIO(payload), mode='r:gz') as archive:
            for member in archive:
                name = member.name
                if member.isdir() and name == '.':
                    continue  # tarfile normalizes the archive root ./ to .
                if name.startswith('./'):
                    name = name[2:]
                if member.isdir():
                    name = name.removesuffix('/')
                    if not name: continue  # the canonical ./ archive-root directory
                if (not name or name.startswith('/') or '\\' in name or
                        any(part in ('', '.', '..') for part in name.split('/')) or
                        member.issym() or member.islnk() or not (member.isdir() or member.isfile())):
                    raise IdentityError('archive_entry', 'Unsafe archive entry')
                if not name.endswith('.dll'):
                    continue
                if not member.isfile() or _FRAMEWORK_DLL.fullmatch(name) is None:
                    raise IdentityError('archive_entry', 'Noncanonical framework DLL archive entry')
                stream = archive.extractfile(member)
                if stream is None:
                    raise IdentityError('archive_entry', 'Unreadable framework DLL archive member')
                sha = hashlib.sha256(stream.read()).hexdigest()
                if name in policy and policy[name] != sha:
                    raise IdentityError('archive_entry', 'Conflicting duplicate framework DLL')
                policy[name] = sha
    except (tarfile.TarError, EOFError, OSError) as error:
        raise IdentityError('archive_entry', 'Malformed runtime archive') from error


def _check_archive_policy_shape(policy):
    if (type(policy) is not dict or not policy or
            any(type(path) is not str or _FRAMEWORK_DLL.fullmatch(path) is None or
                type(sha) is not str or re.fullmatch(r'[0-9a-f]{64}', sha) is None
                for path,sha in policy.items()) or
            not set(_REQUIRED_FRAMEWORK_RECORDS.values()) <= set(policy)):
        raise IdentityError('framework_policy', 'Malformed framework archive policy')


def archive_framework_policy(runtime, archives_dir):
    """Derive full allowlist from both frozen archives, then verify extracted files.

    The two archives legitimately duplicate identical CoreCLR DLL entries. A conflicting
    duplicate, traversal, link, special entry, or noncanonical DLL is rejected. This
    function reads archive bytes only and never extracts or modifies runtime files.
    """
    root = runtime.parent
    if not _canonical_path(str(runtime)) or not root.is_dir():
        raise IdentityError('framework_archive_disk', 'Noncanonical private runtime root')
    policy = {}
    for filename, expected in ARCHIVE_SHA512.items():
        path = archives_dir / filename
        if not _canonical_path(str(path)) or not path.is_file():
            raise IdentityError('archive_integrity', 'Runtime archive unavailable or linked')
        payload = path.read_bytes()  # hash and parse the very same immutable bytes
        if hashlib.sha512(payload).hexdigest() != expected:
            raise IdentityError('archive_integrity', 'Runtime archive SHA512 mismatch')
        _archive_framework_entries(payload, policy)
    _check_archive_policy_shape(policy)
    for relative, expected in policy.items():
        path = root / relative
        if (not _canonical_path(str(path)) or not path.is_file() or
                hashlib.sha256(path.read_bytes()).hexdigest() != expected):
            raise IdentityError('framework_archive_disk', 'Extracted runtime DLL differs from fixed archive')
    return dict(sorted(policy.items()))


def validate_framework_assemblies(records, runtime, policy):
    """Validate the remaining loaded framework closure against a trusted archive policy.

    Caller must derive policy using archive_framework_policy before launch and recompute
    it in offline validation; serialized policy alone is not a trust root. Full actual
    file verification occurs there. Non-strict resolve here also rejects existing
    symlinks and lexical aliases, while supporting pure synthetic offline records.
    """
    _check_archive_policy_shape(policy)
    if type(records) is not list:
        raise IdentityError('identity_record', 'Framework assemblies must be a record list')
    names, locations = set(), set()
    observed = {}
    for record in records:
        _require_record(record, ('name','location','sha256'))
        name, location = record['name'], record['location']
        if name in names or location in locations or name in FRAMEWORK:
            raise IdentityError('framework_archive_loaded', 'Duplicate or misplaced framework record')
        names.add(name); locations.add(location)
        path = Path(location)
        try:
            relative = path.relative_to(runtime.parent).as_posix()
        except ValueError as error:
            raise IdentityError('framework_archive_loaded', 'Framework assembly escaped private runtime') from error
        if (path.name != name + '.dll' or relative not in policy or
                record['sha256'] != policy[relative]):
            raise IdentityError('framework_archive_loaded', 'Framework assembly not in fixed archive allowlist')
        observed[name] = relative
    if any(observed.get(name) != relative for name,relative in _REQUIRED_FRAMEWORK_RECORDS.items()):
        raise IdentityError('framework_archive_loaded', 'Required framework anchor missing or moved')
