#!/usr/bin/env python3
"""Observe standard manual-build outputs and native CodeQL sources; never run product code."""
import argparse, base64, collections, hashlib, json, os, re, subprocess, zipfile
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath
from urllib.parse import unquote
from verify_request import strict_json
ROOT=Path(__file__).resolve().parent
LEDGER=strict_json((ROOT/'coverage-ledger.json').read_text())
SDK='10.0.114'
TRANSITIVE_DEBUG={
 'test/SharpLink.MultiClusterTest.Contracts/SharpLink.MultiClusterTest.Contracts.csproj',
 'test/SharpLink.StaticCodecOwnerTest.Contracts/SharpLink.StaticCodecOwnerTest.Contracts.csproj',
 'test/SharpLink.ReadOwnershipAllocationProbe/SharpLink.ReadOwnershipAllocationProbe.csproj',
}
COPY_PROJECT='test/SharpLink.Generator.Tests/SharpLink.Generator.Tests.csproj'
COPY_SOURCE='src/SharpLink.Abstractions/Sdk/SharpLinkMetadata.cs'
COPY_INCLUDE='../../'+COPY_SOURCE
COPY_NAME='SharpLinkMetadata.source.cs'
# Official TUnit v1.73.19 (ff2dc518...) packs this props/source pair under buildTransitive/<TFM>.
PACKAGE_ID='TUnit.Core/1.73.19'
PACKAGE_PATH='tunit.core/1.73.19'
PACKAGE_MEMBER='buildTransitive/net10.0/TUnit.Core.GeneratedNamespace.cs'
PACKAGE_PROPS='buildTransitive/net10.0/TUnit.Core.props'
PACKAGE_KEY='nuget:'+PACKAGE_ID+'/'+PACKAGE_MEMBER
def sha(path):
 with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
def write(path,value):
 with path.open('x') as stream:json.dump(value,stream,indent=2,allow_nan=False);stream.write('\n')
def regular(path):
 if not path.is_file() or path.is_symlink() or path.resolve()!=path.absolute():raise ValueError('Missing or symlinked evidence file: '+str(path))
 return path

def validate_ledger(source,tracked):
 projects=LEDGER['projects'];counts=collections.Counter(p['scope'] for p in projects)
 if counts!={'direct':64,'transitive':3,'unreached':30} or len({p['path'] for p in projects})!=97 or sum(p['production'] for p in projects)!=10:raise ValueError('Frozen project ledger cardinality changed')
 for p in projects:
  if tracked.get(p['path'])!=p['sourceSha256'] or sha(source/p['path'])!=p['sourceSha256']:raise ValueError('Project source differs from ledger')
 if {p['path'] for p in projects if p['scope']=='transitive'}!=TRANSITIVE_DEBUG:raise ValueError('Frozen transitive project mapping changed')
 if sha(source/'Sharplink.slnx')!=LEDGER['solutionSha256']:raise ValueError('Solution differs from ledger')
 expected={x['path']:x['sha256'] for x in LEDGER['trackedCSharp']}
 if expected!={p:h for p,h in tracked.items() if p.endswith('.cs')}:raise ValueError('Tracked C# baseline differs from frozen ledger')
 return projects

def project_for_generated(relative,projects):
 path=PurePosixPath(relative)
 matches=[p for p in projects if p['scope']!='unreached' and path.is_relative_to(PurePosixPath(p['path']).parent/'obj')]
 if len(matches)!=1:raise ValueError('Generated input outside one built project obj boundary: '+relative)
 return matches[0]['path']

def xml_root(data):
 if b'<!DOCTYPE' in data.upper() or b'<!ENTITY' in data.upper():raise ValueError('DTD/entity is not allowed in evidence XML')
 return ET.fromstring(data)

def project_configuration(project):
 if project['scope']=='direct':return 'Release'
 if project['scope']=='transitive' and project['path'] in TRANSITIVE_DEBUG:return 'Debug'
 raise ValueError('Project has no frozen configuration mapping: '+project['path'])

