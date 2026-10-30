"""Synthetic Python-only controls. Mock API receipts are NOT official NuGet execution evidence."""
import base64, copy, hashlib, json, os, tempfile, unittest, zipfile
from pathlib import Path
from unittest.mock import patch
import package_evidence as p

class PackageControls(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name); self.source = self.root / 'source'; self.source.mkdir()
        self.out = self.root / 'out'; self.out.mkdir(); self.tools = self.root / 'tools'; self.tools.mkdir()
        self.sdk = self.root / 'dotnet/sdk/10.0.114'; self.sdk.mkdir(parents=True)
        (self.sdk.parent.parent / 'dotnet').write_bytes(b'synthetic not executable')
        for name in p.SDK_ASSEMBLIES: (self.sdk / (name + '.dll')).write_bytes(('synthetic ' + name).encode())
        self.bin = self.root / 'helper-bin'; self.bin.mkdir(); self.helper = self.bin / 'NuGetEvidence.dll'
        for name in ('NuGetEvidence.dll', 'NuGetEvidence.deps.json', 'NuGetEvidence.runtimeconfig.json'): (self.bin / name).write_bytes(('synthetic ' + name).encode())
        self.safe = {'completed': True, 'processGroupQuiescent': True, 'exitCode': 0}
        self.manifest = {'status': 'HELPER_BUILT', 'helperDll': str(self.helper), 'process': self.safe,
                         'sdk': p.sdk_identity(self.sdk), 'files': {f.name: p.sha(f) for f in self.bin.iterdir()}}
        (self.bin / 'helper-build.json').write_text(json.dumps(self.manifest))
        self.cache = self.root / 'packages'; self.pkg = self.cache / p.PACKAGE_PATH; self.pkg.mkdir(parents=True)
        self.nupkg = self.pkg / p.NUPKG
        self.member = b'namespace TUnit.Generated;'
        self.props = b'<Project><ItemGroup><Compile Include="$(MSBuildThisFileDirectory)TUnit.Core.GeneratedNamespace.cs" Visible="false" /></ItemGroup></Project>'
        with zipfile.ZipFile(self.nupkg, 'w') as z:
            z.writestr(p.MEMBER, self.member); z.writestr(p.PROPS, self.props)
            z.writestr('.signature.p7s', b'SYNTHETIC_NOT_A_REAL_SIGNATURE')
        for name, data in ((p.MEMBER, self.member), (p.PROPS, self.props)):
            f = self.pkg / name; f.parent.mkdir(parents=True, exist_ok=True); f.write_bytes(data)
        # Deliberately distinct from raw ZIP hash; this value is only a mock official API output.
        self.content = base64.b64encode(b'c' * 64).decode()
        self.projects = [{'path': name, 'scope': 'direct'} for name in sorted(p.EXPECTED_PROJECTS)]
        self.projects += [{'path': 'src/SharpLink.Generator/SharpLink.Generator.csproj', 'scope': 'direct'}]
        self.projects += [{'path': 'src/P%d/P%d.csproj' % (i, i), 'scope': 'direct'} for i in range(61)]
        (self.tools / 'coverage-ledger.json').write_text(json.dumps({'projects': self.projects}))
        self.assets = []
        for project in self.projects:
            directory = (self.source / project['path']).parent
            path = directory / 'obj/project.assets.json'; path.parent.mkdir(parents=True)
            doc = {'project': {'restore': {'projectPath': str(self.source / project['path']), 'outputPath': str(directory / 'obj') + '/', 'originalTargetFrameworks': ['net10.0']}, 'frameworks': {'net10.0': {}}}, 'libraries': {}}
            if project['path'] == 'src/SharpLink.Generator/SharpLink.Generator.csproj':
                doc['project']['restore']['originalTargetFrameworks'] = ['netstandard2.0']; doc['project']['frameworks'] = {'netstandard2.0': {}}
            if project['path'] in p.EXPECTED_PROJECTS:
                doc.update(libraries={p.PACKAGE_ID: {'type': 'package', 'path': p.PACKAGE_PATH, 'sha512': self.content, 'files': [p.MEMBER, p.PROPS]}},
                           targets={'net10.0': {p.PACKAGE_ID: {'build': {p.PROPS: {}}}}}, packageFolders={str(self.cache) + '/': {}})
                self.assets.append(path)
            path.write_text(json.dumps(doc))
        self.calls = []
    def fake_execute(self, argv, cwd, timeout, path, capture=False):
        self.calls.append((argv, cwd, timeout)); report = dict(self.safe); p.host.save(path, report)
        snapshot = Path(argv[-2]); d = p.digest(snapshot)
        official = {'status': 'CONTENT_HASH_OBSERVED_NOT_SIGNATURE_VERIFICATION', 'signatureTrust': 'NOT_VERIFIED',
                    'method': p.METHOD, 'sdkDirectory': str(self.sdk), 'packagePath': str(snapshot),
                    'rawSha256': d['rawSha256'], 'rawSha512': d['rawSha512'], 'packageBytes': d['bytes'],
                    'contentHashAlgorithm': 'SHA512', 'contentHash': self.content,
                    'helper': {'path': str(self.helper), 'sha256': p.sha(self.helper)},
                    'methodModuleVersionId': 'synthetic-mvid', 'methodMetadataToken': 123,
                    'loadedAssemblies': [dict(name='NuGet.Packaging', assemblyVersion='7.0.3.0', informationalVersion='synthetic',
                                             moduleVersionId='synthetic-mvid', **self.manifest['sdk']['assemblies']['NuGet.Packaging'])]}
        p.host.save(Path(argv[-1]), official)
        return report
    def collect(self, invoke=None):
        with patch.object(p, 'ROOT', self.tools), patch.object(p.host, 'execute', side_effect=invoke or self.fake_execute):
            return p.collect(self.source, self.out, self.sdk, self.helper)
    def mutate_asset(self, fn, index=0):
        path = self.assets[index]; doc = json.loads(path.read_text()); fn(doc); path.write_text(json.dumps(doc))
    def test_generator_netstandard_without_tunit_is_valid_and_identity_retained(self):
        project = next(x for x in self.projects if x['path'] == 'src/SharpLink.Generator/SharpLink.Generator.csproj')
        row = p.assets_observation(self.source, project)
        self.assertEqual(row['expectedTargetFramework'], 'netstandard2.0'); self.assertEqual(row['resolvedTUnitIds'], []); self.assertFalse(row['problems']); self.assertTrue(row['assetsSha256'])
    def test_tunit_wrong_target_framework_and_generator_wrong_identity_rejected(self):
        project = self.projects[0]; path = self.source / Path(project['path']).parent / 'obj/project.assets.json'
        doc = json.loads(path.read_text()); doc['project']['restore']['originalTargetFrameworks'] = ['netstandard2.0']; doc['project']['frameworks'] = {'netstandard2.0': {}}; path.write_text(json.dumps(doc))
        self.assertTrue(p.assets_observation(self.source, project)['problems'])
        generator = next(x for x in self.projects if x['path'] == 'src/SharpLink.Generator/SharpLink.Generator.csproj'); path = self.source / Path(generator['path']).parent / 'obj/project.assets.json'
        doc = json.loads(path.read_text()); doc['project']['restore']['projectPath'] = str(self.source / 'wrong.csproj'); path.write_text(json.dumps(doc))
        self.assertTrue(p.assets_observation(self.source, generator)['problems'])
    def test_official_content_distinct_from_raw_and_called_once_for_five_refs(self):
        result = self.collect(); self.assertFalse(result['problems']); self.assertEqual(len(self.calls), 1)
        self.assertEqual(len(result['references']), 5); self.assertEqual(self.calls[0][2], 60)
        binding = next(iter(result['externalPackageSources'].values()))
        self.assertNotEqual(binding['nupkgContentHash'], binding['nupkgRawSha512'])
        self.assertEqual(binding['signatureTrust'], 'NOT_VERIFIED')
        self.assertEqual(len(list((self.out / 'package-evidence').glob('*.nupkg'))), 1)
    def test_final_manual_archive_consumes_collector_binding_without_second_api_call(self):
        import manual as manual
        result = self.collect(); self.assertFalse(result['problems'])
        source_file = self.source / 'src/P0/A.cs'; source_file.write_bytes(b'class A {}')
        workspace = {'src/P0/A.cs': {'kind': 'tracked', 'sha256': p.sha(source_file)}}
        build = {'workspaceCSharp': workspace, 'projects': [dict(project=x['path'], scope=x['scope'], production=False, status='PROJECT_OUTPUT_OBSERVED') for x in self.projects],
                 'evidenceStatus': 'BUILD_OUTPUTS_OBSERVED_NOT_SCAN_COMPLETION'}
        (self.out / 'manual-build.json').write_text(json.dumps(build))
        db = self.root / 'database'; (db / 'csharp').mkdir(parents=True)
        with zipfile.ZipFile(db / 'csharp/src.zip', 'w') as archive:
            archive.writestr(str(source_file).lstrip('/'), source_file.read_bytes())
            archive.writestr(str(self.pkg / p.MEMBER).lstrip('/'), self.member)
        ledger = {'projects': self.projects, 'trackedCSharp': [{'path': 'src/P0/A.cs'}], 'counts': {}, 'oldErrorsOutsideCandidates': []}
        with patch.object(manual, 'LEDGER', ledger), patch.object(manual, 'ROOT', self.tools), patch.object(p.host, 'execute') as execute:
            receipt = manual.archive(self.source, db, self.out)
        execute.assert_not_called(); self.assertIn(p.KEY, receipt['csharpFiles'])
        self.assertEqual(receipt['csharpFiles'][p.KEY]['nupkgContentHash'], self.content)
    def test_first_mismatch_preserves_all_five_expected_actual_and_bytes(self):
        self.mutate_asset(lambda d: d['libraries'][p.PACKAGE_ID].update(sha512=base64.b64encode(b'x' * 64).decode()))
        result = self.collect(); self.assertTrue(result['problems']); self.assertEqual(len(result['references']), 5)
        self.assertEqual(result['packages'][0]['contentHash'], self.content)
        self.assertTrue((self.out / 'package-evidence' / p.NUPKG).is_file())
        self.assertTrue(all('expectedHashRaw' in r for r in result['references']))
        self.assertFalse(result['externalPackageSources'])
        self.assertTrue(result['observationCompleted']); self.assertTrue(result['safeToContinue'])
        self.assertEqual(result['status'], 'INCOMPLETE'); self.assertTrue(result['comparisonProblems'])
        with patch.dict(os.environ, {}, clear=True): self.assertEqual(p.observation_exit_code(result), 0)
        import manual
        with self.assertRaises(ValueError): manual.package_sources(self.source, self.projects[0], result)
    def test_timeout_or_unsafe_process_keeps_partial_and_never_accepts(self):
        def fail(argv, cwd, timeout, path):
            self.fake_execute(argv, cwd, timeout, path)
            return dict(self.safe, timedOut=True)
        result = self.collect(fail); self.assertTrue(result['problems']); self.assertTrue(result['packages'][0]['official'])
        self.assertEqual(result['status'], 'INCOMPLETE'); self.assertFalse(result['externalPackageSources'])
        self.assertFalse(result['observationCompleted']); self.assertFalse(result['safeToContinue'])
    def test_helper_api_failure_keeps_raw_package_and_five_refs(self):
        def fail(argv, cwd, timeout, path):
            p.host.save(Path(argv[-1]), {'status': 'INCOMPLETE', 'error': 'synthetic official API failure'})
            return dict(self.safe, exitCode=1)
        result = self.collect(fail); self.assertTrue(result['packages'][0]['rawSha512']); self.assertEqual(len(result['references']), 5)
        self.assertTrue((self.out / 'package-evidence/TUnit.Core.props').is_file())
        self.assertFalse(result['observationCompleted']); self.assertFalse(result['safeToContinue'])
    def test_only_hash_comparison_mismatch_can_allow_continuation(self):
        self.mutate_asset(lambda d: d['libraries'][p.PACKAGE_ID].update(sha512=base64.b64encode(b'x' * 64).decode()))
        (self.pkg / p.MEMBER).write_bytes(b'also-invalid-member')
        result = self.collect(); self.assertTrue(result['comparisonProblems']); self.assertTrue(result['problems'])
        self.assertFalse(result['observationCompleted']); self.assertFalse(result['safeToContinue'])
        with patch.dict(os.environ, {}, clear=True): self.assertEqual(p.observation_exit_code(result), 1)
    def test_all_unsafe_or_unknown_process_states_block_even_with_hash_values(self):
        variants = [dict(timedOut=True), dict(descendantLeak=True), dict(cancelled='signal 15'),
                    dict(cleanupTimedOut=True), dict(cleanupError='unknown'), dict(error='spawn failure'),
                    dict(processGroupQuiescent=False), dict(completed=False), dict(exitCode=1), dict(exitCode=None)]
        for index, flags in enumerate(variants):
            with self.subTest(flags=flags):
                self.out = self.root / ('unsafe-out-' + str(index)); self.out.mkdir()
                def bad(argv, cwd, timeout, path):
                    self.fake_execute(argv, cwd, timeout, path)
                    return dict(self.safe, **flags)
                result = self.collect(bad)
                self.assertFalse(result['observationCompleted']); self.assertFalse(result['safeToContinue'])
                self.assertTrue(result['packages'][0]['official']); self.assertTrue(result['problems'])
    def test_unexpected_exception_and_baseexception_do_not_grant_permission(self):
        with patch.object(p, 'validate_helper', side_effect=RuntimeError('unhandled adapter condition')):
            result = self.collect()
        self.assertFalse(result['observationCompleted']); self.assertFalse(result['safeToContinue'])
        self.out = self.root / 'cancelled-out'; self.out.mkdir()
        with patch.object(p, 'validate_helper', side_effect=KeyboardInterrupt('cancelled outside process host')):
            with self.assertRaises(KeyboardInterrupt): self.collect()
        partial = json.loads((self.out / 'package-evidence.json').read_text())
        self.assertFalse(partial['observationCompleted']); self.assertFalse(partial['safeToContinue'])
    def test_continuation_flags_only_persist_after_mandatory_finally_save(self):
        saves = []; original_save = p.host.save
        def observed_save(path, doc):
            if path.name == 'package-evidence.json': saves.append(copy.deepcopy(doc))
            original_save(path, doc)
        with patch.object(p.host, 'save', side_effect=observed_save): result = self.collect()
        self.assertTrue(result['safeToContinue']); self.assertTrue(saves[-1]['safeToContinue'])
        self.assertTrue(all(not row['safeToContinue'] and not row['observationCompleted'] for row in saves[:-1]))
    def test_finally_save_failure_never_emits_safe_output(self):
        self.mutate_asset(lambda d: d['libraries'][p.PACKAGE_ID].update(sha512=base64.b64encode(b'x' * 64).decode()))
        original_save = p.host.save
        def fail_save(path, doc):
            if path.name == 'package-evidence.json' and doc.get('comparisonProblems'):
                self.assertFalse(doc['safeToContinue']); self.assertFalse(doc['observationCompleted'])
                raise OSError('synthetic final persistence failure')
            original_save(path, doc)
        output = self.root / 'github-output'; output.write_text('')
        with patch.dict(os.environ, GITHUB_OUTPUT=str(output)), patch.object(p.host, 'save', side_effect=fail_save):
            with self.assertRaises(OSError): self.collect()
        self.assertEqual(output.read_text(), '')
        partial = json.loads((self.out / 'package-evidence.json').read_text())
        self.assertFalse(partial['safeToContinue']); self.assertFalse(partial['observationCompleted'])
    def test_workflow_output_is_observation_permission_not_validation_success(self):
        self.mutate_asset(lambda d: d['libraries'][p.PACKAGE_ID].update(sha512=base64.b64encode(b'x' * 64).decode()))
        result = self.collect(); output = self.root / 'github-output'
        with patch.dict(os.environ, GITHUB_OUTPUT=str(output)):
            self.assertEqual(p.observation_exit_code(result), 0)
            self.assertEqual(p.observation_exit_code(dict(result, safeToContinue=False)), 1)
        self.assertEqual(output.read_text(), 'safe=true\nsafe=false\n')
        self.assertEqual(result['status'], 'INCOMPLETE'); self.assertTrue(result['problems'])
    def test_sdk_or_helper_changed_fail_before_execute_but_after_preservation(self):
        (self.sdk / 'NuGet.Packaging.dll').write_bytes(b'changed')
        result = self.collect(); self.assertFalse(self.calls); self.assertTrue(result['problems'])
        self.assertTrue((self.out / 'package-evidence' / p.NUPKG).is_file())
    def test_untrusted_loaded_assembly_or_method_mvid_rejected(self):
        def bad(argv, cwd, timeout, path):
            result = self.fake_execute(argv, cwd, timeout, path); file = Path(argv[-1]); doc = json.loads(file.read_text())
            doc['loadedAssemblies'][0]['sha256'] = '0' * 64; file.write_text(json.dumps(doc)); return result
        self.assertTrue(self.collect(bad)['problems'])
    def test_raw_package_receipt_mismatch_rejected(self):
        def bad(argv, cwd, timeout, path):
            result = self.fake_execute(argv, cwd, timeout, path); file = Path(argv[-1]); doc = json.loads(file.read_text())
            doc['rawSha512'] = self.content; file.write_text(json.dumps(doc)); return result
        self.assertTrue(self.collect(bad)['problems'])
    def test_package_mutation_during_helper_rejected(self):
        def bad(argv, cwd, timeout, path):
            result = self.fake_execute(argv, cwd, timeout, path); self.nupkg.write_bytes(b'changed'); return result
        self.assertTrue(self.collect(bad)['problems'])
    def test_assets_mutation_during_helper_rejected(self):
        def bad(argv, cwd, timeout, path):
            result = self.fake_execute(argv, cwd, timeout, path)
            self.assets[0].write_text(self.assets[0].read_text() + ' ')
            return result
        self.assertTrue(self.collect(bad)['problems'])
    def test_materialized_mutation_during_helper_rejected(self):
        def bad(argv, cwd, timeout, path):
            result = self.fake_execute(argv, cwd, timeout, path)
            (self.pkg / p.PROPS).write_bytes(b'changed during observation')
            return result
        self.assertTrue(self.collect(bad)['problems'])
    def test_missing_or_wrong_module_binding_rejected(self):
        def bad(argv, cwd, timeout, path):
            result = self.fake_execute(argv, cwd, timeout, path); file = Path(argv[-1]); doc = json.loads(file.read_text())
            doc['methodModuleVersionId'] = 'different-module'; file.write_text(json.dumps(doc)); return result
        self.assertTrue(self.collect(bad)['problems'])
    def test_missing_required_official_receipt_rejected(self):
        def bad(argv, cwd, timeout, path): return dict(self.safe)
        result = self.collect(bad); self.assertTrue(result['problems']); self.assertFalse(result['externalPackageSources'])
    def test_props_source_materialized_tamper_preserved_but_rejected(self):
        (self.pkg / p.MEMBER).write_bytes(b'changed')
        result = self.collect(); self.assertTrue(result['problems']); self.assertEqual((self.out / 'package-evidence/TUnit.Core.GeneratedNamespace.cs').read_bytes(), b'changed')
    def test_assets_credential_like_fields_not_exported(self):
        self.mutate_asset(lambda d: d.update(secret='DO_NOT_EXPORT', logs=['DO_NOT_EXPORT'], unrelated={'password': 'DO_NOT_EXPORT'}))
        result = self.collect(); self.assertFalse(result['problems']); self.assertNotIn('DO_NOT_EXPORT', json.dumps(result))
    def test_wrong_member_selection_rejected_with_partial_expected(self):
        self.mutate_asset(lambda d: d.update(targets={'net10.0': {p.PACKAGE_ID: {'build': {'other.props': {}}}}}))
        result = self.collect(); self.assertTrue(result['problems']); self.assertEqual(len(result['references']), 5)
    def test_missing_or_ambiguous_nupkg_not_exempted(self):
        self.nupkg.unlink(); result = self.collect(); self.assertTrue(result['problems']); self.assertFalse(self.calls)
    def test_extra_version_and_conflicting_content_hash_rejected(self):
        self.mutate_asset(lambda d: d['libraries'][p.PACKAGE_ID].update(contentHash=base64.b64encode(b'x' * 64).decode()))
        self.assertTrue(self.collect()['problems'])
    def test_unsafe_zip_and_duplicate_members_rejected(self):
        with zipfile.ZipFile(self.nupkg, 'a') as z: z.writestr('../outside', b'x')
        result = self.collect(); self.assertTrue(result['problems']); self.assertTrue(result['packages'][0]['contentHash'])
    def test_assets_wrong_project_identity_is_partial_failure(self):
        self.mutate_asset(lambda d: d['project']['restore'].update(projectPath='/outside/P.csproj'))
        result = self.collect(); self.assertTrue(result['problems']); self.assertEqual(len(result['references']), 5)
    def test_missing_helper_receipt_retains_package_and_no_call(self):
        (self.bin / 'helper-build.json').unlink(); result = self.collect(); self.assertTrue(result['problems']); self.assertFalse(self.calls)
        self.assertTrue(result['packages'][0]['rawSha256'])
    def test_symlinked_materialized_file_not_followed_or_exported(self):
        target = self.pkg / p.MEMBER; target.unlink(); target.symlink_to(self.pkg / p.PROPS)
        result = self.collect(); self.assertTrue(result['problems']); self.assertFalse((self.out / 'package-evidence/TUnit.Core.GeneratedNamespace.cs').exists())
    def test_no_overwrite_or_retry(self):
        self.collect()
        with self.assertRaises(ValueError): self.collect()
    def test_export_directory_cannot_be_under_product(self):
        inner = self.source / 'evidence'; inner.mkdir()
        with patch.object(p.host, 'execute') as execute:
            result = p.collect(self.source, inner, self.sdk, self.helper)
        execute.assert_not_called(); self.assertTrue(result['problems'])
    def test_build_helper_fixed_budget_no_external_packages_and_partial_failure(self):
        inputs = self.tools / 'NuGetEvidence'; inputs.mkdir()
        for name in ('Program.cs', 'NuGetEvidence.csproj'): (inputs / name).write_text('synthetic source')
        stage, output = self.root / 'build-src', self.root / 'build-bin'
        def fail(argv, cwd, timeout, path):
            self.assertEqual(timeout, 120); self.assertIn('--disable-build-servers', argv)
            self.assertIn('-nodeReuse:false', argv); self.assertIn('-p:UseSharedCompilation=false', argv)
            self.assertEqual(json.loads((cwd / 'global.json').read_text())['sdk']['rollForward'], 'disable')
            self.assertIn('<clear />', (cwd / 'NuGet.Config').read_text()); return dict(self.safe, exitCode=1)
        with patch.object(p, 'ROOT', self.tools), patch.object(p.host, 'execute', side_effect=fail):
            with self.assertRaises(ValueError): p.build_helper(stage, output, self.sdk)
        self.assertEqual(json.loads((output / 'helper-build.json').read_text())['status'], 'INCOMPLETE')
    def test_helper_source_contains_no_package_reference_or_product_compile(self):
        root = Path(p.__file__).parent / 'NuGetEvidence'
        project = (root / 'NuGetEvidence.csproj').read_text(); code = (root / 'Program.cs').read_text()
        self.assertNotIn('PackageReference', project); self.assertIn('CancellationToken.None, null', code)
        self.assertIn('GetContentHash', code); self.assertIn('NOT_VERIFIED', code)
    def test_helper_build_rejects_staged_source_or_generated_template_mutation(self):
        inputs = self.tools / 'NuGetEvidence'; inputs.mkdir()
        for name in ('Program.cs', 'NuGetEvidence.csproj'): (inputs / name).write_text('synthetic reviewed source')
        for index, name in enumerate(('Program.cs', 'NuGetEvidence.csproj', 'global.json', 'NuGet.Config')):
            with self.subTest(name=name):
                stage, output = self.root / ('mutated-src-' + str(index)), self.root / ('mutated-bin-' + str(index))
                def mutate(argv, cwd, timeout, path):
                    (cwd / name).write_bytes(b'changed during build')
                    return dict(self.safe)
                with patch.object(p, 'ROOT', self.tools), patch.object(p.host, 'execute', side_effect=mutate):
                    with self.assertRaisesRegex(ValueError, 'inputs changed during build'): p.build_helper(stage, output, self.sdk)
                result = json.loads((output / 'helper-build.json').read_text())
                self.assertEqual(result['status'], 'INCOMPLETE')
                self.assertEqual(result['stageInputHashesBefore'], result['expectedStageInputHashes'])
                self.assertNotEqual(result['stageInputHashesAfter'][name], result['expectedStageInputHashes'][name])
    def test_helper_build_rejects_copy_not_equal_to_reviewed_source_before_execution(self):
        inputs = self.tools / 'NuGetEvidence'; inputs.mkdir()
        for name in ('Program.cs', 'NuGetEvidence.csproj'): (inputs / name).write_text('synthetic reviewed source')
        stage, output = self.root / 'bad-copy-src', self.root / 'bad-copy-bin'
        def bad_copy(source, target): Path(target).write_bytes(b'wrong copied bytes')
        with patch.object(p, 'ROOT', self.tools), patch.object(p.shutil, 'copyfile', side_effect=bad_copy), patch.object(p.host, 'execute') as execute:
            with self.assertRaisesRegex(ValueError, 'before build'): p.build_helper(stage, output, self.sdk)
        execute.assert_not_called()

if __name__ == '__main__': unittest.main()
