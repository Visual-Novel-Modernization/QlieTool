using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QlieTool;

/// <summary>Independent reader and variable-size repacker for QLIE FilePackVer1.0.</summary>
public sealed class QlieV1Archive : IDisposable
{
    private const int FooterSize = 0x1c;
    private const int RowTailSize = 28;
    private const int MaxEntryCount = 1_000_000;
    private const int CopyBufferSize = 1 << 20;
    private static readonly Encoding Cp932 = CreateCp932();
    private readonly FileStream _file;
    private readonly byte[] _footer;
    private bool _disposed;

    public string SourcePath { get; }
    public long FileLength { get; }
    public uint IndexOffset { get; }
    public IReadOnlyList<QlieV1Entry> Entries { get; }

    private QlieV1Archive(string path, FileStream file, long fileLength, uint indexOffset, byte[] footer, List<QlieV1Entry> entries)
    {
        SourcePath = path;
        _file = file;
        FileLength = fileLength;
        IndexOffset = indexOffset;
        _footer = footer;
        Entries = entries;
    }

    public static QlieV1Archive Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.RandomAccess);
        try
        {
            if (file.Length < FooterSize) throw new InvalidDataException("file is too short for QLIE V1 footer");
            var footerAt = file.Length - FooterSize;
            var footer = ReadAt(file, footerAt, FooterSize);
            if (!footer.AsSpan(0, 14).SequenceEqual(Encoding.ASCII.GetBytes("FilePackVer1.0")))
                throw new InvalidDataException("not a QLIE FilePackVer1.0 archive");
            var count = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(0x10, 4));
            var index = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(0x14, 4));
            if (count > MaxEntryCount) throw new InvalidDataException($"V1 entry count exceeds limit: {count}");
            if (index > footerAt) throw new InvalidDataException("V1 index offset points beyond the footer");
            var entries = ReadEntries(file, checked((int)count), index, footerAt);
            return new QlieV1Archive(path, file, file.Length, index, footer, entries);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public static bool IsV1(string path)
    {
        if (!File.Exists(path)) return false;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < FooterSize) return false;
        var magic = ReadAt(file, file.Length - FooterSize, 14);
        return magic.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes("FilePackVer1.0"));
    }

    /// <summary>Writes stored entry bytes using indexed names and a source-bound JSON manifest.</summary>
    public string Extract(string outputDirectory, string? manifestPath = null)
    {
        ThrowIfDisposed();
        var root = EnsureDirectoryTree(outputDirectory);
        manifestPath = Path.GetFullPath(manifestPath ?? Path.Combine(root, "qlie-v1-manifest.json"));
        EnsureNoReparseComponents(manifestPath);
        if (File.Exists(manifestPath)) throw new IOException($"refusing to overwrite {manifestPath}");

        var targets = new List<(QlieV1Entry Entry, string Path)>(Entries.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            var destination = SafePath(root, entry.Name);
            if (!seen.Add(destination)) throw new InvalidDataException($"duplicate extracted path: {entry.Name}");
            EnsureDirectoryTree(Path.GetDirectoryName(destination)!);
            EnsureNoReparseComponents(destination);
            if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException($"refusing to overwrite {destination}");
            if (string.Equals(destination, manifestPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("archive entry collides with manifest path");
            targets.Add((entry, destination));
        }

        var manifest = CreateManifest();
        foreach (var (entry, destination) in targets)
        {
            using var source = OpenStored(entry);
            using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);
            source.CopyTo(target, CopyBufferSize);
        }
        EnsureDirectoryTree(Path.GetDirectoryName(manifestPath)!);
        EnsureNoReparseComponents(manifestPath);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
        return manifestPath;
    }

    /// <summary>Reads and checks every bounded stored payload against its legacy TOC checksum.</summary>
    public QlieV1Verification Verify()
    {
        ThrowIfDisposed();
        var mismatches = 0;
        foreach (var entry in Entries)
        {
            using var hashStream = OpenStored(entry);
            var actualHash = ComputeLegacyPayloadHash(hashStream);
            if (actualHash != entry.Hash) mismatches++;
        }
        if (mismatches != 0) throw new InvalidDataException($"{mismatches} V1 payload hashes do not match the TOC");
        return new QlieV1Verification(Entries.Count, FileLength, IndexOffset, ComputeSha256(SourcePath));
    }

    /// <summary>
    /// Rebuilds payloads, 28-byte rows, and the V1 footer. Unchanged bytes are copied from source;
    /// modified entries are emitted as literal payloads only when their source flags are zero and
    /// source stored/unpacked lengths agree.
    /// </summary>
    public string Repack(string inputDirectory, string outputPath, string? manifestPath = null)
    {
        ThrowIfDisposed();
        var inputRoot = Path.GetFullPath(inputDirectory);
        if (!Directory.Exists(inputRoot)) throw new DirectoryNotFoundException($"input directory not found: {inputRoot}");
        EnsureNoReparseComponents(inputRoot, allowMissingLeaf: false);
        outputPath = Path.GetFullPath(outputPath);
        if (string.Equals(outputPath, SourcePath, StringComparison.OrdinalIgnoreCase)) throw new IOException("output archive must differ from source archive");
        EnsureDirectoryTree(Path.GetDirectoryName(outputPath)!);
        EnsureNoReparseComponents(outputPath);
        if (File.Exists(outputPath)) throw new IOException($"refusing to overwrite {outputPath}");

        manifestPath = Path.GetFullPath(manifestPath ?? Path.Combine(inputRoot, "qlie-v1-manifest.json"));
        EnsureNoReparseComponents(manifestPath, allowMissingLeaf: false);
        var manifest = JsonSerializer.Deserialize<QlieV1Manifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidDataException("invalid V1 manifest");
        ValidateManifest(manifest);

        var manifestByIndex = manifest.Entries.ToDictionary(x => x.Index);
        var payloads = new List<RepackPayload>(Entries.Count);
        foreach (var entry in Entries)
        {
            var row = manifestByIndex[entry.Index];
            if (row.Index != entry.Index || row.Name != entry.Name || row.Offset != entry.Offset || row.StoredSize != entry.StoredSize ||
                row.UnpackedSize != entry.UnpackedSize || row.Flags != entry.Flags || row.Hash != entry.Hash ||
                !string.Equals(row.CipherNameBase64, Convert.ToBase64String(entry.CipherNameBytes), StringComparison.Ordinal))
                throw new InvalidDataException($"manifest entry {entry.Index} metadata differs from source archive");
            var path = SafePath(inputRoot, row.ExtractedPath);
            EnsureNoReparseComponents(path, allowMissingLeaf: false);
            if (!File.Exists(path)) throw new FileNotFoundException($"missing extracted entry {row.ExtractedPath}", path);
            var currentSha = ComputeSha256(path);
            if (string.Equals(currentSha, row.ExtractedSha256, StringComparison.OrdinalIgnoreCase))
            {
                payloads.Add(new RepackPayload(entry, path, true, entry.StoredSize, entry.UnpackedSize, entry.Flags, entry.Hash));
                continue;
            }
            if (entry.Flags != 0 || entry.StoredSize != entry.UnpackedSize)
                throw new NotSupportedException($"edited V1 entry {entry.Index} has non-literal metadata (flags=0x{entry.Flags:X}, stored={entry.StoredSize}, unpacked={entry.UnpackedSize})");
            var newSize = new FileInfo(path).Length;
            if (newSize > uint.MaxValue) throw new InvalidDataException($"edited entry {entry.Index} exceeds V1 u32 size");
            var newHash = ComputeLegacyPayloadHash(path);
            payloads.Add(new RepackPayload(entry, path, false, checked((uint)newSize), checked((uint)newSize), entry.Flags, newHash));
        }

        var tempPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "." + Path.GetFileName(outputPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            WriteRebuiltArchive(tempPath, payloads);
            EnsureNoReparseComponents(outputPath);
            File.Move(tempPath, outputPath);
            return outputPath;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    public Stream OpenStored(QlieV1Entry entry)
    {
        ThrowIfDisposed();
        if (entry.Index < 0 || entry.Index >= Entries.Count || !ReferenceEquals(Entries[entry.Index], entry))
            throw new ArgumentException("entry does not belong to this archive", nameof(entry));
        return new V1BoundedStream(_file, entry.Offset, entry.StoredSize);
    }

    private QlieV1Manifest CreateManifest()
    {
        var entries = new List<QlieV1ManifestEntry>(Entries.Count);
        foreach (var entry in Entries)
        {
            string sha;
            using (var stream = OpenStored(entry)) sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            entries.Add(new QlieV1ManifestEntry
            {
                Index = entry.Index,
                Name = entry.Name,
                CipherNameBase64 = Convert.ToBase64String(entry.CipherNameBytes),
                Offset = entry.Offset,
                StoredSize = entry.StoredSize,
                UnpackedSize = entry.UnpackedSize,
                Flags = entry.Flags,
                Hash = entry.Hash,
                ExtractedPath = entry.Name,
                ExtractedSha256 = sha
            });
        }
        return new QlieV1Manifest
        {
            Format = "QLIE",
            Version = "1.0",
            Count = Entries.Count,
            IndexOffset = IndexOffset,
            ArchiveLength = FileLength,
            SourceName = Path.GetFileName(SourcePath),
            SourceSha256 = ComputeSha256(SourcePath),
            Entries = entries
        };
    }

    private void ValidateManifest(QlieV1Manifest manifest)
    {
        if (manifest.Format != "QLIE" || manifest.Version != "1.0") throw new InvalidDataException("manifest is not a QLIE V1.0 manifest");
        if (manifest.Count != Entries.Count || manifest.Entries.Count != Entries.Count || manifest.IndexOffset != IndexOffset || manifest.ArchiveLength != FileLength)
            throw new InvalidDataException("manifest archive metadata does not match source archive");
        if (!string.Equals(ComputeSha256(SourcePath), manifest.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("source archive SHA-256 does not match manifest");
        if (manifest.Entries.Select(x => x.Index).Distinct().Count() != Entries.Count || manifest.Entries.Any(x => x.Index < 0 || x.Index >= Entries.Count))
            throw new InvalidDataException("manifest entry indexes are invalid");
    }

    private void WriteRebuiltArchive(string tempPath, IReadOnlyList<RepackPayload> payloads)
    {
        using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);
        var offsets = new uint[payloads.Count];
        var buffer = new byte[CopyBufferSize];
        for (var i = 0; i < payloads.Count; i++)
        {
            if (output.Position > uint.MaxValue) throw new InvalidDataException("V1 payload offset exceeds u32");
            offsets[i] = checked((uint)output.Position);
            var payload = payloads[i];
            if (payload.ReuseSource) CopyRangeTo(output, payload.Entry.Offset, payload.StoredSize, buffer);
            else CopyFileTo(output, payload.Path, payload.StoredSize, buffer);
        }
        if (output.Position > uint.MaxValue) throw new InvalidDataException("V1 index offset exceeds u32");
        var indexOffset = checked((uint)output.Position);
        var number = new byte[8];
        for (var i = 0; i < payloads.Count; i++)
        {
            var payload = payloads[i];
            var name = payload.Entry.CipherNameBytes;
            if (name.Length > ushort.MaxValue) throw new InvalidDataException($"entry name exceeds V1 u16 length: {payload.Entry.Name}");
            BinaryPrimitives.WriteUInt16LittleEndian(number, checked((ushort)name.Length));
            output.Write(number[..2]);
            output.Write(name);
            BinaryPrimitives.WriteUInt64LittleEndian(number, offsets[i]);
            output.Write(number);
            BinaryPrimitives.WriteUInt32LittleEndian(number, payload.StoredSize);
            output.Write(number[..4]);
            BinaryPrimitives.WriteUInt32LittleEndian(number, payload.UnpackedSize);
            output.Write(number[..4]);
            BinaryPrimitives.WriteUInt64LittleEndian(number, payload.Flags);
            output.Write(number);
            BinaryPrimitives.WriteUInt32LittleEndian(number, payload.Hash);
            output.Write(number[..4]);
        }
        var footer = _footer.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(0x10, 4), checked((uint)payloads.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(0x14, 4), indexOffset);
        output.Write(footer);
        output.Flush(true);
    }

    private static List<QlieV1Entry> ReadEntries(FileStream file, int count, uint indexOffset, long footerAt)
    {
        var entries = new List<QlieV1Entry>(count);
        var indexEnd = checked((long)indexOffset);
        for (var i = 0; i < count; i++)
        {
            if (indexEnd > footerAt - 2) throw new InvalidDataException($"V1 index row {i} is truncated");
            var length = BinaryPrimitives.ReadUInt16LittleEndian(ReadAt(file, indexEnd, 2));
            indexEnd += 2;
            if (length == 0 || indexEnd > footerAt - length - RowTailSize) throw new InvalidDataException($"V1 index row {i} name/metadata is truncated");
            var cipherName = ReadAt(file, indexEnd, length);
            indexEnd += length;
            var clearName = cipherName.ToArray();
            TransformName(clearName);
            var name = Cp932.GetString(clearName);
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException($"V1 index row {i} has an empty name");
            var row = ReadAt(file, indexEnd, RowTailSize);
            indexEnd += RowTailSize;
            var offset64 = BinaryPrimitives.ReadUInt64LittleEndian(row.AsSpan(0, 8));
            var stored = BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(8, 4));
            var unpacked = BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(12, 4));
            var flags = BinaryPrimitives.ReadUInt64LittleEndian(row.AsSpan(16, 8));
            var hash = BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(24, 4));
            if (offset64 > (ulong)long.MaxValue || offset64 > indexOffset || stored > indexOffset - offset64 || offset64 + stored > (ulong)file.Length)
                throw new InvalidDataException($"V1 entry {i} has invalid payload placement");
            entries.Add(new QlieV1Entry(i, name, clearName, cipherName, checked((long)offset64), stored, unpacked, flags, hash));
        }
        if (indexEnd != footerAt) throw new InvalidDataException($"V1 index size mismatch: parsed {indexEnd - indexOffset} bytes, expected {footerAt - indexOffset}");
        return entries;
    }

    private static uint ComputeLegacyPayloadHash(string path)
    {
        using var stream = File.OpenRead(path);
        return ComputeLegacyPayloadHash(stream);
    }

    private static uint ComputeLegacyPayloadHash(Stream stream)
    {
        return SimdAccelerated.ComputeLegacyPayloadHash(stream);
    }

    private static ulong AddWord(ulong left, ulong right)
    {
        ulong result = 0;
        for (var lane = 0; lane < 4; lane++)
        {
            var sum = ((left >> (lane * 16)) & 0xffff) + ((right >> (lane * 16)) & 0xffff);
            result |= (sum & 0xffff) << (lane * 16);
        }
        return result;
    }

    private void CopyRangeTo(Stream destination, long offset, long length, byte[]? buffer = null)
    {
        if (offset < 0 || length < 0 || offset > FileLength - length) throw new ArgumentOutOfRangeException();
        buffer ??= new byte[CopyBufferSize];
        lock (_file)
        {
            _file.Position = offset;
            while (length > 0)
            {
                var want = (int)Math.Min(buffer.Length, length);
                var read = _file.Read(buffer, 0, want);
                if (read == 0) throw new EndOfStreamException();
                destination.Write(buffer, 0, read);
                length -= read;
            }
        }
    }

    private void CopyFileTo(Stream destination, string path, uint expectedLength, byte[] buffer)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        if (source.Length != expectedLength) throw new InvalidDataException($"entry file size changed during repack: {path}");
        var remaining = (long)expectedLength;
        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new EndOfStreamException($"entry file truncated during repack: {path}");
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static byte[] ReadAt(FileStream file, long offset, int length)
    {
        var bytes = new byte[length];
        file.Position = offset;
        ReadExactly(file, bytes);
        return bytes;
    }

    private static void ReadExactly(Stream source, Span<byte> destination)
    {
        var readTotal = 0;
        while (readTotal < destination.Length)
        {
            var read = source.Read(destination[readTotal..]);
            if (read == 0) throw new EndOfStreamException();
            readTotal += read;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TransformName(Span<byte> bytes)
    {
        var key = unchecked((byte)(bytes.Length + (0xC4 ^ 0x3E)));
        for (var k = 1; k <= bytes.Length; k++)
            bytes[k - 1] ^= unchecked((byte)(k + (key ^ k)));
    }

    private static Encoding CreateCp932()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    private static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException($"unsafe V1 entry path: {relative}");
        var normalized = relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var components = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0 || components.Any(x => x is "." or "..")) throw new InvalidDataException($"unsafe V1 entry path: {relative}");
        var full = Path.GetFullPath(Path.Combine(root, normalized));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"unsafe V1 entry path: {relative}");
        return full;
    }

    private static string EnsureDirectoryTree(string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(full);
        EnsureNoReparseComponents(full, allowMissingLeaf: false);
        return full;
    }

    private static void EnsureNoReparseComponents(string path, bool allowMissingLeaf = true)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new InvalidDataException($"path has no root: {path}");
        var current = root;
        var parts = full[root.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            FileAttributes attrs;
            try { attrs = File.GetAttributes(current); }
            catch (FileNotFoundException) { if (allowMissingLeaf || i < parts.Length - 1) return; return; }
            catch (DirectoryNotFoundException) { if (allowMissingLeaf || i < parts.Length - 1) return; return; }
            if ((attrs & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"reparse point is not allowed: {current}");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(QlieV1Archive));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _file.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record RepackPayload(QlieV1Entry Entry, string Path, bool ReuseSource, uint StoredSize, uint UnpackedSize, ulong Flags, uint Hash);

    private sealed class HashSinkStream : Stream
    {
        private readonly IncrementalHash _hash;
        private HashSinkStream(IncrementalHash hash) => _hash = hash;
        public static HashSinkStream For(IncrementalHash hash) => new(hash);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => _hash.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _hash.AppendData(buffer);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

public sealed class QlieV1Entry
{
    internal QlieV1Entry(int index, string name, byte[] clearNameBytes, byte[] cipherNameBytes, long offset, uint storedSize, uint unpackedSize, ulong flags, uint hash)
    {
        Index = index;
        Name = name;
        ClearNameBytes = clearNameBytes;
        CipherNameBytes = cipherNameBytes;
        Offset = offset;
        StoredSize = storedSize;
        UnpackedSize = unpackedSize;
        Flags = flags;
        Hash = hash;
    }

    public int Index { get; }
    public string Name { get; }
    public byte[] ClearNameBytes { get; }
    public byte[] CipherNameBytes { get; }
    public long Offset { get; }
    public uint StoredSize { get; }
    public uint UnpackedSize { get; }
    public ulong Flags { get; }
    public uint Hash { get; }
}

public sealed class QlieV1Manifest
{
    public string Format { get; set; } = "QLIE";
    public string Version { get; set; } = "1.0";
    public int Count { get; set; }
    public uint IndexOffset { get; set; }
    public long ArchiveLength { get; set; }
    public string SourceName { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public List<QlieV1ManifestEntry> Entries { get; set; } = new();
}

public sealed class QlieV1ManifestEntry
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string CipherNameBase64 { get; set; } = "";
    public long Offset { get; set; }
    public uint StoredSize { get; set; }
    public uint UnpackedSize { get; set; }
    public ulong Flags { get; set; }
    public uint Hash { get; set; }
    public string ExtractedPath { get; set; } = "";
    public string ExtractedSha256 { get; set; } = "";
}

public sealed record QlieV1Verification(int CheckedEntries, long ArchiveLength, uint IndexOffset, string ArchiveSha256);

/// <summary>A read-only view of an archive payload that cannot read beyond its TOC-declared length.</summary>
internal sealed class V1BoundedStream : Stream
{
    private readonly Stream _source;
    private long _remaining;
    public V1BoundedStream(Stream source, long offset, long length) { _source = source; _source.Position = offset; _remaining = length; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _remaining;
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        count = (int)Math.Min(count, _remaining);
        if (count <= 0) return 0;
        var read = _source.Read(buffer, offset, count);
        _remaining -= read;
        return read;
    }
    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, _remaining);
        if (count <= 0) return 0;
        var read = _source.Read(buffer[..count]);
        _remaining -= read;
        return read;
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public static class QlieV1Pack
{
    public static QlieV1Archive Open(string path) => QlieV1Archive.Open(path);
    public static bool IsV1(string path) => QlieV1Archive.IsV1(path);

    public static IReadOnlyList<QlieV1Entry> List(string path)
    {
        using var archive = Open(path);
        return archive.Entries.ToArray();
    }

    public static string Extract(string path, string outputDirectory, string? manifestPath = null)
    {
        using var archive = Open(path);
        return archive.Extract(outputDirectory, manifestPath);
    }

    public static QlieV1Verification Verify(string path)
    {
        using var archive = Open(path);
        return archive.Verify();
    }

    public static string Repack(string sourcePath, string inputDirectory, string outputPath, string? manifestPath = null)
    {
        using var archive = Open(sourcePath);
        return archive.Repack(inputDirectory, outputPath, manifestPath);
    }
}
