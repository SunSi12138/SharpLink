"""Archive only actually loaded build DLLs and the executable host's two config files."""
import json, shutil
from pathlib import Path, PurePosixPath
from prepare import inventory, sha
CONFIGS=('HotCostProbe/release/SharpLink.UnitTests.deps.json','HotCostProbe/release/SharpLink.UnitTests.runtimeconfig.json')

def required_closure(rows, build_root, binaries):
    base=PurePosixPath(build_root)/'build/bin';selected=set()
    for row in rows:
        for assembly in row['Network']['LoadedAssemblies']:
            path=PurePosixPath(assembly['Path'])
            if '..' in path.parts:raise ValueError('Unsafe archive path')
            if path.is_relative_to(base):
                relative=str(path.relative_to(base))
                if path.suffix!='.dll' or binaries.get(relative)!=assembly['Sha256']:raise ValueError('Loaded binary missing/mismatched in build manifest')
                selected.add(relative)
    if not selected:raise ValueError('No actually loaded build DLLs to archive')
    selected.update(CONFIGS)
    if any(name not in binaries for name in selected):raise ValueError('Missing executable configuration/binary')
    return {name:binaries[name] for name in sorted(selected)}

def archive_closure(rows, build, output, binaries):
    build=Path(build);output=Path(output);expected=required_closure(rows,str(build),binaries)
    destination=output/'actual-binaries'
    for name,digest in expected.items():
        source=build/'build/bin'/name;target=destination/name
        if source.is_symlink() or sha(source)!=digest:raise ValueError('Original binary bytes drifted before archival')
        target.parent.mkdir(parents=True,exist_ok=True)
        if target.exists():
            if target.is_symlink() or sha(target)!=digest:raise ValueError('Existing archived bytes drifted')
        else:shutil.copyfile(source,target)
    if inventory(destination)!=expected:raise ValueError('Archive missing/extra/mismatched bytes')
    (output/'archived-binary-closure.json').write_text(json.dumps(expected,indent=2)+'\n')

def validate_archive(rows, build_root, output, binaries):
    from verify_request import strict_json
    output=Path(output);expected=required_closure(rows,build_root,binaries)
    retained=strict_json((output/'archived-binary-closure.json').read_text())
    if retained!=expected or inventory(output/'actual-binaries')!=expected:
        raise ValueError('Archived original DLL/config bytes do not match loaded/build manifests')
