#!/usr/bin/env python3
"""Retain complete JIT versions and distinguish exact tier/PGO labels."""
import hashlib
import json
import pathlib
import re
import sys

HEADER = re.compile(r'^; Assembly listing for method (.+) \(([^()]*)\)$')
FOOTER = re.compile(r'^; Total bytes of code (\d+)')
INSTRUCTION_BYTES = re.compile(r'^\s+(?:[0-9A-F]{2})+\s+[a-z][a-z0-9]*\b')
OPTIMIZED_TIERS = {'Tier1', 'Tier1-OSR'}


def inventory(path, destination, scenario):
    destination.mkdir(parents=True, exist_ok=True)
    lines = path.read_text(errors='replace').splitlines(keepends=True) if path.exists() else []
    blocks, problems = [], []
    current = None
    for number, line in enumerate(lines, 1):
        match = HEADER.match(line.rstrip())
        if match:
            if current is not None:
                problems.append(dict(kind='nested-header-before-footer', first=current['start_line'], line=number))
                current['complete'] = False
                blocks.append(current)
            current = dict(method=match[1], tier=match[2], start_line=number, lines=[line])
        elif '; Assembly listing for method' in line:
            problems.append(dict(kind='unparseable-header', line=number))
            if current is not None:
                current['lines'].append(line)
                current['complete'] = False
                blocks.append(current)
                current = None
        elif current is not None:
            current['lines'].append(line)
            finish = FOOTER.match(line.rstrip())
            if finish:
                current.update(end_line=number, code_bytes=int(finish[1]), complete=True)
                blocks.append(current)
                current = None
        elif FOOTER.match(line.rstrip()):
            problems.append(dict(kind='orphan-footer', line=number))
    if current is not None:
        current['complete'] = False
        blocks.append(current)
        problems.append(dict(kind='missing-footer', line=current['start_line']))
    if not blocks:
        problems.append(dict(kind='no-method-blocks'))
    versions = []
    for index, block in enumerate(blocks):
        text = ''.join(block.pop('lines'))
        filename = f'block-{index:03d}.txt'
        (destination / filename).write_text(text)
        pgo = re.search(r'^; optimized using (.+)$', text, re.MULTILINE)
        profile = re.search(r'^; with (.+): fgCalledCount is (.+)$', text, re.MULTILINE)
        osr_entries = re.findall(r'^; OSR variant for entry point (.*)$', text, re.MULTILINE)
        expects_osr = block['tier'].endswith('-OSR')
        osr_consistent = (len(osr_entries) == 1 and re.fullmatch(r'0x[0-9a-fA-F]+', osr_entries[0]) is not None
                          if expects_osr else not osr_entries)
        if not osr_consistent:
            problems.append(dict(kind='inconsistent-OSR-label-entry', line=block['start_line'],
                                 tier=block['tier'], entries=osr_entries))
        block.update(index=index, file=filename, sha256=hashlib.sha256(text.encode()).hexdigest(),
            pgo_source=pgo[1] if pgo else None, profile_source=profile[1] if profile else None,
            called_count=profile[2] if profile else None, osr_entry=osr_entries[0] if len(osr_entries) == 1 else None,
            osr_label_consistent=osr_consistent,
            optimized_code='; optimized code\n' in text,
            # The release x64 JIT prints uppercase byte pairs, then a lowercase
            # mnemonic. Plain opcodes such as add/dec/fadd are not byte columns.
            instruction_byte_lines=sum(bool(INSTRUCTION_BYTES.match(line)) for line in text.splitlines()),
            callsites=[dict(line=block['start_line'] + offset, text=line.strip())
                for offset, line in enumerate(text.splitlines()) if re.search(r'\b(?:call|jmp)\b', line)])
        block['optimized_dynamic_pgo'] = (block['complete'] and osr_consistent and block['tier'] in OPTIMIZED_TIERS
            and block.get('code_bytes', 0) > 0 and block['instruction_byte_lines'] > 0
            and block['optimized_code'] and block['pgo_source'] == 'Dynamic PGO')
        # Keep each exact signature, generic instantiation, tier, and OSR entry.
        # Never pick the newest version or merge OSR with ordinary Tier1.
        block['comparison_key'] = [block['method'], block['tier'], block['pgo_source'], block['osr_entry']]
        versions.append(block)
    root_name = 'SendClientStreamAsync' if scenario.startswith('Client') else 'PumpGeneratedOutboundStreamAsync'
    target = [v['index'] for v in versions if root_name in v['method'] and ':MoveNext(' in v['method']
              and v['optimized_dynamic_pgo']]
    if not target:
        problems.append(dict(kind='target-root-Tier1-Dynamic-PGO-not-captured', target=root_name))
    result = dict(raw_file=str(path), raw_sha256=hashlib.sha256(path.read_bytes()).hexdigest() if path.exists() else None,
        methods=versions, problems=problems, target_root_versions=target,
        status='inconclusive' if problems else 'pending-independent-path-review',
        call_chain_status='unproved-until-instruction-review',
        review_requirements=['Trace the actual generic RPC caller through sized/unsized async bodies to admission.',
            'Inspect emitted validation calls or the guards in the optimized caller when inlined.',
            'Match unchanged signatures, instantiations, exact tiers, PGO sources and OSR entries; explicitly map changed helper roles from source.',
            'Missing or Tier0-only target paths, mixed blocks, and unresolved callsites stop as inconclusive.',
            'G2 may already optimize away the NativeAOT out-store; unchanged optimized G2/V is valid evidence.'],
        boundary='Code collection only; root-tier coverage is not a validated call chain, mechanism pass, or timing result.')
    (destination / 'inventory.json').write_text(json.dumps(result, indent=2) + '\n')
    return result


if __name__ == '__main__':
    result = inventory(pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]), sys.argv[3])
    print(json.dumps({key: result[key] for key in ('status', 'problems', 'target_root_versions')}, indent=2))
