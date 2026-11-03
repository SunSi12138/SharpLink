"""Data-only bounded command diagnostics. No acceptance or environment changes."""
from collections import Counter
from dataclasses import dataclass
import hashlib
import os
from pathlib import Path
import re
import stat
import hosted_process as host

MAX_CAPTURE=16*1024*1024
# Only SDK/NuGet diagnostic identifiers, never surrounding arbitrary text.
CODES=re.compile(r'(?<![A-Za-z0-9_])(?P<severity>error|warning)\s+(?P<code>NU[0-9]{4}|MSB[0-9]{4}|NETSDK[0-9]{4})(?![A-Za-z0-9_])\s*:',re.I)
EXCEPTIONS=('System.ArgumentNullException','System.ArgumentException','System.TypeInitializationException',
            'System.IO.DirectoryNotFoundException','System.IO.FileNotFoundException','System.UnauthorizedAccessException',
            'NuGet.Configuration.NuGetConfigurationException','NuGet.Configuration.ConfigurationException')
KNOWN_REASONS=frozenset(('Unexpected hosted command exit','Source/protected output mutation','Protected source/output changed',
                        'Protected cache mutation','Protected package cache changed','Prior private evidence changed',
                        'Special/reparse guarded input','Unselected or nested git metadata forbidden',
                        'Pinned config/restore input changed','Frozen payload changed','Hosted total deadline exceeded'))


def stream_summary(raw):
    if not isinstance(raw,bytes) or len(raw)>MAX_CAPTURE:raise ValueError('Capture exceeds diagnostic bound')
    text=raw.decode('utf-8-sig',errors='replace')
    counts=Counter((m.group('severity').lower(),m.group('code').upper()) for m in CODES.finditer(text))
    keys=sorted(counts)
    return {'byteCount':len(raw),'rawSHA256':hashlib.sha256(raw).hexdigest(),'decodedLineCount':len(text.splitlines()),
            'replacementCharacterCount':text.count('\ufffd'),'codeOccurrences':sum(counts.values()),'codeKindCount':len(keys),
            'codes':[{'severity':s,'code':c,'count':counts[(s,c)]} for s,c in keys[:16]],'codesTruncated':len(keys)>16,
            'knownExceptionTypes':[{'type':name,'count':len(re.findall(r'(?<![A-Za-z0-9_.])'+re.escape(name)+r'(?![A-Za-z0-9_.])',text))}
                                   for name in EXCEPTIONS if re.search(r'(?<![A-Za-z0-9_.])'+re.escape(name)+r'(?![A-Za-z0-9_.])',text)]}


def known_reason(error):
    # Never str(error), message suffixes, raw reports or arbitrary exception args.
    if type(error) is ValueError and len(error.args)==1 and isinstance(error.args[0],str) and error.args[0] in KNOWN_REASONS:return error.args[0]
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
