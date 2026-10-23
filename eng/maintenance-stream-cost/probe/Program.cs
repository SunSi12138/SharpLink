using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

internal static partial class Program
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static int _sink;
    public static async Task Main(string[] args)
    {
        if (args[0] == "metadata")
        {
            CaptureMetadata(args[1]);
            return;
        }
        var count = int.Parse(args[1]);
        var micro = new { NotMeasured = true };
        var network = await ExerciseNetwork(count);
        File.WriteAllText(args[2], JsonSerializer.Serialize(new
        {
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            RuntimeBinding = CaptureRuntimeBinding(),
            Settings = new[] { "DOTNET_TieredCompilation", "DOTNET_TieredPGO", "DOTNET_TC_QuickJit", "DOTNET_TC_QuickJitForLoops", "DOTNET_TC_CallCounting", "DOTNET_TC_CallCountingDelayMs", "DOTNET_TC_CallCountThreshold", "DOTNET_ReadyToRun" }
                .ToDictionary(key => key, Environment.GetEnvironmentVariable),
            Count = count,
            Micro = micro,
            Network = network,
            Sink = _sink
        }, Json));
    }

    private static object CaptureRuntimeBinding() => new
    {
        Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimeVersion = Environment.Version.ToString(),
        CoreLibPath = typeof(object).Assembly.Location,
        HostPath = Environment.ProcessPath
    };

    private static object ExerciseMicro(int count)
    {
        var limits = new SharpLinkProtocolOptions();
        var options = new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.None, limits.MaxFramePayloadBytes, 65536, 65536);
        var session = (RpcSession)RuntimeHelpers.GetUninitializedObject(typeof(RpcSession));
        typeof(RpcSession).GetField("_protocolState", All)!.SetValue(session,
            new RpcSessionProtocolState(RpcSessionProtocolPhase.Ready, options, null));
        var request = Frame(ProtocolV2FrameType.Request, ProtocolV2Constants.RequestPrefixBytes);
        var response = Frame(ProtocolV2FrameType.Response, sizeof(int));
        var parseType = typeof(ProtocolV2FrameParser).GetMethod("ParseType", All)!.CreateDelegate<Func<byte, ProtocolV2FrameType>>();
        var header = typeof(ProtocolV2FrameParser).GetMethod("ValidateHeader", All)!.CreateDelegate<Action<ProtocolV2FrameType, ProtocolV2FrameFlags, ulong>>();
        var payload = typeof(ProtocolV2FrameParser).GetMethod("ValidatePayloadShape", All)!.CreateDelegate<Action<ProtocolV2FrameType, ProtocolV2FrameFlags, ReadOnlySequence<byte>, SharpLinkProtocolOptions>>();
        var rows = new List<object>();
        // Warmup and two separated blocks allow Tier1 promotion; all arms receive exactly
        // the same frame mix. This is not a throughput benchmark and has no latency claim.
        for (int block = 0; block < 3; block++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < count; i++)
            {
                var bytes = (i & 1) == 0 ? request : response;
                var type = (i & 1) == 0 ? ProtocolV2FrameType.Request : ProtocolV2FrameType.Response;
                var sequence = new ReadOnlySequence<byte>(bytes);
                if (!session.TryReadInboundFrame(ref sequence, limits, out var h, out var p) || !sequence.IsEmpty)
                    throw new InvalidOperationException("Ordinary frame parse failed.");
                session.EnsureInboundFrameAllowed(type);
                if (!RpcSessionProtocolRules.IsFrameAllowed(RpcSessionProtocolPhase.Ready, type) || !session.CanAcceptCalls)
                    throw new InvalidOperationException("Ordinary Ready frame disallowed.");
                if (parseType((byte)type) != type) throw new InvalidOperationException("ParseType mismatch.");
                header(type, ProtocolV2FrameFlags.None, 1);
                payload(type, ProtocolV2FrameFlags.None, p, limits);
                _sink += (int)h.Type;
            }
            // Read before allocating the result record; otherwise that record contaminates
            // every measured block by 32 bytes (retained in the initial negative samples).
            var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            rows.Add(new { Block = block, AllocatedBytes = allocatedBytes });
            Thread.Sleep(100);
        }
        return rows;
    }

    private static byte[] Frame(ProtocolV2FrameType type, int payloadLength)
    {
        var bytes = new byte[ProtocolV2Constants.HeaderBytes + payloadLength];
        bytes[0] = ProtocolV2Constants.Magic;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), payloadLength);
        bytes[5] = (byte)type;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(7), 1);
        return bytes;
    }

    private static void CaptureMetadata(string output)
    {
        var assemblies = new[] { typeof(ProtocolV2FrameParser).Assembly, typeof(SharpLinkClient).Assembly, typeof(SharpLinkServer).Assembly };
        var names = new HashSet<string>
        {
            "SharpLink.Runtime.RpcSession", "SharpLink.Runtime.RpcSessionProtocolState", "SharpLink.Runtime.NegotiatedSessionOptions",
            "SharpLink.Client.ClientConnection", "SharpLink.Client.SharpLinkClient", "SharpLink.Server.SharpLinkServer",
            "SharpLink.Server.ServerConnectionState", "SharpLink.Server.AdmissionStateKernel"
        };
        var classes = assemblies.SelectMany(a => a.GetTypes()).Where(t => names.Contains(t.FullName!)).Select(Layout).ToArray();
        var states = assemblies.SelectMany(a => a.GetTypes()).Where(t => t.IsValueType &&
            (t.Name.Contains("ProcessRequestLoop") || t.Name.Contains("DispatchRequestAsync"))).Select(t => new
            {
                Type = t.FullName,
                Bytes = (int)typeof(Unsafe).GetMethod("SizeOf")!.MakeGenericMethod(t).Invoke(null, null)!,
                Fields = t.GetFields(All).Where(f => !f.IsStatic).Select(f => new { f.Name, Type = f.FieldType.ToString() }).ToArray()
            }).ToArray();
        var methodNames = new HashSet<string> { "ParseType", "ValidateHeader", "ValidatePayloadShape", "TryReadFrameCore", "TryReadFrame", "IsFrameAllowed",
            "EnsureInboundFrameAllowed", "EnsureOutboundFrameAllowed", "TryReadInboundFrame", "get_CanAcceptCalls", "get_ProtocolPhase", "get_IsConnected",
            "EnsureExpansion", "EnsureReconnectLoop", "GetReadyConnection", "GetRpcChannel", "TryReserveCallAdmission", "TryReserveOwnCallAdmission", "TryAcceptRequest", "DispatchRpc", "DispatchRpcAsync", "DispatchOneWayRpc", "InvokeServiceAsync" };
        var methods = assemblies.SelectMany(a => a.GetTypes()).SelectMany(t => t.GetMethods(All | BindingFlags.DeclaredOnly))
            .Where(m => methodNames.Contains(m.Name) || m.Name.EndsWith(".GetRpcChannel") || (m.Name == "MoveNext" &&
                (m.DeclaringType!.Name.Contains("ProcessRequestLoop") || m.DeclaringType!.Name.Contains("DispatchRequestAsync"))))
            .Where(m => m.GetMethodBody() is not null).Select(m => new
            {
                Key = $"{m.DeclaringType!.FullName}::{m}",
                Impl = m.GetMethodImplementationFlags().ToString(),
                Bytes = m.GetMethodBody()!.GetILAsByteArray()!.Length,
                MaxStack = m.GetMethodBody()!.MaxStackSize,
                Locals = m.GetMethodBody()!.LocalVariables.Select(l => l.LocalType.ToString()).ToArray(),
                IL = Decode(m)
            }).ToArray();
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            RuntimeBinding = CaptureRuntimeBinding(),
            Assemblies = assemblies.Select(a => new { a.FullName, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) }).ToArray(),
            Classes = classes, StateMachines = states, Methods = methods
        }, Json));
    }

    private static object Layout(Type t)
    {
        var sample = RuntimeHelpers.GetUninitializedObject(t);
        var roots = new object[1000];
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < roots.Length; i++) roots[i] = RuntimeHelpers.GetUninitializedObject(t);
        var bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / roots.Length;
        var offsets = t.GetFields(All).Where(f => !f.IsStatic).Select(f =>
        {
            var method = new DynamicMethod("Offset", typeof(long), new[] { typeof(object) }, true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, t); il.Emit(OpCodes.Ldflda, f); il.Emit(OpCodes.Conv_I);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Conv_I); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Ret);
            return new { f.Name, Type = f.FieldType.ToString(), Offset = method.CreateDelegate<Func<object, long>>()(sample) };
        }).ToArray();
        GC.KeepAlive(roots);
        return new { Type = t.FullName, ManagedAllocationBytes = bytes, Fields = offsets };
    }

    private static string[] Decode(MethodInfo m)
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => unchecked((ushort)o.Value));
        var bytes = m.GetMethodBody()!.GetILAsByteArray()!;
        var rows = new List<string>();
        for (int p = 0; p < bytes.Length;)
        {
            var offset = p;
            ushort code = bytes[p++];
            if (code == 0xfe) code = (ushort)(0xfe00 | bytes[p++]);
            var op = opcodes[code];
            object? operand = null;
            var count = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + BitConverter.ToInt32(bytes, p) * 4,
                _ => 4
            };
            if (count != 0) operand = Convert.ToHexString(bytes.AsSpan(p, count));
            if (op.OperandType is OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineType or OperandType.InlineTok)
            {
                try
                {
                    var member = m.Module.ResolveMember(BitConverter.ToInt32(bytes, p), m.DeclaringType?.GetGenericArguments(), m.GetGenericArguments());
                    operand = $"{member?.DeclaringType?.FullName}::{member}";
                }
                catch (ArgumentException) { }
            }
            else if (op.OperandType == OperandType.InlineString) operand = m.Module.ResolveString(BitConverter.ToInt32(bytes, p));
            rows.Add($"IL_{offset:X4}: {op.Name} {operand}".TrimEnd());
            p += count;
        }
        return rows.ToArray();
    }
}
