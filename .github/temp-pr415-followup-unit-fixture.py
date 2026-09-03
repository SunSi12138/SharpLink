from pathlib import Path

path = Path("test/SharpLink.UnitTests/Client/SharpLinkClientContractDependencyTests.cs")
text = path.read_text()
old = '''        var dependencyManifest = new TestManifest(dependencyAssembly, []);\n        var dependantManifest = new TestManifest(\n'''
new = '''        var dependencyType = dependencyAssembly\n            .DefineDynamicModule("Dependency")\n            .DefineType("SharpLink.ContractDependency.Marker", TypeAttributes.Public)\n            .CreateType()!;\n        var dependantModuleBuilder = dependantAssembly.DefineDynamicModule("Dependant");\n        var dependantTypeBuilder = dependantModuleBuilder.DefineType(\n            "SharpLink.ContractDependency.Dependant", TypeAttributes.Public);\n        _ = dependantTypeBuilder.DefineField(\n            "Dependency", dependencyType, FieldAttributes.Public);\n        _ = dependantTypeBuilder.CreateType();\n\n        var dependencyManifest = new TestManifest(dependencyAssembly, []);\n        var dependantManifest = new TestManifest(\n'''
if old not in text:
    raise RuntimeError("contract dependency fixture insertion point not found")
path.write_text(text.replace(old, new, 1))
