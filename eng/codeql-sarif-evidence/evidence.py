#!/usr/bin/env python3
"""Preserve and validate one exact-source SARIF export, never assert GitHub alert closure."""
import argparse, collections, hashlib, json, os, re, shutil, subprocess
from decimal import Decimal,InvalidOperation
from pathlib import Path
from verify_request import strict_json
SOURCE='c22b945f71c075c878e93a7c92bf4e45a8d33a6f'
TREE='12b88cb38821e2c40bd1fb209a6244396bed52ba'
ACTION='9b3254085494b55a0f7c4c2b3d2d52cc712ce370'
BUNDLE_SHA='64518dc413c8e7b5733fee2489e830a899bb1e3f28c8873b5869d5cc90164c5c'
ROOT=Path(__file__).resolve().parent
EXPECTED=strict_json((ROOT/'expected-queries.json').read_text())
if len(EXPECTED['queries'])!=59 or len(set(EXPECTED['queries']))!=59:raise ValueError('Frozen query set must contain exactly59 unique paths')
def sha(path):
 with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
def write(path,data):
 with path.open('x') as stream:json.dump(data,stream,indent=2,allow_nan=False);stream.write('\n')
def git(root,*args):return subprocess.check_output(['git',*args],cwd=root,text=True,timeout=30).strip()
def source_identity(source):
 if git(source,'rev-parse','HEAD')!=SOURCE or git(source,'rev-parse','HEAD^{tree}')!=TREE or git(source,'status','--porcelain'):raise ValueError('Source must be exact clean c22/tree12b88')
 files=git(source,'ls-files','-z').split('\0');manifest={p:sha(source/p) for p in files if p}
 if not manifest or not any(p.endswith('.cs') for p in manifest):raise ValueError('Empty C# source inventory')
 return manifest

def validate_environment(environment):
 allowed={'RUNNER_DEBUG':{'','0'},'CODEQL_ACTION_DEBUG_COMBINED_SARIF':{'','false'},'CODEQL_OVERLAY_DATABASE_MODE':{'','none'},'CODE_SCANNING_IS_ANALYZING_DEFAULT_BRANCH':{'','false'},'CODEQL_ACTION_TEMP':{'',environment.get('RUNNER_TEMP','')}}
 for key,values in allowed.items():
  if environment.get(key,'') not in values:raise ValueError('Forbidden debug/overlay/temp override: '+key)
 return {key:environment.get(key,'') for key in allowed}

def effective_config(config,temp,source,db):
 # The raw action configuration is read locally, never copied into the artifact.
 expected={'languages':['csharp'],'analysisKinds':['code-scanning'],'buildMode':'none','debugMode':False,'overlayDatabaseMode':'none','useOverlayDatabaseCaching':False,'trapCaches':{},'dependencyCachingEnabled':'none','dependencyCachingRestoredKeys':[],'extraQueryExclusions':[]}
 for key,value in expected.items():
  if key not in config or config[key]!=value or (isinstance(value,bool) and type(config[key]) is not bool):raise ValueError('Effective CodeQL scope/caching changed: '+key)
 if config.get('repositoryRoot')!=str(source.resolve()) or config.get('dbLocation')!=str(db.resolve()):raise ValueError('Effective source/database root mismatch')
 if config.get('originalUserInput')!={}:raise ValueError('Unexpected user configuration')
 # This pinned action removes empty query/pack fields from its default configuration.
 # Reject model packs, threat-model overrides, and unknown fields as well as filters.
 if config.get('computedConfig')!={}:raise ValueError('Effective default configuration changed')
 if (temp/'pr-diff-range.json').exists():raise ValueError('Diff-informed source filtering is forbidden')
 return {**expected,'repositoryRoot':config['repositoryRoot'],'dbLocation':config['dbLocation'],'userConfigurationAbsent':True,'effectiveQueryOrPathCustomizationAbsent':True,'diffRangeFileAbsent':True}

def config_receipt(source,db,out,stage):
 validate_environment(os.environ)
 temp=Path(os.environ['RUNNER_TEMP']);path=temp/'config'
 config=strict_json(path.read_text());receipt=effective_config(config,temp,source,db);receipt['actionConfigSha256']=sha(path)
 write(out/('effective-scope-'+stage+'.json'),receipt)

