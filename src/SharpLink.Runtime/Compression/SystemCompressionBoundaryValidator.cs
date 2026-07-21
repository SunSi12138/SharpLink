namespace SharpLink.Runtime;

/// <summary>
/// Parses the self-delimiting parts of the built-in gzip and raw-deflate wire profiles.
/// Stream-based decompressors may read beyond the end marker into caller-owned trailing bytes,
/// so the underlying stream position cannot be used as the compressed-format boundary.
/// </summary>
internal static class SystemCompressionBoundaryValidator
{
    private const int MaxCodeBits = 15;
    private static readonly int[] SLengthBases =
    [
        3, 4, 5, 6, 7, 8, 9, 10,
        11, 13, 15, 17, 19, 23, 27, 31,
        35, 43, 51, 59, 67, 83, 99, 115,
        131, 163, 195, 227, 258
    ];
    private static readonly int[] SLengthExtraBits =
    [
        0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 2, 2,
        3, 3, 3, 3, 4, 4, 4, 4,
        5, 5, 5, 5, 0
    ];
    private static readonly int[] SDistanceBases =
    [
        1, 2, 3, 4, 5, 7, 9, 13,
        17, 25, 33, 49, 65, 97, 129, 193,
        257, 385, 513, 769, 1025, 1537, 2049, 3073,
        4097, 6145, 8193, 12289, 16385, 24577
    ];
    private static readonly int[] SDistanceExtraBits =
    [
        0, 0, 0, 0, 1, 1, 2, 2,
        3, 3, 4, 4, 5, 5, 6, 6,
        7, 7, 8, 8, 9, 9, 10, 10,
        11, 11, 12, 12, 13, 13
    ];
    private static readonly HuffmanTable SFixedLiterals = CreateFixedLiteralTable();
    private static readonly HuffmanTable SFixedDistances = CreateFixedDistanceTable();

    internal static int Validate(
        SystemCompressionKind kind,
        ReadOnlySequence<byte> payload,
        int maxOutputBytes,
        CancellationToken cancellationToken)
    {
        byte[]? contiguous = null;
        ReadOnlySpan<byte> source;
        if (payload.IsSingleSegment)
        {
            source = payload.FirstSpan;
        }
        else
        {
            contiguous = payload.ToArray();
            source = contiguous;
        }

        return kind switch
        {
            SystemCompressionKind.Gzip => ValidateGzip(source, maxOutputBytes, cancellationToken),
            SystemCompressionKind.Deflate => ValidateRawDeflate(source, maxOutputBytes, cancellationToken),
            _ => throw new InvalidOperationException("Compression boundary validation is not available for this kind.")
        };
    }

    private static int ValidateGzip(
        ReadOnlySpan<byte> source,
        int maxOutputBytes,
        CancellationToken cancellationToken)
    {
        var headerLength = ReadGzipHeader(source);
        var reader = new DeflateBitReader(source[headerLength..]);
        var written = ValidateDeflate(ref reader, maxOutputBytes, cancellationToken);
        var footerOffset = checked(headerLength + reader.ConsumedBytes);
        if (source.Length - footerOffset != sizeof(uint) + sizeof(uint))
            throw new InvalidDataException("Gzip payload is truncated or contains trailing data.");
        var footer = source[footerOffset..];
        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(footer[sizeof(uint)..]);
        if (declaredSize != checked((uint)written))
            throw new InvalidDataException("Gzip payload length trailer does not match its deflate stream.");
        return written;
    }

    private static int ValidateRawDeflate(
        ReadOnlySpan<byte> source,
        int maxOutputBytes,
        CancellationToken cancellationToken)
    {
        var reader = new DeflateBitReader(source);
        var written = ValidateDeflate(ref reader, maxOutputBytes, cancellationToken);
        if (reader.ConsumedBytes != source.Length)
            throw new InvalidDataException("Deflate payload contains trailing data.");
        return written;
    }

