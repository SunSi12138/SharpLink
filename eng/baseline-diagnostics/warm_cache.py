#!/usr/bin/env python3
"""Prepare one shared public build-dependency cache from the exact baseline. No probes/tests."""
import argparse
import os
from pathlib import Path
import subprocess
from common import CONTRACT, SOURCE, write
PRODUCTS = ('Abstractions', 'Client', 'Runtime', 'Sdk', 'Server')
from prepare import verify_source


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--dotnet', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    source = args.source.resolve(strict=True)
    verify_source(source)
    dotnet = args.dotnet.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    cli = output / 'cli-home'; cli.mkdir()
    env = dict(os.environ, DOTNET_CLI_HOME=str(cli), DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
               DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_PROCESSOR_COUNT='2',
               NUGET_HTTP_CACHE_PATH=str(output / 'http-cache'), NUGET_SCRATCH=str(output / 'scratch'))
    if subprocess.check_output([dotnet, '--version'], cwd=source, env=env, text=True).strip() != CONTRACT['sdk']:
        raise ValueError('Wrong actual SDK')
    # Restore only an export: the independently verified source checkout stays untouched.
    archive = output / 'baseline-source.tar'
    with (output / 'baseline-archive.log').open('w') as log:
        subprocess.run(['git', 'archive', '--format=tar', '-o', str(archive), SOURCE],
                       cwd=source, check=True, stdout=log, stderr=subprocess.STDOUT, timeout=900)
    exported = output / 'baseline-source'; exported.mkdir()
    with (output / 'baseline-extract.log').open('w') as log:
        subprocess.run(['tar', '-xf', str(archive), '-C', str(exported)], check=True,
                       stdout=log, stderr=subprocess.STDOUT, timeout=900)
    packages = output / 'packages'
    config = output / 'NuGet.config'
    config.write_text('<configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>\n')
    commands = []
    for name in PRODUCTS:
        command = [str(dotnet), 'restore', str(exported / f'src/SharpLink.{name}/SharpLink.{name}.csproj'),
                   '--packages', str(packages), '--configfile', str(config), '-p:NuGetAudit=false',
                   '--disable-parallel', '-m:1', '-nr:false']
        commands.append(command)
        write(output / 'commands.json', commands)
        with (output / f'restore-{name}.log').open('w') as log:
            subprocess.run(command, cwd=exported, env=env, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=900)
    verify_source(source)
    print('DEPENDENCY_CACHE_READY; no product probes or tests executed')


if __name__ == '__main__':
    main()
