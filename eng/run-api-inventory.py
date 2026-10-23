#!/usr/bin/env python3
"""Inventory actual MSBuild production project targets with Roslyn; preserve failures as evidence."""

import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import sys

TOOL_ROOT = Path(__file__).resolve().parent.parent
TOOL_PROJECT = TOOL_ROOT / "eng/SharpLink.ApiInventory/SharpLink.ApiInventory.csproj"
TOOL = TOOL_PROJECT.parent / "bin/Release/net10.0/SharpLink.ApiInventory.dll"


def digest(data):
    return hashlib.sha256(data).hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def run(command, root, log, timeout=300):
    with log.open("w", encoding="utf-8") as stream:
        stream.write(json.dumps(command) + "\n")
        stream.flush()
        result = subprocess.run(command, cwd=root, stdout=stream, stderr=subprocess.STDOUT,
                                timeout=timeout, check=False, env={**os.environ, "DOTNET_CLI_UI_LANGUAGE": "en"})
    return result.returncode


def evaluate(project, root, framework=None):
    command = ["dotnet", "msbuild", str(project), "-nologo", "-property:Configuration=Release",
               "-getProperty:TargetFramework,TargetFrameworks,TargetFrameworkMoniker,TargetPath,AssemblyName,IsPackable,MSBuildSDKsPath,MSBuildVersion,NETCoreSdkVersion,RuntimeIdentifier,DefineConstants,LangVersion",
               "-getItem:Compile,ProjectReference,AdditionalFiles"]
    if framework:
        command.append("-property:TargetFramework=" + framework)
    result = subprocess.run(command, cwd=root, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60)
    if result.returncode:
        raise ValueError(f"MSBuild evaluation failed for {project}: {result.stdout}\n{result.stderr}")
    return json.loads(result.stdout)


def git_provenance(root):
    def git(*arguments):
        result = subprocess.run(["git", *arguments], cwd=root, text=True, capture_output=True)
        return result.stdout.strip() if result.returncode == 0 else None
    repository = git("rev-parse", "--show-toplevel")
    untracked = {}
    if repository:
        result = subprocess.run(["git", "ls-files", "--others", "--exclude-standard", "--full-name", "-z"],
                                cwd=root, text=True, capture_output=True, check=True)
        untracked = {name: digest((Path(repository) / name).read_bytes())
                     for name in sorted(result.stdout.split("\0")) if name and (Path(repository) / name).is_file()}
    return {"commit": git("rev-parse", "HEAD"), "tree": git("rev-parse", "HEAD^{tree}"),
            "sourceDirectoryWithinRepository": os.path.relpath(root, repository).replace(os.sep, "/") if repository else None,
            "status": git("status", "--porcelain=v1", "--untracked-files=all"),
            "untrackedFilesSha256": untracked,
            "trackedDiffSha256": digest((git("diff", "HEAD", "--binary") or "").encode())}


def coverage_errors(expected, actual):
    """Coverage cannot succeed with a missing, duplicate, unexpected or failed target."""
    expected_ids = [(target["project"], target["framework"]) for target in expected]
    actual_ids = [(target["project"], target["framework"]) for target in actual]
    errors = []
    if len(set(expected_ids)) != len(expected_ids) or len(set(actual_ids)) != len(actual_ids):
        errors.append("Duplicate target identities")
    if set(expected_ids) != set(actual_ids):
        errors.append(f"Target coverage mismatch: missing={sorted(set(expected_ids) - set(actual_ids))}, unexpected={sorted(set(actual_ids) - set(expected_ids))}")
    for target in actual:
        if not target["complete"]:
            errors.append(f"Incomplete target: {target['project']} [{target['framework']}]")
    return errors


