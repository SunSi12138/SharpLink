#!/usr/bin/env python3
"""Offline fixture tests only: no CLR, probes, downloads, or timed workloads."""
import hashlib, tempfile, unittest
from pathlib import Path
from inventory import inventory
from verify_archives import verify
from collect import validate_environment

class ToolTests(unittest.TestCase):
    def test_missing_and_inline_body_never_accepted(self):
        self.assertEqual(inventory('')['status'],'INCOMPLETE_NO_MATCHING_NATIVE_BODY')
        text='; Assembly listing for method SharpLink.Runtime.SendPump:TryEnqueue() (Tier1)\n lock or dword ptr [rsp], 0\n mov eax, [rdi+8]\n'
        result=inventory(text);self.assertFalse(result['accepted']);self.assertEqual(len(result['bodies']),1)
        self.assertEqual(result['bodies'][0]['tierLabels'],['Tier1'])
    def test_tier0_and_arm_instruction_hints_not_proof(self):
        result=inventory('; Assembly listing for method SharpLink.Runtime.WakeupSignal:Signal() (Instrumented Tier0)\n dmb ish\n ldr w1, [x0]\n casal w1, w2, [x3]\n')
        self.assertFalse(result['accepted']);self.assertEqual(len(result['bodies'][0]['instructionHints']),2)
    def test_inherited_runtime_overrides_rejected(self):
        validate_environment({'DOTNET_ROOT':'/tmp/example','PATH':'x'})
        for key in ['DOTNET_TieredCompilation','COMPlus_ReadyToRun','DOTNET_GCServer','DOTNET_JitDisasm','DOTNET_PROCESSOR_COUNT','DOTNET_EnableDiagnostics']:
            with self.assertRaises(ValueError):validate_environment({key:'0'})
    def test_archive_hashes_and_completeness(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);rows=[]
            for c in ['sdk','runtime','aspnetcore-runtime']:
                p=root/c;p.write_bytes(c.encode());rows.append({'rid':'linux-x64','component':c,'name':c,'hash':hashlib.sha512(c.encode()).hexdigest(),'url':'https://example.invalid/'+c})
            self.assertEqual(len(verify({'artifacts':rows},'linux-x64',root)),3)
            (root/'sdk').write_bytes(b'corrupted')
            with self.assertRaises(ValueError):verify({'artifacts':rows},'linux-x64',root)
            with self.assertRaises(ValueError):verify({'artifacts':rows[:2]},'linux-x64',root)
if __name__=='__main__':unittest.main()
