#!/usr/bin/env python3
"""Enforce the reviewed version calibration against the reviewed published release boundary."""

import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

def verify_generator_identity(root):
    """Source guard for the actual authority consumers; not compiler/runtime evidence."""
    authority = "SharpLink.Abstractions.SharpLinkGeneratedManifestVersions"
    emitter = (root / "src/SharpLink.Generator/RpcGenerator.ManifestEmitter.cs").read_text()
    bootstrap = (root / "src/SharpLink.Generator/RpcGenerator.ReferencedManifestBootstrap.cs").read_text()
    required_emitter = (
        f"private const string GeneratedAbiIdentity = {authority}.AbiIdentity;",
        f'sb.AppendLine($"    public int ApiVersion => {{{authority}.Api}};");',
        f'sb.AppendLine($"    public int ProtocolVersion => {{{authority}.Protocol}};");',
        f'sb.AppendLine($"[assembly: SharpLinkGeneratedAssemblyManifestAttribute(typeof(SharpLink.Generated.{{manifestTypeName}}), {{{authority}.Api}}, {{{authority}.Protocol}},',
        '\\"{GeneratedAbiIdentity}\\")]',
    )
    required_bootstrap = (
        f"attribute.ConstructorArguments[1].Value is not {authority}.Api ||",
        f"attribute.ConstructorArguments[2].Value is not {authority}.Protocol ||",
        "!string.Equals(abiIdentity, GeneratedAbiIdentity, StringComparison.Ordinal)",
    )
    for label, source, snippets in (("emitter", emitter, required_emitter),
                                     ("bootstrap", bootstrap, required_bootstrap)):
        for snippet in snippets:
            if source.count(snippet) != 1:
                raise ValueError(f"Generator {label} must use the shared manifest authority: {snippet}")
    project = ET.parse(root / "src/SharpLink.Generator/SharpLink.Generator.csproj")
    for group in project.findall("./PropertyGroup"):
        if group.find("DefineConstants") is not None and group.attrib:
            raise ValueError("Generator shared authority symbol must be unconditional")
    for group in project.findall("./ItemGroup"):
        if any("SharpLinkGeneratedManifestVersions.cs" in str(item.attrib) for item in group.findall("Compile")) and group.attrib:
            raise ValueError("Generator shared authority compile link must be unconditional")
    definitions = project.findall(".//DefineConstants")
    if len(definitions) != 1 or definitions[0].attrib or definitions[0].text != "$(DefineConstants);SHARPLINK_GENERATOR":
        raise ValueError("Generator must explicitly select the internal shared authority")
    links = [item for item in project.findall(".//Compile")
             if item.attrib.get("Include", "").replace("\\", "/") ==
             "../SharpLink.Abstractions/SharpLinkGeneratedManifestVersions.cs"]
    if len(links) != 1 or links[0].attrib != {
            "Include": "..\\SharpLink.Abstractions\\SharpLinkGeneratedManifestVersions.cs",
            "Link": "Shared\\SharpLinkGeneratedManifestVersions.cs"}:
        raise ValueError("Generator must compile the actual Abstractions authority source")
    source = (root / "src/SharpLink.Abstractions/SharpLinkGeneratedManifestVersions.cs").read_text()
    if source.count("#if SHARPLINK_GENERATOR\ninternal\n#else\npublic\n#endif\nstatic class SharpLinkGeneratedManifestVersions") != 1:
        raise ValueError("Shared authority must stay public only in Abstractions, internal in Generator")


def verify(root):
    manifest = json.loads((root / "eng/release-versions.json").read_text())
    if manifest["schemaVersion"] != 1:
        raise ValueError("Unknown version-calibration manifest schema")
    version = ET.parse(root / "Directory.Build.props").findtext(".//VersionPrefix")
    if version != manifest["release"]:
        raise ValueError(f"Release version {version} needs an explicitly reviewed calibration manifest")

    observed = {}
    for entry in manifest["versions"]:
        matches = re.findall(entry["pattern"], (root / entry["path"]).read_text(encoding="utf-8-sig"))
        if len(matches) != 1 or int(matches[0]) != entry["current"]:
            raise ValueError(f"{entry['name']}: expected {entry['current']}, found {matches}")
        before, after, policy = entry["published"], entry["current"], entry["policy"]
        valid = {
            "increment": before is not None and after == before + 1,
            "unchanged": before is not None and after == before,
            "initial": before is None and after == 1,
            "floor": before is None and after == observed.get("Protocol minor"),
        }
        if not valid.get(policy, False):
            raise ValueError(f"Invalid release calibration for {entry['name']}: {policy}")
        observed[entry["name"]] = after
    verify_generator_identity(root)
    return manifest, observed


if __name__ == "__main__":
    manifest, observed = verify(Path(__file__).resolve().parent.parent)
    print(f"Verified {len(observed)} version declarations against released {manifest['publishedBaseline']['version']}; no development-only increments.")