def normalize_evaluation(evaluation, root):
    # MSBuild includes absolute paths and volatile file timestamps on items. Keep
    # semantic identity/selection metadata and actual input hashes, not timestamps.
    items = {}
    for kind, values in evaluation.get("Items", {}).items():
        items[kind] = []
        for item in values:
            record = {key: (os.path.relpath(value, root).replace(os.sep, "/") if key == "FullPath" else value)
                      for key, value in item.items()
                      if key in ("Identity", "FullPath", "Link", "ReferenceOutputAssembly", "OutputItemType")}
            path = Path(item["FullPath"])
            record["fileSha256"] = digest(path.read_bytes()) if path.is_file() else None
            items[kind].append(record)
    return {"properties": evaluation["Properties"], "items": items}


def combine(output, expected, targets, errors, excluded, scope):
    symbols, relations = [], []
    for target in targets:
        directory = output / target["directory"]
        for filename, rows in (("symbols.jsonl", symbols), ("relations.jsonl", relations)):
            path = directory / filename
            if path.exists():
                rows.extend(json.loads(line) for line in path.read_text(encoding="utf-8").splitlines())
    if len({row["id"] for row in symbols}) != len(symbols):
        errors.append("Duplicate stable symbol IDs across target reports")
    for name, rows, key in (("symbols", symbols, lambda row: row["id"]),
                            ("relations", relations, lambda row: (row["source"], row["kind"], row["target"]))):
        (output / f"{name}.jsonl").write_text("".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n"
                                                     for row in sorted(rows, key=key)), encoding="utf-8")
    pending = [{"symbolId": row["id"], "classification": "needs-decision", "rationale": None, "evidence": []}
               for row in sorted(symbols, key=lambda row: row["id"]) if row["externallyVisible"] or row["nonSealedClass"]]
    write_json(output / "decisions.json", {"schemaVersion": 1, "approvalStatus": "not-reviewed", "decisions": pending})
    coverage = {"schemaVersion": 1, "scope": scope, "complete": not errors,
                "hostOs": sys.platform, "hostArchitecture": platform.machine(), "configuration": "Release",
                "expectedTargets": expected, "actualTargets": targets, "failures": errors,
                "excludedScopes": excluded, "symbolCountByTarget": dict(sorted(Counter(row["project"] + "@" + row["framework"] for row in symbols).items())),
                "symbolCountByOrigin": dict(sorted(Counter(row["origin"] for row in symbols).items())),
                "symbolCountByEffectiveAccessibility": dict(sorted(Counter(row["effectiveAccessibility"] for row in symbols).items())),
                "typeCounts": [{"project": key[0], "framework": key[1], "accessibility": key[2], "sealed": key[3], "abstract": key[4], "count": count}
                               for key, count in sorted(Counter((row["project"], row["framework"], row["effectiveAccessibility"],
                                                                row["details"]["isSealed"], row["details"]["isAbstract"])
                                                               for row in symbols if row["kind"] == "NamedType").items())],
                "unionIdentityCountIgnoringTfm": len({(row["project"], row["semanticId"]) for row in symbols}),
                "pendingDecisionCount": len(pending)}
    write_json(output / "coverage.json", coverage)
    lines = ["# Roslyn declaration inventory", "", f"Declared scope: `{scope}`.", "",
             f"Coverage: **{'complete for the selected host/declaration scope' if not errors else 'INCOMPLETE'}**.",
             "Release API audit approval: **not reviewed**. No absence of a reference/derived type proves safe removal.", "",
             "Explicit, generated and compiler-implicit rows are separate; partial declarations share one ID.",
             "Accessors are method rows associated with their property/event, not extra independent properties.",
             "The cross-TFM union counts identities only, not equal API shapes. Nested protected accessibility is",
             "an envelope plus containing constraints; it is not a proof of any specific caller's access.", "",
             "| Project / TFM | Complete | Symbols |", "| --- | --- | ---: |"]
    for target in targets:
        key = target["project"] + "@" + target["framework"]
        lines.append(f"| {key} | {target['complete']} | {coverage['symbolCountByTarget'].get(key, 0)} |")
    lines.extend(["", "## Coverage limitations", "", *("- " + gap for gap in excluded), "",
                  "## Failures", "", *("- " + error for error in errors), "",
                  "## Non-sealed classes (including abstract/internal classes)", "",
                  "A public constructor alone does not imply a usable external instance; check abstractness and containing visibility.", "",
                  "| Stable ID | Effective accessibility | Abstract | Public instance constructor |",
                  "| --- | --- | --- | --- |"])
    for row in sorted(symbols, key=lambda row: row["id"]):
        if row["nonSealedClass"]:
            escaped = row["id"].replace("|", "\\|").replace("`", "\\`").replace("<", "&lt;").replace(">", "&gt;")
            lines.append(f"| {escaped} | {row['effectiveAccessibility']} | {row['details']['isAbstract']} | {row['hasPublicInstanceConstructor']} |")
    lines.extend(["", "All externally visible symbols and non-sealed classes are explicitly listed as needs-decision in `decisions.json`.", ""])
    (output / "inventory.md").write_text("\n".join(lines), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=TOOL_ROOT)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--coverage-config", type=Path, default=TOOL_ROOT / "eng/api-inventory/scope.json")
    args = parser.parse_args()
    root, output = args.root.resolve(), args.output.resolve()
    if output.exists():
        parser.error("Output must be a new directory; existing reports/baselines are never overwritten")
    output.mkdir(parents=True)
    config = json.loads(args.coverage_config.read_text(encoding="utf-8"))
    if config.get("schemaVersion") != 1 or config.get("scope") not in ("production-declarations", "synthetic-fixture"):
        parser.error("Unsupported inventory coverage configuration")
    initial_source = git_provenance(root)
    tool_files = sorted(TOOL_PROJECT.parent.glob("*.cs")) + [TOOL_PROJECT, Path(__file__), args.coverage_config,
                  TOOL_ROOT / "Directory.Packages.props", TOOL_ROOT / "global.json"]
    provenance = {"schemaVersion": 1, "source": initial_source, "tool": git_provenance(TOOL_ROOT),
                  "toolSourceSha256": {str(path.relative_to(TOOL_ROOT)) if path.is_relative_to(TOOL_ROOT) else str(path): digest(path.read_bytes()) for path in tool_files},
                  "host": platform.platform()}
    expected, targets, errors = [], [], []
    try:
        if run(["dotnet", "build", str(TOOL_PROJECT), "-c", "Release", "-v", "minimal"], TOOL_ROOT, output / "tool-build.log"):
            raise ValueError("Inventory tool build failed; see tool-build.log")
        provenance["toolBinarySha256"] = digest(TOOL.read_bytes())
        provenance["toolRuntimeFilesSha256"] = {path.name: digest(path.read_bytes()) for path in sorted(TOOL.parent.glob("*.dll"))}
        projects = sorted(path for path in root.glob(config["projectGlob"])
                          if not ({"obj", "bin"} & set(path.relative_to(root).parts)))
        if not projects:
            raise ValueError("No production projects discovered")
        provenance["discoveredProjects"] = [path.relative_to(root).as_posix() for path in projects]
        controls = set(projects)
        for project in projects:
            parent = project.parent
            while parent.is_relative_to(root):
                for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.Config", "nuget.config"):
                    candidate = parent / name
                    if candidate.is_file():
                        controls.add(candidate)
                if parent == root:
                    break
                parent = parent.parent
        provenance["sourceControlFilesSha256"] = {path.relative_to(root).as_posix(): digest(path.read_bytes()) for path in sorted(controls)}
        for project in projects:
            relative = project.relative_to(root).as_posix()
            try:
                outer = evaluate(project, root)
                properties = outer["Properties"]
                frameworks = (properties["TargetFrameworks"] or properties["TargetFramework"]).split(";")
                if not all(frameworks) or len(set(frameworks)) != len(frameworks):
                    raise ValueError("Missing or duplicate evaluated TFMs")
                for framework in frameworks:
                    expected.append({"project": relative, "framework": framework})
            except Exception as exception:
                errors.append(f"Discovery {relative}: {exception}")
        for required in config.get("requiredTargets", []):
            if required not in expected:
                errors.append(f"Required target missing from MSBuild discovery: {required}")
        for required in config.get("requiredGeneratedProjects", []):
            if required not in {target["project"] for target in expected}:
                errors.append(f"Required generated project not discovered: {required}")
        for index, target in enumerate(expected):
            relative, framework = target["project"], target["framework"]
            directory = output / "targets" / f"{index:02}-{Path(relative).stem}-{framework}"
            directory.mkdir(parents=True)
            generated = directory / "build-generated"
            generated.mkdir()
            result = {**target, "directory": directory.relative_to(output).as_posix(), "complete": False}
            targets.append(result)
            print(f"Inventory {relative} [{framework}]", flush=True)
            try:
                project = root / relative
                evaluation = evaluate(project, root, framework)
                write_json(directory / "evaluation.json", normalize_evaluation(evaluation, root))
                properties = evaluation["Properties"]
                if properties["TargetFramework"] != framework:
                    raise ValueError("Actual MSBuild target differs from requested target")
                base_command = ["dotnet", "build", str(project), "-c", "Release", "-f", framework, "-v", "minimal",
                                "-warnaserror:CS8784,CS8785,CS8032,CS8033,CS8034"]
                if run(base_command, root, directory / "build.log"):
                    raise ValueError("Project/reference build failed; see build.log")
                if run(base_command + ["--no-restore", "--no-incremental", "-property:BuildProjectReferences=false",
                                       "-property:EmitCompilerGeneratedFiles=true", "-property:CompilerGeneratedFilesOutputPath=" + str(generated)],
                       root, directory / "compiler-generated-build.log"):
                    raise ValueError("Compiler rebuild failed; see compiler-generated-build.log")
                exit_code = run(["dotnet", str(TOOL), str(root), str(project), framework, "Release",
                                 str(Path(properties["MSBuildSDKsPath"]).parent), str(directory), str(generated), properties["TargetPath"]],
                                root, directory / "inventory.log")
                report_path = directory / "target.json"
                if not report_path.exists():
                    raise ValueError("Workspace returned no target report; see inventory.log")
                report = json.loads(report_path.read_text(encoding="utf-8"))
                if exit_code or not report["complete"]:
                    raise ValueError("Workspace inventory incomplete; see target.json and inventory.log")
                if report["inputs"].get("targetFrameworkMoniker") != properties["TargetFrameworkMoniker"]:
                    raise ValueError("Compilation TargetFrameworkAttribute differs from evaluated TFM")
                if report["inputs"].get("assemblyName") != properties["AssemblyName"]:
                    raise ValueError("Compilation assembly name differs from evaluated project")
                if relative in config.get("requiredGeneratedProjects", []) and not any(document["kind"] == "source-generated" for document in report["documents"]):
                    raise ValueError("Required source-generated documents were not produced")
                result["complete"] = True
                result["sdkVersion"] = properties["NETCoreSdkVersion"]
                result["documentCount"] = len(report["documents"])
                result["generatedDocumentCount"] = sum(document["kind"] == "source-generated" for document in report["documents"])
            except Exception as exception:
                result["failure"] = str(exception)
                errors.append(f"{relative} [{framework}]: {exception}")
    except Exception as exception:
        errors.append(str(exception))
    errors.extend(coverage_errors(expected, targets))
    if git_provenance(root) != initial_source:
        errors.append("Source Git state changed during inventory; retry on an unchanged tree")
    combine(output, expected, targets, errors, config.get("excludedScopes", []), config["scope"])
    provenance["outputsSha256"] = {path.relative_to(output).as_posix(): digest(path.read_bytes())
                                   for path in sorted(output.rglob("*")) if path.is_file()}
    write_json(output / "provenance.json", provenance)
    print(f"Inventory {'FAILED' if errors else 'complete for declared scope'}: {output}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
