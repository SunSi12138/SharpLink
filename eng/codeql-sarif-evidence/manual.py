#!/usr/bin/env python3
"""Observe standard manual-build outputs and native CodeQL sources; never run product code."""
import argparse, collections, hashlib, json, os, re, subprocess, zipfile
from pathlib import Path, PurePosixPath
from urllib.parse import unquote
from verify_request import strict_json
ROOT=Path(__file__).resolve().parent
LEDGER=strict_json((ROOT/'coverage-ledger.json').read_text())
SDK='10.0.114'
def sha(path):
 with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
def write(path,value):
 with path.open('x') as stream:json.dump(value,stream,indent=2,allow_nan=False);stream.write('\n')
def regular(path):
 if not path.is_file() or path.is_symlink():raise ValueError('Missing or symlinked evidence file: '+str(path))
 return path

def validate_ledger(source,tracked):
 projects=LEDGER['projects'];counts=collections.Counter(p['scope'] for p in projects)
 if counts!={'direct':64,'transitive':3,'unreached':30} or len({p['path'] for p in projects})!=97 or sum(p['production'] for p in projects)!=10:raise ValueError('Frozen project ledger cardinality changed')
 for p in projects:
  if tracked.get(p['path'])!=p['sourceSha256'] or sha(source/p['path'])!=p['sourceSha256']:raise ValueError('Project source differs from ledger')
 if sha(source/'Sharplink.slnx')!=LEDGER['solutionSha256']:raise ValueError('Solution differs from ledger')
 expected={x['path']:x['sha256'] for x in LEDGER['trackedCSharp']}
 if expected!={p:h for p,h in tracked.items() if p.endswith('.cs')}:raise ValueError('Tracked C# baseline differs from frozen ledger')
 return projects

def project_for_generated(relative,projects):
 path=PurePosixPath(relative)
 matches=[p for p in projects if p['scope']!='unreached' and path.is_relative_to(PurePosixPath(p['path']).parent/'obj')]
 if len(matches)!=1:raise ValueError('Generated input outside one built project obj boundary: '+relative)
 return matches[0]['path']

def scan_workspace(source,tracked,projects):
 found={};globals_found=[]
 for directory,dirs,files in os.walk(source):
  dirs[:]=[d for d in dirs if d!='.git']
  if any((Path(directory)/d).is_symlink() for d in dirs):raise ValueError('Symlinked workspace directory')
  for name in files:
   p=Path(directory)/name;rel=str(p.relative_to(source))
   if name.lower()=='global.json':globals_found.append(rel)
   if name.lower().endswith('.cs'):
    regular(p)
    if rel in tracked:
     if sha(p)!=tracked[rel]:raise ValueError('Tracked C# changed during build')
     found[rel]={'sha256':sha(p),'kind':'tracked'}
    else:found[rel]={'sha256':sha(p),'kind':'generated-build-output','owningProjectByOutputDirectory':project_for_generated(rel,projects),'specificGeneratorProducer':'UNKNOWN_NOT_INFERRED_FROM_DIRECTORY'}
 if set(p for p in tracked if p.endswith('.cs'))-set(found) or globals_found!=['global.json']:raise ValueError('Missing tracked C# or extra global.json')
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

def build(source,out,restore_outcome,build_outcome):
 original=strict_json((out/'source-identity.json').read_text());tracked=original['sourceFiles'];projects=validate_ledger(source,tracked)
 result={'buildMode':'manual','configuration':'Release','restoreOutcome':restore_outcome,'buildOutcome':build_outcome,'sdkVersion':None,'projects':[],'workspaceCSharp':{},'problems':[],
  'limitations':['Output DLL hashes and project.assets metadata identify build artifacts, not a complete Compile item list or proof every compilation unit was extracted.','Output directory ownership does not identify the specific generator that produced a source file.','No tests or product executables are run. No binaries/PDB/database/source archive bytes are exported.']}
 try:
  sdk_receipt=strict_json((out/'process-sdk.json').read_text());lifecycle=strict_json((out/'process-lifecycle.json').read_text());sdk=sdk_receipt.get('capturedOutput','').strip();result['sdkVersion']=sdk
  if sdk!=SDK or lifecycle.get('safeToAnalyze') is not True:raise ValueError('SDK/process lifecycle not established')
 except Exception as error:result['problems'].append(str(error))
 if (restore_outcome,build_outcome)!=('success','success'):result['problems'].append('Restore/build was not successful; partial outputs cannot complete evidence')
 try:result['workspaceCSharp']=scan_workspace(source,tracked,projects)
 except Exception as error:result['problems'].append(str(error))
 for project in projects:
  row={'project':project['path'],'scope':project['scope'],'production':project['production'],'purpose':project['purpose'],'status':'NOT_IN_SOLUTION_CLOSURE' if project['scope']=='unreached' else 'INCOMPLETE'};result['projects'].append(row)
  if project['scope']=='unreached':continue
  try:
   assets=project_assets(source,project);tfm=assets['frameworks'][0];directory=(source/project['path']).parent/'bin/Release'/tfm;assembly=project['assemblyName']['value'];dll=regular(directory/(assembly+'.dll'));pdb=regular(directory/(assembly+'.pdb'))
   row.update(status='PROJECT_OUTPUT_OBSERVED',assets=assets,targetFramework=tfm,assemblyName=assembly,dll={'path':str(dll.relative_to(source)),'sha256':sha(dll)},pdb={'path':str(pdb.relative_to(source)),'sha256':sha(pdb)},pdbCodeViewBinding='NOT_CHECKED',compilerInputList='NOT_OBTAINED_FROM_BINARY_METADATA')
  except Exception as error:row['error']=str(error);result['problems'].append(project['path']+': '+str(error))
 result['evidenceStatus']='INCOMPLETE' if result['problems'] else 'BUILD_OUTPUTS_OBSERVED_NOT_SCAN_COMPLETION'
 write(out/'manual-build.json',result)
 if result['problems']:raise ValueError('; '.join(result['problems']))

