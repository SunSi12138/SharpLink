"""Data-only bounded command diagnostics. No acceptance or environment changes."""
from collections import Counter
from dataclasses import dataclass
import hashlib
import os
from pathlib import Path
import re
import stat
import hosted_process as host
from audit_diagnostics import KNOWN_AUDIT_REASONS

MAX_CAPTURE=16*1024*1024
# Fixed literal categories only, never an extracted arbitrary line/message.
# MSBuild XMake.cs/Strings.resx at the SDK-pinned VMR define the first four.
TOOL_LITERALS={
    'msbuild-fatal': 'MSBUILD : error MSB1025: An internal failure occurred while running MSBuild.',
    'msbuild-logger-unhandled': 'This is an unhandled exception from a logger -- PLEASE OPEN A BUG AGAINST THE LOGGER OWNER.',
    'msbuild-project-cache-unhandled': 'This is an unhandled exception from a project cache -- PLEASE OPEN A BUG AGAINST THE PROJECT CACHE OWNER.',
    'msbuild-engine-unhandled': 'This is an unhandled exception in MSBuild Engine -- PLEASE OPEN A BUG AGAINST THE MSBUILD TEAM.',
}

# Only SDK/NuGet diagnostic identifiers, never surrounding arbitrary text.
CODES=re.compile(r'(?<![A-Za-z0-9_])(?P<severity>error|warning)\s+(?P<code>NU[0-9]{4}|MSB[0-9]{4}|NETSDK[0-9]{4})(?![A-Za-z0-9_])\s*:',re.I)
EXCEPTIONS=('System.ArgumentNullException','System.ArgumentException','System.TypeInitializationException',
            'System.IO.DirectoryNotFoundException','System.IO.FileNotFoundException','System.UnauthorizedAccessException',
            'NuGet.Configuration.NuGetConfigurationException','NuGet.Configuration.ConfigurationException')
KNOWN_REASONS=frozenset(('Unexpected hosted command exit','Source/protected output mutation','Protected source/output changed',
                        'Protected cache mutation','Protected package cache changed','Prior private evidence changed',
                        'Special/reparse guarded input','Unselected or nested git metadata forbidden',
                        'Pinned config/restore input changed','Frozen payload changed','Hosted total deadline exceeded',
                        'Missing required Windows program folder','Windows program folder is not absolute',
                        'Windows folder identity changed','Protected Windows profile changed','Protected Windows profile mutation'))


def stream_summary(raw):
    if not isinstance(raw,bytes) or len(raw)>MAX_CAPTURE:raise ValueError('Capture exceeds diagnostic bound')
    text=raw.decode('utf-8-sig',errors='replace')
    counts=Counter((m.group('severity').lower(),m.group('code').upper()) for m in CODES.finditer(text))
    keys=sorted(counts)
    return {'byteCount':len(raw),'rawSHA256':hashlib.sha256(raw).hexdigest(),'decodedLineCount':len(text.splitlines()),
            'replacementCharacterCount':text.count('\ufffd'),'nulByteCount':raw.count(b'\0'),
            'encodingBOM':('utf8' if raw.startswith(b'\xef\xbb\xbf') else 'utf16le' if raw.startswith(b'\xff\xfe') else 'utf16be' if raw.startswith(b'\xfe\xff') else 'none'),
            'fixedToolLiterals':[{'category':key,'count':text.count(value)} for key,value in TOOL_LITERALS.items() if value in text],
            'uncodedErrorLineCount':len(re.findall(r'(?im)^[^\r\n]*?\berror\s*:',text)),
            'codeOccurrences':sum(counts.values()),'codeKindCount':len(keys),
            'codes':[{'severity':s,'code':c,'count':counts[(s,c)]} for s,c in keys[:16]],'codesTruncated':len(keys)>16,
            'knownExceptionTypes':[{'type':name,'count':len(re.findall(r'(?<![A-Za-z0-9_.])'+re.escape(name)+r'(?![A-Za-z0-9_.])',text))}
                                   for name in EXCEPTIONS if re.search(r'(?<![A-Za-z0-9_.])'+re.escape(name)+r'(?![A-Za-z0-9_.])',text)]}


def known_reason(error):
    # Never str(error), message suffixes, raw reports or arbitrary exception args.
    if type(error) is ValueError and len(error.args)==1 and isinstance(error.args[0],str) and error.args[0] in KNOWN_REASONS|KNOWN_AUDIT_REASONS:return error.args[0]
    return None


