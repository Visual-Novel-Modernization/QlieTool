using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace QlieTool;

/// <summary>HashVer1.3 table and QLIE 1PC writer, reconstructed against data7.pack / EXE.</summary>
public static class HashVer13
{
    private const uint TransformAdd = 0xCE24F523;
    private const uint TransformSeed = 0xFEC9753E;
    private const uint TransformState = 0xA73C5F9D;
    private const int PairTableSize = 0x2000;
    private const int BlockSize = 0x2000;
    private const int MaxDistinct = 0xC8;

    private sealed class BlockState
    {
        public readonly List<int> Symbols = new();
        public readonly int[] PairLeft = new int[PairTableSize];
        public readonly int[] PairRight = new int[PairTableSize];
        public readonly int[] PairCount = new int[PairTableSize];
        public readonly bool[] Used = new bool[256];
    }

    /// <summary>Build the decoded v1.3 name index blob. Verified byte-identical for data7's 522-entry table.</summary>
    public static byte[] BuildDecodedTable(IReadOnlyList<QlieEntry> entries)
    {
        if (entries.Count > ushort.MaxValue) throw new InvalidDataException("HashVer1.3 supports at most 65535 entries in the observed u16 tail");
        var buckets = Enumerable.Range(0, 256).Select(_ => new List<QlieEntry>()).ToArray();
        foreach (var entry in entries)
        {
            if (entry.Index < 0 || entry.Index >= entries.Count) throw new InvalidDataException($"invalid TOC index {entry.Index}");
            var name = entry.ClearNameBytes.Length != 0 ? entry.ClearNameBytes : Encoding.GetEncoding(932).GetBytes(entry.Name);
            var bucket = BucketHash(name);
            buckets[bucket].Add(entry);
        }
        using var output = new MemoryStream();
        foreach (var bucket in buckets)
        {
            if (bucket.Count > ushort.MaxValue) throw new InvalidDataException("HashVer1.3 bucket entry count exceeds u16");
            WriteU16(output, (ushort)bucket.Count);
            foreach (var entry in bucket.OrderBy(x => x.Index))
            {
                var name = entry.ClearNameBytes.Length != 0 ? entry.ClearNameBytes : Encoding.GetEncoding(932).GetBytes(entry.Name);
                if (name.Length > ushort.MaxValue) throw new InvalidDataException($"HashVer1.3 name exceeds u16 length: {entry.Name}");
                WriteU16(output, (ushort)name.Length);
                output.Write(name, 0, name.Length);
                WriteU64(output, checked((ulong)entry.Index * 2UL));
                WriteU32(output, NameKeyHash(name));
            }
        }
        foreach (var entry in entries.OrderBy(x => x.Index)) WriteU16(output, checked((ushort)entry.Index));
        return output.ToArray();
    }

    /// <summary>Build the HashVer1.3 section body. Header +0x18 is the appended offset-stream byte count (2 per entry); +0x1C is compressed stream size.</summary>
    public static byte[] BuildSection(IReadOnlyList<QlieEntry> entries)
    {
        var decoded = BuildDecodedTable(entries);
        var compressed = Encode1Pc(decoded);
        var transformed = Transform(compressed, 0x428, 0, encrypt: true);
        var section = new byte[checked(32 + transformed.Length)];
        Encoding.ASCII.GetBytes("HashVer1.3\0\0").CopyTo(section, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(0x10, 4), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(0x14, 4), checked((uint)entries.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(0x18, 4), checked((uint)(entries.Count * 2))); // observed v1.3 tail-byte count
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(0x1c, 4), checked((uint)transformed.Length));
        transformed.CopyTo(section, 32);
        return section;
    }