def copied_content(source,tracked,projects):
 owners=[p for p in projects if p['path']==COPY_PROJECT and p['scope']!='unreached']
 if not owners:return {}
 if len(owners)!=1:raise ValueError('Ambiguous declared copy project')
 project=owners[0];csproj=regular(source/COPY_PROJECT)
 if tracked.get(COPY_PROJECT)!=sha(csproj):raise ValueError('Declared copy project changed')
 root=xml_root(csproj.read_bytes());matches=[]
 for group in root:
  for item in group:
   if item.tag=='None' and item.get('Link')==COPY_NAME:
    if group.get('Condition') or item.attrib!={'Include':COPY_INCLUDE,'Link':COPY_NAME,'CopyToOutputDirectory':'PreserveNewest'}:raise ValueError('Declared content copy mapping changed')
    matches.append(item)
 if len(matches)!=1:raise ValueError('Missing/ambiguous declared content copy')
 origin=regular(source/COPY_SOURCE)
 if tracked.get(COPY_SOURCE)!=sha(origin):raise ValueError('Declared content copy source changed')
 target=str(PurePosixPath(COPY_PROJECT).parent/'bin'/project_configuration(project)/'net10.0'/COPY_NAME)
 return {target:{'kind':'copied-tracked-content','owningProject':COPY_PROJECT,'originalTrackedPath':COPY_SOURCE,'originalTrackedSha256':tracked[COPY_SOURCE],'declarationProjectSha256':sha(csproj),'declarationItemType':'None','copyToOutputDirectory':'PreserveNewest','compilerInputProven':False}}

def scan_workspace(source,tracked,projects,problems=None):
 # A supplied problem sink permits partial inventory export without accepting the scan.
 errors=[];found={};globals_found=[]
 try:copies=copied_content(source,tracked,projects)
 except Exception as error:copies={};errors.append(str(error))
 for directory,dirs,files in os.walk(source):
  dirs[:]=sorted(d for d in dirs if d!='.git')
  for name in list(dirs):
   if (Path(directory)/name).is_symlink():errors.append('Symlinked workspace directory: '+str(Path(directory)/name));dirs.remove(name)
  for name in sorted(files):
   p=Path(directory)/name;rel=str(p.relative_to(source))
   if name.lower()=='global.json':globals_found.append(rel)
   if not name.lower().endswith('.cs'):continue
   digest=None
   try:
    regular(p);digest=sha(p)
    if rel in tracked:
     if digest!=tracked[rel]:raise ValueError('Tracked C# changed during build: '+rel)
     found[rel]={'sha256':digest,'kind':'tracked'}
    elif rel in copies:
     if digest!=copies[rel]['originalTrackedSha256']:raise ValueError('Declared content copy bytes differ: '+rel)
     found[rel]={'sha256':digest,**copies[rel]}
    else:found[rel]={'sha256':digest,'kind':'generated-build-output','owningProjectByOutputDirectory':project_for_generated(rel,projects),'specificGeneratorProducer':'UNKNOWN_NOT_INFERRED_FROM_DIRECTORY'}
   except Exception as error:
    found[rel]={'kind':'unknown-or-invalid','error':str(error)}
    if digest is not None:found[rel]['sha256']=digest
    errors.append(str(error))
 if set(p for p in tracked if p.endswith('.cs'))-set(found) or sorted(globals_found)!=['global.json']:errors.append('Missing tracked C# or extra global.json')
 if set(copies)-set(found):errors.append('Missing declared content copy output')
 if problems is not None:problems.extend(errors)
 elif errors:raise ValueError('; '.join(errors))
 return found

def source_relative(value,source):
 p=Path(value)
 if not p.is_absolute() or '..' in p.parts or not p.is_relative_to(source):raise ValueError('Input path outside exact source root: '+value)
 relative=str(p.relative_to(source))
 if p.resolve()!=p:raise ValueError('Input path resolves through a symlink')
 return relative

def project_assets(source,project):
 directory=(source/project['path']).parent;path=regular(directory/'obj/project.assets.json');doc=strict_json(path.read_text());meta=doc['project'];restore=meta['restore']
 if restore['projectPath']!=str(source/project['path']) or Path(restore['outputPath']).resolve()!=directory/'obj':raise ValueError('Assets belong to another project')
 frameworks=sorted(meta['frameworks'])
 expected='netstandard2.0' if project['path']=='src/SharpLink.Generator/SharpLink.Generator.csproj' else 'net10.0'
 if frameworks!=[expected] or restore['originalTargetFrameworks']!=[expected]:raise ValueError('Unexpected evaluated target framework')
 return {'path':str(path.relative_to(source)),'sha256':sha(path),'frameworks':frameworks}

