#!/usr/bin/env python3
"""Keep both .NET 10 transitive scan reports; fail closed on findings or gaps.

CLI reference: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-list
"""
import argparse
import json
from pathlib import Path
import xml.etree.ElementTree as ET
from release_204_evidence import read_json


def validate(root, source):
    failures, findings = [], []
    expected = {str((source / element.attrib['Path'].replace('\\', '/')).resolve())
                for element in ET.parse(source / 'Sharplink.slnx').getroot().iter('Project')}
    for kind in ('vulnerable', 'deprecated'):
        try:
            if (root / (kind + '.stderr')).read_text().strip():
                failures.append(kind + ': nonempty stderr requires review')
            data = read_json(root / (kind + '.json'))
            if type(data['version']) is not int or data['version'] != 1:
                failures.append(kind + ': invalid schema version')
            parameters = data['parameters'].split()
            if '--' + kind not in parameters or '--include-transitive' not in parameters:
                failures.append(kind + ': wrong scan parameters')
            if not data.get('sources'):
                failures.append(kind + ': no scan sources')
            projects = data['projects']
            paths = []
            def reject_diagnostics(value):
                if isinstance(value, dict):
                    for key, item in value.items():
                        if key in ('logs', 'problems', 'errors') and item:
                            failures.append(kind + ': scan diagnostics ' + json.dumps(item))
                        reject_diagnostics(item)
                elif isinstance(value, list):
                    for item in value:
                        reject_diagnostics(item)
            reject_diagnostics(data)
            for project in projects:
                paths.append(str((source / project['path']).resolve()))
                for framework in project.get('frameworks', []):
                    for collection in ('topLevelPackages', 'transitivePackages'):
                        for package in framework.get(collection, []):
                            findings.append(dict(Scan=kind, Project=project['path'], Framework=framework['framework'], Package=package))
            if len(paths) != len(set(paths)) or set(paths) != expected:
                failures.append(kind + ': incomplete/duplicate solution project coverage')
        except (KeyError, ValueError, TypeError, OSError) as error:
            failures.append(kind + ': invalid scan report: ' + str(error))
    if findings:
        failures.append('Dependency findings require resolution or explicit review; no automatic severity waiver.')
    return dict(DependencyScansPassed=not failures, Failures=failures, Findings=findings)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('root', type=Path)
    parser.add_argument('source', type=Path)
    args = parser.parse_args()
    result = validate(args.root, args.source)
    (args.root / 'dependency-validation.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))
    raise SystemExit(0 if result['DependencyScansPassed'] else 1)
