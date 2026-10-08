#!/usr/bin/env python3
"""Copy retained safety evidence and enforce every delivered artifact budget."""
import argparse
import json
import os
import pathlib
import shutil
import tempfile

from codegen import BUDGET, budget
from prepare import save, sha


def package(source, output):
    output.mkdir(parents=True, exist_ok=True)
    assert not any(output.iterdir()), 'Never publish stale or previously unverified packages'
    reports, failures = {}, []

    def publish(name, staged):
        report = dict(bytes=budget(staged), limit_exclusive=BUDGET,
            files={str(file.relative_to(staged)): sha(file) for file in staged.rglob('*') if file.is_file()})
        assert report['files'], 'An empty package is not ready'
        staged.rename(output / name)
        reports[name] = report
        # Outputs are appended only after copy, hash and budget checks plus atomic
        # placement. An error in another group cannot revoke this ready package.
        if os.environ.get('GITHUB_OUTPUT'):
            with pathlib.Path(os.environ['GITHUB_OUTPUT']).open('a') as stream:
                stream.write('ready_' + name.replace('-', '_') + '=true\n')

    def record_failure(name, error):
        failures.append(dict(artifact=name, error=repr(error)))

    for kind in ('jit', 'native'):
        root = source / kind
        if not root.exists():
            continue
        for item in sorted(root.iterdir()):
            if item.is_dir():
                name = kind + '-' + item.name
                try:
                    with tempfile.TemporaryDirectory(prefix='readonly-lease-package-', dir=output.parent) as temporary:
                        staged = pathlib.Path(temporary) / name
                        shutil.copytree(item, staged)
                        publish(name, staged)
                except (AssertionError, OSError, ValueError) as error:
                    record_failure(name, error)

    # Build safety last so it includes all earlier packaging errors. If copying
    # safety evidence itself fails, still retain a bounded failure-only manifest.
    try:
        with tempfile.TemporaryDirectory(prefix='readonly-lease-package-', dir=output.parent) as temporary:
            safety = pathlib.Path(temporary) / 'safety'
            safety.mkdir()
            for directory in ('source', 'env', 'default', 'experimental'):
                root = source / directory
                if not root.exists():
                    continue
                for file in root.rglob('*'):
                    if not file.is_file():
                        continue
                    relative = file.relative_to(root)
                    if any(part.startswith(('bin-', 'project-', 'lifecycle-bin-', 'lifecycle-project-'))
                           for part in relative.parts):
                        continue
                    destination = safety / directory / relative
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(file, destination)
            save(safety / 'artifact-manifest.json', reports)
            save(safety / 'packaging-failures.json', failures)
            publish('safety', safety)
    except (AssertionError, OSError, ValueError) as error:
        record_failure('safety', error)
        with tempfile.TemporaryDirectory(prefix='readonly-lease-package-', dir=output.parent) as temporary:
            safety = pathlib.Path(temporary) / 'safety'
            save(safety / 'packaging-failures.json', failures)
            save(safety / 'artifact-summary.json', {name: report['bytes'] for name, report in reports.items()})
            publish('safety', safety)
    print(json.dumps({name: value['bytes'] for name, value in reports.items()}, indent=2))
    if failures:
        raise SystemExit(1)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=pathlib.Path)
    parser.add_argument('output', type=pathlib.Path)
    args = parser.parse_args()
    package(args.source.resolve(), args.output.resolve())