def package_digest(path):
 regular(path)
 if path.stat().st_size>128*1024*1024:raise ValueError('Unexpected nupkg size')
 with path.open('rb') as stream:return base64.b64encode(hashlib.file_digest(stream,'sha512').digest()).decode('ascii')

def content_hash(value):
 if not isinstance(value,str):raise ValueError('Missing assets package content hash')
 value=value.removeprefix('sha512-')
 try:decoded=base64.b64decode(value,validate=True)
 except Exception as error:raise ValueError('Malformed assets package content hash') from error
 if len(decoded)!=64:raise ValueError('Invalid assets SHA512 length')
 return value

def package_member_bytes(nupkg):
 with zipfile.ZipFile(regular(nupkg)) as package:
  names=set()
  for item in package.infolist():
   if item.filename in names or '\\' in item.filename or PurePosixPath(item.filename).is_absolute() or '..' in PurePosixPath(item.filename).parts or item.external_attr>>16&0o170000==0o120000:raise ValueError('Unsafe/duplicate nupkg member')
   names.add(item.filename)
  result={}
  for name in (PACKAGE_MEMBER,PACKAGE_PROPS):
   try:item=package.getinfo(name)
   except KeyError as error:raise ValueError('Missing exact nupkg source/props member') from error
   if item.is_dir() or item.file_size>2*1024*1024:raise ValueError('Invalid package source/props member')
   result[name]=package.read(item)
  return result

def package_sources(source,project):
 # Only one concretely observed dependency source is supported. This is not a cache exemption.
 assets=project_assets(source,project);path=source/assets['path'];doc=strict_json(path.read_text());libraries=doc.get('libraries',{})
 ids=[name for name in libraries if name.split('/')[0].lower()=='tunit.core']
 if not ids:return {}
 if ids!=[PACKAGE_ID]:raise ValueError('Unexpected TUnit.Core resolved package identity')
 library=libraries[PACKAGE_ID]
 if library.get('type')!='package' or library.get('path')!=PACKAGE_PATH:raise ValueError('Unexpected TUnit.Core package path/type')
 package_files=library.get('files',[])
 if not isinstance(package_files,list) or any(package_files.count(name)!=1 for name in (PACKAGE_MEMBER,PACKAGE_PROPS)):raise ValueError('Assets do not declare exact TUnit source/props members')
 expected=content_hash(library.get('sha512',library.get('contentHash')))
 if 'contentHash' in library and content_hash(library['contentHash'])!=expected:raise ValueError('Conflicting assets package content hashes')
 selected=[]
 for target,items in doc.get('targets',{}).items():
  if target!='net10.0' and not target.startswith('net10.0/'):continue
  entry=items.get(PACKAGE_ID,{})
  if PACKAGE_PROPS in entry.get('build',{}) or PACKAGE_PROPS in entry.get('buildTransitive',{}):selected.append(target)
 if not selected:raise ValueError('Assets do not select exact TUnit buildTransitive props')
 candidates=[]
 for folder in doc.get('packageFolders',{}):
  root=Path(folder)
  if not root.is_absolute() or '..' in root.parts or root.resolve()!=root:raise ValueError('Unsafe assets package folder')
  package_root=root/PACKAGE_PATH
  if (package_root/'tunit.core.1.73.19.nupkg').exists():candidates.append(package_root)
 if len(candidates)!=1:raise ValueError('Missing/ambiguous actual TUnit nupkg')
 package_root=candidates[0];nupkg=regular(package_root/'tunit.core.1.73.19.nupkg')
 if package_digest(nupkg)!=expected:raise ValueError('Actual TUnit nupkg SHA512 differs from assets content hash')
 members=package_member_bytes(nupkg);props=regular(package_root/PACKAGE_PROPS);materialized=regular(package_root/PACKAGE_MEMBER)
 if props.read_bytes()!=members[PACKAGE_PROPS] or materialized.read_bytes()!=members[PACKAGE_MEMBER]:raise ValueError('Materialized TUnit source/props differs from nupkg member')
 root=xml_root(members[PACKAGE_PROPS]);includes=[]
 for element in root.iter():
  if element.tag.rsplit('}',1)[-1]=='Compile' and element.get('Include')=='$(MSBuildThisFileDirectory)TUnit.Core.GeneratedNamespace.cs':includes.append(element)
 if len(includes)!=1:raise ValueError('Missing/ambiguous exact package Compile Include')
 binding={'canonicalSourceId':PACKAGE_KEY,'kind':'external-package-source','packageId':'TUnit.Core','packageVersion':'1.73.19','packagePath':PACKAGE_PATH,'member':PACKAGE_MEMBER,'memberSha256':hashlib.sha256(members[PACKAGE_MEMBER]).hexdigest(),'materializedSha256':sha(materialized),'nupkgPath':str(nupkg),'nupkgSha256':sha(nupkg),'nupkgSha512':expected,'propsMember':PACKAGE_PROPS,'propsPath':str(props),'propsSha256':sha(props),'compileInclude':'$(MSBuildThisFileDirectory)TUnit.Core.GeneratedNamespace.cs','referencingProjects':[{'project':project['path'],'assetsPath':assets['path'],'assetsSha256':assets['sha256'],'assetsContentHash':expected,'selectedTargets':sorted(selected)}],'sourceBytesExported':False,'perProjectCompilerInputProven':False}
 return {str(materialized):binding}

