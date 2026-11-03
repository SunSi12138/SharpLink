"""Fixed audit reasons and data-only context. No acceptance or filesystem access."""
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import PurePosixPath,PureWindowsPath

KNOWN_AUDIT_REASONS=frozenset(('Actual assets differ from original19 project closure', 'Actual original project edges outside precise conditional graph', 'Archive duplicate/case alias', 'Assets/canonical metadata mismatch', 'Duplicate RID-specific copy destination', 'Installed bytes differ from official archive', 'Installed file absent from official archive', 'Missing SHA512 string', 'Missing assets canonical hash', 'Missing required originalaccept runtime packages', 'Missing/symlink original graph input', 'Official package cache is empty', 'Original asset outside audited official cache', 'Original fixture/project/import bytes changed', 'Original package asset path identity mismatch', 'Original production assembly copy differs from project output', 'Original project restore reported warning/error', 'Original runtime copied bytes differ from official asset', 'Original19 static reference attributes changed', 'Originalaccept TUnit upgrade identity changed', 'Originalaccept version-specific engine/MTP absent', 'Orphan cache file/directory without archive', 'Package nuspec identity mismatch', 'Package nuspec missing/duplicate', 'Package was not restored from sole official source', 'Project library is outside actual restored reference closure', 'Project library lacks a supported relative project path', 'Project library path/msbuildProject identity mismatch', 'Project reference escapes exact source', 'RID output contains symlink/special', 'RID output root is not an ordinary directory', 'RID-specific asset is symlink/special', 'RID-specific copied bytes differ from official asset', 'Raw archive/sidecar SHA512 mismatch', 'Runtime asset absent from exact restore target', 'Runtime package outside original restored Integration assets', 'Runtime package path differs from actual assets', 'Special file in official cache', 'Symlink anywhere in official cache', 'Symlink in official cache', 'Unbound RID-specific runtime asset', 'Unexpected original asset library kind', 'Unexpected original import selector', 'Unexpected original package folder', 'Unexpected original restore source', 'Unknown built dependency library kind', 'Unmapped extra/missing RID output file', 'Unmapped extra/missing original runtime root DLL', 'Unpinned actual project restore identity', 'Unsupported RID-specific copy destination', 'Unsupported pinned ReferenceOutputAssembly', 'Wrong SHA512 length', 'Wrong actual RestoreProjectName', 'Wrong original Integration asset target', 'Wrong original Integration project closure', 'Wrong original production runtime ProjectReferences', 'Wrong original restored framework', 'Wrong original restored project identity', 'Wrong originalaccept actual TUnit package', 'Wrong originalaccept runtime request'))
PHASES=frozenset(('restore-start','restore-assets','restore-name','restore-project','restore-library','restore-closure','restore-target','cache-start','cache-tree','cache-archive','cache-member'))

def digest(value):
    return hashlib.sha256(json.dumps(value,sort_keys=True,separators=(',',':'),allow_nan=False).encode()).hexdigest()
def kind(value):
    return {str:'string',dict:'object',list:'array',int:'integer',bool:'boolean',float:'number',type(None):'null'}.get(type(value),'unsupported')
def field(value):
    result={'type':kind(value)}
    if result['type']!='unsupported':result['sha256']=digest(value)
    if type(value) in (dict,list,str):result['count']=len(value)
    return result
def obj(value):return value if type(value) is dict else {}
def path_field(value,expected=None):
    result=field(value)
    if type(value) is str:
        result.update(hasBackslash='\\' in value,windowsAbsolute=PureWindowsPath(value).is_absolute(),posixAbsolute=PurePosixPath(value).is_absolute())
        if expected is not None:result['lexicallyMatchesExpected']=os.path.normcase(os.path.normpath(value))==os.path.normcase(os.path.normpath(expected))
    return result

@dataclass(frozen=True)
class AuditContext:
    state:dict
    projects:dict
    def public(self):
        s=self.state
        if s.get('phase') not in PHASES:raise ValueError('Unselected audit diagnostic phase')
        out={'phase':s['phase'],'qualification':'Diagnostic shape only; original audit rejection unchanged'}
        name=s.get('project')
        project=self.projects.get(name) if type(name) is str else None
        if project is not None:out['knownProject']={'name':name,'relative':project['path'],'sourceSHA256':project['sha256']}
        elif name is not None:out['unknownProject']=field(name)
        for key in ('expectedAssets','foundAssets'):
            if key in s:
                values=s[key];out[key]={'count':len(values),'setSHA256':digest(sorted(values))}
        if 'expectedAssets' in s and 'foundAssets' in s:
            out['missingAssetsCount']=len(s['expectedAssets']-s['foundAssets']);out['extraAssetsCount']=len(s['foundAssets']-s['expectedAssets'])
        if 'document' in s:
            document=obj(s['document']);restore=obj(obj(document.get('project')).get('restore'))
            out['restoreFields']={
                'projectPath':path_field(restore.get('projectPath'),s.get('expectedProjectPath')),
                'projectName':field(restore.get('projectName')),
                'frameworks':field(restore.get('frameworks')),
                'sources':field(restore.get('sources')),
                'packageFolders':field(document.get('packageFolders')),
                'logs':field(document.get('logs')),
                'libraries':field(document.get('libraries')),
            }
            if project is not None:
                out['restoreFields']['projectNameMatchesExpected']=restore.get('projectName')==project['restoreName']
                out['restoreFields']['expectedFrameworkPresent']=project['framework'] in obj(restore.get('frameworks'))
            logs=document.get('logs');codes={}
            if type(logs) is list:
                for row in logs:
                    code=obj(row).get('code')
                    if type(code) is str:
                        import re
                        if re.fullmatch(r'(?:NU|MSB|NETSDK)[0-9]{4}',code):codes[code]=codes.get(code,0)+1
            out['restoreLogCodes']=[{'code':code,'count':codes[code]} for code in sorted(codes)[:16]]
            out['restoreLogCodeKinds']=len(codes);out['restoreLogCodesTruncated']=len(codes)>16
        if 'library' in s:
            library=obj(s['library']);out['libraryFields']={'identity':field(s.get('libraryIdentity')),'type':field(library.get('type')),
                'path':path_field(library.get('path')),'msbuildProject':path_field(library.get('msbuildProject')),'sha512':field(library.get('sha512'))}
        for key in ('packageRelative','installedRelative','metadata'):
            if key in s:out[key]=field(s[key])
        for key in ('archiveCount','archiveMemberCount','installedMemberPresent'):
            if key in s:
                if type(s[key]) not in (int,bool):raise ValueError('Invalid audit diagnostic count')
                out[key]=s[key]
        return out


def attach(original,state,projects):
    if getattr(original,'audit_context',None) is not None:return
    try:original.audit_context=AuditContext(dict(state),projects)
    except BaseException as diagnostic:
        raise BaseExceptionGroup('Original audit failure and context failure',[original,diagnostic]) from None
