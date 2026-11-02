"""Synthetic cache/schema/path controls. No SDK, network, real package, or GC claims."""
import base64
import hashlib
import json
from pathlib import Path
import tempfile
from zipfile import ZipFile
import audit
import run as runner
import classify as c
from test_classify import specimen, SOURCE
import xml.etree.ElementTree as ET


def diagnostic_controls():
    import os
    from types import SimpleNamespace
    with tempfile.TemporaryDirectory(prefix='sl971-origin-synthetic-') as temporary:
        base=Path(temporary); source=base/'source'; packages=base/'cache'; sdk=base/'sdk'
        for folder in (source,packages,sdk): folder.mkdir()
        manifest=sdk/'sdk-manifests/10.0.100/example/WorkloadManifest.targets'
        manifest.parent.mkdir(parents=True);manifest.write_text('<Project/>')
        delimiter='='*140
        def root_header(path): return '\n'+delimiter+'\n'+str(path)+'\n'+delimiter+'\n'
        detail=audit.origin_detail(manifest,root_header(manifest),source,packages,sdk,{})
        assert detail['category']=='sdk-container-unsealed' and detail['headerRole']=='root'
        assert detail['relativePath']=='sdk-manifests/10.0.100/example/WorkloadManifest.targets'
        assert detail['regular'] is True and detail['fileSHA256']==audit.digest(manifest)
        assert str(base) not in json.dumps(detail)
        assert audit.header_role('\n'+delimiter+'\n  <Import Project="x">\n\n'+str(manifest)+'\n'+delimiter+'\n',manifest)=='import-open'
        assert audit.header_role('\n'+delimiter+'\n  </Import>\n\n'+str(manifest)+'\n'+delimiter+'\n',manifest)=='import-close'
        assert audit.header_role(str(manifest),manifest)=='other-comment'
        pp=base/'pp.xml';pp.write_text('<Project><!--'+root_header(manifest)+'--></Project>')
        try:audit.expanded(pp,source,packages,{},diagnostic_sdk_root=sdk,source_pins={},cache_pins={})
        except ValueError as failure:
            assert str(failure)=='Import origin missing/ambiguous in exact sealed inputs'
            rows=runner.failure_leaves([failure],'preprocess-baseline-0')
            assert rows[0]['importOrigins']['rejectedCount']==1
            assert rows[0]['importOrigins']['rejected'][0]==detail
        else:raise AssertionError('Diagnostic must not accept unsealed manifest')
        # Unknown path and normalized escape do not invoke open/stat/content read.
        saved_open=audit.os.open; saved_stat=audit.os.stat
        def forbidden(*args,**kwargs):raise AssertionError('Unknown external filesystem access')
        audit.os.open=forbidden;audit.os.stat=forbidden
        try:
            outside=base/'SECRET_OUTSIDE.targets'
            unknown=audit.origin_detail(outside,str(outside),source,packages,sdk,{})
            escape=audit.origin_detail(sdk/'../SECRET_OUTSIDE.targets','',source,packages,sdk,{})
            assert unknown['category']==escape['category']=='outside-known-roots'
            assert unknown['exists'] is None and 'relativePath' not in unknown and 'fileSHA256' not in unknown
            assert 'SECRET' not in json.dumps([unknown,escape])
            assert escape['normalizedSpellingDiffers'] is True
        finally:audit.os.open=saved_open;audit.os.stat=saved_stat
        alias=manifest.parent/'child/../WorkloadManifest.targets'
        alias_row=audit.origin_detail(alias,'',source,packages,sdk,{str(manifest):(audit.digest(manifest),0)})
        assert alias_row['normalizedExactSealedMember'] and alias_row['normalizedSpellingDiffers']
        diag=audit.import_diagnostics([(alias,'')],source,packages,sdk,{str(manifest):(audit.digest(manifest),0)})
        assert diag.rejected_count==1  # Normalization does not widen acceptance.
        linked=sdk/'link';linked.symlink_to(base/'SECRET_OUTSIDE')
        row=audit.origin_detail(linked/'outside.targets','',source,packages,sdk,{})
        assert row['symlink'] and not row['regular'] and 'fileSHA256' not in row
        fifo=sdk/'pipe.targets';os.mkfifo(fifo)
        row=audit.origin_detail(fifo,'',source,packages,sdk,{})
        assert row['regular'] is False and 'fileSHA256' not in row
        missing=sdk/'missing.targets'
        assert audit.origin_detail(missing,'',source,packages,sdk,{})['exists'] is False
        diagnostics=audit.import_diagnostics([(manifest,'')]*20,source,packages,sdk,{})
        assert diagnostics.candidate_count==20 and diagnostics.rejected_count==20 and len(diagnostics.rejected)==16 and diagnostics.public()['truncated']
        # Formatter failure preserves original rejection as first leaf/fullgraph.
        saved=audit.import_diagnostics
        def broken(*args,**kwargs):raise RuntimeError('SECRET_DIAGNOSTIC')
        audit.import_diagnostics=broken
        try:
            try:audit.expanded(pp,source,packages,{},diagnostic_sdk_root=sdk,source_pins={},cache_pins={})
            except ExceptionGroup as group:
                assert type(group.exceptions[0]) is ValueError and type(group.exceptions[1]) is RuntimeError
                assert str(group.exceptions[0])=='Import origin missing/ambiguous in exact sealed inputs'
                assert 'SECRET' not in json.dumps(runner.failure_leaves([group],'preprocess-baseline-0'))
            else:raise AssertionError('Diagnostic failure must not mask or accept body failure')
        finally:audit.import_diagnostics=saved
    return ['known-root-safe-hash', 'header-role-only', 'unsealed-still-rejected',
            'unknown-no-filesystem-access', 'escape-no-filesystem-access', 'alias-still-rejected',
            'symlink-no-follow', 'fifo-no-read', 'missing-file', 'bounded-16-total-count',
            'diagnostic-error-body-first']



