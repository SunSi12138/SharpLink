"""Exact immutable fixture method spans, not report/catalog-derived expectations.
Pinned TUnit generators use MethodDeclarationSyntax.GetLocation().GetLineSpan()
then +1 for both line coordinates; attributes belong to the declaration span.
Only this six-method, fixed-byte C# syntax is recognized, not a general parser.
"""
import hashlib
from pathlib import Path
import re
import classify as old

FIXTURE_SHA='f2bf615cc01cab31a45382825138cf47aac5de7b87da3ccf8e38ba4a437e0d2a'


def spans(source):
    raw=Path(source).read_bytes()
    old.require(hashlib.sha256(raw).hexdigest()==FIXTURE_SHA,'Wrong178 immutable span source bytes')
    text=raw.decode('utf-8');result={}
    for method in (*old.EXPECTED,old.POS_METHOD):
        pattern=(r'^    \[Test\]\n(?:    \[(?:Arguments\((?:false|true)\)|Explicit)\]\n)*'
                 r'    public (?:async )?(?:void|Task) '+re.escape(method)+r'\([^\n)]*\)\n'
                 r'(?:        =>[^\n]+;|    \{\n[\s\S]*?^    \})')
        matches=list(re.finditer(pattern,text,re.MULTILINE))
        old.require(len(matches)==1,'Ambiguous178 immutable method span')
        match=matches[0]
        result[method]=(text.count('\n',0,match.start())+1,text.count('\n',0,match.end())+1)
    old.require(len(result)==6,'Wrong178 immutable test method set')
    return result


def validate_ledger(ledger,source):
    expected=spans(source)
    for row in ledger.values():
        method=row['type']['methodName'];location=row['location']
        old.require(method in expected and type(location.get('lineStart')) is int and type(location.get('lineEnd')) is int and
                    (location['lineStart'],location['lineEnd'])==expected[method],
                    'Discovered span differs from pinned178 fixture method')
    return ledger


def native_span(test,row):
    start=row['location']['lineStart'];end=row['location']['lineEnd']
    old.require(type(test.get('lineNumber')) is int and test['lineNumber']==start,'Wrong178 native source start line')
    if end>start:
        old.require(type(test.get('endLineNumber')) is int and test['endLineNumber']==end,'Wrong178 native source end line')
    else:
        old.require('endLineNumber' not in test,'Unexpected178 native same-line end field')
