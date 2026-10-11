using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Diagnostics.Runtime;

// Diagnostic-only, fixed synthetic dump. Never attaches, runs target code, reads strings,
// walks roots, follows delegates/continuations, or extracts arbitrary arrays or environment.
internal static class Program
{
    private const string ExpectedSha = "904d23ea529f02e7f8845edfcfa83fec03223e774c6c9cc65dabab740a1fad95";
    private const string ExpectedCoreClrBuildId = "79945f51fb2612f13b7667a10a8fd29122664791";
    private const string ExpectedDacSha = "54554757342c4325ab0eb8147afd1eafec1913fae6b2739cde225387673bdd23";
    private const int MaxObjects = 512, MaxPerRootType = 64, MaxFields = 160;
    private const int MaxObjectDepth = 4, MaxStructDepth = 8;
    private static readonly HashSet<string> Roots = new(StringComparer.Ordinal)
    {
        "SharpLink.Runtime.StreamFlowController+CreditWaiter",
        "SharpLink.Runtime.StreamFlowController+SendState",
        "SharpLink.Runtime.StreamFlowController",
        "SharpLink.Server.ServerCallCancellationState",
        "SharpLink.Client.ClientConnection",
        "SharpLink.Client.PendingRequestTable",
        "SharpLink.Runtime.PreCreditSerializedBudget",
        "SharpLink.Runtime.RpcSession+SendPump",
        "SharpLink.Runtime.PooledAsyncStreamDispatcher<System.Int32>",
        "SharpLink.DynamicPlugin.DynamicPluginService"
    };
    private static readonly (ulong Address, string Label, string TypeFragment)[] Known =
    {
        (0x7efd1b88c588, "helper", "SharpLink.IntegrationTests.RuntimeAssemblyIntegrationTests+<ExecuteDynamicStreamExitAndUnloadAsync>d__11"),
        (0x7efd1b98ea08, "credit-waiter-async", "SharpLink.Runtime.StreamFlowController+CreditWaiter+<WaitAsync>d__8"),
        (0x7efd1b8c2aa8, "outbound-pump-async", "SharpLink.Runtime.RpcSession+<PumpGeneratedOutboundStreamAsync>d__141"),
        (0x7efd1b8c2320, "awaited-task", "System.Threading.Tasks.Task"),
        (0x7efd1b8c2a40, "service-iterator-async", "SharpLink.DynamicPlugin.DynamicPluginService+<ServerStreamAsync>d__33")
    };
    private static readonly HashSet<string> FollowFields = new(StringComparer.Ordinal)
    {
        "Completion", "Owner", "Node", "Next", "_invocationCancellation", "_source",
        "m_task", "_task", "_pumpTask", "_capacityChanged", "_remoteTerminalPublication",
        "_cancellation", "_linkedCancellation", "lifetimeCancellation", "<lifetimeCancellation>5__2",
        "_linkedTokenSource", "<>x__combinedTokens", "_waiters", "head", "next", "prev", "list", "item",
        "_head", "_tail", "_next", "_prev", "_owner", "_node", "Node", "Registrations", "Source",
        "_registrations", "Callbacks", "Next", "Prev", "_reg1", "_reg2",
        "_serverStreamDisposed", "_blockStarted", "_blockRelease", "_synchronousBlockStarted",
        "_synchronousBlockRelease", "_rejectResponseStarted", "_rejectResponseRelease",
        "<PendingCalls>k__BackingField", "_pendingCalls", "_wakeup", "_promise",
        "_disposeCompletion", "<>4__this", "StateMachine", "_obj", "_object", "<lifetimeCancellation>5__4", "callState", "<callState>5__3"
    };
    private static readonly HashSet<string> ServiceStatics = new(StringComparer.Ordinal)
    {
        "_serverStreamDisposed", "_blockStarted", "_blockRelease", "_synchronousBlockStarted",
        "_synchronousBlockRelease", "_rejectResponseStarted", "_rejectResponseRelease",
        "_created", "_disposed", "_notifications"
    };
    private static DataTarget Target = null!;
    private static ClrHeap Heap = null!;
    private static readonly Queue<(ulong Address, string Why, int Depth)> Queue = new();
    private static readonly HashSet<ulong> Seen = new();
    private static readonly HashSet<ulong> StaticTypes = new();
    private static int Errors, Bounds;