    private static int ReadGzipHeader(ReadOnlySpan<byte> source)
    {
        const byte extraFlag = 1 << 2;
        const byte nameFlag = 1 << 3;
        const byte commentFlag = 1 << 4;
        const byte headerChecksumFlag = 1 << 1;
        if (source.Length < 10 || source[0] != 0x1f || source[1] != 0x8b || source[2] != 8)
            throw new InvalidDataException("Gzip header is invalid or truncated.");
        var flags = source[3];
        if ((flags & 0xe0) != 0)
            throw new InvalidDataException("Gzip header contains reserved flags.");

        var offset = 10;
        if ((flags & extraFlag) != 0)
        {
            EnsureAvailable(source, offset, sizeof(ushort));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);
            offset = checked(offset + sizeof(ushort));
            EnsureAvailable(source, offset, extraLength);
            offset += extraLength;
        }
        if ((flags & nameFlag) != 0)
            offset = SkipZeroTerminated(source, offset);
        if ((flags & commentFlag) != 0)
            offset = SkipZeroTerminated(source, offset);
        if ((flags & headerChecksumFlag) != 0)
        {
            EnsureAvailable(source, offset, sizeof(ushort));
            offset += sizeof(ushort);
        }
        return offset;
    }

    private static int SkipZeroTerminated(ReadOnlySpan<byte> source, int offset)
    {
        while (offset < source.Length)
        {
            if (source[offset++] == 0)
                return offset;
        }
        throw new InvalidDataException("Gzip header text field is truncated.");
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> source, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > source.Length - count)
            throw new InvalidDataException("Gzip header is truncated.");
    }

    private static int ValidateDeflate(
        ref DeflateBitReader reader,
        int maxOutputBytes,
        CancellationToken cancellationToken)
    {
        var written = 0;
        bool isFinal;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            isFinal = reader.ReadBits(1) != 0;
            switch (reader.ReadBits(2))
            {
                case 0:
                    reader.AlignToByte();
                    var length = reader.ReadBits(16);
                    var complement = reader.ReadBits(16);
                    if ((ushort)(length ^ ushort.MaxValue) != (ushort)complement)
                        throw new InvalidDataException("Deflate stored block length is invalid.");
                    AddOutput(ref written, length, maxOutputBytes);
                    reader.SkipBytes(length);
                    break;
                case 1:
                    DecodeCompressedBlock(
                        ref reader,
                        SFixedLiterals,
                        SFixedDistances,
                        ref written,
                        maxOutputBytes,
                        cancellationToken);
                    break;
                case 2:
                    ReadDynamicTables(ref reader, out var literals, out var distances);
                    DecodeCompressedBlock(
                        ref reader,
                        literals,
                        distances,
                        ref written,
                        maxOutputBytes,
                        cancellationToken);
                    break;
                default:
                    throw new InvalidDataException("Deflate block uses the reserved encoding type.");
            }
        }
        while (!isFinal);
        return written;
    }

    private static void DecodeCompressedBlock(
        ref DeflateBitReader reader,
        HuffmanTable literals,
        HuffmanTable distances,
        ref int written,
        int maxOutputBytes,
        CancellationToken cancellationToken)
    {
        var decodedSymbols = 0;
        while (true)
        {
            if ((decodedSymbols++ & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var symbol = literals.Decode(ref reader);
            if (symbol < 256)
            {
                AddOutput(ref written, 1, maxOutputBytes);
                continue;
            }
            if (symbol == 256)
                return;
            var lengthIndex = symbol - 257;
            if ((uint)lengthIndex >= (uint)SLengthBases.Length)
                throw new InvalidDataException("Deflate length symbol is reserved.");
            var length = SLengthBases[lengthIndex] + reader.ReadBits(SLengthExtraBits[lengthIndex]);
            var distanceSymbol = distances.Decode(ref reader);
            if ((uint)distanceSymbol >= (uint)SDistanceBases.Length)
                throw new InvalidDataException("Deflate distance symbol is reserved.");
            var distance = SDistanceBases[distanceSymbol] +
                reader.ReadBits(SDistanceExtraBits[distanceSymbol]);
            if (distance > written)
                throw new InvalidDataException("Deflate distance exceeds the decoded history.");
            AddOutput(ref written, length, maxOutputBytes);
        }
    }

    private static void ReadDynamicTables(
        ref DeflateBitReader reader,
        out HuffmanTable literals,
        out HuffmanTable distances)
    {
        var literalCount = reader.ReadBits(5) + 257;
        var distanceCount = reader.ReadBits(5) + 1;
        var codeLengthCount = reader.ReadBits(4) + 4;
        if (literalCount > 286 || distanceCount > 32)
            throw new InvalidDataException("Deflate dynamic table dimensions are invalid.");

        ReadOnlySpan<byte> order = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];
        Span<int> codeLengthLengths = stackalloc int[19];
        for (var index = 0; index < codeLengthCount; index++)
            codeLengthLengths[order[index]] = reader.ReadBits(3);
        var codeLengths = HuffmanTable.Create(codeLengthLengths);

        var total = literalCount + distanceCount;
        var lengths = new int[total];
        var written = 0;
        while (written < total)
        {
            var symbol = codeLengths.Decode(ref reader);
            switch (symbol)
            {
                case <= 15:
                    lengths[written++] = symbol;
                    break;
                case 16:
                    if (written == 0)
                        throw new InvalidDataException("Deflate code-length repeat has no predecessor.");
                    RepeatLength(lengths, ref written, total, reader.ReadBits(2) + 3, lengths[written - 1]);
                    break;
                case 17:
                    RepeatLength(lengths, ref written, total, reader.ReadBits(3) + 3, 0);
                    break;
                case 18:
                    RepeatLength(lengths, ref written, total, reader.ReadBits(7) + 11, 0);
                    break;
                default:
                    throw new InvalidDataException("Deflate code-length symbol is invalid.");
            }
        }
        if (lengths[256] == 0)
            throw new InvalidDataException("Deflate literal table has no end-of-block symbol.");
        literals = HuffmanTable.Create(lengths.AsSpan(0, literalCount));
        var distanceLengths = lengths.AsSpan(literalCount, distanceCount);
        distances = distanceCount == 1 && distanceLengths[0] == 0
            ? HuffmanTable.Empty
            : HuffmanTable.Create(distanceLengths);
    }

    private static void RepeatLength(
        int[] lengths,
        ref int written,
        int total,
        int count,
        int value)
    {
        if (count > total - written)
            throw new InvalidDataException("Deflate code-length repeat exceeds its table.");
        lengths.AsSpan(written, count).Fill(value);
        written += count;
    }

    private static void AddOutput(ref int written, int count, int maxOutputBytes)
    {
        if (count > maxOutputBytes - written)
            throw new SharpLinkCompressionOutputLimitException(maxOutputBytes);
        written += count;
    }

    private static HuffmanTable CreateFixedLiteralTable()
    {
        Span<int> lengths = stackalloc int[288];
        lengths[..144].Fill(8);
        lengths[144..256].Fill(9);
        lengths[256..280].Fill(7);
        lengths[280..].Fill(8);
        return HuffmanTable.Create(lengths);
    }

    private static HuffmanTable CreateFixedDistanceTable()
    {
        Span<int> lengths = stackalloc int[32];
        lengths.Fill(5);
        return HuffmanTable.Create(lengths);
    }

    private sealed class HuffmanTable(Dictionary<int, int> symbols, int maxBits)
    {
        internal static HuffmanTable Empty { get; } = new([], 0);

        internal static HuffmanTable Create(ReadOnlySpan<int> lengths)
        {
            Span<int> counts = stackalloc int[MaxCodeBits + 1];
            var symbolCount = 0;
            var maxBits = 0;
            foreach (var length in lengths)
            {
                if ((uint)length > MaxCodeBits)
                    throw new InvalidDataException("Deflate Huffman code length is invalid.");
                if (length == 0)
                    continue;
                counts[length]++;
                symbolCount++;
                maxBits = Math.Max(maxBits, length);
            }
            if (symbolCount == 0)
                throw new InvalidDataException("Deflate Huffman table is empty.");

            var remaining = 1;
            for (var bits = 1; bits <= MaxCodeBits; bits++)
            {
                remaining = (remaining << 1) - counts[bits];
                if (remaining < 0)
                    throw new InvalidDataException("Deflate Huffman table is oversubscribed.");
            }
            if (remaining != 0 && symbolCount != 1)
                throw new InvalidDataException("Deflate Huffman table is incomplete.");

            Span<int> nextCodes = stackalloc int[MaxCodeBits + 1];
            var code = 0;
            for (var bits = 1; bits <= MaxCodeBits; bits++)
            {
                code = (code + counts[bits - 1]) << 1;
                nextCodes[bits] = code;
            }
            var symbols = new Dictionary<int, int>(symbolCount);
            for (var symbol = 0; symbol < lengths.Length; symbol++)
            {
                var length = lengths[symbol];
                if (length == 0)
                    continue;
                var reversed = ReverseBits(nextCodes[length]++, length);
                symbols.Add((length << 16) | reversed, symbol);
            }
            return new HuffmanTable(symbols, maxBits);
        }

        internal int Decode(ref DeflateBitReader reader)
        {
            var code = 0;
            for (var length = 1; length <= maxBits; length++)
            {
                code |= reader.ReadBits(1) << (length - 1);
                if (symbols.TryGetValue((length << 16) | code, out var symbol))
                    return symbol;
            }
            throw new InvalidDataException("Deflate Huffman symbol is invalid.");
        }

        private static int ReverseBits(int value, int count)
        {
            var reversed = 0;
            for (var index = 0; index < count; index++)
            {
                reversed = (reversed << 1) | (value & 1);
                value >>= 1;
            }
            return reversed;
        }
    }

    private ref struct DeflateBitReader(ReadOnlySpan<byte> source)
    {
        private readonly ReadOnlySpan<byte> _source = source;
        private uint _bits;
        private int _bitCount;
        private int _offset;

        internal int ConsumedBytes => _offset;

        internal int ReadBits(int count)
        {
            if ((uint)count > 16)
                throw new ArgumentOutOfRangeException(nameof(count));
            while (_bitCount < count)
            {
                if (_offset >= _source.Length)
                    throw new InvalidDataException("Deflate payload is truncated.");
                _bits |= (uint)_source[_offset++] << _bitCount;
                _bitCount += 8;
            }
            var mask = count == 0 ? 0U : (1U << count) - 1;
            var value = (int)(_bits & mask);
            _bits >>= count;
            _bitCount -= count;
            return value;
        }

        internal void AlignToByte()
        {
            _bits = 0;
            _bitCount = 0;
        }

        internal void SkipBytes(int count)
        {
            if (_bitCount != 0)
                throw new InvalidOperationException("Deflate reader must be byte-aligned before skipping bytes.");
            if (count < 0 || _offset > _source.Length - count)
                throw new InvalidDataException("Deflate stored block is truncated.");
            _offset += count;
        }
    }
}
