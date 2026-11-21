namespace SharpLink.Runtime;

internal enum NStudyInventoryStatus : int { Current, Unavailable, CapacityExceeded }
internal readonly record struct NStudyInventoryEntry(long Contract, RpcHash128 Hash, NStudyTarget Target);
internal readonly record struct NStudyObservedTarget(
    NStudyAuthority Authority, long Contract, RpcHash128 Hash, NStudyTarget Target);

internal sealed class NStudyInventorySnapshot(
    NStudyAuthority authority, long revision, NStudyInventoryStatus status, NStudyInventoryEntry[] entries)
{
    internal NStudyAuthority Authority { get; } = authority;
    internal long Revision { get; } = revision;
    internal NStudyInventoryStatus Status { get; } = status;
    internal IReadOnlyList<NStudyInventoryEntry> Entries { get; } = Array.AsReadOnly(entries);

    internal NStudyObservedTarget Select(long contract)
    {
        foreach (var entry in Entries)
        {
            if (entry.Contract == contract)
                return new(Authority, entry.Contract, entry.Hash, entry.Target);
        }
        throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "No current registration for the observed contract.");
    }
}

internal static partial class NStudyWire
{
    internal const ProtocolV2FrameType InventoryQuery = (ProtocolV2FrameType)251;
    internal const ProtocolV2FrameType InventoryReply = (ProtocolV2FrameType)252;
    internal const int InventoryHeaderBytes = 64;
    internal const int InventoryEntryBytes = 48;
    private const int InventoryMaxEntries = 4096;

    internal static int GetInventoryCapacity(int maxFramePayloadBytes)
        => Math.Clamp((maxFramePayloadBytes - InventoryHeaderBytes) / InventoryEntryBytes, 0, InventoryMaxEntries);

    private static bool IsInventoryLength(int size)
        => size >= InventoryHeaderBytes && size <= InventoryHeaderBytes + InventoryEntryBytes * InventoryMaxEntries &&
            (size - InventoryHeaderBytes) % InventoryEntryBytes == 0;

    internal static void WriteInventory(IBufferWriter<byte> writer, NStudyInventorySnapshot snapshot)
    {
        var size = InventoryHeaderBytes + InventoryEntryBytes * snapshot.Entries.Count;
        ValidateInventoryHeader(snapshot.Authority, snapshot.Revision, snapshot.Status, snapshot.Entries.Count, size);
        var bytes = writer.GetSpan(size)[..size];
        _ = snapshot.Authority.Server.TryWriteBytes(bytes[..16]);
        _ = snapshot.Authority.RegistryEpoch.TryWriteBytes(bytes[16..32]);
        _ = snapshot.Authority.PhysicalSession.TryWriteBytes(bytes[32..48]);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[48..], snapshot.Revision);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[56..], (int)snapshot.Status);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[60..], snapshot.Entries.Count);
        var offset = InventoryHeaderBytes;
        foreach (var entry in snapshot.Entries)
        {
            WriteContract(bytes[offset..], entry.Contract, entry.Hash);
            WriteTarget(bytes[(offset + 24)..], entry.Target);
            offset += InventoryEntryBytes;
        }
        writer.Advance(size);
    }

    internal static NStudyInventorySnapshot ReadInventory(ReadOnlySequence<byte> payload)
    {
        var size = checked((int)payload.Length);
        ValidateLength(InventoryReply, size);
        Span<byte> header = stackalloc byte[InventoryHeaderBytes];
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryCopyTo(header))
            throw Invalid("Private inventory header is incomplete.");
        reader.Advance(header.Length);
        var authority = new NStudyAuthority(new Guid(header[..16]), new Guid(header[16..32]), new Guid(header[32..48]));
        var revision = BinaryPrimitives.ReadInt64LittleEndian(header[48..]);
        var status = (NStudyInventoryStatus)BinaryPrimitives.ReadInt32LittleEndian(header[56..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header[60..]);
        ValidateInventoryHeader(authority, revision, status, count, size);
        var entries = new NStudyInventoryEntry[count];
        Span<byte> entry = stackalloc byte[InventoryEntryBytes];
        for (var i = 0; i < count; i++)
        {
            if (!reader.TryCopyTo(entry))
                throw Invalid("Private inventory entry is incomplete.");
            reader.Advance(entry.Length);
            var (contract, hash) = ReadContract(entry);
            var target = ReadTarget(entry[24..]);
            if (target == default || i != 0 && contract <= entries[i - 1].Contract)
                throw Invalid("Private inventory entries must have exact ordered identities.");
            entries[i] = new(contract, hash, target);
        }
        return new(authority, revision, status, entries);
    }

    private static void ValidateInventoryHeader(NStudyAuthority authority, long revision,
        NStudyInventoryStatus status, int count, int size)
    {
        if (authority.Server == Guid.Empty || authority.RegistryEpoch == Guid.Empty ||
            authority.PhysicalSession == Guid.Empty || revision < 0 ||
            status is < NStudyInventoryStatus.Current or > NStudyInventoryStatus.CapacityExceeded ||
            count < 0 || count > InventoryMaxEntries || size != InventoryHeaderBytes + InventoryEntryBytes * count ||
            status != NStudyInventoryStatus.Current && count != 0)
            throw Invalid("Invalid private inventory header.");
    }
}