def merge_package_sources(target,additions):
 for path,binding in additions.items():
  if path not in target:target[path]=binding;continue
  old=target[path]
  if {k:v for k,v in old.items() if k!='referencingProjects'}!={k:v for k,v in binding.items() if k!='referencingProjects'}:raise ValueError('Conflicting external package source provenance')
  for reference in binding['referencingProjects']:
   if reference not in old['referencingProjects']:old['referencingProjects'].append(reference)

def verify_package_source(path,source,binding):
 # Recheck package/materialized/assets bytes at native archive collection, not only build time.
 if binding.get('canonicalSourceId')!=PACKAGE_KEY or binding.get('packageId')!='TUnit.Core' or binding.get('packageVersion')!='1.73.19' or binding.get('member')!=PACKAGE_MEMBER or binding.get('propsMember')!=PACKAGE_PROPS:raise ValueError('Unsupported external package binding')
 materialized=Path(path);nupkg=Path(binding['nupkgPath']);props=Path(binding['propsPath'])
 if materialized!=nupkg.parent/PACKAGE_MEMBER or props!=nupkg.parent/PACKAGE_PROPS or nupkg.name!='tunit.core.1.73.19.nupkg' or not str(nupkg.parent).endswith('/'+PACKAGE_PATH):raise ValueError('External package paths do not match exact members')
 if package_digest(nupkg)!=binding['nupkgSha512'] or sha(nupkg)!=binding['nupkgSha256']:raise ValueError('Package bytes changed after build snapshot')
 members=package_member_bytes(nupkg)
 if regular(materialized).read_bytes()!=members[PACKAGE_MEMBER] or regular(props).read_bytes()!=members[PACKAGE_PROPS] or sha(props)!=binding['propsSha256'] or sha(materialized)!=binding['materializedSha256'] or hashlib.sha256(members[PACKAGE_MEMBER]).hexdigest()!=binding['memberSha256']:raise ValueError('Package member/materialized bytes changed after build snapshot')
 if not binding['referencingProjects']:raise ValueError('No resolved project package provenance')
 for reference in binding['referencingProjects']:
  rel=reference['assetsPath']
  if PurePosixPath(rel).is_absolute() or '..' in PurePosixPath(rel).parts or sha(regular(source/rel))!=reference['assetsSha256'] or reference['assetsContentHash']!=binding['nupkgSha512']:raise ValueError('Package assets provenance changed')
 return materialized.read_bytes()

def archived_source_key(value,source,receipt):
 path=Path(value)
 if not path.is_absolute() or '..' in path.parts or path.resolve()!=path:raise ValueError('Unsafe archived source path')
 if path.is_relative_to(source):return source_relative(value,source)
 key=receipt.get('externalSourcePaths',{}).get(value)
 row=receipt.get('csharpFiles',{}).get(key,{})
 if key!=PACKAGE_KEY or row.get('kind')!='external-package-source' or not value.endswith('/'+PACKAGE_PATH+'/'+PACKAGE_MEMBER) or row.get('packageId')!='TUnit.Core' or row.get('packageVersion')!='1.73.19' or row.get('member')!=PACKAGE_MEMBER:raise ValueError('External source lacks exact native package provenance')
 return key