    private static void Emit(object value) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); }
    private static string Hex(ulong x) => $"0x{x:x16}";
    private static string Canonical(string name) => name.Replace("SharpLink.Runtime.PooledAsyncStreamDispatcher`1[[System.Int32, System.Private.CoreLib]]", "SharpLink.Runtime.PooledAsyncStreamDispatcher<System.Int32>", StringComparison.Ordinal);
    private static bool RootType(string name) => Roots.Contains(Canonical(name));
    private static bool FollowType(string name) => RootType(name)
        || name == "System.Threading.CancellationTokenSource"
        || name.StartsWith("System.Threading.CancellationTokenSource+", StringComparison.Ordinal)
        || name == "System.Threading.Tasks.TaskCompletionSource"
        || name.StartsWith("System.Threading.Tasks.TaskCompletionSource<", StringComparison.Ordinal)
        || name.StartsWith("System.Threading.Tasks.TaskCompletionSource`", StringComparison.Ordinal)
        || name == "System.Threading.Tasks.Task"
        || name.StartsWith("System.Threading.Tasks.Task<", StringComparison.Ordinal)
        || name.StartsWith("System.Threading.Tasks.Task`", StringComparison.Ordinal)
        || name.StartsWith("System.Collections.Generic.LinkedList", StringComparison.Ordinal) && name.Contains("SharpLink.Runtime.StreamFlowController+CreditWaiter", StringComparison.Ordinal)
        || name == "SharpLink.DynamicPlugin.DynamicPluginService+<ServerStreamAsync>d__33"
        || name == "SharpLink.Runtime.PreCreditSerializedBudget+Waiter"
        || name == "SharpLink.Runtime.RpcSession+SendPump+WakeupSignal";
    private static void Error(string context, Exception ex)
    {
        Errors++;
        Console.Error.WriteLine($"[{context}] {ex}"); Console.Error.Flush();
        Emit(new { kind = "error", context, exception = ex.GetType().FullName, message = ex.Message });
    }
    private static void Bound(string context)
    {
        Bounds++;
        Emit(new { kind = "bound", context });
    }
    private static byte[] Read(ulong address, int count)
    {
        if (address == 0) throw new InvalidDataException("Zero field address; metadata/static initialization unavailable.");
        var bytes = new byte[count];
        int actual = Target.DataReader.Read(address, bytes.AsSpan());
        if (actual != count) throw new EndOfStreamException($"Unreadable field at {Hex(address)}: expected {count}, read {actual}. Never interpreted as zero/null.");
        return bytes;
    }
    private static ulong Pointer(ulong address) => BinaryPrimitives.ReadUInt64LittleEndian(Read(address, 8));
    private static string Scalar(ClrElementType element, ulong address)
    {
        object value = element switch
        {
            ClrElementType.Boolean => Read(address, 1)[0] == 0 ? "false" : "true",
            ClrElementType.Int8 => unchecked((sbyte)Read(address, 1)[0]),
            ClrElementType.UInt8 => Read(address, 1)[0],
            ClrElementType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(Read(address, 2)),
            ClrElementType.UInt16 or ClrElementType.Char => BinaryPrimitives.ReadUInt16LittleEndian(Read(address, 2)),
            ClrElementType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(Read(address, 4)),
            ClrElementType.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(Read(address, 4)),
            ClrElementType.Int64 or ClrElementType.NativeInt => BinaryPrimitives.ReadInt64LittleEndian(Read(address, 8)),
            ClrElementType.UInt64 or ClrElementType.NativeUInt => BinaryPrimitives.ReadUInt64LittleEndian(Read(address, 8)),
            ClrElementType.Float => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(Read(address, 4))),
            ClrElementType.Double => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(Read(address, 8))),
            ClrElementType.Pointer or ClrElementType.FunctionPointer or ClrElementType.ByRef => Hex(Pointer(address)),
            _ => throw new NotSupportedException($"Unsupported scalar element {element}; no guessed interpretation.")
        };
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }
    private static void Field(ClrField field, ulong address, string path, int objectDepth, int structDepth)
    {
        string name = field.Name ?? throw new InvalidDataException("Missing field name.");
        string? typeName = field.Type?.Name;
        if (field.IsObjectReference)
        {
            ulong reference = Pointer(address);
            string? actualType = reference == 0 ? null : Heap.GetObject(reference).Type?.Name;
            bool eligible = reference != 0 && FollowFields.Contains(name) && actualType is not null && FollowType(actualType);
            Emit(new { kind = "reference", path, fieldAddress = Hex(address), declaredType = typeName, reference = Hex(reference), actualType, followed = eligible && objectDepth < MaxObjectDepth, stringContent = "never-read" });
            if (reference != 0 && actualType is null) throw new InvalidDataException($"Unresolved object type at {Hex(reference)}.");
            if (eligible && !Seen.Contains(reference))
            {
                if (objectDepth >= MaxObjectDepth) Bound(path + ": object-depth");
                else Queue.Enqueue((reference, path, objectDepth + 1));
            }
        }
        else if (field.ElementType == ClrElementType.Struct)
        {
            Emit(new { kind = "value-type", path, fieldAddress = Hex(address), type = typeName });
            if (structDepth >= MaxStructDepth) { Bound(path + ": struct-depth"); return; }
            var type = field.Type ?? throw new InvalidDataException($"Unresolved value type for {path}.");
            Fields(type, address, true, path, objectDepth, structDepth + 1);
        }
        else
        {
            string value = Scalar(field.ElementType, address);
            Emit(new { kind = "scalar", path, fieldAddress = Hex(address), type = typeName, element = field.ElementType.ToString(), value });
        }
    }
    private static void Fields(ClrType type, ulong address, bool interior, string path, int objectDepth, int structDepth)
    {
        var fields = type.Fields;
        if (fields.Length > MaxFields) { Bound(path + ": field-count"); }
        if (fields.Length == 0 && type.Name != "SharpLink.DynamicPlugin.DynamicPluginService" && type.Name != "System.Threading.Tasks.VoidTaskResult")
            Error(path, new InvalidDataException("No instance field metadata available; cannot assume no state."));
        foreach (var field in fields.Take(MaxFields))
        {
            string child = path + "." + (field.Name ?? "<missing-name>");
            try { Field(field, field.GetAddress(address, interior), child, objectDepth, structDepth); }
            catch (Exception ex) { Error(child, ex); }
        }
    }
    private static void ServiceStaticFields(ClrType type)
    {
        if (type.Name != "SharpLink.DynamicPlugin.DynamicPluginService" || !StaticTypes.Add(type.MethodTable)) return;
        var fields = type.StaticFields.Where(f => f.Name is not null && ServiceStatics.Contains(f.Name)).ToArray();
        if (fields.Length != ServiceStatics.Count) Error("service-statics", new InvalidDataException($"Expected {ServiceStatics.Count} named statics; found {fields.Length}."));
        foreach (var field in fields)
        {
            string path = $"static:{Hex(type.MethodTable)}.{field.Name}";
            try { Field(field, field.GetAddress(type.Module.AppDomain), path, 0, 0); }
            catch (Exception ex) { Error(path, ex); }
        }
    }
    private static void Drain()
    {
        while (Queue.TryDequeue(out var item))
        {
            if (Seen.Contains(item.Address)) continue;
            if (Seen.Count >= MaxObjects) { Bound("total-object-count"); Queue.Clear(); return; }
            Seen.Add(item.Address);
            try
            {
                ClrObject obj = Heap.GetObject(item.Address);
                ClrType type = obj.Type ?? throw new InvalidDataException("Missing object type.");
                var known = Known.FirstOrDefault(x => x.Address == item.Address);
                if (known.Address != 0 && (type.Name is null || !type.Name.Contains(known.TypeFragment, StringComparison.Ordinal)))
                    throw new InvalidDataException($"Known object {known.Label} has unexpected type {type.Name}.");
                if (known.Address == 0 && !FollowType(type.Name ?? "")) throw new InvalidDataException("Object outside fixed type allowlist.");
                Emit(new { kind = "object", address = Hex(item.Address), why = item.Why, depth = item.Depth, type = type.Name, methodTable = Hex(type.MethodTable), module = Path.GetFileName(type.Module.Name), moduleAddress = Hex(type.Module.Address), type.IsCollectible, assemblyLoadContext = Hex(type.AssemblyLoadContextAddress), loaderAllocatorHandle = Hex(type.LoaderAllocatorHandle) });
                Fields(type, item.Address, false, Hex(item.Address), item.Depth, 0);
                ServiceStaticFields(type);
            }
            catch (Exception ex) { Error(Hex(item.Address), ex); }
        }
    }
    // Read only bounded ELF headers and GNU build-id notes from the local runtime binary.
    // No executable loading, arbitrary strings, shell, network, or dump mutation.
    private static string LocalElfBuildId(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length is < 64 or > 134217728) throw new InvalidDataException("Unexpected ELF file size.");
        byte[] At(ulong offset, int byteCount)
        {
            if (byteCount < 0 || offset > (ulong)file.Length || (ulong)byteCount > (ulong)file.Length - offset)
                throw new InvalidDataException("ELF read outside file.");
            byte[] data = new byte[byteCount];
            file.Position = checked((long)offset);
            file.ReadExactly(data);
            return data;
        }
        var h = At(0, 64);
        if (h[0] != 0x7f || h[1] != (byte)'E' || h[2] != (byte)'L' || h[3] != (byte)'F'
            || h[4] != 2 || h[5] != 1 || BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(18)) != 62)
            throw new InvalidDataException("Expected little-endian Linux x64 ELF.");
        ulong phoff = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(32));
        int stride = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(54));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(56));
        if (stride is < 56 or > 128 || count is < 1 or > 512)
            throw new InvalidDataException("ELF program-header bounds exceeded.");
        string? found = null;
        int totalNoteBytes = 0;
        for (int i = 0; i < count; i++)
        {
            byte[] ph = At(checked(phoff + (ulong)(i * stride)), 56);
            if (BinaryPrimitives.ReadUInt32LittleEndian(ph) != 4) continue; // PT_NOTE only.
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(ph.AsSpan(8));
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(ph.AsSpan(32));
            if (length > 1048576 || (totalNoteBytes += checked((int)length)) > 4194304)
                throw new InvalidDataException("ELF note bounds exceeded.");
            byte[] notes = At(offset, (int)length);
            int pos = 0;
            while (pos < notes.Length)
            {
                if (notes.Length - pos < 12) throw new InvalidDataException("Truncated ELF note header.");
                uint nameSize = BinaryPrimitives.ReadUInt32LittleEndian(notes.AsSpan(pos));
                uint descSize = BinaryPrimitives.ReadUInt32LittleEndian(notes.AsSpan(pos + 4));
                uint kind = BinaryPrimitives.ReadUInt32LittleEndian(notes.AsSpan(pos + 8));
                if (nameSize > 1048576 || descSize > 1048576) throw new InvalidDataException("ELF note field bound exceeded.");
                int nameStart = pos + 12;
                int descStart = checked(nameStart + (int)((nameSize + 3) & ~3u));
                int end = checked(descStart + (int)((descSize + 3) & ~3u));
                if (end > notes.Length) throw new InvalidDataException("ELF note outside segment.");
                if (kind == 3 && nameSize == 4 && notes.AsSpan(nameStart, 4).SequenceEqual(new byte[] { 71, 78, 85, 0 }))
                {
                    if (descSize is < 1 or > 64) throw new InvalidDataException("Invalid GNU build-id size.");
                    string id = Convert.ToHexString(notes.AsSpan(descStart, (int)descSize)).ToLowerInvariant();
                    if (found is not null && found != id) throw new InvalidDataException("Conflicting GNU build-id notes.");
                    found = id;
                }
                pos = end;
            }
        }
        return found ?? throw new InvalidDataException("Local libcoreclr has no GNU build-id.");
    }
    private static ClrRuntime CreateVerifiedRuntime(ClrInfo info, string dacPath)
    {
        // Normal nonzero versions retain ClrMD's own strict comparison unchanged.
        if (!info.Version.Equals(new Version())) return info.CreateRuntime(dacPath);
        string capturedId = Convert.ToHexString(info.ModuleInfo.BuildId.ToArray()).ToLowerInvariant();
        if (capturedId != ExpectedCoreClrBuildId)
            throw new InvalidDataException($"Unknown-version fallback denied: captured coreclr build-id {capturedId}.");
        dacPath = Path.GetFullPath(dacPath);
        if (Path.GetFileName(dacPath) != "libmscordaccore.so") throw new InvalidDataException("Unexpected DAC filename.");
        string corePath = Path.Combine(Path.GetDirectoryName(dacPath)!, "libcoreclr.so");
        string localId = LocalElfBuildId(corePath);
        if (localId != capturedId) throw new InvalidDataException($"Unknown-version fallback denied: local coreclr build-id {localId}.");
        string dacSha;
        using (var input = File.OpenRead(dacPath)) dacSha = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        if (dacSha != ExpectedDacSha) throw new InvalidDataException($"Unknown-version fallback denied: DAC SHA-256 {dacSha}.");
        Emit(new { kind = "runtime-identity", capturedVersion = info.Version.ToString(), capturedCoreClrBuildId = capturedId,
            localCoreClrBuildId = localId, dacSha256 = dacSha, expectedRuntime = "10.0.12", expectedRuntimeCommit = "95017c711e",
            ignoreVersionMismatch = true, reason = "ClrMD ELF dump version unavailable (0.0); captured/local coreclr GNU build IDs and pinned official-runtime DAC SHA-256 independently matched." });
        // The fallback is admitted only for exactly Version(0,0), with all identity checks above.
        // Do not disable signature verification or retry this path for real nonzero mismatches.
        return info.CreateRuntime(dacPath, ignoreMismatch: true);
    }
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Usage: ManagedAbiDumpReader <024-round-3.dmp> <matching-libmscordaccore.so>");
            string hash;
            using (var input = File.OpenRead(args[0])) hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            if (hash != ExpectedSha) throw new InvalidDataException($"Dump hash mismatch: {hash}.");
            Emit(new { kind = "start", dumpSha256 = hash, clrmd = typeof(DataTarget).Assembly.FullName, maxObjectDepth = MaxObjectDepth, maxStructDepth = MaxStructDepth, readOnly = true });
            // No symbol-server/network lookups; required modules must already be restored locally.
            using var target = DataTarget.LoadDump(args[0], new DataTargetOptions { SymbolPaths = Array.Empty<string>() });
            Target = target;
            if (target.DataReader.PointerSize != 8 || !BitConverter.IsLittleEndian) throw new NotSupportedException("This fixed Linux x64 dump reader requires little-endian 64-bit target and host.");
            if (target.ClrVersions.Length != 1) throw new InvalidDataException($"Expected one CLR, got {target.ClrVersions.Length}.");
            using var runtime = CreateVerifiedRuntime(target.ClrVersions[0], args[1]);
            Heap = runtime.Heap;
            Emit(new { kind = "runtime", version = target.ClrVersions[0].Version.ToString(), Heap.CanWalkHeap });
            foreach (var known in Known) Queue.Enqueue((known.Address, known.Label, 0));
            Drain(); // Preserve valuable fixed-address findings even if heap enumeration later fails.
            if (!Heap.CanWalkHeap) throw new InvalidDataException("Heap is not walkable; root-type census incomplete.");
            var counts = Roots.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);
            foreach (var obj in Heap.EnumerateObjects())
            {
                string? name = obj.Type?.Name;
                if (name is null || !RootType(name)) continue;
                int count = ++counts[Canonical(name)];
                if (count > MaxPerRootType) { if (count == MaxPerRootType + 1) Bound(name + ": root-type-count"); continue; }
                Queue.Enqueue((obj.Address, "allowlisted-heap-type", 0));
            }
            Drain();
            foreach (var (type, count) in counts) Emit(new { kind = "census", type, count });
            // An absent type is a finding, not proof of unload or of a strong/weak root relationship.
            Emit(new { kind = "summary", status = Errors == 0 && Bounds == 0 ? "complete-within-declared-scope" : "partial", errors = Errors, bounds = Bounds, objects = Seen.Count, stringReads = 0, rootEnumeration = false, arrayElementReads = 0, targetExecution = false });
            return Errors == 0 && Bounds == 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            Error("fatal", ex);
            Emit(new { kind = "summary", status = "failed-or-partial", errors = Errors, bounds = Bounds, objects = Seen.Count });
            return 1;
        }
    }
}