def archive_manifest(path,source,workspace):
 regular(path);files={};total=0
 with zipfile.ZipFile(path) as archive:
  for item in archive.infolist():
   if item.is_dir() or not item.filename.lower().endswith('.cs'):continue
   total+=item.file_size
   if item.file_size>32*1024*1024 or total>512*1024*1024:raise ValueError('Unexpected native source archive size')
   name=item.filename
   if '\\' in name or '..' in PurePosixPath(name).parts or item.external_attr>>16&0o170000==0o120000:raise ValueError('Unsafe native archive member')
   # Native source archives preserve absolute source paths without the initial slash.
   absolute=name if name.startswith('/') else '/'+name;relative=source_relative(absolute,source)
   if relative not in workspace or relative in files:raise ValueError('Archive input unobserved/duplicated/outside build workspace')
   data=archive.read(item);actual=regular(source/relative).read_bytes()
   if data!=actual:
    # The official archive format transcodes source to UTF-8; permit only BOM removal,
    # not newline or other content rewriting. Preserve both byte hashes separately.
    if actual.startswith(b'\xef\xbb\xbf') and data==actual[3:]:encoding='utf8-bom-removed'
    else:raise ValueError('CodeQL archived input differs from source/build bytes: '+relative)
   else:encoding='byte-identical'
   files[relative]={'archiveSha256':hashlib.sha256(data).hexdigest(),'workspaceSha256':workspace[relative]['sha256'],'bytes':len(data),'contentRelation':encoding,'kind':workspace[relative]['kind']}
 if not files:raise ValueError('No C# inputs in native CodeQL source archive')
 return {'nativeArchiveSha256':sha(path),'archiveBytes':path.stat().st_size,'csharpFiles':files,'csharpCount':len(files),'archiveBytesExported':False,'meaning':'Native source references preserved by extractor, not proof of successful semantic extraction for every file or compilation context.'}

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
 receipt=archive_manifest(paths[0],source,workspace);write(out/'manual-archive.json',receipt)
 observed=set(receipt['csharpFiles']);tracked={x['path'] for x in LEDGER['trackedCSharp']};projects=build_doc['projects'];rows=[]
 for p in projects:
  prefix=str(PurePosixPath(p['project']).parent)+'/'
  rows.append({'project':p['project'],'scope':p['scope'],'production':p['production'],'buildOutputStatus':p['status'],'archivedSourcePathsUnderProjectDirectory':sorted(n for n in observed if n.startswith(prefix)),'perCompilationSemanticExtraction':'NOT_PROVEN_BY_SOURCE_ARCHIVE_OR_OUTPUT_HASH','projectDirectoryGroupingIsNotCompilerUnitProof':True})
 coverage={'staticLedgerSha256':sha(ROOT/'coverage-ledger.json'),'baselineTrackedCount':1383,'actualArchivedTrackedCount':len(observed&tracked),'actualArchivedGeneratedCount':len(observed-tracked),'trackedNotInNativeArchive':sorted(tracked-observed),'projects':rows,'staticBoundary':LEDGER['counts'],'oldCQL2ErrorCount':438,'oldErrorsOutsideStaticCandidates':LEDGER['oldErrorsOutsideCandidates'],'historicalFailureDisposition':'CQL1 and CQL2 remain INCOMPLETE; different manual scope cannot erase diagnostics.','actualTraceScope':'PENDING_OFFICIAL_COMPILER_AND_CODEQL_LOG_REVIEW','wholeRepositoryEquivalent':False}
 write(out/'manual-coverage.json',coverage)
 if build_doc['evidenceStatus']!='BUILD_OUTPUTS_OBSERVED_NOT_SCAN_COMPLETION':raise ValueError('Build output evidence incomplete')
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