def build(source,out,restore_outcome,build_outcome):
 original=strict_json((out/'source-identity.json').read_text());tracked=original['sourceFiles'];projects=validate_ledger(source,tracked)
 result={'buildMode':'manual','configuration':'Release','restoreOutcome':restore_outcome,'buildOutcome':build_outcome,'sdkVersion':None,'projects':[],'workspaceCSharp':{},'externalPackageSources':{},'problems':[],
  'limitations':['Output DLL hashes and project.assets metadata identify build artifacts, not a complete Compile item list or proof every compilation unit was extracted.','Output directory ownership does not identify the specific generator that produced a source file.','No tests or product executables are run. No binaries/PDB/database/source archive bytes are exported.']}
 try:
  sdk_receipt=strict_json((out/'process-sdk.json').read_text());lifecycle=strict_json((out/'process-lifecycle.json').read_text());sdk=sdk_receipt.get('capturedOutput','').strip();result['sdkVersion']=sdk
  if sdk!=SDK or lifecycle.get('safeToAnalyze') is not True:raise ValueError('SDK/process lifecycle not established')
 except Exception as error:result['problems'].append(str(error))
 if (restore_outcome,build_outcome)!=('success','success'):result['problems'].append('Restore/build was not successful; partial outputs cannot complete evidence')
 try:result['workspaceCSharp']=scan_workspace(source,tracked,projects,result['problems'])
 except Exception as error:result['problems'].append(str(error))
 for project in projects:
  row={'project':project['path'],'scope':project['scope'],'production':project['production'],'purpose':project['purpose'],'status':'NOT_IN_SOLUTION_CLOSURE' if project['scope']=='unreached' else 'INCOMPLETE'};result['projects'].append(row)
  if project['scope']=='unreached':continue
  try:
   assets=project_assets(source,project);tfm=assets['frameworks'][0];configuration=project_configuration(project);directory=(source/project['path']).parent/'bin'/configuration/tfm;assembly=project['assemblyName']['value']
   if not isinstance(assembly,str) or not re.fullmatch(r'[A-Za-z0-9_.-]+',assembly):raise ValueError('Unknown/unsafe assembly output identity')
   row.update(invocationConfiguration='Release',observedProjectConfiguration=configuration,configurationEvidence='Frozen exact project/output-path mapping; no configuration fallback',assets=assets,targetFramework=tfm,assemblyName=assembly,pdbCodeViewBinding='NOT_CHECKED',compilerInputList='NOT_OBTAINED_FROM_BINARY_METADATA')
   dll=regular(directory/(assembly+'.dll'));row['dll']={'path':str(dll.relative_to(source)),'sha256':sha(dll)}
   pdb=regular(directory/(assembly+'.pdb'));row['pdb']={'path':str(pdb.relative_to(source)),'sha256':sha(pdb)}
   row['status']='PROJECT_OUTPUT_OBSERVED'
  except Exception as error:row['error']=str(error);result['problems'].append(project['path']+': '+str(error))
  try:merge_package_sources(result['externalPackageSources'],package_sources(source,project))
  except Exception as error:row['packageError']=str(error);result['problems'].append(project['path']+': '+str(error))
 result['evidenceStatus']='INCOMPLETE' if result['problems'] else 'BUILD_OUTPUTS_OBSERVED_NOT_SCAN_COMPLETION'
 write(out/'manual-build.json',result)
 if result['problems']:raise ValueError('; '.join(result['problems']))

