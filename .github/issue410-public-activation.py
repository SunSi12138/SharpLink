from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(path: str, old: str, new: str) -> None:
    file = ROOT / path
    text = file.read_text()
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one match, found {count}: {old[:100]!r}")
    file.write_text(text.replace(old, new, 1))


options = "src/SharpLink.Server/Admission/SharpLinkAdmissionControlOptions.cs"
replace_once(
    options,
    "/// <summary>Configures a fixed-window request-rate limit.</summary>\npublic sealed class SharpLinkFixedWindowLimitOptions\n",
    """/// <summary>Controls when a runtime FixedWindow update becomes active.</summary>\npublic enum SharpLinkFixedWindowUpdateActivation\n{\n    /// <summary>Applies a same-active-Window limit change immediately; otherwise defers the complete target to the next window.</summary>\n    Automatic,\n    /// <summary>Applies the new PermitLimit when the update is published. The Window must already be active and no Window change may be pending.</summary>\n    Immediate,\n    /// <summary>Applies the complete PermitLimit and Window target at the next natural active-window boundary.</summary>\n    NextWindow\n}\n\n/// <summary>Configures a fixed-window request-rate limit.</summary>\npublic sealed class SharpLinkFixedWindowLimitOptions\n""",
)
replace_once(
    options,
    """    // Investigation-only selector for #410. Null keeps the current compatibility inference:\n    // same Window => Immediate, changed Window => NextWindowBoundary. An explicit value lets tests\n    // validate both candidate semantics without freezing public API yet.\n    internal DynamicFixedWindowActivationMode? UpdateActivation { get; set; }\n""",
    """    /// <summary>Gets or sets when this target becomes active during a runtime FixedWindow update.</summary>\n    /// <remarks>\n    /// <see cref=\"SharpLinkFixedWindowUpdateActivation.Automatic\"/> applies a limit-only update immediately\n    /// when the configured <see cref=\"Window\"/> is already active and no Window activation is pending;\n    /// otherwise the complete target activates at the next natural window boundary.\n    /// </remarks>\n    public SharpLinkFixedWindowUpdateActivation UpdateActivation { get; set; }\n""",
)
replace_once(
    options,
    """        ArgumentOutOfRangeException.ThrowIfGreaterThan(Window, SharpLinkTimer.MaximumDelay);\n    }\n}\n\n/// <summary>Configures a segmented sliding-window request-rate limit.</summary>\n""",
    """        ArgumentOutOfRangeException.ThrowIfGreaterThan(Window, SharpLinkTimer.MaximumDelay);\n        if (!Enum.IsDefined(UpdateActivation))\n            throw new ArgumentOutOfRangeException(nameof(UpdateActivation));\n    }\n}\n\n/// <summary>Configures a segmented sliding-window request-rate limit.</summary>\n""",
)

fixed = "src/SharpLink.Server/Admission/AdmissionRateState.FixedWindow.cs"
file = ROOT / fixed
text = file.read_text()
text = text.replace(
    """internal enum DynamicFixedWindowActivationMode\n{\n    Immediate,\n    NextWindowBoundary\n}\n\n""",
    "",
)
text = text.replace("DynamicFixedWindowActivationMode", "SharpLinkFixedWindowUpdateActivation")
text = text.replace("SharpLinkFixedWindowUpdateActivation? activationMode", "SharpLinkFixedWindowUpdateActivation activationMode")
text = text.replace("NextWindowBoundary activation", "NextWindow activation")
file.write_text(text)

counter = "src/SharpLink.Server/Admission/AdmissionRateState.FixedWindowCounter.cs"
file = ROOT / counter
text = file.read_text()
text = text.replace("DynamicFixedWindowActivationMode", "SharpLinkFixedWindowUpdateActivation")
text = text.replace("SharpLinkFixedWindowUpdateActivation? requestedActivation", "SharpLinkFixedWindowUpdateActivation requestedActivation")
text = text.replace("SharpLinkFixedWindowUpdateActivation.NextWindowBoundary", "SharpLinkFixedWindowUpdateActivation.NextWindow")
needle = """                if (requestedActivation == SharpLinkFixedWindowUpdateActivation.NextWindow)\n                    return SharpLinkFixedWindowUpdateActivation.NextWindow;\n\n                return requestedWindowIsActive && !hasPendingTarget\n                    ? SharpLinkFixedWindowUpdateActivation.Immediate\n                    : SharpLinkFixedWindowUpdateActivation.NextWindow;\n"""
replacement = """                if (requestedActivation == SharpLinkFixedWindowUpdateActivation.NextWindow)\n                    return SharpLinkFixedWindowUpdateActivation.NextWindow;\n                if (requestedActivation != SharpLinkFixedWindowUpdateActivation.Automatic)\n                    throw new ArgumentOutOfRangeException(nameof(requestedActivation));\n\n                return requestedWindowIsActive && !hasPendingTarget\n                    ? SharpLinkFixedWindowUpdateActivation.Immediate\n                    : SharpLinkFixedWindowUpdateActivation.NextWindow;\n"""
if text.count(needle) != 1:
    raise RuntimeError("FixedWindow Counter automatic activation inference block changed unexpectedly")
file.write_text(text.replace(needle, replacement, 1))

activation_tests = "test/SharpLink.UnitTests/Server/DynamicFixedWindowActivationModeTests.cs"
file = ROOT / activation_tests
text = file.read_text()
text = text.replace("DynamicFixedWindowActivationMode", "SharpLinkFixedWindowUpdateActivation")
text = text.replace("SharpLinkFixedWindowUpdateActivation.NextWindowBoundary", "SharpLinkFixedWindowUpdateActivation.NextWindow")
text = text.replace(
    "SharpLinkFixedWindowUpdateActivation? activation = null",
    "SharpLinkFixedWindowUpdateActivation activation = SharpLinkFixedWindowUpdateActivation.Automatic",
)
file.write_text(text)

chained = "test/SharpLink.UnitTests/Server/DynamicFixedWindowChainedUpdateRegressionTests.cs"
file = ROOT / chained
text = file.read_text()
text = text.replace("DynamicFixedWindowActivationMode.NextWindowBoundary", "SharpLinkFixedWindowUpdateActivation.NextWindow")
file.write_text(text)

api_tests = ROOT / "test/SharpLink.UnitTests/Server/SharpLinkFixedWindowUpdateActivationTests.cs"
if api_tests.exists():
    raise RuntimeError("public activation API test already exists")
api_tests.write_text(r'''using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class SharpLinkFixedWindowUpdateActivationTests
{
    [Test]
    public void DefaultShouldRemainAutomatic()
    {
        var options = new SharpLinkFixedWindowLimitOptions();
        Ensure(options.UpdateActivation == SharpLinkFixedWindowUpdateActivation.Automatic,
            "default activation must preserve compatibility inference");
    }

    [Test]
    public void InvalidActivationShouldBeRejectedDuringRuleConfiguration()
    {
        var rule = new SharpLinkAdmissionRuleOptions();
        Exception? failure = null;
        try
        {
            rule.UseFixedWindow(rate =>
            {
                rate.PermitLimit = 1;
                rate.Window = TimeSpan.FromSeconds(1);
                rate.UpdateActivation = (SharpLinkFixedWindowUpdateActivation)1234;
            });
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(failure is ArgumentOutOfRangeException,
            "undefined public activation values must fail validation");
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }
}
''')

for path in ROOT.rglob("*.cs"):
    if "DynamicFixedWindowActivationMode" in path.read_text():
        raise RuntimeError(f"internal activation enum reference survived: {path.relative_to(ROOT)}")

print("issue #410 public FixedWindow activation API staged")
