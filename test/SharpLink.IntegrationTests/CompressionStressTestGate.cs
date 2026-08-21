namespace SharpLink.IntegrationTests;

public static class CompressionStressTestGate
{
    internal const string RunStressEnvironmentVariable = "SHARPLINK_RUN_COMPRESSION_STRESS";
    internal const string StressTestName =
        nameof(CompressionMergeGateValidationTests.HundredThousandCapacityRejectedCompressedRequestsShouldNotDecodeOrLeakAccounting);

    [BeforeEvery(Test)]
    public static void SkipLongCompressionStressByDefault()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable(RunStressEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var context = TestContext.Current;
        if (context?.Metadata.TestName == StressTestName &&
            context.Metadata.TestDetails.ClassType == typeof(CompressionMergeGateValidationTests))
        {
            Skip.Test(
                "100k compressed-request ownership stress runs in the dedicated Compression Stress workflow.");
        }
    }
}
