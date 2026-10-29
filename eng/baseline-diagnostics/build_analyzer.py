#!/usr/bin/env python3
"""Build and synthetic parser checks only; never launches a target or trace session."""
import argparse
import os
from pathlib import Path
import subprocess
from common import ROOT, REPO, CONTRACT, strict_json, write, inventory, source_inventory

def main():
    p=argparse.ArgumentParser()
    for name in ('dotnet','output'): p.add_argument('--'+name,type=Path,required=True)
    a=p.parse_args(); output=a.output.resolve(); output.mkdir(parents=True,exist_ok=False)
    if subprocess.check_output([a.dotnet,'--version'],text=True).strip()!=CONTRACT['sdk']: raise ValueError('SDK')
    env=dict(os.environ,DOTNET_CLI_HOME=str(output/'cli-home'),NUGET_HTTP_CACHE_PATH=str(output/'http-cache'),
             NUGET_SCRATCH=str(output/'scratch'),DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER='1')
    build=output/'bin'
    with (output/'build.log').open('xb') as log:
        subprocess.run([str(a.dotnet),'build',str(ROOT/'analyzer/BaselineDiagnostics.Analyzer.csproj'),
            '-c','Release','-m:1','-nr:false','--disable-build-servers','-p:UseSharedCompilation=false',
            '-p:RestoreLockedMode=true','-p:NuGetAudit=false','-o',str(build)],env=env,
            stdout=log,stderr=subprocess.STDOUT,check=True,timeout=600)
    with (output/'self-test.log').open('xb') as log:
        subprocess.run([str(a.dotnet),str(build/'BaselineDiagnostics.Analyzer.dll'),'--self-test'],env=env,
            stdout=log,stderr=subprocess.STDOUT,check=True,timeout=60)
    config=build/'BaselineDiagnostics.Analyzer.runtimeconfig.json'; data=strict_json(config.read_text())
    data['runtimeOptions']['framework']['version']=CONTRACT['runtime'];data['runtimeOptions']['rollForward']='Disable';write(config,data)
    marker=output/'marker-bin'
    with (output/'marker-build.log').open('xb') as log:
        subprocess.run([str(a.dotnet),'build',str(ROOT/'marker_smoke/MarkerSmoke.csproj'),
            '-c','Release','-m:1','-nr:false','--disable-build-servers','-p:UseSharedCompilation=false',
            '-p:NuGetAudit=false','-o',str(marker)],env=env,
            stdout=log,stderr=subprocess.STDOUT,check=True,timeout=600)
    config=marker/'MarkerSmoke.runtimeconfig.json'; data=strict_json(config.read_text())
    data['runtimeOptions']['framework']['version']=CONTRACT['runtime'];data['runtimeOptions']['rollForward']='Disable';write(config,data)
    sha=subprocess.check_output(['git','rev-parse','HEAD'],cwd=REPO,text=True).strip()
    write(output/'analyzer-manifest.json',dict(toolRevision=sha,toolSourceHashes=source_inventory(),snapshot=inventory(build),markerSnapshot=inventory(marker)))
    print('ANALYZER_BUILD_SELFTEST_PASS; synthetic-only; no target execution')

if __name__=='__main__': main()