def before(source,out):
 environment_guard=validate_environment(os.environ)
 identity={'status':'SOURCE_BOUND_ANALYSIS_NOT_YET_COMPLETE','environmentGuard':environment_guard,'sourceCommit':SOURCE,'sourceTree':TREE,'sourceRoot':str(source.resolve()),'sourceFiles':source_identity(source),'requestCommit':git(ROOT,'rev-parse','HEAD'),'requestTree':git(ROOT,'rev-parse','HEAD^{tree}'),'toolCommit':strict_json((out/'request.json').read_text())['reviewedToolCommit'],'toolTree':strict_json((out/'request.json').read_text())['reviewedToolTree'],'actionPin':ACTION,'language':'csharp','buildMode':'none','category':'/language:csharp','querySelection':'unchanged default','originalRun':41026509659,'runId':os.environ.get('GITHUB_RUN_ID'),'runAttempt':os.environ.get('GITHUB_RUN_ATTEMPT'),'scope':'Fresh full-source scan output only; not the historical GitHub open/dismissed alert inventory.'}
 write(out/'source-identity.json',identity)

def capture_toolchain(codeql,bundle,out):
 if sha(bundle)!=BUNDLE_SHA:raise ValueError('Official bundle bytes do not match frozen SHA256')
 result=subprocess.run([str(codeql),'version','--format=json'],capture_output=True,text=True,check=True,timeout=30)
 version=strict_json(result.stdout)
 if version.get('version')!='2.27.3':raise ValueError('Actual CLI version changed')
 pack=codeql.parent/'qlpacks/codeql/csharp-queries/1.10.2'
 manifest=pack/'qlpack.yml' if (pack/'qlpack.yml').is_file() else pack/'codeql-pack.yml'
 content=manifest.read_text()
 for key,value in [('name','codeql/csharp-queries'),('version','1.10.2')]:
  match=re.findall(r'^'+key+r':\s*(.*?)\s*$',content,re.M)
  if len(match)!=1 or match[0] not in (value,chr(34)+value+chr(34),chr(39)+value+chr(39)):raise ValueError('Actual query pack name/version changed')
 query_files={}
 for relative in EXPECTED['queries']:
  candidates=[pack/(relative+ext) for ext in ('.ql','.qlx')]
  files=[p for p in candidates if p.is_file()]
  if not files:raise ValueError('Expected query missing from exact pack: '+relative)
  for p in files:query_files[str(p.relative_to(pack))]=sha(p)
 write(out/'toolchain.json',{'actionPin':ACTION,'bundleSha256':sha(bundle),'codeqlPath':str(codeql),'codeqlBinarySha256':sha(codeql),'actualVersion':version,'queryPack':'codeql/csharp-queries','queryPackVersion':'1.10.2','queryPackManifestSha256':sha(manifest),'queryFileHashes':query_files,'expectedQueryCount':59,'expectedQuerySetSha256':sha(ROOT/'expected-queries.json')})

def rule_entries(run):
 driver=run['tool']['driver'];components=[driver]+run['tool'].get('extensions',[]);entries=[]
 for component_index,component in enumerate(components):
  for rule_index,rule in enumerate(component.get('rules',[])):
   if not isinstance(rule.get('id'),str) or not rule['id']:raise ValueError('Malformed rule identity')
   entries.append({'componentIndex':component_index-1,'componentName':component.get('name'),'componentGuid':component.get('guid'),'ruleIndex':rule_index,'rule':rule})
 if not entries:raise ValueError('No rule metadata')
 return entries

def resolve_rule(result,entries):
 reference=result.get('rule',{});ident=result.get('ruleId',reference.get('id'));index=result.get('ruleIndex',reference.get('index'));component_ref=reference.get('toolComponent',{})
 if not isinstance(component_ref,dict) or set(component_ref)-{'index','name','guid'}:raise ValueError('Unknown tool component reference cannot be resolved safely')
 component=component_ref.get('index',None if component_ref else -1)
 if 'index' in component_ref and (type(component) is not int or component<0):raise ValueError('Invalid tool component index')
 if index is not None and (type(index) is not int or index<0):raise ValueError('Invalid finding rule index')
 matches=[e for e in entries if (component is None or e['componentIndex']==component) and (index is None or e['ruleIndex']==index) and (ident is None or e['rule']['id']==ident) and ('name' not in component_ref or component_ref['name']==e['componentName']) and ('guid' not in component_ref or component_ref['guid']==e['componentGuid'])]
 if ident is None and index is None or len(matches)!=1:raise ValueError('Finding has unresolved/ambiguous rule')
 entry=matches[0]
 if result.get('ruleId') is not None and reference.get('id') is not None and result['ruleId']!=reference['id']:raise ValueError('Conflicting finding rule IDs')
 if result.get('ruleIndex') is not None and reference.get('index') is not None and result['ruleIndex']!=reference['index']:raise ValueError('Conflicting finding rule indices')
 return entry

