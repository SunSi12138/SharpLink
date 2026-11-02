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
    print('SYNTHETIC_AUDIT_PASS '+json.dumps(counts)+' runtimeTests=0')


if __name__=='__main__':main()
