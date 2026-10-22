#!/usr/bin/env python3
"""Record selected build binaries, not an environment dump."""
import hashlib,json,shutil,subprocess
from pathlib import Path
root=Path(__file__).resolve().parent
host=Path(shutil.which('dotnet')).resolve();version=subprocess.check_output([str(host),'--version'],text=True,cwd=root).strip()
assert version=='10.0.102',version
properties=json.loads(subprocess.check_output([str(host),'msbuild',str(root/'probe/HotCostProbe.csproj'),'-nologo','-getProperty:NETCoreSdkVersion,RoslynTargetsPath,MSBuildSDKsPath'],text=True,cwd=root))['Properties']
assert properties['NETCoreSdkVersion']==version,properties
sdk=host.parent/'sdk'/version
files=[host,sdk/'dotnet.dll',sdk/'MSBuild.dll',sdk/'Roslyn/bincore/csc.dll']
result={'SDK':version,'MSBuildProperties':properties,'Files':{str(p):{'Bytes':p.stat().st_size,'Sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in files}}
(root/'evidence').mkdir(exist_ok=True)
(root/'evidence/build-toolchain.json').write_text(json.dumps(result,indent=2)+'\n')
