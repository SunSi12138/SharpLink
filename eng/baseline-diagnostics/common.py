"""Fixed diagnostic-only identities; no performance acceptance or uploads."""
import hashlib
import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parents[1]
SOURCE = '3d76a3a8b55a37e24c62a073fe588cff3d3867be'
TREE = '1c24da3af090acd4d6cdbfb005a9e3bd95304e3a'

def load_shared(name):
    path = ROOT.parent / 'outbound-lease-comparison' / (name + '.py')
    spec = importlib.util.spec_from_file_location('diagnostic_shared_' + name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

old_common = load_shared('common')
old_identity = load_shared('identity')
strict_json = old_common.strict_json
digest = old_common.digest
write = old_common.write
inventory = old_common.inventory
select_cpus = old_common.select_cpus
CONTRACT = strict_json((ROOT / 'protocol.json').read_text())

def source_inventory(root=None):
    # Both reused identity/CPU helpers and this new campaign are exact source inputs.
    paths = [p for p in ROOT.rglob('*') if p.is_file() and
        not {'__pycache__', 'bin', 'obj'} & set(p.relative_to(ROOT).parts)]
    paths += [ROOT.parent / 'outbound-lease-comparison' / name
              for name in ('common.py', 'protocol.json', 'identity.py')]
    paths += [REPO / '.github/workflows/baseline-diagnostics.yml']
    return {str(p.relative_to(REPO)): digest(p) for p in sorted(paths) if p.exists()}