def closure_controls():
    import os
    from types import SimpleNamespace
    passed=[]
    def reject(label,action):
        try:action()
        except (ValueError,OSError):passed.append(label)
        else:raise AssertionError('Unexpected acceptance: '+label)
    with tempfile.TemporaryDirectory(prefix='sl971-closure-synthetic-') as temporary:
        b=Path(temporary); sdk=b/'dotnet';source=b/'main';cache=b/'cache'
        for folder in (sdk,source,cache):folder.mkdir()
        def write(path,text='test'):
            path.parent.mkdir(parents=True,exist_ok=True);path.write_text(text);return path
        selected='10.0.114';band='10.0.100'
        write(sdk/'sdk'/selected/'KnownWorkloadManifests.txt','example\nfallback\n')
        current=write(sdk/'sdk-manifests'/band/'example/1.0.0/WorkloadManifest.targets')
        write(current.with_suffix('.json'),'{}')
        older=write(sdk/'sdk-manifests/9.0.100/fallback/2.0.0/WorkloadManifest.targets')
        write(older.with_suffix('.json'),'{}')
        referenced=write(sdk/'sdk-manifests/9.0.200/cross/3.0.0/WorkloadManifest.targets')
        write(referenced.with_suffix('.json'),'{}')
        write(sdk/'sdk-manifests/8.0.100/workloadsets/10.0.114/baseline.workloadset.json',json.dumps({'cross':'3.0.0/9.0.200'}))
        unrelated=write(sdk/'sdk-manifests/9.0.100/unrelated/8.0.0/WorkloadManifest.targets')
        future=write(sdk/'sdk-manifests/11.0.100/example/1.0.0/WorkloadManifest.targets')
        global_text='{\n  "sdk": {\n    "version": "10.0.102",\n    "rollForward": "latestPatch",\n    "allowPrerelease": false\n  },\n  "test": {\n    "runner": "Microsoft.Testing.Platform"\n  }\n}\n'
        write(source/'global.json',global_text)
        cli=b/'cli';(cli/'.dotnet').mkdir(parents=True)
        lane=SimpleNamespace(env={'baseline':{'DOTNET_CLI_HOME':str(cli)}},roots={'baseline':source},sdk_pins={})
        receipt=runner.workload_inputs(lane,sdk,selected)
        assert receipt['featureBand']==band
        assert all(str(p) in lane.sdk_pins for p in (current,older,referenced))
        assert str(unrelated) not in lane.sdk_pins and str(future) not in lane.sdk_pins
        passed.append('current-fallback-workloadset-exact-closure-not-entire-sdk')
        def guard():
            runner.equal_snapshot(lane.workload_pins)
            for root,snapshot in lane.workload_parents.items():assert runner.selection_members(root)==snapshot
            for (base,path),snapshot in lane.workload_paths.items():assert runner.path_state(base,path)==snapshot
        guard()
        higher=current.parent.parent/'99.0.0';higher.mkdir()
        try:guard()
        except AssertionError:passed.append('higher-candidate-detected')
        else:raise AssertionError('New manifest candidate ignored')
        higher.rmdir()
        absent=cli/'.dotnet/sdk-manifests';absent.mkdir()
        try:guard()
        except AssertionError:passed.append('user-override-creation-detected')
        else:raise AssertionError('New user root ignored')
        absent.rmdir()
        original=current.read_bytes();current.write_text('tampered')
        reject('manifest-bytes-change-detected',guard);current.write_bytes(original)
        unknown=sdk/'sdk-manifests'/band/'example/1.0.0/unknown.targets'
        pins=dict(lane.sdk_pins)
        project=write(source/'Microsoft.NET.Sdk.props','<Project/>');pins[str(project)]=(audit.digest(project),0)
        containers=(source,cache,sdk)
        assert audit.canonical_origin(current,containers,pins)==current
        sibling=current.parent/'sub';sibling.mkdir()
        alias=sibling/'../WorkloadManifest.targets'
        assert audit.canonical_origin(alias,containers,pins)==current
        passed.append('canonical-normal-spelling-exact-file')
        reject('unsealed-file-not-prefix-allowed',lambda:audit.canonical_origin(unknown,containers,pins))
        reject('escaping-source-to-sdk-rejected',lambda:audit.canonical_origin(source/'../dotnet'/current.relative_to(sdk),containers,pins))
        reject('missing-cancelled-parent-rejected',lambda:audit.canonical_origin(current.parent/'missing/../WorkloadManifest.targets',containers,pins))
        link=current.parent/'link';link.symlink_to(sibling,target_is_directory=True)
        reject('symlink-even-cancelled-parent-rejected',lambda:audit.canonical_origin(link/'../WorkloadManifest.targets',containers,pins))
        fifo=current.parent/'fifo.targets';os.mkfifo(fifo);pins[str(fifo)]=('0'*64,0)
        reject('special-origin-rejected',lambda:audit.canonical_origin(fifo,containers,pins));pins.pop(str(fifo))
        encoded=write(source/'double--dash.props','<Project/>');pins[str(encoded)]=(audit.digest(encoded),0)
        assert audit.canonical_origin(source/'double__dash.props',containers,pins)==encoded
        collision=write(source/'double__dash.props','<Project/>');pins[str(collision)]=(audit.digest(collision),0)
        reject('encoded-path-collision-rejected',lambda:audit.canonical_origin(collision,containers,pins))
        pins.pop(str(collision));passed.append('official-comment-encoding-unique-identity')
        delimiter='='*140
        def header(path,kind='root'):
            prefix='' if kind=='root' else ('  </Import>\n\n' if kind=='close' else '  <Import Project="x">\n\n')
            return '<!--\n'+delimiter+'\n'+prefix+str(path)+'\n'+delimiter+'\n-->'
        pp=b/'pp.xml';pp.write_text(header(project)+'<Project>'+header(current,'open')+'<!-- /SECRET/EXAMPLE.targets -->'+header(project,'close')+'</Project>')
        results=audit.expanded(pp,source,cache,lane.sdk_pins,diagnostic_sdk_root=sdk,source_pins={str(project):pins[str(project)]},cache_pins={})
        assert set(results)=={str(project),str(current)}
        passed.append('official-root-open-close-ignore-arbitrary-comment')
        current.write_text('changed')
        reject('expanded-exact-hash-mismatch',lambda:audit.expanded(pp,source,cache,lane.sdk_pins,diagnostic_sdk_root=sdk,source_pins={str(project):pins[str(project)]},cache_pins={}))
        current.write_bytes(original)
        lane.env['baseline']['DOTNETSDK_WORKLOAD_MANIFEST_ROOTS']='SECRET'
        reject('resolver-override-env-rejected',lambda:runner.workload_inputs(lane,sdk,selected))
        lane.env['baseline'].pop('DOTNETSDK_WORKLOAD_MANIFEST_ROOTS')
        userlocal=write(sdk/'metadata/workloads'/band/'userlocal','')
        reject('userlocal-selector-rejected',lambda:runner.workload_inputs(lane,sdk,selected));userlocal.unlink()
        link.unlink();fifo.unlink()
        noncanonical=sdk/'sdk-manifests/10.0.100-preview.1';noncanonical.mkdir()
        reject('unsupported-feature-band-not-silently-skipped',lambda:runner.workload_inputs(lane,sdk,selected));noncanonical.rmdir()
        reject('noncanonical-feature-band-rounding',lambda:runner.manifest_feature_band('10.0.114'))
        reject('noncanonical-feature-band-leading-zero',lambda:runner.manifest_feature_band('010.0.100'))
        known=sdk/'sdk'/selected/'KnownWorkloadManifests.txt';original_known=known.read_text()
        known.write_text('..\n')
        reject('known-dotdot-identifier-rejected',lambda:runner.workload_inputs(lane,sdk,selected));known.write_text(original_known)
        assert not runner.manifest_identifier('.') and not runner.manifest_identifier('..')
        spec=sdk/'sdk-manifests/8.0.100/workloadsets/10.0.114/baseline.workloadset.json';original_spec=spec.read_text()
        spec.write_text(json.dumps({'.':'3.0.0/9.0.200'}))
        reject('workloadset-dot-identifier-rejected',lambda:runner.workload_inputs(lane,sdk,selected));spec.write_text(original_spec)
        spec.write_text(json.dumps({'cross':'3.0.0/9.0.214'}))
        reject('explicit-spec-feature-band-noncanonical-rejected',lambda:runner.workload_inputs(lane,sdk,selected));spec.write_text(original_spec)

    return passed



