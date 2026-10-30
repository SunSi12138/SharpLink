using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

internal static partial class Program
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const int WarmupCalls = 1250, RoundCount = 5, CallsPerRound = 5000;
    public static async Task Main(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("off" or "on-no-update" or "post-update"))
            throw new ArgumentException("Expected state and new output path.");
        if (File.Exists(args[1])) throw new IOException("Refusing to overwrite output.");
        using var partialFile = new FileStream(args[1] + ".partial.jsonl", FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var partialWriter = new StreamWriter(partialFile, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true);
        void RecordPartial(object record)
        {
            // Outside every measured interval; preserve completed work even if later cleanup hangs.
            partialWriter.WriteLine(JsonSerializer.Serialize(record));
            partialWriter.Flush();
            partialFile.Flush(flushToDisk: true);
        }
        var network = await ExerciseNetwork(args[0], RecordPartial);
        var result = new
        {
            Protocol = "maintenance-tls-unary-limited-v1",
            Runtime = RuntimeInformation.FrameworkDescription,
            RuntimeVersion = Environment.Version.ToString(),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            OS = RuntimeInformation.OSDescription,
            ProcessorCount = Environment.ProcessorCount,
            HostPath = Environment.ProcessPath,
            CoreLibPath = typeof(object).Assembly.Location,
            CoreLibSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(object).Assembly.Location))),
            Settings = new[] { "DOTNET_TieredCompilation", "DOTNET_TieredPGO", "DOTNET_TC_QuickJit", "DOTNET_TC_QuickJitForLoops", "DOTNET_ReadyToRun", "DOTNET_gcServer" }
                .ToDictionary(key => key, Environment.GetEnvironmentVariable),
            Network = network
        };
        using var output = new FileStream(args[1], FileMode.CreateNew);
        await JsonSerializer.SerializeAsync(output, result, new JsonSerializerOptions { WriteIndented = true });
    }
}