def assess_sarif(doc):
 if doc.get('version')!='2.1.0' or not isinstance(doc.get('runs'),list) or len(doc['runs'])!=1:raise ValueError('Expected exactly one complete SARIF2.1.0 run')
 run=doc['runs'][0];driver=run['tool']['driver']
 if driver.get('name')!='CodeQL' or driver.get('semanticVersion',driver.get('version'))!='2.27.3':raise ValueError('SARIF producer/version mismatch')
 category=run.get('automationDetails',{}).get('id')
 if category not in ('/language:csharp','/language:csharp/'):raise ValueError('SARIF category changed/missing')
 invocations=run.get('invocations')
 if not isinstance(invocations,list) or not invocations:raise ValueError('Missing SARIF invocation')
 execution_errors=['Unsuccessful or unknown invocation'] if any(x.get('executionSuccessful') is not True for x in invocations) else []
 notifications=[]
 for invocation in invocations:
  for kind in ('toolExecutionNotifications','toolConfigurationNotifications'):
   for n in invocation.get(kind,[]):notifications.append({'kind':kind,'notification':n})
 if any(x['notification'].get('level')=='error' for x in notifications):execution_errors.append('Tool execution/configuration error')
 entries=rule_entries(run)
 if not isinstance(run.get('results'),list):raise ValueError('Missing results array is not a zero-finding scan')
 findings=[];counts=collections.Counter();severity_counts=collections.Counter()
 for index,result in enumerate(run['results']):
  entry=resolve_rule(result,entries);rule=entry['rule'];level=result.get('level',rule.get('defaultConfiguration',{}).get('level','warning'));score=rule.get('properties',{}).get('security-severity');severity=None
  if level not in ('none','note','warning','error'):raise ValueError('Invalid finding level')
  if score is not None:
   try:value=Decimal(str(score))
   except InvalidOperation:raise ValueError('Invalid security-severity metadata')
   if not value.is_finite() or value<0 or value>10:raise ValueError('Out-of-range security-severity metadata')
   severity='critical' if value>=9 else 'high' if value>=7 else 'medium' if value>=4 else 'low'
  counts[level]+=1;severity_counts[severity or 'not-provided']+=1
  findings.append({'resultIndex':index,'ruleId':rule['id'],'ruleComponentIndex':entry['componentIndex'],'ruleIndex':entry['ruleIndex'],'security-severity':score,'securitySeverityBand':severity,'reportedLevel':result.get('level'),'effectiveLevel':level,'suppressions':result.get('suppressions'),'baselineState':result.get('baselineState'),'rule':rule,'originalResult':result})
 return {'executionEvidence':'INCOMPLETE' if execution_errors else 'COMPLETE','executionErrors':execution_errors,'securityDecision':'NOT_ASSESSED_REQUIRES_INDEPENDENT_REVIEW','historicalGitHubAlertDisposition':'NOT_OBTAINED','findingCount':len(findings),'findingLevelCounts':dict(counts),'securitySeverityCounts':dict(severity_counts),'findings':findings,'rules':entries,'notifications':notifications,'sarifVersionControlProvenance':run.get('versionControlProvenance'),'coverageBoundary':'C# build-mode none default-query scan; completed execution does not prove every source file semantically extracted or absence of all vulnerabilities.'}

