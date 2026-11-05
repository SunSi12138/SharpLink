#!/usr/bin/env python3
"""Append one exact four-body derivation to the unchanged historical census.

Original catalog derivation, D2 proof, fixture history and rebase outputs stay
byte-for-byte intact. This layer binds current source to its immediate parent.
"""
import copy
import importlib.util
import json
from pathlib import Path
import sys
sys.dont_write_bytecode=True
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('historical_rebase',ROOT/'eng/rebase-three-fixture-census.py')
history=importlib.util.module_from_spec(spec);spec.loader.exec_module(history)
sha=history.sha;render=history.render;tree_id=history.tree_id;require=history.require;text_span=history.text_span
PARENT_HEAD='4c26ac7bd90995a684a1daa7d94078a27538df47'
PARENT_TREE='bf52f2d1ba5147b1fd06ad0c7e4bcb734b6e80e3'
PRODUCT_TREE='c3a94c896418a5d5ec62646461f17c67dc89bc96'
PARENT_TEST_TREE='47527763f1de7aa592f94120a346d843456d6cf1'
CANDIDATE_TEST_TREE='395db8d7cecea0158892f7ab6725c2a43031e572'
SOURCE='test/SharpLink.UnitTests/Runtime/NegotiatedSessionOptionsTests.cs'
OLD_SOURCE_SHA256='16d8a6ddf73b868c011ed391c4f29d519621e770679c03913219ac037a4bc554'
NEW_SOURCE_SHA256='ba2c03dc21940eb01ace60bd0391e415e43cd5b28461709dc85a93ecedb2bbea'
HISTORY='eng/runtime-handshake-full-history'
PARENT_CATALOG=HISTORY+'/parent-unit-census.json'
PREIMAGE=HISTORY+'/NegotiatedSessionOptionsTests.cs.txt'
MANIFEST=HISTORY+'/four-method-manifest.json'
PARENT_CATALOG_SHA256='f2164dcc06d4ca0d5aeb91a5e19cbf3d67e25321d9346948d7ccaf10d19f632c'
HISTORY_PINS={PARENT_CATALOG:PARENT_CATALOG_SHA256,PREIMAGE:OLD_SOURCE_SHA256,
 MANIFEST:'417aacd985d781a6b7b177dd666ddb71003f39e81683f50cbc92e4fc5111b764',
 HISTORY+'/parent-full-diagnostic-result.json':'157d3674e3be1b8876cbf9dd615adf8f2b0bab98a795beae48ebacf87f6fe62e',
 'eng/rebase-three-fixture-census.py':'4a7f520ae1e6a23c3ee30533b6dee75202e4c71c551c3ecde4529015a2416b86',
 'eng/three-fixture-census-rebase.json':'76760142bcd923121ebe7cbce1a04304d18d8b58ec064543660494a2cec6a0a1'}
METHODS=frozenset(('HandshakeCompletionShouldPublishOneCompleteImmutableSnapshot',
 'ConcurrentHandshakeCompletionShouldHaveOneWinnerAndOneSnapshot',
 'HandshakeCompletionAndTerminalRaceShouldNeverPublishPartialState',
 'InvalidHandshakeOptionsShouldFaultWithoutPublishingReady'))

def body(data,row):
    start,end=row['byteStart'],row['byteEndExclusive']
    require(type(start) is int and type(end) is int and 0<=start<end<=len(data),'RH_BODY_SPAN')
    value=data[start:end]
    require(value.startswith(b'{') and value.endswith(b'}') and len(value)==row['bytes'] and sha(value)==row['sha256'],'RH_BODY_HASH')
    require(data.count(b'\n',0,start)+1==row['startLine'] and data.count(b'\n',0,end)+1==row['endLine'],'RH_BODY_LINES')
    return value