def archive_manifest(path,source,workspace,external=None,problems=None):
 regular(path);files={};total=0;external=external or {};external_paths={};errors=[]
 with zipfile.ZipFile(path) as archive:
  for item in archive.infolist():
   if item.is_dir() or not item.filename.lower().endswith('.cs'):continue
   try:
    total+=item.file_size
    if item.file_size>32*1024*1024 or total>512*1024*1024:raise ValueError('Unexpected native source archive size')
    name=item.filename
    if '\\' in name or '..' in PurePosixPath(name).parts or item.external_attr>>16&0o170000==0o120000:raise ValueError('Unsafe native archive member')
    absolute=name if name.startswith('/') else '/'+name;native_path=Path(absolute)
    if native_path.is_relative_to(source):
     relative=source_relative(absolute,source)
     if relative not in workspace or workspace[relative].get('kind')=='unknown-or-invalid':raise ValueError('Archive input unobserved/invalid in build workspace')
     row=workspace[relative];actual=regular(source/relative).read_bytes()
     if hashlib.sha256(actual).hexdigest()!=row['sha256']:raise ValueError('Workspace input changed since build snapshot: '+relative)
     extra={};kind=row['kind'];workspace_hash=row['sha256']
    else:
     binding=external.get(absolute)
     if not binding:raise ValueError('Archive external source lacks resolved package provenance: '+absolute)
     actual=verify_package_source(absolute,source,binding);relative=binding['canonicalSourceId'];kind='external-package-source';workspace_hash=binding['materializedSha256'];extra={'packageId':binding['packageId'],'packageVersion':binding['packageVersion'],'member':binding['member'],'memberSha256':binding['memberSha256'],'nupkgSha256':binding['nupkgSha256'],'nupkgSha512':binding['nupkgSha512'],'propsSha256':binding['propsSha256']}
    if relative in files:raise ValueError('Duplicated native archive C# identity')
    data=archive.read(item)
    if data!=actual:
     # Official source archive transcodes UTF-8 BOM only; never permit arbitrary rewriting.
     if actual.startswith(b'\xef\xbb\xbf') and data==actual[3:]:encoding='utf8-bom-removed'
     else:raise ValueError('CodeQL archived input differs from source/build bytes: '+relative)
    else:encoding='byte-identical'
    files[relative]={'archiveSha256':hashlib.sha256(data).hexdigest(),'workspaceSha256':workspace_hash,'bytes':len(data),'contentRelation':encoding,'kind':kind,**extra}
    if kind=='external-package-source':external_paths[absolute]=relative
    if kind=='copied-tracked-content':files[relative].update(compilerInputProven=False,originalTrackedPath=row['originalTrackedPath'],originalTrackedSha256=row['originalTrackedSha256'],declarationItemType='None')
   except Exception as error:errors.append(str(error))
 if not files:errors.append('No verified C# inputs in native CodeQL source archive')
 receipt={'nativeArchiveSha256':sha(path),'archiveBytes':path.stat().st_size,'csharpFiles':files,'externalSourcePaths':external_paths,'csharpCount':len(files),'archiveBytesExported':False,'inventoryStatus':'INCOMPLETE' if errors else 'INPUT_INVENTORY_OBSERVED_NOT_SEMANTIC_COMPLETION','problems':errors,'meaning':'Native source references preserved by extractor, not proof of successful semantic extraction for every file or compilation context; content copies remain non-Compile evidence.'}
 if problems is not None:problems.extend(errors)
 elif errors:raise ValueError('; '.join(errors))
 return receipt

def native_log_receipt(source,db,out):
 logs=list((db/'csharp').rglob('csharp.log'))
 if len(logs)!=1:raise ValueError('Exactly one native C# extractor log required')
 log=regular(logs[0]);contexts={};unsupported=[];errors=[]
 for number,line in enumerate(log.read_text().splitlines(),1):
  match=re.fullmatch(r'\[(\d+)\] (?:\[\d+\] )?\[(INFO|ERROR|WARNING|DEBUG|TRACE)\] (.*)',line)
  relevant=any(token in line for token in ('EXTRACTION ','Current working directory: ','[ERROR]'))
  if not match:
   if relevant:unsupported.append(number)
   continue
  pid=int(match[1]);level=match[2];message=match[3]
  if not relevant:continue
  context=contexts.setdefault(pid,{'pid':pid,'startLines':[],'terminalRecords':[],'cwdRecords':[],'errorLines':[]})
  if level=='ERROR':context['errorLines'].append(number);errors.append(number)
  if message.startswith('EXTRACTION STARTING at '):context['startLines'].append(number)
  elif message.startswith('EXTRACTION SUCCEEDED in '):context['terminalRecords'].append({'line':number,'status':'SUCCEEDED'})
  elif message.startswith('EXTRACTION FAILED with '):context['terminalRecords'].append({'line':number,'status':'FAILED'})
  elif 'EXTRACTION ' in message:unsupported.append(number)
  if 'Current working directory: ' in message:
   value=message.split('Current working directory: ',1)[1];p=Path(value)
   if not p.is_absolute() or not p.is_relative_to(source) or p.resolve()!=p:raise ValueError('Extractor working directory outside exact product')
   context['cwdRecords'].append({'line':number,'path':value})
 for context in contexts.values():
  starts=context['startLines'];ends=context['terminalRecords'];cwds=context['cwdRecords']
  context['complete']=len(starts)==len(ends)==len(cwds)==1 and starts[0]<cwds[0]['line']<ends[0]['line'] and ends[0]['status']=='SUCCEEDED' and not context['errorLines']
 args={str(p.relative_to(db/'csharp')):{'sha256':sha(regular(p)),'bytes':p.stat().st_size} for p in log.parent.glob('csharp.*.txt')}
 receipt={'logPathRelativeToDatabase':str(log.relative_to(db/'csharp')),'logSha256':sha(log),'logBytes':log.stat().st_size,'contexts':list(contexts.values()),'argumentLogHashes':args,'unsupportedRelevantLines':unsupported,'errorLines':errors,'rawLogsOrArgumentsExported':False,'perProjectCompilationContextMapping':'NOT_PROVEN; no PID-to-project or source-archive union equivalence asserted.'}
 write(out/'native-compiler-log.json',receipt)
 if unsupported or errors or not contexts or not args or any(not x['complete'] for x in contexts.values()):raise ValueError('Missing/incomplete/ambiguous/failed native extractor context evidence')
 return receipt