def collect(source,db,sarif_root,out,init_outcome,analyze_outcome):
 # Raw output is retained before any completeness checks, including failed action outcomes.
 write(out/'step-outcomes.json',{'init':init_outcome,'analyze':analyze_outcome})
 sarifs=list(sarif_root.glob('*.sarif'))
 target=sarif_root/'csharp.sarif'
 if target.is_file() and not target.is_symlink():shutil.copyfile(target,out/'csharp.sarif')
 if len(sarifs)!=1 or sarifs[0].name!='csharp.sarif' or sarifs[0].is_symlink():raise ValueError('Missing/extra SARIF output; any valid csharp raw file retained')
 summary=assess_sarif(strict_json((out/'csharp.sarif').read_text()))
 sarif_complete=summary['executionEvidence']=='COMPLETE'
 if sarif_complete:summary['executionEvidence']='SARIF_VALIDATED_CAMPAIGN_NOT_YET_COMPLETE'
 summary['sourceCommit']=SOURCE;summary['sourceTree']=TREE;summary['sarifSha256']=sha(out/'csharp.sarif')
 write(out/'findings.json',summary)
 if not sarif_complete:raise ValueError('SARIF execution/configuration errors: findings retained, execution INCOMPLETE')
 if init_outcome!='success' or analyze_outcome!='success':raise ValueError('CodeQL action did not complete successfully')
 config_receipt(source,db,out,'after')
 original=strict_json((out/'source-identity.json').read_text())
 if original['sourceFiles']!=source_identity(source):raise ValueError('Source changed during analysis')
 toolchain=strict_json((out/'toolchain.json').read_text())
 if toolchain['actualVersion']['version']!='2.27.3' or toolchain['bundleSha256']!=BUNDLE_SHA or toolchain['queryPackVersion']!='1.10.2':raise ValueError('Frozen toolchain missing/changed')
 results=db/'csharp/results/codeql/csharp-queries';actual={str(p.relative_to(results))[:-5]:{'sha256':sha(p),'bytes':p.stat().st_size} for p in results.rglob('*.bqrs') if p.is_file() and not p.is_symlink()}
 if set(actual)!=set(EXPECTED['queries']) or any(v['bytes']==0 for v in actual.values()):raise ValueError('Actual executed query result set differs from original59 queries')
 write(out/'query-result-hashes.json',{'originalRun':41026509659,'queryPack':'codeql/csharp-queries','queryPackVersion':'1.10.2','queryCount':len(actual),'results':actual,'noDatabaseBytesExported':True})
 write(out/'completion.json',{'executionEvidence':'COMPLETE','securityDecision':'NOT_ASSESSED_REQUIRES_INDEPENDENT_REVIEW','sourceCommit':SOURCE,'sourceTree':TREE,'sourceFileCount':len(original['sourceFiles']),'sarifSha256':sha(out/'csharp.sarif'),'findingCount':summary['findingCount'],'findingLevelCounts':summary['findingLevelCounts'],'securitySeverityCounts':summary['securitySeverityCounts']})
 print(json.dumps({'executionEvidence':'COMPLETE','securityDecision':summary['securityDecision'],'findingCount':summary['findingCount'],'findingLevelCounts':summary['findingLevelCounts'],'securitySeverityCounts':summary['securitySeverityCounts']}))

ALLOWED={'effective-scope-before.json','effective-scope-after.json','request.json','source-identity.json','toolchain.json','step-outcomes.json','query-result-hashes.json','csharp.sarif','findings.json','completion.json','incomplete-before.json','incomplete-toolchain.json','incomplete-collect.json'}
def finalize(out):
 files=list(out.iterdir())
 if any(p.name not in ALLOWED or p.is_symlink() or not p.is_file() for p in files):raise ValueError('Unexpected output: refusing broad artifact export')
 required={'request.json','source-identity.json','toolchain.json','effective-scope-before.json','effective-scope-after.json','step-outcomes.json','query-result-hashes.json','csharp.sarif','findings.json','completion.json'}
 complete=required<={p.name for p in files} and not any(p.name.startswith('incomplete-') for p in files)
 if complete:
  completion=strict_json((out/'completion.json').read_text())
  complete=completion.get('executionEvidence')=='COMPLETE' and completion.get('securityDecision')=='NOT_ASSESSED_REQUIRES_INDEPENDENT_REVIEW' and completion.get('sarifSha256')==sha(out/'csharp.sarif')
 write(out/'artifact-hashes.json',{'executionEvidence':'COMPLETE' if complete else 'INCOMPLETE','securityDecision':'NOT_ASSESSED_REQUIRES_INDEPENDENT_REVIEW','files':{p.name:{'sha256':sha(p),'bytes':p.stat().st_size}for p in sorted(files)}})
 if not complete:raise ValueError('Missing campaign completion: retained partial evidence is INCOMPLETE')

def main():
 p=argparse.ArgumentParser();p.add_argument('mode',choices=['before','toolchain','collect','finalize']);p.add_argument('--source',type=Path);p.add_argument('--output',type=Path,required=True);p.add_argument('--codeql',type=Path);p.add_argument('--bundle',type=Path);p.add_argument('--db',type=Path);p.add_argument('--sarif',type=Path);p.add_argument('--init-outcome');p.add_argument('--analyze-outcome');a=p.parse_args()
 try:
  if a.mode=='before':before(a.source.resolve(),a.output)
  elif a.mode=='toolchain':
   config_receipt(a.source.resolve(),a.db,a.output,'before')
   capture_toolchain(a.codeql.resolve(),a.bundle,a.output)
  elif a.mode=='collect':collect(a.source.resolve(),a.db,a.sarif,a.output,a.init_outcome,a.analyze_outcome)
  else:finalize(a.output)
 except Exception as error:
  path=a.output/('incomplete-'+a.mode+'.json')
  if a.mode!='finalize' and not path.exists():write(path,{'executionEvidence':'INCOMPLETE','securityDecision':'NOT_ASSESSED','stage':a.mode,'errorType':type(error).__name__,'message':str(error)})
  raise
if __name__=='__main__':main()