def metadata_controls():
    from types import SimpleNamespace
    import os
    passed=[]
    def reject(label, action, expected=(ValueError,OSError,UnicodeError)):
        try:action()
        except expected:passed.append(label)
        else:raise AssertionError('Unexpected metadata acceptance: '+label)
    # Exact C# raw literal from SDKv10.0.114 provider test; no claim that this
    # sample is the actual failed CI file. Whitespace follows raw-string trim.
    official='{\n"ios": "11.0.2/8.0.100",\n"android": "33.0.2-rc.1/8.0.200",\n"maui": "15.0.1-rc.456/8.0.200-rc.2",\n}\n'
    expected={'ios':'11.0.2/8.0.100','android':'33.0.2-rc.1/8.0.200','maui':'15.0.1-rc.456/8.0.200-rc.2'}
    assert audit.metadata_json(official,'workload-set')==expected
    assert audit.metadata_json(official,'install-state')==expected
    passed.append('official-exact-trailing-comma-literal-accepted')
    reject('original-strict-reader-still-rejects-extension',lambda:json.loads(official))
    text='{"url":"https://example.invalid/a//b", "literal":"/*not comment*/,}", "quote":"\\\"", "slash":"\\\\", /*comment*/ "array":[1,true,null,], //line\n}'
    value=audit.metadata_json(text,'workload-set')
    assert value=={'url':'https://example.invalid/a//b','literal':'/*not comment*/,}','quote':'"','slash':'\\','array':[1,True,None]}
    passed.append('quoted-delimiters-escapes-and-jsonc-preserved')
    reject('install-state-comments-still-rejected',lambda:audit.metadata_json(text,'install-state'))
    assert audit.metadata_json('{"manifests":{},"useWorkloadSets":true,}', 'install-state')=={'manifests':{},'useWorkloadSets':True}
    passed.append('install-state-trailing-comma-only')
    for label,text in [('lone-object-comma','{,}'),('lone-array-comma','[,]'),
                       ('double-comma','{"x":1,,}'),('missing-value','{"x":,}'),
                       ('unterminated-comment','{"x":1,/*'),('unterminated-string','{"x":"missing}'),
                       ('invalid-slash','{"x":1/2}'),('duplicate-key','{"x":1,"x":2,}'),
                       ('nonfinite-number','{"x":NaN,}'),('unknown-token','{"x":bad,}'),
                       ('comment-only','/* nothing */'),('invalid-escape','{"x":"\\q"}')]:
        reject(label,lambda text=text:audit.metadata_json(text,'workload-set'))
    text='/*first*/\n{"é":"value", // comment\n "x": broken,}'
    try:audit.metadata_json(text,'workload-set')
    except json.JSONDecodeError as error:
        assert error.doc==text and error.pos==text.index('broken') and error.lineno==3 and error.colno==7
        passed.append('decoded-text-codepoint-position-preserved')
    else:raise AssertionError('Malformed token accepted')
    with tempfile.TemporaryDirectory(prefix='sl971-metadata-synthetic-') as temporary:
        root=Path(temporary);sdk=root/'sdk';sdk.mkdir();folder=sdk/'sdk-manifests/10.0.100/workloadsets/10.0.114';folder.mkdir(parents=True)
        path=folder/'baseline.workloadset.json';path.write_bytes(b'\xef\xbb\xbf'+official.encode())
        assert audit.read_workload_metadata(path,sdk,'workload-set')==expected
        passed.append('utf8-bom-existing-policy-preserved')
        reject('strict-report-reader-remains-strict',lambda:c.read_json(path))
        text='{"SECRET_TOKEN": /*SECRET_COMMENT*/ broken,}'
        path.write_text(text)
        try:audit.read_workload_metadata(path,sdk,'workload-set')
        except json.JSONDecodeError as original:
            rows=runner.failure_leaves([original],'runtime-inventory')
            public=json.dumps(rows)
            assert 'SECRET' not in public and str(root) not in public
            receipt=rows[0]['workloadMetadata']
            assert receipt['sdkRelativePath']=='sdk-manifests/10.0.100/workloadsets/10.0.114/baseline.workloadset.json'
            assert receipt['position']==text.index('broken') and receipt['fileSHA256']==audit.digest(path)
            assert receipt['coordinateUnit']=='decoded-text-codepoints-after-UTF8-BOM-removal'
            assert 'message' not in rows[0]
            context=SimpleNamespace(work=root,manifest_sha='synthetic',stage='runtime-inventory',results=[],active_scope=None,active_uid=None)
            sent=[];errors=[original]
            def broken_writer(*args,**kwargs):raise RuntimeError('SECRET_CONSOLE')
            assert runner.finish(context,errors,writer=broken_writer,fallback=lambda fd,b:sent.append(b))==1
            assert errors[0] is original and len(errors)>1 and b'SECRET' not in b''.join(sent)
            assert json.loads((root/'RESULT.json').read_text())['status']=='FAILED'
            passed.extend(['safe-known-relative-decode-receipt','console-fault-original-body-preserved'])
            saved_public=audit.MetadataDiagnostics.public
            def broken_public(self):raise RuntimeError('SECRET_METADATA_FORMATTER')
            audit.MetadataDiagnostics.public=broken_public
            try:
                errors=[original];sent=[]
                assert runner.finish(context,errors,writer=lambda *a,**kw:None,fallback=lambda fd,b:sent.append(b))==1
                assert errors[0] is original and len(errors)>1 and b'SECRET' not in b''.join(sent)
                assert json.loads((root/'RESULT.json').read_text())['status']=='FAILED'
                passed.append('metadata-formatter-fault-original-body-preserved')
            finally:audit.MetadataDiagnostics.public=saved_public

        else:raise AssertionError('Malformed actual file accepted')
        saved=audit.metadata_diagnostic
        def broken_diagnostic(*args):raise RuntimeError('SECRET_DIAGNOSTIC')
        audit.metadata_diagnostic=broken_diagnostic
        try:
            try:audit.read_workload_metadata(path,sdk,'workload-set')
            except ExceptionGroup as error:
                assert type(error.exceptions[0]) is json.JSONDecodeError and type(error.exceptions[1]) is RuntimeError
                assert 'SECRET' not in json.dumps(runner.failure_leaves([error],'runtime-inventory'))
                passed.append('diagnostic-fault-original-body-first')
            else:raise AssertionError('Diagnostic failure swallowed original')
        finally:audit.metadata_diagnostic=saved
        path.write_bytes(b'\xff\xfeinvalid')
        try:audit.read_workload_metadata(path,sdk,'workload-set')
        except UnicodeDecodeError as error:
            receipt=runner.failure_leaves([error],'stage')[0]['workloadMetadata']
            assert receipt['line'] is None and receipt['column'] is None and receipt['position'] is None
            passed.append('unknown-encoding-fails-no-fabricated-text-position')
        else:raise AssertionError('Unknown encoding silently decoded')
        path.unlink();path.symlink_to(root/'SECRET_OUTSIDE')
        reject('symlink-metadata-not-followed',lambda:audit.read_workload_metadata(path,sdk,'workload-set'));path.unlink()
        os.mkfifo(path)
        reject('special-metadata-not-read',lambda:audit.read_workload_metadata(path,sdk,'workload-set'));path.unlink()
        reject('outside-sdk-metadata-rejected',lambda:audit.read_workload_metadata(root/'SECRET_OUTSIDE',sdk,'workload-set'))
    return passed