def archive(source,db,out):
 build_doc=strict_json((out/'manual-build.json').read_text());workspace=build_doc['workspaceCSharp'];paths=list((db/'csharp').glob('src.zip'))
 if len(paths)!=1:raise ValueError('Exactly one native csharp/src.zip required')
 problems=[];receipt=archive_manifest(paths[0],source,workspace,build_doc.get('externalPackageSources',{}),problems);write(out/'manual-archive.json',receipt)
 observed=set(receipt['csharpFiles']);tracked={x['path'] for x in LEDGER['trackedCSharp']};projects=build_doc['projects'];rows=[]
 for p in projects:
  prefix=str(PurePosixPath(p['project']).parent)+'/'
  rows.append({'project':p['project'],'scope':p['scope'],'production':p['production'],'buildOutputStatus':p['status'],'archivedSourcePathsUnderProjectDirectory':sorted(n for n in observed if n.startswith(prefix)),'perCompilationSemanticExtraction':'NOT_PROVEN_BY_SOURCE_ARCHIVE_OR_OUTPUT_HASH','projectDirectoryGroupingIsNotCompilerUnitProof':True})
 coverage={'staticLedgerSha256':sha(ROOT/'coverage-ledger.json'),'baselineTrackedCount':1383,'actualArchivedTrackedCount':len(observed&tracked),'actualArchivedGeneratedCount':sum(x['kind']=='generated-build-output' for x in receipt['csharpFiles'].values()),'actualArchivedPackageSourceCount':sum(x['kind']=='external-package-source' for x in receipt['csharpFiles'].values()),'actualArchivedCopiedContentCount':sum(x['kind']=='copied-tracked-content' for x in receipt['csharpFiles'].values()),'trackedNotInNativeArchive':sorted(tracked-observed),'projects':rows,'staticBoundary':LEDGER['counts'],'oldCQL2ErrorCount':438,'oldErrorsOutsideStaticCandidates':LEDGER['oldErrorsOutsideCandidates'],'historicalFailureDisposition':'CQL1, CQL2 and CQL3 remain INCOMPLETE; new source/tool scope cannot erase original diagnostics or fill missing historical bytes.','actualTraceScope':'PENDING_OFFICIAL_COMPILER_AND_CODEQL_LOG_REVIEW','wholeRepositoryEquivalent':False}
 write(out/'manual-coverage.json',coverage)
 if build_doc['evidenceStatus']!='BUILD_OUTPUTS_OBSERVED_NOT_SCAN_COMPLETION':problems.append('Build output evidence incomplete')
 if problems:raise ValueError('; '.join(problems))
 # Every one of the ten production projects needs at least a native tracked source reference;
 # static membership or a copied DLL alone is not scan input evidence.
 for p in projects:
  if p['production']:
   prefix=str(PurePosixPath(p['project']).parent)+'/'
   if not any(name.startswith(prefix) and name in tracked for name in observed):raise ValueError('No native tracked source evidence for production project '+p['project'])
 return receipt

if __name__=='__main__':
 parser=argparse.ArgumentParser();parser.add_argument('mode',choices=['build']);parser.add_argument('--source',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);parser.add_argument('--restore-outcome',required=True);parser.add_argument('--build-outcome',required=True);args=parser.parse_args()
 try:build(args.source.resolve(),args.output,args.restore_outcome,args.build_outcome)
 except Exception as error:
  write(args.output/'incomplete-build.json',{'executionEvidence':'INCOMPLETE','stage':'manual-build','errorType':type(error).__name__,'message':str(error)});raise
