#!/usr/bin/env python3
"""Offline full-coverage and bounded-output counterexamples; no .NET processes."""
import copy
import contextlib
import io
import hashlib
import json
from pathlib import Path
import tempfile
import subprocess
import unittest
from unittest.mock import patch
import compare as c


def symbol(name='T:N.A', visible=True):
    s = {k: None for k in c.SYMBOL_KEYS}
    s.update(schemaVersion=1, id='src/A.csproj@net10.0|A|'+name, semanticId='A|'+name,
             project='src/A.csproj', framework='net10.0', assembly='A', kind='NamedType',
             name=name, metadataName=name, signature='public class '+name,
             declaredAccessibility='Public', effectiveAccessibility='Public', accessibilityConstraints=['Public'],
             origin='explicit-source', isImplicit=False, hasGeneratedSource=False, externallyVisible=visible,
             nonSealedClass=False, hasPublicInstanceConstructor=True,
             details={'isSealed': True, 'isAbstract': False, 'parameters': [{'name':'x','default':1},{'name':'y','default':2}], 'interfaces': ['I1','I2']},
             declarations=[], locations=[])
    return s


def relation(s, kind='interface', target='B|T:I1'):
    return dict(source=s['id'], kind=kind, target=target, targetAssembly='B', targetAccessibility='Public')


class ProjectionTests(unittest.TestCase):
    def compare(self, old, new, old_rel=(), new_rel=(), **kw):
        a, _, _ = c.project(old, list(old_rel)); b, symbols, relations = c.project(new, list(new_rel))
        return c.differences(a, b, symbols, relations, **kw)

    def test_exact_and_location_only_unchanged(self):
        s=symbol(); n=copy.deepcopy(s); n['locations']=[{'body':'not exported'}];n['declarations']=[{'sha256':'changed'}]
        code,summary,records=self.compare([s],[n]);self.assertEqual(code,0);self.assertEqual(summary['unchanged'],1);self.assertFalse(records)

    def test_all_semantic_fields_change_digest(self):
        s=symbol()
        for key in c.semantic(s):
            if key in ('id','schemaVersion','externallyVisible','nonSealedClass'):continue
            n=copy.deepcopy(s)
            value=n[key]
            if type(value) is bool:n[key]=not value
            elif isinstance(value,str):n[key]=value+'changed'
            elif isinstance(value,list):n[key]=value+['changed']
            elif isinstance(value,dict):n[key]={**value,'changed':1}
            else:n[key]='changed'
            with self.subTest(key=key):self.assertEqual(self.compare([s],[n])[0],1)

    def test_scalar_type_and_array_order_are_semantic(self):
        s=symbol()
        for mutate in [lambda d:d.update(parameters=list(reversed(d['parameters']))),lambda d:d.update(interfaces=list(reversed(d['interfaces']))),lambda d:d['parameters'][0].update(default=1.0),lambda d:d['parameters'][0].update(default=True),lambda d:d.update(attributes=[{'arguments':['b','a']}]),lambda d:d.update(refKind='Ref',nullable=True,scoped='Scoped',constantValue=9,genericConstraints=['new()'])]:
            n=copy.deepcopy(s);mutate(n['details']);self.assertEqual(self.compare([s],[n])[0],1)

    def test_added_removed_and_count_preserving_substitution(self):
        s=symbol();n=symbol('T:N.B');code,summary,records=self.compare([s],[n]);self.assertEqual(code,1);self.assertEqual((summary['added'],summary['removed'],summary['unchanged']),(1,1,0));self.assertEqual(len(records),2)

    def test_visibility_nonsealed_and_generated_changes(self):
        s=symbol()
        for key,value in [('externallyVisible',False),('nonSealedClass',True),('hasGeneratedSource',True),('isImplicit',True),('origin','source-generated')]:
            n=copy.deepcopy(s);n[key]=value;self.assertEqual(self.compare([s],[n])[0],1)

    def test_relations_add_remove_incoming_internal_and_reorder(self):
        a=symbol();b=symbol('T:N.Internal',False);r=relation(b,'base',a['semanticId']);r['targetAssembly']='A'
        code,summary,records=self.compare([a,b],[a,b],[],[r]);self.assertEqual(code,1);self.assertNotIn('symbol',records[0]);self.assertEqual(records[0]['addedRelations'],[r])
        code,summary,records=self.compare([a,b],[a,b],[r],[]);self.assertEqual(records[0]['removedRelationSha256'],[c.digest(r)])
        r2=relation(a);self.assertEqual(self.compare([a,b],[a,b],[r,r2],[r2,r])[0],0)

    def test_duplicate_unknown_schema_and_missing_relation_source_fail(self):
        s=symbol();r=relation(s)
        variants=[lambda:c.project([s,s],[]),lambda:c.project([s],[r,r]),lambda:c.project([{**s,'schemaVersion':True}],[]),lambda:c.project([{**s,'unknown':1}],[]),lambda:c.project([s],[{**r,'unknown':1}]),lambda:c.project([s],[{**r,'source':'missing'}])]
        for run in variants:
            with self.assertRaises(ValueError):run()
        with self.assertRaises(ValueError):c.parse('{"x":1,"x":2}')
        with self.assertRaises(ValueError):c.parse('{"x":NaN}')

    def test_bounds_never_emit_partial_records(self):
        old=[symbol('T:N.'+str(i)) for i in range(33)];new=copy.deepcopy(old)
        for s in new:s['signature']+=' changed'
        code,summary,records=self.compare(old,new);self.assertEqual(code,2);self.assertEqual(summary['changed'],33);self.assertFalse(records)
        s=symbol();n=copy.deepcopy(s);n['signature']='x'*8192
        self.assertEqual(self.compare([s],[n])[0],2)
        n['signature']='changed'
        code,summary,records=self.compare([s],[n],limits=(32,8192,1));self.assertEqual(code,2);self.assertFalse(records)

    def test_added_id_with_large_relations_is_incomplete(self):
        s=symbol();rs=[relation(s,target='B|T:I'+str(i)) for i in range(80)]
        code,summary,records=self.compare([], [s], [], rs);self.assertEqual(code,2);self.assertFalse(records)

    def test_exact_byte_boundary(self):
        s=symbol();n=copy.deepcopy(s);n['signature']='changed'
        code,summary,records=self.compare([s],[n]);size=summary['diagnosticBytes']
        self.assertEqual(self.compare([s],[n],limits=(1,size,size))[0],1)
        self.assertEqual(self.compare([s],[n],limits=(1,size-1,size))[0],2)

    def test_assembly_grants_are_not_silently_ignored(self):
        s=symbol();r={'source':'src/A.csproj@net10.0|A','kind':'internals-visible-to','target':'Friend','targetAssembly':'Friend','targetAccessibility':'AssemblyGrant'}
        old,_,_=c.project([s],[]);new,symbols,relations=c.project([s],[r])
        code,summary,records=c.differences(old,new,symbols,relations,[],[c.digest(r)]);self.assertEqual(code,1);self.assertTrue(summary['assemblyRelationsChanged']);self.assertEqual(records[0]['addedRelations'],[r])