def derive(parent,old_bytes,new_bytes,manifest):
    require(set(manifest)=={'schemaVersion','baseHead','path','oldFileSHA256','newFileSHA256','oldFileBytes','newFileBytes','offsetConvention','methods'},'RH_MANIFEST_SHAPE')
    require(manifest['schemaVersion']==1 and manifest['baseHead']==PARENT_HEAD and manifest['path']==SOURCE,'RH_MANIFEST_IDENTITY')
    require(sha(old_bytes)==OLD_SOURCE_SHA256==manifest['oldFileSHA256'] and sha(new_bytes)==NEW_SOURCE_SHA256==manifest['newFileSHA256'],'RH_SOURCE_PIN')
    require(len(old_bytes)==manifest['oldFileBytes'] and len(new_bytes)==manifest['newFileBytes'],'RH_SOURCE_LENGTH')
    rows=manifest['methods'];require(type(rows) is list and len(rows)==4 and {v['method'] for v in rows}==METHODS,'RH_EXACT_FOUR')
    require(len(parent['methods'])==2463 and len(parent['sourcePins'])==1599,'RH_COUNTS')
    require(sum(m['expectedCases'] for m in parent['methods'] if m['initialEligibility']=='ordinary' and 'Release' in m['configurations'])==2879 and
            sum(m['expectedCases'] for m in parent['methods'] if m['initialEligibility']=='explicit' and 'Release' in m['configurations'])==5,'RH_CASE_COUNTS')
    old=old_bytes.decode('utf-8');new=new_bytes.decode('utf-8');require('\r' not in old+new,'RH_NEWLINES')
    by_name={v['method']:v for v in rows};restored=new_bytes
    previous=len(new_bytes)
    for row in sorted(rows,key=lambda v:v['new']['byteStart'],reverse=True):
        before=body(old_bytes,row['old']);after=body(new_bytes,row['new'])
        require(before!=after and row['new']['byteEndExclusive']<=previous,'RH_DISTINCT_BODIES')
        previous=row['new']['byteStart']
        restored=restored[:row['new']['byteStart']]+before+restored[row['new']['byteEndExclusive']:]
    require(restored==old_bytes,'RH_ALL_OTHER_BYTES')
    result=copy.deepcopy(parent);affected=[];changed=[]
    for index,method in enumerate(parent['methods']):
        if method['source']!=SOURCE:continue
        before=text_span(old,method);p=method['sourceProvenance'];name=method['methodName']
        require(new.count(p['signature'])==1,'RH_SIGNATURE_UNIQUE')
        if name in METHODS:
            row=by_name[name]
            require(row['identity']==method['namespace']+'.'+method['className']+'.'+name and row['parameters']==method['parameters']==[] and row['discoveredCases']==method['expectedCases']==1,'RH_METHOD_IDENTITY')
            require(method['signatureLine']==row['old']['signatureLine'] and method['endLine']==row['old']['endLine'],'RH_OLD_LINES')
            old_start=len(old[:p['startOffset']].encode());old_end=len(old[:p['endOffset']].encode())
            require(old_end==row['old']['byteEndExclusive'],'RH_OLD_BOUNDARY')
            prefix=old_bytes[old_start:row['old']['byteStart']]
            start_byte=row['new']['byteStart']-len(prefix);end_byte=row['new']['byteEndExclusive']
            require(new_bytes[start_byte:row['new']['byteStart']]==prefix,'RH_PREFIX_UNCHANGED')
            start=len(new_bytes[:start_byte].decode());end=len(new_bytes[:end_byte].decode())
            require(new[start:end]!=before,'RH_CHANGED_BODY');changed.append(name)
        else:
            require(new.count(before)==1,'RH_OTHER_METHOD_SLICE');start=new.index(before);end=start+len(before)
        rebound=copy.deepcopy(method);q=rebound['sourceProvenance']
        rebound['attributeLine']=new.count('\n',0,start)+1
        rebound['signatureLine']=rebound['attributeLine']+method['signatureLine']-method['attributeLine']
        rebound['endLine']=new.count('\n',0,end)+1
        q.update(testAttributeLine=rebound['attributeLine'],startOffset=start,endOffset=end,spanSHA256=sha(new[start:end].encode()))
        require(new[start:end].startswith(before[:before.index(p['signature'])]+p['signature']),'RH_SIGNATURE_ATTRIBUTES')
        if name in METHODS:require(rebound['signatureLine']==by_name[name]['new']['signatureLine'] and rebound['endLine']==by_name[name]['new']['endLine'],'RH_NEW_LINES')
        result['methods'][index]=rebound
        affected.append({'method':name,'bodyChanged':name in METHODS,'oldLines':[method[k] for k in ('attributeLine','signatureLine','endLine')],
            'newLines':[rebound[k] for k in ('attributeLine','signatureLine','endLine')],
            'oldOffsets':[p['startOffset'],p['endOffset']],'newOffsets':[start,end],
            'oldSpanSHA256':p['spanSHA256'],'newSpanSHA256':q['spanSHA256']})
    require(set(changed)==METHODS and len(changed)==4 and len(affected)==10,'RH_METHOD_COUNT')
    require(parent['sourcePins'][SOURCE]==OLD_SOURCE_SHA256,'RH_PARENT_PIN')
    result['sourcePins'][SOURCE]=NEW_SOURCE_SHA256
    return result,affected

