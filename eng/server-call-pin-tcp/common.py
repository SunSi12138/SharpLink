"""Immutable diagnostic contract and offline-only helpers."""
import hashlib
import json
import re
from fractions import Fraction
from pathlib import Path

def strict_json(text):
    def object_pairs(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f'Duplicate JSON key {key}')
            result[key] = value
        return result
    def constant(value):
        raise ValueError(f'Nonfinite JSON constant {value}')
    return json.loads(text, object_pairs_hook=object_pairs, parse_constant=constant)


ROOT = Path(__file__).resolve().parent
CONTRACT = strict_json((ROOT / 'protocol.json').read_text())
ARMS = ('baseline', 'candidate')
PRODUCTS = ('Abstractions', 'Client', 'Runtime', 'Sdk', 'Server')


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def write(path, data):
    Path(path).write_text(json.dumps(data, indent=2) + '\n')


def inventory(root):
    return {str(p.relative_to(root)): digest(p) for p in sorted(root.rglob('*')) if p.is_file()}


def schedule():
    cells = [(mode, n) for n in CONTRACT['items'] for mode in CONTRACT['modes']]
    result = []
    for pair in range(1, CONTRACT['pairsPerCell'] + 1):
        k = (pair - 1) % len(cells)
        for mode, n in cells[k:] + cells[:k]:
            for arm in ARMS if pair % 2 else ARMS[::-1]:
                result.append(dict(id=f'{mode}-n{n}-r{pair}-{arm}', mode=mode, items=n,
                                   pair=pair, arm=arm))
    return result


def nearest_rank(values, numerator, denominator):
    if not values or any(type(v) is not int or v < 0 for v in values):
        raise ValueError('Require nonempty nonnegative integer tick vector')
    if not 0 < numerator <= denominator:
        raise ValueError('Invalid quantile')
    rank = (len(values) * numerator + denominator - 1) // denominator
    return sorted(values)[rank - 1]


def rational(value):
    value = Fraction(value)
    return {'numerator': value.numerator, 'denominator': value.denominator, 'decimal': float(value)}


def median(values):
    values = sorted(map(Fraction, values))
    n = len(values)
    if not n:
        raise ValueError('No median of empty sample')
    return values[n // 2] if n % 2 else (values[n // 2 - 1] + values[n // 2]) / 2


def source_inventory(root):
    return {str(p.relative_to(root)): digest(p) for p in sorted(root.rglob('*'))
            if p.is_file() and p.suffix != '.nupkg' and not {'__pycache__', 'bin', 'obj'} & set(p.relative_to(root).parts)}


def select_cpus(mapping, allowed):
    rows = []
    for line in mapping.splitlines():
        if not line or line.startswith('#'):
            continue
        parts = line.split(',')
        if len(parts) != 4:
            raise ValueError('Unrecognized guest topology row')
        cpu, core, socket, online = parts
        if cpu.isdigit() and core.isdigit() and socket.isdigit() and online == 'Y' and int(cpu) in allowed:
            rows.append((int(cpu), int(socket), int(core)))
    rows.sort()
    if len({r[0] for r in rows}) != len(rows):
        raise ValueError('Duplicate CPU topology')
    if rows:
        for row in rows[1:]:
            if row[1:] != rows[0][1:]:
                return [rows[0][0], row[0]]
    raise ValueError('Two online allowed distinct guest-reported cores not provable')


def gate_schedule(phase):
    if phase not in ('strict-cleanup', 'normal-validate'):
        raise ValueError('Invalid gate phase')
    arms = ARMS
    return [dict(id=f'{mode}-n{count}-{arm}', arm=arm, mode=mode, items=count)
            for count in CONTRACT['items'] for mode in CONTRACT['modes'] for arm in arms]


def verify_preparation_witness(manifest):
    """Pure offline binding; actual PE metadata is independently reread before TCP."""
    if manifest.get('metadataPreflight') != 'passed':
        raise ValueError('Missing compiled preparation identity proof')
    if set(manifest['arms']) != set(ARMS):
        raise ValueError('Unexpected prepared arms')
    for arm in ARMS:
        record = manifest['arms'][arm]
        if (record['source']['commit'], record['source']['tree']) != (
                CONTRACT[arm + 'Source'], CONTRACT[arm + 'Tree']):
            raise ValueError('Prepared arm source/tree differs')
        if set(record['fixtureMetadata']) != {'probe', 'module'}:
            raise ValueError('Missing compiled fixture identity')
        for metadata in record['fixtureMetadata'].values():
            expected = {'Arm': arm, 'SourceTree': CONTRACT[arm + 'Tree'],
                        'ExpectedProductCommit': CONTRACT[arm + 'Source']}
            if any(metadata.get(key) != value for key, value in expected.items()):
                raise ValueError('Compiled fixture arm/source/tree differs')
        if set(record['productMetadata']) != set(PRODUCTS) | {'Generator'}:
            raise ValueError('Missing own product/generator compiled identity')
        for name, metadata in record['productMetadata'].items():
            if (set(metadata) != {'assemblyName', 'mvid', 'informationalVersion'}
                    or metadata['assemblyName'] != 'SharpLink.' + name
                    or metadata['informationalVersion'] != CONTRACT[arm + 'Version'] + '+' + CONTRACT[arm + 'Source']
                    or not re.fullmatch('[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}', metadata['mvid'])):
                raise ValueError('Compiled product/generator identity differs')
        for name in PRODUCTS:
            filename = f'SharpLink.{name}.dll'
            if record['snapshot'][filename] != record['closure'][filename]:
                raise ValueError('TCP and correctness product bytes differ')
    return manifest