    /// <summary>Rebuild a target archive's HashVer section from the source's verified name-index template.</summary>
    public static byte[] RebuildGap(QlieArchive archive, byte[] trailerBlock)
    {
        var sourceGap = archive.CopyIndexTrailerGap();
        var hash14 = Encoding.ASCII.GetBytes("HashVer1.4\0\0");
        if (sourceGap.Length >= 68 && sourceGap.AsSpan(0, 12).SequenceEqual(hash14))
            return RebuildGap14(archive, trailerBlock, sourceGap);
        var magic = Encoding.ASCII.GetBytes("HashVer1.3\0\0");
        if (sourceGap.Length < 68 || !sourceGap.AsSpan(0, 12).SequenceEqual(magic))
            return sourceGap; // Preserve non-HashVer V3 fixtures and unknown variants verbatim.
        if (trailerBlock.Length != 0x400) throw new InvalidDataException("V3 trailer block must be 0x400 bytes");

        var bucketCount = BinaryPrimitives.ReadUInt32LittleEndian(sourceGap.AsSpan(0x10, 4));
        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(sourceGap.AsSpan(0x14, 4));
        var extraLength = BinaryPrimitives.ReadUInt32LittleEndian(sourceGap.AsSpan(0x18, 4));
        var compressedLength = BinaryPrimitives.ReadUInt32LittleEndian(sourceGap.AsSpan(0x1c, 4));
        if (bucketCount != 256 || entryCount != archive.Entries.Count || extraLength != entryCount * 2)
            throw new InvalidDataException("HashVer1.3 group, entry, or observed extra-tail counts do not match");
        if (compressedLength > int.MaxValue || 32L + compressedLength + 36 != sourceGap.Length)
            throw new InvalidDataException("HashVer1.3 compressed stream and terminal prefix do not cover the source gap");

        var sourceSuffix = sourceGap.AsSpan(checked(32 + (int)compressedLength), 36);
        var sourceSectionLength = checked((uint)(32 + compressedLength));
        if (BinaryPrimitives.ReadUInt32LittleEndian(sourceSuffix.Slice(32, 4)) != sourceSectionLength)
            throw new InvalidDataException("HashVer1.3 terminal copy-length field is inconsistent");
        var arcKey = QlieArchive.CalculateArcKey(trailerBlock.AsSpan(0, 0x100));
        if (arcKey != archive.ArcKey) throw new InvalidDataException("HashVer1.3 terminal key changed; rebuilding a trailer-derived ArcKey is not implemented");

        var encryptedStream = sourceGap.AsSpan(32, checked((int)compressedLength)).ToArray();
        var packedStream = Transform(encryptedStream, 0x428, 0, encrypt: false);
        var highLevelStream = Transform(encryptedStream, 0x428 - encryptedStream.Length, 0, encrypt: false);
        if (packedStream.Length >= 4 && !(packedStream[0] == 0x31 && packedStream[1] == 0x50 && packedStream[2] == 0x43 && packedStream[3] == 0xFF))
            packedStream = highLevelStream;
        byte[] decoded;
        if (packedStream.Length >= 4 && packedStream[0] == 0x31 && packedStream[1] == 0x50 && packedStream[2] == 0x43 && packedStream[3] == 0xFF)
        {
            var innerSize = BinaryPrimitives.ReadUInt32LittleEndian(packedStream.AsSpan(8, 4));
            if (innerSize > 0x20000000) throw new InvalidDataException("HashVer1.3 inner 1PC output exceeds the native decoder bound");
            decoded = QlieArchive.Decompress1Pc(packedStream, innerSize)
                ?? throw new InvalidDataException("HashVer1.3 1PC stream failed to decode");
        }
        else
        {
            // Some V3 writer versions store a raw, unwrapped name-index stream in this lane.
            // On the data7 archive, the first decrypted bytes must be 1PC; fail closed if not.
            throw new InvalidDataException("HashVer1.3 transformed stream does not begin with the expected 1PC wrapper");
        }
        if (decoded.Length < extraLength) throw new InvalidDataException("HashVer1.3 extra tail exceeds decoded table size");
        var rebuiltDecoded = RebuildDecodedTable(decoded, archive.Entries, checked((int)extraLength));
        var rebuiltPacked = Encode1Pc(rebuiltDecoded);
        var rebuiltEncrypted = Transform(rebuiltPacked, 0x428, 0, encrypt: true);

        using var section = new MemoryStream(checked(32 + rebuiltEncrypted.Length));
        section.Write(magic);
        section.Write(new byte[4]);
        WriteU32(section, bucketCount);
        WriteU32(section, entryCount);
        WriteU32(section, checked((uint)extraLength));
        WriteU32(section, checked((uint)rebuiltEncrypted.Length));
        section.Write(rebuiltEncrypted);
        var sectionBytes = section.ToArray();
        var terminal = BuildTerminalMarker(sourceSuffix.Slice(0, 32), checked((uint)sectionBytes.Length));
        var result = new byte[checked(sectionBytes.Length + terminal.Length)];
        sectionBytes.CopyTo(result, 0);
        terminal.CopyTo(result, sectionBytes.Length);
        return result;
    }