def main():
    counts = dict(positive=0, negative=0)
    def reject(label, action):
        try: action()
        except (ValueError, TypeError, KeyError, FileNotFoundError): counts['negative'] += 1
        else: raise AssertionError('Unexpected acceptance: '+label)
    canonical = base64.b64encode(b'c'*64).decode()
    with tempfile.TemporaryDirectory(prefix='sl971-synthetic-') as temporary:
        root = Path(temporary)/'packages'
        folder = root/'example'/'1.0.0'; folder.mkdir(parents=True)
        archive = folder/'example.1.0.0.nupkg'
        nuspec = b'<package><metadata><id>Example</id><version>1.0.0</version></metadata></package>'
        with ZipFile(archive, 'w') as z:
            z.writestr('Example.nuspec', nuspec);z.writestr('lib/net10.0/Example.dll', b'synthetic-only')
        (folder/'example.nuspec').write_bytes(nuspec)
        dll=folder/'lib/net10.0/Example.dll';dll.parent.mkdir(parents=True);dll.write_bytes(b'synthetic-only')
        raw=base64.b64encode(hashlib.sha512(archive.read_bytes()).digest()).decode()
        assert raw != canonical
        (folder/'example.1.0.0.nupkg.sha512').write_text(raw)
        metadata=folder/'.nupkg.metadata'
        metadata.write_text(json.dumps(dict(version=2,source='https://api.nuget.org/v3/index.json',contentHash=canonical)))
        result=audit.cache_audit(root)['example/1.0.0']
        assert result['archiveSHA512']==raw and result['canonicalContentHash']==canonical
        audit.content_hash_matches('sha512-'+canonical,canonical);counts['positive']+=1
        reject('canonical is not raw archive',lambda:audit.content_hash_matches(canonical,raw))
        orphan=root/'orphan';orphan.mkdir();reject('orphan directory',lambda:audit.cache_audit(root));orphan.rmdir()
        orphan=root/'stray.txt';orphan.write_text('x');reject('orphan file',lambda:audit.cache_audit(root));orphan.unlink()
        extra=folder/'foreign';extra.write_text('x');reject('foreign installed file',lambda:audit.cache_audit(root));extra.unlink()
        extra=folder/'link';extra.symlink_to('missing');reject('dangling symlink',lambda:audit.cache_audit(root));extra.unlink()
        import os
        extra=folder/'fifo';os.mkfifo(extra);reject('special FIFO',lambda:audit.cache_audit(root));extra.unlink()
        dll.write_bytes(b'tampered');reject('wrong installed bytes',lambda:audit.cache_audit(root));dll.write_bytes(b'synthetic-only')
        sidecar=folder/'example.1.0.0.nupkg.sha512';sidecar.write_text(canonical);reject('wrong raw sidecar',lambda:audit.cache_audit(root));sidecar.write_text(raw)
        metadata.write_text(json.dumps(dict(source='https://not-official.invalid',contentHash=canonical)))
        reject('wrong source metadata',lambda:audit.cache_audit(root))
    with tempfile.TemporaryDirectory(prefix='sl971-input-synthetic-') as temporary:
        root=Path(temporary); old=root/'10.0.102';old.mkdir()
        initial=runner.selection_members(root)
        higher=root/'10.0.114';higher.mkdir()
        assert runner.selection_members(root) != initial; counts['positive']+=1
        higher.rmdir();assert runner.selection_members(root) == initial
        node=root/'fifo';os.mkfifo(node)
        reject('SDK special selection candidate',lambda:runner.selection_members(root))
        reject('guarded tree special node',lambda:runner.files(root));node.unlink()
        node=root/'dangling';node.symlink_to('missing')
        reject('SDK symlink selection candidate',lambda:runner.selection_members(root));node.unlink()
    c.check_optional_filter({},'selector');c.check_optional_filter({'filter':'selector'},'selector');counts['positive']+=2
    reject('wrong echoed selector',lambda:c.check_optional_filter({'filter':'other'},'selector'))
    reject('null echoed selector',lambda:c.check_optional_filter({'filter':None},'selector'))
    c.check_publication('a'*32,set());counts['positive']+=1
    reject('duplicate publication',lambda:c.check_publication('a'*32,{'a'*32}))
    reject('malformed publication',lambda:c.check_publication(None,set()))
    # Same actual source bytes/line anchors, explicitly different reported PathMap.
    n,xml,rows,scope=specimen()
    virtual='/_/terminal-main/test/SharpLink.TerminalSignal.Focused/ServerTerminalSignalObservationTests.cs'
    t=n['groups'][0]['tests'][0];t['filePath']=virtual
    t['exception']['stackTrace']=t['exception']['stackTrace'].replace(str(SOURCE),virtual)
    next(iter(rows.values()))['location']['file']=virtual
    xml[0][0][0].text=xml[0][0][0].text.replace(str(SOURCE),virtual)
    c.classify(n,ET.tostring(xml,encoding='unicode'),rows,scope,SOURCE,reported_source_file=virtual);counts['positive']+=1
    reject('wrong virtual path',lambda:c.classify(n,ET.tostring(xml,encoding='unicode'),rows,scope,SOURCE,reported_source_file='/wrong'))
    # Bounded public leaves are a field allowlist; original graphs stay private.
    from types import SimpleNamespace
    outcome=dict(stage='baseline-start-1',exitCode=2,timedOut=False,errors=['SECRET_ERROR'],
                 cleanupErrors=['SECRET_CLEANUP'],reportingErrors=[],remainingOwned={},argv=['SECRET_ARGV'])
    command=runner.owner.CommandFailure(outcome,(RuntimeError('SECRET_CAUSE'),))
    leaf_errors=[ExceptionGroup('SECRET_GROUP',[command,ValueError('Wrong actual UID set'),ValueError('SECRET_TOKEN')])]
    rows=runner.failure_leaves(leaf_errors,'baseline-start-1','baseline-start','actual.uid')
    public=json.dumps(rows)
    assert 'SECRET' not in public and 'Wrong actual UID set' in public
    assert rows[0]['exit']==2 and rows[0]['timeout'] is False and rows[0]['cleanupErrorCount']==1 and rows[0]['remainingOwnedEmpty'] is True
    assert set(rows[0])=={'stage','scope','uid','exceptionType','exit','timeout','cleanupErrorCount','remainingOwnedEmpty'}
    assert len(runner.failure_leaves([ExceptionGroup('many',[ValueError('x') for _ in range(20)])],'stage'))==8
    counts['positive']+=2
    missing=dict(outcome);missing.pop('remainingOwned')
    assert runner.failure_leaves([runner.owner.CommandFailure(missing,())],'stage')[0]['remainingOwnedEmpty'] is None
    counts['positive']+=1
    with tempfile.TemporaryDirectory(prefix='sl971-report-synthetic-') as temporary:
        context=SimpleNamespace(work=Path(temporary),manifest_sha='synthetic',stage='baseline-start-1',
                                results=[],active_scope='baseline-start',active_uid='actual.uid')
        sent=[];original=ValueError('Wrong actual UID set');errors=[original]
        def broken_writer(*args,**kwargs): raise OSError('SECRET_CONSOLE')
        assert runner.finish(context,errors,writer=broken_writer,fallback=lambda fd,b:sent.append(b))==1
        assert errors[0] is original and any(isinstance(e,OSError) for e in errors)
        assert b'SECRET' not in b''.join(sent)
        receipt=json.loads((context.work/'RESULT.json').read_text())
        assert receipt['status']=='FAILED' and receipt['errors'][0]['message']=='Wrong actual UID set'
        counts['positive']+=1
        saved=runner.failure_leaves
        def broken_formatter(*args): raise RuntimeError('SECRET_FORMATTER')
        runner.failure_leaves=broken_formatter
        try:
            errors=[original];sent=[]
            assert runner.finish(context,errors,writer=lambda *a,**k:None,fallback=lambda fd,b:sent.append(b))==1
            assert errors[0] is original and any(isinstance(e,RuntimeError) for e in errors)
            assert b'SECRET' not in b''.join(sent)
        finally:runner.failure_leaves=saved
        counts['positive']+=1
        errors=[original]
        def broken_fallback(*args):raise OSError('SECRET_FALLBACK')
        assert runner.finish(context,errors,writer=broken_writer,fallback=broken_fallback)==1
        assert errors[0] is original and len(errors)>=3
        receipt=json.loads((context.work/'RESULT.json').read_text());assert receipt['status']=='FAILED' and len(receipt['errors'])>=3
        counts['positive']+=1
        # A nominal success whose console publication fails is not pipeline green.
        errors=[]
        assert runner.finish(context,errors,writer=broken_writer,fallback=lambda fd,b:None)==1 and errors
        assert json.loads((context.work/'RESULT.json').read_text())['status']=='FAILED'
        counts['positive']+=1
    diagnostic=diagnostic_controls()
    closure=closure_controls()
    metadata=metadata_controls()
    print('SYNTHETIC_AUDIT_PASS '+json.dumps(counts)+' diagnosticControls='+json.dumps(diagnostic)+' closureControls='+json.dumps(closure)+' metadataControls='+json.dumps(metadata)+' runtimeTests=0')


if __name__=='__main__':main()