class ReviewReportTests(unittest.TestCase):
    def test_overflow_preserves_full_report_without_stdout_or_approval(self):
        old=[symbol('T:N.'+str(i)) for i in range(33)]
        new=copy.deepcopy(old)
        for item in new:item['signature']+=' changed'
        a,_,_=c.project(old,[]); b,symbols,relations=c.project(new,[])
        with tempfile.TemporaryDirectory() as temporary:
            path=Path(temporary)/'review'/'comparison.json'
            code,summary,records=c.differences(a,b,symbols,relations,report=path)
            saved=c.read(path)
            self.assertEqual(code,2);self.assertEqual(records,[])
            self.assertEqual(saved['summary'],summary)
            self.assertEqual(summary['status'],'INCOMPLETE_REVIEW_OUTPUT')
            self.assertEqual(len(saved['records']),33)
            self.assertEqual(saved['approvalStatus'],'not-reviewed')
            self.assertEqual((saved['source'],saved['tree']),(c.SOURCE,c.TREE))
            self.assertNotIn('required',saved);self.assertNotIn('historical',saved)

    def test_no_diff_report_does_not_grant_approval_and_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as temporary:
            path=Path(temporary)/'comparison.json'
            code,summary,records=c.differences({}, {}, {}, {}, report=path)
            self.assertEqual(code,0);self.assertEqual(records,[])
            self.assertEqual(c.read(path)['approvalStatus'],'not-reviewed')
            original=path.read_bytes()
            with self.assertRaises(ValueError):c.differences({}, {}, {}, {}, report=path)
            self.assertEqual(path.read_bytes(),original)

    def test_validation_failure_retains_bounded_diagnostic_without_approval(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);report=root/'review'/'comparison.json';output=io.StringIO()
            with patch('sys.argv',['compare.py',str(root/'missing-inventory'),'--fixture',str(root/'missing-fixture'),'--report',str(report)]), contextlib.redirect_stdout(output):
                code=c.main()
            self.assertEqual(code,2)
            saved=c.read(report)
            self.assertEqual(saved['summary']['status'],'INCOMPLETE')
            self.assertEqual(saved['records'],[])
            self.assertEqual(saved['approvalStatus'],'not-reviewed')
            self.assertNotIn('Traceback',output.getvalue())

    def test_report_existing_and_dangling_symlink_parents_rejected(self):
        for exists in (False,True):
            with self.subTest(existing_target=exists), tempfile.TemporaryDirectory() as temporary:
                root=Path(temporary);outside=root/'outside'
                if exists:outside.mkdir()
                parent=root/'review';parent.symlink_to(outside,target_is_directory=True)
                with self.assertRaisesRegex(ValueError,'parent is a symlink'):
                    c.write_review_report(parent/'nested'/'comparison.json',{'status':'INCOMPLETE'},[])
                self.assertFalse((outside/'nested'/'comparison.json').exists())

    def test_report_normal_new_parents_succeed(self):
        with tempfile.TemporaryDirectory() as temporary:
            path=Path(temporary)/'review'/'nested'/'comparison.json'
            c.write_review_report(path,{'status':'INCOMPLETE'},[])
            self.assertEqual(c.read(path)['summary']['status'],'INCOMPLETE')

    def test_driver_noclobber_rejects_existing_and_symlink_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);target=root/'existing';target.write_text('preserved')
            link=root/'link';link.symlink_to(target)
            dangling=root/'dangling';dangling.symlink_to(root/'missing')
            for path in (target,link,dangling):
                result=subprocess.run(['bash','-c','set -C; printf replaced > "$1"','driver',str(path)],capture_output=True)
                self.assertNotEqual(result.returncode,0)
            self.assertEqual(target.read_text(),'preserved');self.assertFalse((root/'missing').exists())

    def test_report_symlink_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            path=Path(temporary)/'comparison.json';path.symlink_to(Path(temporary)/'missing')
            with self.assertRaises(ValueError):c.write_review_report(path,{'status':'INCOMPLETE'},[])


class InventoryTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name)
        self.targets=[dict(project='src/A.csproj',framework='net10.0',assembly='A')];self.s=symbol();self.tool={'tool':'a'*64}
        self.data={'symbols.jsonl':[self.s],'relations.jsonl':[],
          'coverage.json':dict(schemaVersion=1,complete=True,failures=[],scope='production-declarations',hostOs='linux',hostArchitecture='x86_64',configuration='Release',expectedTargets=self.targets,actualTargets=[{**self.targets[0],'complete':True,'sdkVersion':'10.0.102'}],symbolCountByTarget={'src/A.csproj@net10.0':1},pendingDecisionCount=1,symbolCountByOrigin={'explicit-source':1},symbolCountByEffectiveAccessibility={'Public':1},unionIdentityCountIgnoringTfm=1,typeCounts=[dict(project='src/A.csproj',framework='net10.0',accessibility='Public',sealed=True,abstract=False,count=1)]),
          'decisions.json':dict(schemaVersion=1,approvalStatus='not-reviewed',decisions=[dict(symbolId=self.s['id'],classification='needs-decision',rationale=None,evidence=[])])}
        source=dict(commit=c.SOURCE,tree=c.TREE,status='',untrackedFilesSha256={},trackedDiffSha256=c.EMPTY,sourceDirectoryWithinRepository='.')
        self.provenance=dict(schemaVersion=1,source=source,tool=copy.deepcopy(source),toolSourceSha256=self.tool,discoveredProjects=['src/A.csproj'])

    def write(self):
        for name,data in self.data.items():
            (self.root/name).write_bytes(b''.join(c.canonical(x)+b'\n' for x in data) if name.endswith('.jsonl') else c.canonical(data))
        self.provenance['outputsSha256']={name:hashlib.sha256((self.root/name).read_bytes()).hexdigest() for name in self.data}
        (self.root/'provenance.json').write_bytes(c.canonical(self.provenance))

    def validate(self):
        self.write();return c.validate_inventory(self.root,c.SOURCE,c.TREE,'10.0.102',self.targets,self.tool)

    def test_complete(self):self.assertEqual(len(self.validate()[0]),1)

    def test_profile_source_hash_and_decision_counterexamples(self):
        baseline=copy.deepcopy((self.data,self.provenance))
        mutations=[lambda:self.data['coverage.json'].update(complete=False),lambda:self.data['coverage.json'].update(schemaVersion=True),lambda:self.data['coverage.json'].update(actualTargets=[]),lambda:self.data['coverage.json']['actualTargets'].append(self.data['coverage.json']['actualTargets'][0]),lambda:self.data['coverage.json']['actualTargets'][0].update(sdkVersion='10.0.113'),lambda:self.data['coverage.json'].update(hostOs='win32'),lambda:self.provenance['source'].update(commit='0'*40),lambda:self.provenance['tool'].update(status=' M x'),lambda:self.provenance.update(toolSourceSha256={}),lambda:self.data['decisions.json']['decisions'].clear(),lambda:self.data['decisions.json']['decisions'][0].update(classification='keep-public'),lambda:self.data['coverage.json'].update(pendingDecisionCount=True),lambda:self.data['coverage.json'].update(symbolCountByOrigin={'explicit-source':True}),lambda:self.data['coverage.json'].update(typeCounts=[]),lambda:self.provenance.update(discoveredProjects=[]),lambda:self.data['coverage.json'].update(unionIdentityCountIgnoringTfm=True)]
        for i,mutate in enumerate(mutations):
            self.data,self.provenance=copy.deepcopy(baseline);mutate()
            with self.subTest(i=i),self.assertRaises(ValueError):self.validate()

    def test_hash_manifest_tamper_missing_extra_and_escape(self):
        self.write();(self.root/'symbols.jsonl').write_text('tamper')
        with self.assertRaises(ValueError):c.verify_reports(self.root)
        self.write();(self.root/'extra').write_text('extra')
        with self.assertRaises(ValueError):c.verify_reports(self.root)
        (self.root/'extra').unlink();self.provenance['outputsSha256']={'../outside':'0'*64};(self.root/'provenance.json').write_bytes(c.canonical(self.provenance))
        with self.assertRaises(ValueError):c.verify_reports(self.root)

    def test_synthetic_fixture_schema_and_all_hashes(self):
        required={str(i):{'symbolSha256':'a'*64,'relationSha256':[]} for i in range(3397)}
        fixture={'schemaVersion':1,'projection':'all-symbol-fields-except-declarations-locations+incident-relations-v1',
                 'historical':{'source':c.HISTORICAL_SOURCE,'tree':c.HISTORICAL_TREE,'sdk':'10.0.113'},
                 'targets':[],'toolSourceSha256':{},'required':required,'requiredSha256':c.digest(required),
                 'assemblyRelationSha256':[],'sourceControlFilesSha256':{}}
        c.validate_fixture(fixture)
        for change in [lambda f:f.update(schemaVersion=True),lambda f:f.update(requiredSha256='0'*64),lambda f:f.update(projection='unknown'),lambda f:f.update(extra=True)]:
            n=copy.deepcopy(fixture);change(n)
            with self.assertRaises(ValueError):c.validate_fixture(n)


