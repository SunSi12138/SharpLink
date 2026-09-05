from pathlib import Path

kernel_path = Path("src/SharpLink.Server/Admission/AdmissionStateKernel.cs")
support_path = Path("src/SharpLink.Server/Admission/AdmissionStateKernel.Types.cs")
text = kernel_path.read_text(encoding="utf-8")
marker = "internal enum AdmissionRuleStateScope : byte\n"
if text.count(marker) != 1:
    raise RuntimeError(f"expected one kernel support marker, found {text.count(marker)}")
index = text.index(marker)
body = text[index:]
kernel = text[:index].rstrip() + "\n"
if kernel.count("internal sealed class AdmissionStateKernel") != 1:
    raise RuntimeError("kernel owner was not preserved")
if "internal sealed class AdmissionUpdatePlan" not in body:
    raise RuntimeError("update-plan support block was not captured")
kernel_path.write_text(kernel, encoding="utf-8")
support_path.write_text(
    "namespace SharpLink.Server;\n\n" + body,
    encoding="utf-8")
