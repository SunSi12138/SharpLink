using System;
using System.IO;
using System.Text.Json;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.CSharp;

// A small comparison probe supports independent real-generator counterfactuals.
// It does not produce an inventory or an approval record.
if (args.Length == 5 && args[0] == "--compare-generated")
{
    var binding = LoggerGeneratorBinding.FromFile(args[1]);
    var comparison = GeneratedSourceComparison.Compare(args[2], File.ReadAllText(args[3]), File.ReadAllText(args[4]),
        new CSharpParseOptions(LanguageVersion.CSharp14), binding);
    Console.WriteLine(JsonSerializer.Serialize(comparison, InventoryReport.Json));
    return comparison.Equivalent ? 0 : 1;
}

if (args.Length != 8)
{
    Console.Error.WriteLine("Usage: SharpLink.ApiInventory ROOT PROJECT TFM CONFIGURATION MSBUILD_PATH OUTPUT BUILD_GENERATED TARGET_PATH");
    return 2;
}

// Register the exact SDK selected by `dotnet msbuild` in the source root, before
// JIT-compiling code which touches MSBuildWorkspace. Do not pick the newest SDK.
MSBuildLocator.RegisterMSBuildPath(Path.GetFullPath(args[4]));
return await InventoryRunner.RunAsync(args);
