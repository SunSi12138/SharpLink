using System.IO.MemoryMappedFiles;

namespace SharpLink.Runtime;

internal sealed unsafe class SharedMemoryMapping : IAsyncDisposable
{
    private readonly FileStream _file;
    private readonly MemoryMappedFile _mappedFile;
    private readonly MemoryMappedViewAccessor _view;
    private readonly string? _pathToDelete;
    private readonly UnmanagedMemoryManager _memoryManager;
    private byte* _pointer;
    private int _disposed;

    private SharedMemoryMapping(
        FileStream file,
        MemoryMappedFile mappedFile,
        MemoryMappedViewAccessor view,
        byte* pointer,
        int length,
        string? pathToDelete)
    {
        _file = file;
        _mappedFile = mappedFile;
        _view = view;
        _pointer = pointer;
        Length = length;
        _pathToDelete = pathToDelete;
        _memoryManager = new UnmanagedMemoryManager(pointer, length);
    }

    public int Length { get; }
    public Memory<byte> Memory => _memoryManager.Memory;
    internal byte* Pointer => _pointer;

    public static SharedMemoryMapping CreateServer(int capacity, ReadOnlySpan<byte> nonce, out string path)
    {
        var directory = GetMappingDirectory();
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        CleanupUnownedMappings(directory);

        path = Path.Combine(directory, $"{Guid.NewGuid():N}.shm");
        var length = SharedMemoryLayout.GetMappingLength(capacity);
        var options = FileOptions.Asynchronous | FileOptions.RandomAccess;
        if (OperatingSystem.IsWindows())
            options |= FileOptions.DeleteOnClose;

        FileStream? file = null;
        try
        {
            file = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                options);
            file.SetLength(length);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var mapping = Create(file, length, path);
            SharedMemoryLayout.Initialize(mapping, capacity, nonce);
            return mapping;
        }
        catch
        {
            file?.Dispose();
            TryDelete(path);
            throw;
        }
    }

    public static SharedMemoryMapping OpenClient(string path, int capacity, ReadOnlySpan<byte> nonce)
    {
        ValidateMappingPath(path);
        var length = SharedMemoryLayout.GetMappingLength(capacity);
        FileStream? file = null;
        try
        {
            file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                             UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                {
                    throw new UnauthorizedAccessException("Shared-memory mapping permissions are not user-only.");
                }
            }
            if (file.Length != length)
                throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Shared-memory mapping length did not match the negotiated layout.");
            var mapping = Create(file, length, pathToDelete: null);
            SharedMemoryLayout.Validate(mapping, capacity, nonce);
            return mapping;
        }
        catch
        {
            file?.Dispose();
            throw;
        }
    }

    internal static void ValidateMappingPath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new SharpLinkException(SharpLinkErrorCode.PermissionDenied, "Shared-memory mapping path was not fully qualified.");
        var fullPath = Path.GetFullPath(path);
        var expectedDirectory = GetMappingDirectory();
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(fullPath), expectedDirectory, comparison) ||
            !Guid.TryParseExact(Path.GetFileNameWithoutExtension(fullPath), "N", out _) ||
            !string.Equals(Path.GetExtension(fullPath), ".shm", comparison))
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.PermissionDenied,
                "Shared-memory mapping path is outside the user-only transport directory.");
        }
        if (new FileInfo(fullPath).LinkTarget is not null)
            throw new SharpLinkException(SharpLinkErrorCode.PermissionDenied, "Shared-memory mapping path cannot be a symbolic link.");
    }

    internal static string GetMappingDirectory()
        => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sharplink-shm"));

    private static void CleanupUnownedMappings(string directory)
    {
        foreach (var candidate in Directory.EnumerateFiles(directory, "*.shm", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using (new FileStream(
                           candidate,
                           FileMode.Open,
                           FileAccess.ReadWrite,
                           FileShare.None,
                           bufferSize: 1,
                           FileOptions.DeleteOnClose))
                {
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void UnlinkAfterClientOpened()
    {
        if (!OperatingSystem.IsWindows() && _pathToDelete is not null)
            TryDelete(_pathToDelete);
    }

    private static SharedMemoryMapping Create(FileStream file, int length, string? pathToDelete)
    {
        var mappedFile = MemoryMappedFile.CreateFromFile(
            file,
            mapName: null,
            capacity: length,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            leaveOpen: true);
        MemoryMappedViewAccessor? view = null;
        byte* pointer = null;
        try
        {
            view = mappedFile.CreateViewAccessor(0, length, MemoryMappedFileAccess.ReadWrite);
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            pointer += view.PointerOffset;
            return new SharedMemoryMapping(file, mappedFile, view, pointer, length, pathToDelete);
        }
        catch
        {
            if (pointer is not null && view is not null)
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            view?.Dispose();
            mappedFile.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        _memoryManager.Invalidate();
        if (_pointer is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
        }
        _view.Dispose();
        _mappedFile.Dispose();
        _file.Dispose();
        if (_pathToDelete is not null)
            TryDelete(_pathToDelete);
        return ValueTask.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
        }
    }

    private sealed unsafe class UnmanagedMemoryManager(byte* pointer, int length) : MemoryManager<byte>
    {
        private byte* _pointer = pointer;

        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(_pointer is null, this);
            return new Span<byte>(_pointer, length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if ((uint)elementIndex > (uint)length)
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            ObjectDisposedException.ThrowIf(_pointer is null, this);
            return new MemoryHandle(_pointer + elementIndex, default, this);
        }

        public override void Unpin()
        {
        }

        internal void Invalidate() => _pointer = null;

        protected override void Dispose(bool disposing) => _pointer = null;
    }
}

internal static unsafe class SharedMemoryLayout
{
    public const int HeaderBytes = 4096;
    private const ulong Magic = 0x314D48534B4E4C53UL;
    private const int Version = 1;
    private const int MagicOffset = 0;
    private const int VersionOffset = 8;
    private const int CapacityOffset = 12;
    private const int NonceOffset = 16;
    public const int NonceBytes = 32;
    private const int ClientToServerDescriptorOffset = 128;
    private const int ServerToClientDescriptorOffset = 1024;

    public static int GetMappingLength(int capacity)
        => checked(HeaderBytes + checked(capacity * 2));

    public static void Initialize(SharedMemoryMapping mapping, int capacity, ReadOnlySpan<byte> nonce)
    {
        if (nonce.Length != NonceBytes)
            throw new ArgumentException($"Nonce must contain {NonceBytes} bytes.", nameof(nonce));
        mapping.Memory.Span[..HeaderBytes].Clear();
        var header = mapping.Memory.Span[..HeaderBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(header[MagicOffset..], Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header[VersionOffset..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(header[CapacityOffset..], capacity);
        nonce.CopyTo(header[NonceOffset..(NonceOffset + NonceBytes)]);
    }

    public static void Validate(SharedMemoryMapping mapping, int capacity, ReadOnlySpan<byte> nonce)
    {
        var header = mapping.Memory.Span[..HeaderBytes];
        if (BinaryPrimitives.ReadUInt64LittleEndian(header[MagicOffset..]) != Magic ||
            BinaryPrimitives.ReadInt32LittleEndian(header[VersionOffset..]) != Version ||
            BinaryPrimitives.ReadInt32LittleEndian(header[CapacityOffset..]) != capacity ||
            !header[NonceOffset..(NonceOffset + NonceBytes)].SequenceEqual(nonce))
        {
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Shared-memory mapping header validation failed.");
        }
    }

    public static SharedMemoryRingDirection GetDirection(
        SharedMemoryMapping mapping,
        bool clientToServer)
    {
        var capacity = BinaryPrimitives.ReadInt32LittleEndian(mapping.Memory.Span[CapacityOffset..]);
        var descriptorOffset = clientToServer
            ? ClientToServerDescriptorOffset
            : ServerToClientDescriptorOffset;
        var dataOffset = clientToServer ? HeaderBytes : checked(HeaderBytes + capacity);
        return new SharedMemoryRingDirection(mapping, descriptorOffset, dataOffset, capacity);
    }
}

internal sealed unsafe class SharedMemoryRingDirection
{
    private const int WritePositionOffset = 0;
    private const int ReadPositionOffset = 128;
    private const int ReaderWaitingOffset = 256;
    private const int WriterWaitingOffset = 384;
    private const int WriterClosedOffset = 512;
    private const int ReaderClosedOffset = 640;

    private readonly SharedMemoryMapping _mapping;
    private readonly int _descriptorOffset;

    public SharedMemoryRingDirection(
        SharedMemoryMapping mapping,
        int descriptorOffset,
        int dataOffset,
        int capacity)
    {
        _mapping = mapping;
        _descriptorOffset = descriptorOffset;
        Capacity = capacity;
        Mask = capacity - 1;
        Memory = mapping.Memory.Slice(dataOffset, capacity);
    }

    public int Capacity { get; }
    public int Mask { get; }
    public Memory<byte> Memory { get; }

    public long ReadWritePosition() => Volatile.Read(ref WritePosition);
    public long ReadReadPosition() => Volatile.Read(ref ReadPosition);
    public void PublishWritePosition(long value) => Volatile.Write(ref WritePosition, value);
    public void PublishReadPosition(long value) => Volatile.Write(ref ReadPosition, value);
    public bool IsWriterClosed => Volatile.Read(ref WriterClosed) != 0;
    public bool IsReaderClosed => Volatile.Read(ref ReaderClosed) != 0;
    public void CloseWriter() => Volatile.Write(ref WriterClosed, 1);
    public void CloseReader() => Volatile.Write(ref ReaderClosed, 1);
    public void SetReaderWaiting() => Volatile.Write(ref ReaderWaiting, 1);
    public void SetWriterWaiting() => Volatile.Write(ref WriterWaiting, 1);
    public void ClearReaderWaiting() => Volatile.Write(ref ReaderWaiting, 0);
    public void ClearWriterWaiting() => Volatile.Write(ref WriterWaiting, 0);
    public bool TakeReaderWaiting() => Interlocked.Exchange(ref ReaderWaiting, 0) != 0;
    public bool TakeWriterWaiting() => Interlocked.Exchange(ref WriterWaiting, 0) != 0;

    public int GetAvailableBytes(long writePosition, long readPosition)
    {
        var available = unchecked((ulong)(writePosition - readPosition));
        if (available > (ulong)Capacity)
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "Shared-memory ring cursors exceeded the negotiated capacity.");
        return (int)available;
    }

    private ref long WritePosition
        => ref Unsafe.AsRef<long>(_mapping.Pointer + _descriptorOffset + WritePositionOffset);
    private ref long ReadPosition
        => ref Unsafe.AsRef<long>(_mapping.Pointer + _descriptorOffset + ReadPositionOffset);
    private ref int ReaderWaiting
        => ref Unsafe.AsRef<int>(_mapping.Pointer + _descriptorOffset + ReaderWaitingOffset);
    private ref int WriterWaiting
        => ref Unsafe.AsRef<int>(_mapping.Pointer + _descriptorOffset + WriterWaitingOffset);
    private ref int WriterClosed
        => ref Unsafe.AsRef<int>(_mapping.Pointer + _descriptorOffset + WriterClosedOffset);
    private ref int ReaderClosed
        => ref Unsafe.AsRef<int>(_mapping.Pointer + _descriptorOffset + ReaderClosedOffset);
}
