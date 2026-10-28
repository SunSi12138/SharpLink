#!/usr/bin/env python3
"""Read-only portable bundle census for review; no extraction or CLR execution.
Does not replace independent source/build/runtime proof or receipt-schema review.
"""
import argparse,hashlib,json,tarfile
from pathlib import Path

def audit(bundle,expected):
    with bundle.open('rb') as f:actual=hashlib.file_digest(f,'sha256').hexdigest()
    if actual!=expected:raise ValueError('Bundle digest mismatch')
    hashes={};documents={}
    with tarfile.open(bundle,'r:gz') as tar:
        for m in tar:
            p=Path(m.name)
            if not m.isfile() or p.is_absolute() or '..' in p.parts or m.name in hashes:raise ValueError('Unsafe/duplicate bundle member')
            with tar.extractfile(m) as f:
                if m.name in ('prepared-index.json','receipts/D.json','receipts/C.json','receipts/revalidated-D.json','receipts/revalidated-C.json','evidence/host.json'):
                    data=f.read();hashes[m.name]=hashlib.sha256(data).hexdigest();documents[m.name]=json.loads(data)
                else:hashes[m.name]=hashlib.file_digest(f,'sha256').hexdigest()
    index=documents['prepared-index.json'];root=Path('/tmp/sharplink-wakeup-native-v1' if index['rid']=='linux-x64' else '/private/tmp/sharplink-wakeup-native-v1')
    receipt_files=index.get('receiptFiles',{'D':'receipts/D.json','C':'receipts/C.json'})
    if receipt_files not in ({'D':'receipts/D.json','C':'receipts/C.json'},{'D':'receipts/revalidated-D.json','C':'receipts/revalidated-C.json'}):raise ValueError('Unexpected receipt paths')
    if 'receiptFiles' in index and index['rid']!='osx-arm64':raise ValueError('Only ARM has revalidated receipts')
    for arm in ('D','C'):
        if hashes[receipt_files[arm]]!=index['receipts'][arm]:raise ValueError('Receipt identity mismatch')
        receipt=documents[receipt_files[arm]];seen=set()
        for row in receipt['files']:
            rel=str(Path(row['path']).relative_to(root))
            if rel in seen or hashes.get(rel)!=row['sha256']:raise ValueError('Receipt file missing/changed/duplicated: '+rel)
            seen.add(rel)
    return {'bundleSha256':actual,'memberCount':len(hashes),'preparedIndex':index,'prepareHostOnly':documents['evidence/host.json'],'allReceiptFileHashesMatch':True,'acceptance':'INDEPENDENT_REVIEW_REQUIRED; no source-to-binary attestation or collection approval inferred'}
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('bundle',type=Path);p.add_argument('sha256');a=p.parse_args();print(json.dumps(audit(a.bundle,a.sha256),indent=2))
