"""Unchanged originalaccept raw/path predicates; data-only bounded diagnostics."""
from dataclasses import dataclass
import hashlib
import re
import classify as c
import accept_results as accept
FOCUSED='test/SharpLink.IntegrationTests'
SOURCE_FILE='TransportConnectionIntegrationTests.Lifecycle.cs'
REPORTED_SOURCE='/_/'+FOCUSED+'/'+SOURCE_FILE
def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()

class DiscoveryPathDiagnostics:
    """Data-only allowlist. Never open or resolve a path supplied by the report."""
    def __init__(self, document, source):
        rows = []
        mismatches = 0
        for test in document.get('tests', []):
            if not isinstance(test, dict): continue
            location = test.get('location', {})
            if not isinstance(location, dict): continue
            actual = location.get('file')
            if actual == REPORTED_SOURCE: continue
            mismatches += 1
            if len(rows) >= 8: continue
            uid = test.get('uid')
            category = ('non-string' if not isinstance(actual, str) else
                        'exact-selected-physical-fixture' if actual == str(source) else
                        'legacy-terminal-virtual-fixture' if actual == '/_/terminal-main/'+FOCUSED+'/'+SOURCE_FILE else
                        'other-underscore-virtual-root' if actual.startswith('/_/') else 'unbound-path')
            row = {'pathClass':category,
                   'actualPathSHA256':hashlib.sha256(actual.encode()).hexdigest() if isinstance(actual,str) else None,
                   'actualUidSHA256':hashlib.sha256(uid.encode()).hexdigest() if isinstance(uid,str) else None}
            for field in ('lineStart','lineEnd'):
                if type(location.get(field)) is int: row[field] = location[field]
            rows.append(row)
        self.data = {'expectedReportedPath':REPORTED_SOURCE,
                     'selectedGitRelativeSource':FOCUSED+'/'+SOURCE_FILE,
                     'selectedSourceSHA256':sha(source), 'mismatchCount':mismatches,
                     'rows':rows, 'truncated':mismatches>len(rows),
                     'qualification':'diagnostic-only; strict path rejection unchanged'}

    def public(self):
        return self.data


def discover_with_path_receipt(document, scope, source):
    try:
        return accept.discovery(document,scope,source)
    except ValueError as original:
        if original.args != ('Wrong originalaccept discovered source path',): raise
        try:
            original.discovery_paths = DiscoveryPathDiagnostics(document, source)
        except BaseException as reporting:
            raise BaseExceptionGroup('Discovery failure and diagnostic failure', [original, reporting]) from None
        raise



@dataclass(frozen=True)
class RawEventDiagnostics:
    scope: str
    stderr_text: str
    stdout_text: str | None

    def public(self):
        def stream(text):
            prefix='Unobserved task exception: '
            lines=text.splitlines();matches=[]
            for index,line in enumerate(lines):
                if prefix not in line:continue
                offset=line.index(prefix);tail=line[offset+len(prefix):]
                category=('aggregate-exception' if tail.startswith('System.AggregateException:') else
                          'ioexception' if tail.startswith('System.IO.IOException:') else 'other-or-empty')
                if len(matches)<16:
                    matches.append({'lineNumber':index+1,'lineLength':len(line),'firstPrefixOffset':offset,
                                    'prefixesOnLine':line.count(prefix),'headerCategory':category,
                                    'decodedLineSHA256':hashlib.sha256(line.encode()).hexdigest()})
            count=sum(prefix in line for line in lines)
            return {'decodedTextSHA256':hashlib.sha256(text.encode()).hexdigest(),'characterCount':len(text),
                    'lineCount':len(lines),'exactPrefixOccurrences':text.count(prefix),
                    'caseInsensitivePrefixOccurrences':len(re.findall(re.escape(prefix),text,re.I)),
                    'linesContainingExactPrefix':count,'lineStartExactPrefixCount':sum(line.startswith(prefix) for line in lines),
                    'unhandledAppDomainOccurrences':text.count('Unhandled exception in AppDomain'),
                    'replacementCharacterCount':text.count('\ufffd'),'headerLines':matches,'headerLinesTruncated':count>16}
        return {'scope':self.scope if self.scope in ('baseline','baseline-green','fixed','control') else 'unknown',
                'qualification':'diagnostic only; unchanged raw-output rejection; decoded UTF8 text hashes, not raw-byte hashes',
                'stderr':stream(self.stderr_text),'stdout':stream(self.stdout_text) if self.stdout_text is not None else None}


def validate_stderr(scope,text,stdout_loader=None):
    try:
        c.require('Unhandled exception in AppDomain' not in text,'Originalaccept unhandled process exception')
        if scope=='baseline':
            c.require(text.count('Unobserved task exception: ')==1,'Wrong originalaccept raw event-header count')
        else:c.require(not re.search('unobserved',text,re.I),'Unexpected originalaccept fixed/green stderr event')
    except ValueError as original:
        try:
            stdout=stdout_loader() if stdout_loader is not None else None
            original.raw_events=RawEventDiagnostics(scope,text,stdout)
        except BaseException as diagnostic:
            raise BaseExceptionGroup('Original raw output rejection and diagnostic failure',[original,diagnostic])
        raise