def generate(root):
    root=Path(root)
    for path,digest in HISTORY_PINS.items():require(sha((root/path).read_bytes())==digest,'RH_HISTORY_PIN')
    parent=json.loads((root/PARENT_CATALOG).read_bytes());old=(root/PREIMAGE).read_bytes();new=(root/SOURCE).read_bytes()
    manifest=json.loads((root/MANIFEST).read_bytes());result,affected=derive(parent,old,new,manifest)
    require(tree_id(root/'src')==PRODUCT_TREE,'RH_PRODUCT_TREE')
    require(tree_id(root/'test',{SOURCE.removeprefix('test/'):old})==PARENT_TEST_TREE,'RH_PARENT_TEST_TREE')
    require(tree_id(root/'test')==CANDIDATE_TEST_TREE,'RH_CURRENT_TEST_TREE')
    for path,digest in result['sourcePins'].items():require(sha((root/path).read_bytes())==digest,'RH_ALL_SOURCE_PINS')
    for method in result['methods']:text_span((root/method['source']).read_text(),method)
    # Preserve the entire previous derivation under its original key. The new
    # layer is explicit, never rewriting original provenance as current proof.
    derivation={'kind':'runtime-handshake-four-body-source-rebase','parentCommit':PARENT_HEAD,'parentTree':PARENT_TREE,
        'parentCatalogSHA256':PARENT_CATALOG_SHA256,'parentTestTree':PARENT_TEST_TREE,
        'candidateProductTree':PRODUCT_TREE,'candidateTestTree':CANDIDATE_TEST_TREE,
        'changedSourcePaths':[SOURCE],'changedMethodBodies':4,'methodCount':2463,'sourcePinCount':1599,
        'expectedOrdinaryCases':2879,'expectedExplicitCases':5,
        'candidateSourcePinsSHA256':sha(history.canonical(result['sourcePins'])),
        'qualification':'Derived source catalog only; not a test execution result.'}
    result['runtimeHandshakeDerivation']=derivation
    report={'schemaVersion':1,'scope':'runtime-handshake-four-body-catalog-rebase',**derivation,
        'candidateCatalogSHA256':sha(render(result)),'deletedMethods':0,'addedMethods':0,
        'unchangedMethodBodies':2459,'methodRecordsInEditedFile':affected,
        'allOtherParentTestBytesUnchanged':True,'nonMethodBytesUnchanged':True,'productBytesUnchanged':True,
        'parentFileSHA256':OLD_SOURCE_SHA256,'candidateFileSHA256':NEW_SOURCE_SHA256,
        'bodyManifestSHA256':HISTORY_PINS[MANIFEST],'historicalDerivationPreservedExactly':True,
        'historicalDiagnostic':{'runId':42004659522,'passed':2618,'errors':261,'runtimeHandshakePassed':1,
            'runtimeHandshakeFailed':3,'qualified':False,'evidenceSHA256':HISTORY_PINS[HISTORY+'/parent-full-diagnostic-result.json']},
        'candidateExecution':'not run','fullSuiteQualified':False}
    return result,report

def main():
    require(sys.argv[1:] in (['--check'],['--write']),'RH_ARGUMENTS')
    catalog,report=generate(ROOT)
    for path,data in {'eng/unit-census.json':render(catalog),'eng/runtime-handshake-census-rebase.json':render(report)}.items():
        if sys.argv[1]=='--write':(ROOT/path).write_bytes(data)
        else:require((ROOT/path).read_bytes()==data,'RH_REPRODUCIBILITY')
    print(json.dumps({'catalogSHA256':sha(render(catalog)),'testTree':CANDIDATE_TEST_TREE,'srcTree':PRODUCT_TREE,
        'methods':2463,'ordinaryCases':2879,'explicitCases':5,'changedBodies':4,'fullSuiteQualified':False},sort_keys=True))
if __name__=='__main__':main()
