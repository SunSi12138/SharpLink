from pathlib import Path

source = Path("test/SharpLink.UnitTests/Runtime/SharpLinkRuntimeContextTests.cs")
text = source.read_text()
block = '''    [Test]\n    public void DisposedContextShouldRejectCodecResolution()\n    {\n        var context = CreateRuntimeBuilder().Build(includeGeneratedAssemblyCatalog: false);\n        context.Dispose();\n        context.Dispose();\n        try\n        {\n            _ = context.Codecs.GetCodec<TaggedValue>();\n            throw new Exception("expected disposed Context to reject Codec resolution");\n        }\n        catch (ObjectDisposedException)\n        {\n        }\n    }\n\n'''
if block not in text:
    raise RuntimeError("disposed-context test block not found")
source.write_text(text.replace(block, "", 1))

partial = Path("test/SharpLink.UnitTests/Runtime/SharpLinkRuntimeContextReferencedCodecTests.cs")
text = partial.read_text()
marker = '''    private sealed class ReferencedCodecManifest(\n'''
insert = '''    [Test]\n    public void DisposedContextShouldRejectCodecResolution()\n    {\n        var context = CreateRuntimeBuilder().Build(includeGeneratedAssemblyCatalog: false);\n        context.Dispose();\n        context.Dispose();\n        try\n        {\n            _ = context.Codecs.GetCodec<TaggedValue>();\n            throw new Exception("expected disposed Context to reject Codec resolution");\n        }\n        catch (ObjectDisposedException)\n        {\n        }\n    }\n\n'''
if marker not in text:
    raise RuntimeError("partial insertion marker not found")
partial.write_text(text.replace(marker, insert + marker, 1))