class WorkflowTests(unittest.TestCase):
    def test_dormant_exact_source_scoped_temporary_report_workflow(self):
        root=Path(__file__).resolve().parents[2]
        text=(root/'.github/workflows/api-semantic-calibration.yml').read_text()
        self.assertIn("paths: ['eng/api-semantic-calibration/request.json']",text)
        # These controls also run after the authorized request-only trigger commit.
        self.assertTrue((root/'eng/api-semantic-calibration/request.example.json').is_file())
        self.assertEqual(text.count('uses:'),4)
        self.assertEqual(text.count('uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1'),3)
        for forbidden in ('actions/cache','dotnet pack','dotnet publish','dotnet test','dotnet run','workflow_dispatch:', 'continue-on-error:', 'secrets.'):self.assertNotIn(forbidden,text)
        self.assertIn('contents: read',text)
        self.assertIn('actions: read',text)
        self.assertIn('--fixture',text)
        self.assertIn('steps.baseline.outcome',text)
        self.assertFalse((root/'eng/api-semantic-calibration/historical-hashes.json').exists())
        self.assertIn('python3 eng/run-api-inventory.py --output "$INVENTORY"',text)
        self.assertIn('fetch-depth: 2',text)
        self.assertIn('Recheck immutable source',text)
        self.assertIn('          set -C\n',text)
        self.assertNotIn('$RUNNER_TEMP/api-inventory-driver.log',text)
        self.assertIn('> "$RUNNER_TEMP/api-calibration-${{ github.run_id }}-${{ github.run_attempt }}/inventory-driver.log" 2>&1',text)
        upload=text.split('      - name: Preserve current inventory and review diagnostics only')[1]
        self.assertIn('if: always()',upload)
        self.assertIn('retention-days: 3',upload)
        self.assertIn('include-hidden-files: false',upload)
        self.assertEqual(upload.count('${{ runner.temp }}'),3)
        self.assertIn('${{ runner.temp }}/api-calibration-${{ github.run_id }}-${{ github.run_attempt }}/inventory-driver.log',upload)
        for forbidden in ('expected.json','historical.zip','api-baseline-', 'candidate/', 'tools/', 'GH_READ_TOKEN', '**'):
            self.assertNotIn(forbidden,upload)
        self.assertIn('uses: actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9',upload)


if __name__=='__main__':unittest.main()
