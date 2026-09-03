from pathlib import Path

path = Path("test/SharpLink.UnitTests/Client/SharpLinkClientContractDependencyTests.cs")
text = path.read_text()
old = '''        var dependencyAssembly = AssemblyBuilder.DefineDynamicAssembly(\n            new AssemblyName("SharpLink.ContractDependency.B." + Guid.NewGuid().ToString("N")),\n            AssemblyBuilderAccess.Run);\n        var dependantAssembly = AssemblyBuilder.DefineDynamicAssembly(\n            new AssemblyName("SharpLink.ContractDependency.A." + Guid.NewGuid().ToString("N")),\n            AssemblyBuilderAccess.Run);\n        var dependencyManifest = new TestManifest(dependencyAssembly, []);\n'''
new = '''        var dependencyAssembly = typeof(IService).Assembly;\n        var dependantAssembly = client.GetType().Assembly;\n        var dependencyManifest = new TestManifest(dependencyAssembly, []);\n'''
if old not in text:
    raise RuntimeError("contract dependency fixture block not found")
path.write_text(text.replace(old, new, 1))
