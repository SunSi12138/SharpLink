using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace SharpLink.ReadOwnershipAllocationProbe;

internal static class Program
{
    private const string Fixture = "SharpLink.UnitTests.Runtime.ReadOwnershipPipeReaderLifetimeTests";
    private const int RequiredCacheEntries = 4096;
    private const int MaximumPrimingTypes = 20_000;
    private const int CacheArrayLength = (RequiredCacheEntries + 1) * 6;

    public static int Main(string[] args)
    {
        string scenario = "invalid", nonce = "invalid";
        try
        {
            if (args.Length != 5 || !Guid.TryParseExact(args[2], "N", out _))
                throw new ArgumentException("Expected unit assembly, exact case, nonce, module ID, and result path.");
            var assemblyPath = Path.GetFullPath(args[0]);
            scenario = args[1];
            nonce = args[2];
            var methodName = scenario switch
            {
                "SuccessfulSuspensionsShouldNotAllocateAfterWarmup" => "MeasureSuccessfulSuspensions",
                "RegisteredConsumerSuspensionsShouldNotAllocateAfterWarmup" => "MeasureRegisteredConsumerSuspensions",
                _ => throw new ArgumentException($"Unknown allocation case '{scenario}'.")
            };

            // Resolve the tested output, but never invoke its TUnit entry point or test discovery.
            var resolver = new AssemblyDependencyResolver(assemblyPath);
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                var path = resolver.ResolveAssemblyToPath(name);
                return path is null ? null : AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            };
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            if (assembly.ManifestModule.ModuleVersionId.ToString("N") != args[3])
                throw new InvalidOperationException("The probe did not load the parent's exact UnitTests module.");
            var type = assembly.GetType(Fixture, throwOnError: true)!;
            RuntimeHelpers.RunModuleConstructor(type.Module.ModuleHandle);
            RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException(Fixture, methodName);
            if (method.ReturnType != typeof(void) || method.GetParameters().Length != 0)
                throw new InvalidOperationException("The allocation measurement signature changed.");
            var measure = method.CreateDelegate<Action>();

            // Preserve the non-default ExecutionContext capture path, without a test runner.
            // No flow suppression, custom scheduler, GC mode, or tiering override is applied.
            var ambient = new AsyncLocal<object?>();
            var sentinel = new object();
            ambient.Value = sentinel;
            if (ExecutionContext.IsFlowSuppressed())
                throw new InvalidOperationException("The allocation probe requires normal ExecutionContext flow.");

            var tableField = GetSupportedCacheField();
            var primedTypes = PrimeRuntimeCastCache(tableField);
            var beforeTable = ReadCache(tableField);
            // Exactly ONE original measurement: its 1,000 reader warmups, 10,000 measured
            // rounds, both consumer paths and exact zero assertion remain in the UnitTests DLL.
            measure();
            if (!ReferenceEquals(beforeTable, ReadCache(tableField)))
                throw new InvalidOperationException("The runtime cast-cache precondition changed during the oracle.");
            if (!ReferenceEquals(ambient.Value, sentinel) || ExecutionContext.IsFlowSuppressed())
                throw new InvalidOperationException("The allocation oracle did not preserve its non-default context.");

            var result = new
            {
                scenario,
                nonce,
                assemblyMvid = args[3],
                runtime = Environment.Version.ToString(),
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                executed = 1,
                passed = true,
                warmup = 1000,
                measured = 10000,
                allocated = 0,
                cacheEntries = RequiredCacheEntries,
                primedTypes,
                cacheUnchanged = true,
                nonDefaultContext = true,
                contextFlowEnabled = true
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[4]))!);
            File.WriteAllText(args[4], JsonSerializer.Serialize(result));
            Console.WriteLine($"READ_OWNERSHIP_ALLOCATION_PASSED|case={scenario}|nonce={nonce}|mvid={args[3]}|runtime={Environment.Version}|architecture={RuntimeInformation.ProcessArchitecture}|executed=1|warmup=1000|measured=10000|allocated=0");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"READ_OWNERSHIP_ALLOCATION_FAILED|case={scenario}|nonce={nonce}|exception={exception}");
            return 1;
        }
    }

    private static FieldInfo GetSupportedCacheField()
    {
        // Fail-closed CoreCLR 10 / 64-bit capability contract, not an architecture skip.
        // Public source: dotnet/runtime v10.0.2 and v10.0.13, src/coreclr/vm/castcache.{h,cpp}.
        // A 64-bit entry occupies 24 bytes; one extra entry contains the table metadata.
        // Those two builds have a 4096-entry maximum. Other servicing builds are capability-
        // checked, not historically validated: establish this table and require its identity
        // to remain unchanged across the exact-zero body. Do not assume a universal maximum.
        if (Environment.Version.Major != 10 || IntPtr.Size != 8 ||
            RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64) ||
            !RuntimeFeature.IsDynamicCodeSupported || Type.GetType("Mono.Runtime") is not null)
            throw new PlatformNotSupportedException(
                $"The allocation precondition requires dynamic-code CoreCLR 10 on x64 or arm64; " +
                $"current runtime is {Environment.Version}/{RuntimeInformation.ProcessArchitecture}. " +
                "Unsupported test infrastructure must be verified before adding coverage; do not change the zero-byte budget.");
        return typeof(object).Assembly.GetType("System.Runtime.CompilerServices.CastHelpers")?
            .GetField("s_table", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new PlatformNotSupportedException("This runtime does not expose the verified CoreCLR cast-cache layout.");
    }

    private static int[] ReadCache(FieldInfo field)
    {
        var table = field.GetValue(null) as int[];
        if (table is null || table.Length < 18 || table[1] < 1 || table[1] >= RequiredCacheEntries ||
            ((table[1] + 1) & table[1]) != 0 || table.Length != (table[1] + 2) * 6 ||
            table[0] != 64 - BitOperations.Log2((uint)(table[1] + 1)))
            throw new PlatformNotSupportedException("The verified CoreCLR cast-cache array/header layout changed.");
        return table;
    }

    private static int PrimeRuntimeCastCache(FieldInfo field)
    {
        // These ordinary casts, on noncollectible types, naturally grow the runtime cache.
        // Private runtime reflection is READ ONLY. There is no injected table, reader-loop
        // retry, extra reader warmup, allocation exemption, or assertion-threshold adjustment.
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("ReadOwnershipAllocationStartup"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("startup");
        var elapsed = Stopwatch.StartNew();
        var count = 0;
        while (ReadCache(field).Length < CacheArrayLength &&
            count < MaximumPrimingTypes && elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            var type = module.DefineType($"UnrelatedCast{count}", TypeAttributes.Public).CreateType()!;
            _ = typeof(IFormattable).IsInstanceOfType(Activator.CreateInstance(type));
            count++;
        }
        if (ReadCache(field).Length != CacheArrayLength)
            throw new InvalidOperationException($"Runtime cast-cache startup did not establish the required 4096-entry table within " +
                $"{MaximumPrimingTypes} types / 15 seconds (types={count}, arrayLength={ReadCache(field).Length}).");
        return count;
    }
}