def read_capture(work,directory,label,stream):
    if not re.fullmatch(r'(restore|build)-(baseline|fixed)',label) or stream not in ('stdout','stderr'):
        raise ValueError('Unselected command diagnostic')
    if directory!=work/label:raise ValueError('Diagnostic directory is not selected command')
    host.ordinary_directory(work);host.ordinary_directory(directory)
    info=directory.lstat();directory_id=(info.st_dev,info.st_ino)
    path=directory/(label+'.'+stream);before=path.lstat()
    if not stat.S_ISREG(before.st_mode) or getattr(before,'st_file_attributes',0)&0x400 or before.st_size>MAX_CAPTURE:
        raise ValueError('Diagnostic capture is not bounded ordinary file')
    # POSIX nonblocking/no-follow flags close the lstat-to-open FIFO/link race.
    # Windows has no POSIX FIFO/O_NONBLOCK; use its available binary/no-inherit
    # flags and still reject any nonregular or changed handle before reading.
    flags=os.O_RDONLY|getattr(os,'O_NONBLOCK',0)|getattr(os,'O_NOFOLLOW',0)|getattr(os,'O_BINARY',0)|getattr(os,'O_NOINHERIT',0)
    fd=None;handle=None;errors=[];raw=None
    try:
        fd=os.open(path,flags)
        actual=os.fstat(fd)
        if (actual.st_dev,actual.st_ino)!=(before.st_dev,before.st_ino) or not stat.S_ISREG(actual.st_mode):
            raise ValueError('Diagnostic capture changed before read')
        handle=os.fdopen(fd,'rb');fd=None
        raw=handle.read(MAX_CAPTURE+1)
        after=os.fstat(handle.fileno())
        host.ordinary_directory(directory);last=path.lstat();final=directory.lstat()
        if ((final.st_dev,final.st_ino)!=directory_id or (last.st_dev,last.st_ino)!=(before.st_dev,before.st_ino)
                or not stat.S_ISREG(last.st_mode) or getattr(last,'st_file_attributes',0)&0x400
                or (before.st_size,before.st_mtime_ns)!=(after.st_size,after.st_mtime_ns)
                or (last.st_size,last.st_mtime_ns)!=(after.st_size,after.st_mtime_ns) or len(raw)!=after.st_size):
            raise ValueError('Diagnostic capture changed during read')
    except BaseException as error:errors.append(error)
    finally:
        try:
            if handle is not None:handle.close()
            elif fd is not None:os.close(fd)
        except BaseException as error:errors.append(error)
    if len(errors)>1:raise BaseExceptionGroup('Diagnostic read and close failures',errors)
    if errors:raise errors[0]
    return raw


@dataclass(frozen=True)
class CommandCodes:
    label:str
    stdout:dict
    stderr:dict
    def public(self):
        return {'command':self.label,'stdout':self.stdout,'stderr':self.stderr,
                'qualification':'Diagnostic counts only; no command acceptance or root-cause claim'}


def attach(original,work,directory,label):
    if not re.fullmatch(r'(restore|build)-(baseline|fixed)',label):return
    try:
        original.command_codes=CommandCodes(label,stream_summary(read_capture(work,directory,label,'stdout')),
                                           stream_summary(read_capture(work,directory,label,'stderr')))
    except BaseException as diagnostic:
        raise BaseExceptionGroup('Original command failure and safe diagnostic failure',[original,diagnostic]) from None


@dataclass(frozen=True)
class ProtectedInventory:
    variant:str
    boundary:str
    summary:dict
    def public(self):
        return {'variant':self.variant,'boundary':self.boundary,**self.summary,
                'qualification':'Diagnostic inventory difference only; original equality rejection unchanged'}


def inventory_difference(expected,actual,known_paths):
    # Inputs are already computed original guard snapshots. No filesystem reads,
    # path resolution, or arbitrary basename/content/message publication here.
    if not isinstance(expected,dict) or not isinstance(actual,dict) or not isinstance(known_paths,frozenset):
        raise ValueError('Invalid diagnostic inventory inputs')
    for values in (expected,actual):
        if any(not isinstance(k,str) or not isinstance(v,str) or not re.fullmatch(r'[0-9a-f]{64}',v) for k,v in values.items()):
            raise ValueError('Invalid diagnostic inventory shape')
    if any(not isinstance(k,str) or not k or k.startswith('/') or '\\' in k or ':' in k or any(x in ('','..','.') for x in k.split('/')) for k in known_paths):
        raise ValueError('Invalid known diagnostic path catalog')
    keys=sorted(k for k in expected.keys()|actual.keys() if expected.get(k)!=actual.get(k))
    counts=Counter('added' if k not in expected else 'removed' if k not in actual else 'changed' for k in keys)
    rows=[]
    for key in keys[:16]:
        row={'kind':'added' if key not in expected else 'removed' if key not in actual else 'changed',
             'pathSHA256':hashlib.sha256(key.encode()).hexdigest(),
             'beforeSHA256':expected.get(key),'afterSHA256':actual.get(key),
             'pathClass':'catalog-known' if key in known_paths else 'unknown-relative-hash-only'}
        if key in known_paths:row['knownRelativePath']=key
        rows.append(row)
    return {'expectedFiles':len(expected),'actualFiles':len(actual),'differenceCount':len(keys),
            'added':counts['added'],'removed':counts['removed'],'changed':counts['changed'],
            'records':rows,'truncated':len(keys)>len(rows)}


def require_inventory(actual,expected,reason,variant,boundary,known_paths):
    # Same equality predicate and original ValueError. Diagnostic errors are
    # independently appended, never replacing the guard or accepting mutation.
    if actual==expected:return
    original=ValueError(reason)
    try:
        if reason not in ('Source/protected output mutation','Protected source/output changed') or variant not in ('baseline','fixed') or boundary not in ('phase','current'):
            raise ValueError('Unselected diagnostic inventory guard')
        original.protected_inventory=ProtectedInventory(variant,boundary,inventory_difference(expected,actual,known_paths))
    except BaseException as diagnostic:
        raise BaseExceptionGroup('Original inventory rejection and diagnostic failure',[original,diagnostic]) from None
    raise original