    /// <summary>Rebuild the observed V3.0 HashVer1.4 section. The native writer emits a 68-byte header,
    /// a transformed 1PC name table, and the same 36-byte terminal marker lane used by V1.3.</summary>
    private static byte[] RebuildGap14(QlieArchive archive, byte[] trailerBlock, byte[] sourceGap)
    {
        if (trailerBlock.Length != 0x400) throw new InvalidDataException("V3 trailer block must be 0x400 bytes");
        var bucketCount = BinaryPrimitives.ReadUInt32LittleEndian(sourceGap.AsSpan(0x10, 4));
        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(sourceGap.AsSpan(0x14, 4));
        var extraLength = BinaryPrimitives.ReadUInt32LittleEndian(sourceGap.AsSpan(0x18, 4));
        var compressedLength = BinaryPrimitives.ReadUInt32LittleEndian(sourceGap.AsSpan(0x1c, 4));
        if (bucketCount != 256 || entryCount != archive.Entries.Count || extraLength != entryCount * 2)
            throw new InvalidDataException("HashVer1.4 group, entry, or observed extra-tail counts do not match");
        if (compressedLength > int.MaxValue || 68L + compressedLength + 36 != sourceGap.Length)
            throw new InvalidDataException("HashVer1.4 compressed stream and terminal prefix do not cover the source gap");
        var sourceSuffix = sourceGap.AsSpan(checked(68 + (int)compressedLength), 36);
        var sourceSectionLength = checked((uint)(68 + compressedLength));
        if (BinaryPrimitives.ReadUInt32LittleEndian(sourceSuffix.Slice(32, 4)) != sourceSectionLength)
            throw new InvalidDataException("HashVer1.4 terminal copy-length field is inconsistent");
        var arcKey = QlieArchive.CalculateArcKey(trailerBlock.AsSpan(0, 0x100));
        if (arcKey != archive.ArcKey) throw new InvalidDataException("HashVer1.4 terminal key changed; rebuilding a trailer-derived ArcKey is not implemented");

        var encryptedStream = sourceGap.AsSpan(68, checked((int)compressedLength)).ToArray();
        var packedStream = Transform(encryptedStream, 0x428, 0, encrypt: false);
        var highLevelStream = Transform(encryptedStream, 0x428 - encryptedStream.Length, 0, encrypt: false);
        if (packedStream.Length >= 4 && !(packedStream[0] == 0x31 && packedStream[1] == 0x50 && packedStream[2] == 0x43 && packedStream[3] == 0xFF))
            packedStream = highLevelStream;
        if (packedStream.Length < 12 || packedStream[0] != 0x31 || packedStream[1] != 0x50 || packedStream[2] != 0x43 || packedStream[3] != 0xFF)
            throw new InvalidDataException("HashVer1.4 transformed stream does not begin with the expected 1PC wrapper");
        var innerSize = BinaryPrimitives.ReadUInt32LittleEndian(packedStream.AsSpan(8, 4));
        if (innerSize > 0x20000000) throw new InvalidDataException("HashVer1.4 inner 1PC output exceeds the native decoder bound");
        var decoded = QlieArchive.Decompress1Pc(packedStream, innerSize)
            ?? throw new InvalidDataException("HashVer1.4 1PC stream failed to decode");
        if (decoded.Length < extraLength) throw new InvalidDataException("HashVer1.4 extra tail exceeds decoded table size");
        var rebuiltDecoded = RebuildDecodedTable(decoded, archive.Entries, checked((int)extraLength));
        var rebuiltPacked = Encode1Pc(rebuiltDecoded);
        var rebuiltEncrypted = Transform(rebuiltPacked, 0x428, 0, encrypt: true);

        using var section = new MemoryStream(checked(68 + rebuiltEncrypted.Length));
        section.Write(Encoding.ASCII.GetBytes("HashVer1.4\0\0"));
        section.Write(new byte[4]);
        WriteU32(section, bucketCount);
        WriteU32(section, entryCount);
        WriteU32(section, extraLength);
        WriteU32(section, checked((uint)rebuiltEncrypted.Length));
        WriteU32(section, 1); // HV1.4 mode marker observed at +0x20; +0x24..+0x40 are reserved zeroes.
        section.Write(new byte[32]);
        section.Write(rebuiltEncrypted);
        var sectionBytes = section.ToArray();
        var terminal = BuildTerminalMarker(sourceSuffix.Slice(0, 32), checked((uint)sectionBytes.Length));
        var result = new byte[checked(sectionBytes.Length + terminal.Length)];
        sectionBytes.CopyTo(result, 0);
        terminal.CopyTo(result, sectionBytes.Length);
        return result;
    }

