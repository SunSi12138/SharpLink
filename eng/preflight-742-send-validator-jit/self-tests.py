#!/usr/bin/env python3
"""Small capture-integrity fixtures, without a runtime or product execution."""
import pathlib
import tempfile

from inventory import inventory

METHOD = 'SharpLink.Client.ClientConnection+<SendClientStreamAsync>d__1`1[int]:MoveNext():this'


def block(tier='Tier1', pgo='Dynamic PGO', body='       55                   push rbp\n', footer=True, osr=None):
    if osr is None:
        osr = '; OSR variant for entry point 0x1a\n' if tier.endswith('-OSR') else ''
    return (f'; Assembly listing for method {METHOD} ({tier})\n; {tier} code\n; optimized code\n'
            f'; optimized using {pgo}\n' + osr + body + ('; Total bytes of code 1\n' if footer else ''))


cases = [
    ('tier1-dynamic', block(), True),
    ('tier1-osr', block('Tier1-OSR'), True),
    ('tier0', block('Tier0'), False),
    ('instrumented-tier1', block('Instrumented Tier1'), False),
    ('instrumented-osr', block('Instrumented Tier1-OSR'), False),
    ('static-profile', block(pgo='Static PGO'), False),
    ('blended-profile', block(pgo='Blended PGO'), False),
    ('no-instructions', block(body=''), False),
    ('plain-add-opcode', block(body='       add                  rax, 1\n'), False),
    ('plain-dec-opcode', block(body='       dec                  rax\n'), False),
    ('plain-fadd-opcode', block(body='       fadd                 st(0), st(1)\n'), False),
    ('odd-hex-column', block(body='       ABC                  push rbp\n'), False),
    ('uppercase-byte-column', block(body='       488BC1               mov rax, rcx\n'), True),
    ('osr-entry-missing', block('Tier1-OSR', osr=''), False),
    ('osr-entry-malformed', block('Tier1-OSR', osr='; OSR variant for entry point corrupt\n'), False),
    ('osr-entry-duplicated', block('Tier1-OSR', osr='; OSR variant for entry point 0x1a\n' * 2), False),
    ('osr-entry-unexpected', block(osr='; OSR variant for entry point 0x1a\n'), False),
    ('truncated', block(footer=False), False),
    ('nested-header', block(footer=False) + block(), False),
    ('malformed-nested-header', block(footer=False) + '; Assembly listing for method corrupt\n; Total bytes of code 1\n', False),
]
with tempfile.TemporaryDirectory() as temporary:
    root = pathlib.Path(temporary)
    for name, text, expected in cases:
        raw = root / (name + '.txt')
        raw.write_text(text)
        result = inventory(raw, root / name, 'Client100x16')
        assert (result['status'] == 'pending-independent-path-review') == expected, name
        assert result['call_chain_status'] == 'unproved-until-instruction-review'
    raw = root / 'versions.txt'
    raw.write_text(block('Tier0') + block('Instrumented Tier0') + block() + block('Tier1-OSR'))
    result = inventory(raw, root / 'versions', 'Client100x16')
    assert [method['tier'] for method in result['methods']] == ['Tier0', 'Instrumented Tier0', 'Tier1', 'Tier1-OSR']
    assert result['target_root_versions'] == [2, 3]
    assert result['methods'][3]['osr_entry'] == '0x1a'
print(f'{len(cases) + 1} capture-integrity fixtures passed; every tier/version retained; no fixture establishes a call chain.')
