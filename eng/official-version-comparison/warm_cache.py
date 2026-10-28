#!/usr/bin/env python3
"""Hosted preparation only: populate dev's public dependency cache, never download official2.0.3 again."""
import argparse
import os
from pathlib import Path
import subprocess
from common import CONTRACT, PRODUCTS, write


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--source', type=Path, required=True)
    p.add_argument('--dotnet', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    a = p.parse_args()
    source = a.source.resolve(strict=True)
    dotnet = a.dotnet.resolve(strict=True)
    output = a.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    cli = output / 'cli-home'; cli.mkdir()
    env = dict(os.environ, DOTNET_CLI_HOME=str(cli), DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')
    if subprocess.check_output([dotnet, '--version'], cwd=source, env=env, text=True).strip() != CONTRACT['sdk']:
        raise ValueError('Wrong actual SDK')
    if subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=source, text=True).strip() != CONTRACT['devSource'] or subprocess.check_output(['git', 'status', '--porcelain'], cwd=source, text=True).strip():
        raise ValueError('Expected exact pristine dev57 checkout')
    packages = output / 'packages'
    config = output / 'NuGet.config'
    config.write_text('<configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>\n')
    commands = []
    for name in PRODUCTS:
        command = [str(dotnet), 'restore', str(source / f'src/SharpLink.{name}/SharpLink.{name}.csproj'),
                   '--packages', str(packages), '--configfile', str(config), '-p:NuGetAudit=false', '--disable-parallel']
        commands.append(command)
        write(output / 'commands.json', commands)
        with (output / f'restore-{name}.log').open('w') as log:
            subprocess.run(command, cwd=source, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
    if any(packages.glob('sharplink.*/2.0.3')):
        raise ValueError('Unexpected formal package download: official packages belong only to the separately verified published cache')
    print(f'DEPENDENCY_CACHE_READY {packages}')


if __name__ == '__main__':
    main()
