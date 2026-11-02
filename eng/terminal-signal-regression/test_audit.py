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
        try:audit.expanded(pp,source,packages,{},diagnostic_sdk_root=sdk)
        except ValueError as failure:
            assert str(failure)=='Import outside exact source/official cache/sealed selected SDK'
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
        def broken(*args):raise RuntimeError('SECRET_DIAGNOSTIC')
        audit.import_diagnostics=broken
        try:
            try:audit.expanded(pp,source,packages,{},diagnostic_sdk_root=sdk)
            except ExceptionGroup as group:
                assert type(group.exceptions[0]) is ValueError and type(group.exceptions[1]) is RuntimeError
                assert str(group.exceptions[0])=='Import outside exact source/official cache/sealed selected SDK'
                assert 'SECRET' not in json.dumps(runner.failure_leaves([group],'preprocess-baseline-0'))
            else:raise AssertionError('Diagnostic failure must not mask or accept body failure')
        finally:audit.import_diagnostics=saved
        # A no-rejection diagnostic run is explicitly not runtime qualification.
        context=SimpleNamespace(work=base,manifest_sha='synthetic',stage='preprocess-baseline-0',
                                results=[],active_scope=None,active_uid=None,diagnostic_complete=True)
        emitted=[];errors=[]
        assert runner.finish(context,errors,writer=lambda line,**kw:emitted.append(json.loads(line)))==0
        assert emitted==[dict(classification='DIAGNOSTIC_COMPLETE_NOT_RUNTIME_QUALIFICATION',qualifiedScopes=0,rawArtifactsUploaded=False)]
        assert json.loads((base/'RESULT.json').read_text())['diagnosticOnly'] is True
    return ['known-root-safe-hash', 'header-role-only', 'unsealed-still-rejected',
            'unknown-no-filesystem-access', 'escape-no-filesystem-access', 'alias-still-rejected',
            'symlink-no-follow', 'fifo-no-read', 'missing-file', 'bounded-16-total-count',
            'diagnostic-error-body-first', 'complete-not-runtime-qualification']


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
    print('SYNTHETIC_AUDIT_PASS '+json.dumps(counts)+' diagnosticControls='+json.dumps(diagnostic)+' runtimeTests=0')


if __name__=='__main__':main()