    private static byte[] RebuildDecodedTable(byte[] decoded, IReadOnlyList<QlieEntry> entries, int extraLength)
    {
        var cp932 = Encoding.GetEncoding(932);
        var byName = entries.ToDictionary(e => Convert.ToHexString(e.ClearNameBytes.Length != 0 ? e.ClearNameBytes : cp932.GetBytes(e.Name)), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var input = new MemoryStream(decoded, writable: false);
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        using var output = new MemoryStream(decoded.Length);
        var bucketLists = new List<(byte[] Name, ulong Value)>[256];
        for (var bucket = 0; bucket < 256; bucket++)
        {
            var count = ReadU16(reader, input);
            var rows = new List<(byte[] Name, ulong Value)>(count);
            WriteU16(output, count);
            for (var row = 0; row < count; row++)
            {
                var nameLength = ReadU16(reader, input);
                var name = ReadExactly(reader, input, nameLength);
                var value = ReadU64(reader, input);
                _ = ReadU32(reader, input); // On load the EXE replaces this field with its name-key hash.
                var key = Convert.ToHexString(name);
                if (!byName.ContainsKey(key) || !seen.Add(key)) throw new InvalidDataException("HashVer1.3 source table name set differs from current TOC");
                if (BucketHash(name) != bucket) throw new InvalidDataException("HashVer1.3 source record is in the wrong name bucket");
                WriteU16(output, nameLength);
                output.Write(name, 0, name.Length);
                WriteU64(output, value); // preserve the opaque 8-byte source metadata exactly
                WriteU32(output, NameKeyHash(name));
                rows.Add((name, value));
            }
            bucketLists[bucket] = rows;
        }
        if (seen.Count != entries.Count) throw new InvalidDataException("HashVer1.3 table does not contain every TOC name");
        if (input.Length - input.Position != extraLength) throw new InvalidDataException("HashVer1.3 extra tail length differs from header");
        var tail = ReadExactly(reader, input, extraLength);
        if (extraLength != entries.Count * 2) throw new InvalidDataException("HashVer1.3 extra tail does not match the observed u16-per-entry layout");
        var tailSeen = new bool[entries.Count];
        for (var i = 0; i < tail.Length; i += 2)
        {
            var index = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i, 2));
            if (index >= entries.Count || tailSeen[index]) throw new InvalidDataException("HashVer1.3 entry tail is not a permutation of TOC indexes");
            tailSeen[index] = true;
        }
        output.Write(tail, 0, tail.Length);
        return output.ToArray();
    }

    private static ushort ReadU16(BinaryReader reader, Stream input)
    {
        if (input.Length - input.Position < 2) throw new InvalidDataException("truncated HashVer1.3 table");
        return reader.ReadUInt16();
    }
    private static uint ReadU32(BinaryReader reader, Stream input)
    {
        if (input.Length - input.Position < 4) throw new InvalidDataException("truncated HashVer1.3 table");
        return reader.ReadUInt32();
    }
    private static ulong ReadU64(BinaryReader reader, Stream input)
    {
        if (input.Length - input.Position < 8) throw new InvalidDataException("truncated HashVer1.3 table");
        return reader.ReadUInt64();
    }
    private static byte[] ReadExactly(BinaryReader reader, Stream input, int count)
    {
        if (count < 0 || input.Length - input.Position < count) throw new InvalidDataException("truncated HashVer1.3 table");
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new EndOfStreamException();
        return bytes;
    }

    /// <summary>Constructs the 36-byte QLIE terminal marker lane.</summary>
    public static byte[] BuildTerminalMarker(ReadOnlySpan<byte> sourceMarker, uint sectionLength)
    {
        if (sourceMarker.Length != 32) throw new InvalidDataException("source terminal marker must be 32 bytes");
        var result = new byte[36];
        sourceMarker.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(32, 4), sectionLength);
        return result;
    }

    /// <summary>QLIE's reversible 8-byte transform. The native call supplies a descriptor length (0x428) as the seed term; the loop covers the supplied buffer.</summary>
    public static byte[] Transform(byte[] input, int auxiliary, uint key, bool encrypt)
    {
        var output = input.ToArray();
        var seed = unchecked((uint)(input.Length + auxiliary) + key) ^ TransformSeed;
        uint s0 = TransformState, s1 = TransformState;
        uint prev0 = seed, prev1 = seed;
        var blocks = input.Length / 8;
        for (var block = 0; block < blocks; block++)
        {
            var offset = block * 8;
            s0 = unchecked(s0 + TransformAdd) ^ prev0;
            s1 = unchecked(s1 + TransformAdd) ^ prev1;
            var in0 = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(offset, 4));
            var in1 = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(offset + 4, 4));
            var out0 = in0 ^ s0;
            var out1 = in1 ^ s1;
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset, 4), out0);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset + 4, 4), out1);
            // Encryption feeds plaintext input; decryption feeds recovered plaintext output.
            prev0 = encrypt ? in0 : out0;
            prev1 = encrypt ? in1 : out1;
        }
        return output;
    }

    /// <summary>Native-compatible 1PC encoder, reconstructed from EXE VA 0x484374.</summary>
    public static byte[] Encode1Pc(byte[] data)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0x31, 0x50, 0x43, 0xff });
        WriteU32(output, 1); // 16-bit symbol counts
        WriteU32(output, checked((uint)data.Length));
        var cursor = 0;
        while (cursor < data.Length)
        {
            var state = GatherBlock(data, cursor, out var nextCursor);
            var (symbols, left, right) = MergeTree(state);
            var table = SerializeTable(left, right);
            if (symbols.Count > ushort.MaxValue) throw new InvalidDataException("1PC short block symbol count overflow");
            output.Write(table, 0, table.Length);
            WriteU16(output, (ushort)symbols.Count);
            foreach (var value in symbols) output.WriteByte((byte)value);
            cursor = nextCursor;
        }
        return output.ToArray();
    }

    private static BlockState GatherBlock(byte[] data, int cursor, out int nextCursor)
    {
        var state = new BlockState();
        var distinct = 0;
        while (cursor < data.Length)
        {
            var current = data[cursor++];
            if (state.Symbols.Count != 0)
            {
                var previous = state.Symbols[^1];
                var slot = PairSlot(state, previous, current);
                if (state.PairCount[slot] < 0xff) state.PairCount[slot]++;
            }
            state.Symbols.Add(current);
            if (!state.Used[current]) { state.Used[current] = true; distinct++; }
            if (state.Symbols.Count >= BlockSize || distinct >= MaxDistinct || cursor >= data.Length) break;
        }
        nextCursor = cursor;
        return state;
    }

    private static int PairSlot(BlockState state, int left, int right)
    {
        var slot = (left ^ (right << 5)) & (PairTableSize - 1);
        while (true)
        {
            if (state.PairLeft[slot] == left && state.PairRight[slot] == right) return slot;
            if (state.PairCount[slot] == 0)
            {
                state.PairLeft[slot] = left;
                state.PairRight[slot] = right;
                return slot;
            }
            slot = (slot + 1) & (PairTableSize - 1);
        }
    }

    private static void AdjustPair(BlockState state, int left, int right, int delta)
    {
        var slot = PairSlot(state, left, right);
        if (delta < 0)
        {
            if (state.PairCount[slot] > 1) state.PairCount[slot]--;
        }
        else if (state.PairCount[slot] < 0xff) state.PairCount[slot]++;
    }

    private static (List<int> Symbols, int[] Left, int[] Right) MergeTree(BlockState state)
    {
        var symbols = state.Symbols;
        var childLeft = Enumerable.Range(0, 256).ToArray();
        var childRight = new int[256];
        while (true)
        {
            var node = 255;
            while (node >= 0 && (childLeft[node] != node || state.Used[node])) node--;
            if (node < 0) break;
            var bestCount = 2;
            var bestA = 0; var bestB = 0;
            for (var slot = 0; slot < PairTableSize; slot++)
            {
                var count = state.PairCount[slot];
                if (count > bestCount)
                {
                    bestCount = count;
                    bestA = state.PairLeft[slot];
                    bestB = state.PairRight[slot];
                }
            }
            if (bestCount <= 2 || symbols.Count < 2) break;
            var oldLength = symbols.Count;
            var next = new List<int>(oldLength);
            var i = 0;
            while (i < oldLength - 1)
            {
                if (symbols[i] == bestA && symbols[i + 1] == bestB)
                {
                    if (i > 0)
                    {
                        var previous = next[^1];
                        AdjustPair(state, previous, bestA, -1);
                        AdjustPair(state, previous, node, +1);
                    }
                    if (i < oldLength - 2)
                    {
                        var following = symbols[i + 2];
                        AdjustPair(state, bestB, following, -1);
                        AdjustPair(state, node, following, +1);
                    }
                    next.Add(node);
                    i += 2;
                }
                else
                {
                    next.Add(symbols[i]);
                    i++;
                }
            }
            if (i == oldLength - 1) next.Add(symbols[i]);
            symbols = next;
            childLeft[node] = bestA;
            childRight[node] = bestB;
            var merged = PairSlot(state, bestA, bestB);
            state.PairCount[merged] = 1;
        }
        return (symbols, childLeft, childRight);
    }

    private static byte[] SerializeTable(int[] childLeft, int[] childRight)
    {
        using var output = new MemoryStream();
        var i = 0;
        while (i < 256)
        {
            if (childLeft[i] == i)
            {
                var run = 1;
                i++;
                while (run < 127 && i < 256 && childLeft[i] == i) { run++; i++; }
                output.WriteByte((byte)(0x7f + run));
                if (i >= 256) break;
                output.WriteByte((byte)childLeft[i]);
                if (childLeft[i] != i) output.WriteByte((byte)childRight[i]);
                i++;
                continue;
            }
            var start = i;
            var esi = 0;
            var ebx = i + 1;
            while (true)
            {
                if (esi >= 127 || ebx >= 256) break;
                if (childLeft[ebx] != ebx) { esi++; ebx++; continue; }
                if (esi >= 125 || ebx >= 254) break;
                if (childLeft[ebx + 1] != ebx + 1) { esi++; ebx++; continue; }
                break;
            }
            output.WriteByte((byte)esi);
            var runLength = esi + 1;
            for (var j = start; j < start + runLength; j++)
            {
                output.WriteByte((byte)childLeft[j]);
                if (childLeft[j] != j) output.WriteByte((byte)childRight[j]);
            }
            i = start + runLength;
        }
        return output.ToArray();
    }

    private static int BucketHash(byte[] name)
    {
        uint sum = 0;
        for (var i = 0; i < name.Length; i++) sum = unchecked(sum + (uint)name[i] * (uint)(i + 1));
        return (int)(sum & 0xff);
    }

    private static uint NameKeyHash(byte[] name)
    {
        uint sum = 0;
        for (var i = 0; i < name.Length; i++) sum = unchecked(sum + ((uint)name[i] << ((i + 1) & 0xf)));
        return sum;
    }

    private static void WriteU16(Stream output, ushort value)
    {
        Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, value); output.Write(b);
    }
    private static void WriteU32(Stream output, uint value)
    {
        Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); output.Write(b);
    }
    private static void WriteU64(Stream output, ulong value)
    {
        Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); output.Write(b);
    }
}

/// <summary>Preserves each source entry's packing flag: packed resources use the native 1PC encoder.</summary>
public sealed class QliePayloadCodec : IEntryPayloadCodec
{
    public byte[] Encode(byte[] decoded, QlieEntry sourceEntry) => sourceEntry.Packed ? HashVer13.Encode1Pc(decoded) : decoded.ToArray();
}
