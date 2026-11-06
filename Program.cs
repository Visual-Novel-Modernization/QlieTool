using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Collections.Generic;

namespace QlieTool;

public static class Program
{
    public static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h") { Usage(); return 0; }
            return args[0].ToLowerInvariant() switch
            {
                "list" => RunList(args),
                "extract" => RunExtract(args),
                "clone" => RunRepack(args),
                "repack" => RunRepack(args),
                "verify" => RunVerify(args),
                "self-test" => RunSelfTest(),
                _ => throw new ArgumentException($"Unknown command: {args[0]}")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static void Usage()
    {
        Console.WriteLine("QlieTool - QLIE FilePackVer3.0 archive utility");
        Console.WriteLine("  list <archive.pack> [--key key.fkey] [--game-key keydata.bin]");
        Console.WriteLine("  extract <archive.pack> <directory> [--key key.fkey] [--game-key keydata.bin] [--manifest manifest.json]");
        Console.WriteLine("  repack <manifest.json> <output.pack> --source <original.pack> --input-dir <extracted-dir> [--key key.fkey] [--game-key keydata.bin]");
        Console.WriteLine("  clone <manifest.json> <output.pack> --source <original.pack> --input-dir <extracted-dir> (compatibility alias)");
        Console.WriteLine("  verify <archive.pack> [--key key.fkey] [--game-key keydata.bin]");
        Console.WriteLine("  self-test");
        Console.WriteLine("repack rebuilds payloads, index and footer; unchanged payload bytes may be reused.");
    }

    private static int RunList(string[] args)
    {
        Require(args, 2);
        using var input = TempInput.CopyArchive(args[1], FindOption(args, "--key"), FindOption(args, "--game-key"));
        using var archive = QlieArchive.Open(input.ArchivePath, input.KeyPath, input.GameKeyPath);
        Console.WriteLine($"QLIE FilePackVer{archive.Version}; count={archive.Entries.Count}; index=0x{archive.IndexOffset:X}");
        foreach (var e in archive.Entries)
            Console.WriteLine($"{e.Index,6} {e.Offset,12} {e.StoredSize,10} {e.UnpackedSize,10} packed={e.Packed,-5} enc={e.Encryption,-3} {e.Name}");
        return 0;
    }

    private static int RunExtract(string[] args)
    {
        Require(args, 3);
        var output = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(output);
        using var input = TempInput.CopyArchive(args[1], FindOption(args, "--key"), FindOption(args, "--game-key"));
        using var archive = QlieArchive.Open(input.ArchivePath, input.KeyPath, input.GameKeyPath);
        var rows = new List<ManifestEntry>();
        foreach (var e in archive.Entries)
        {
            var destination = SafeDestination(output, e.Name);
            if (File.Exists(destination)) throw new IOException($"refusing to overwrite {destination}");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var decoded = true;
            Stream source;
            try { source = archive.OpenDecoded(e); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                decoded = false;
                destination = SafeDestination(output, e.Name + ".qlie-raw");
                if (File.Exists(destination)) throw new IOException($"refusing to overwrite {destination}");
                source = archive.OpenStored(e);
            }
            using (source)
            using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan))
                source.CopyTo(target, 1 << 20);
            var row = archive.ManifestEntry(e, destination, decoded);
            row.ExtractedPath = Path.GetRelativePath(output, destination);
            rows.Add(row);
        }
        var manifestPath = FindOption(args, "--manifest") ?? Path.Combine(output, "qlie-manifest.json");
        manifestPath = Path.GetFullPath(manifestPath);
        if (File.Exists(manifestPath)) throw new IOException($"refusing to overwrite {manifestPath}");
        var manifest = archive.CreateManifest(rows, Path.GetFileName(args[1]));
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
        Console.WriteLine($"extracted {rows.Count} entries to {output}");
        Console.WriteLine($"manifest {manifestPath}");
        return 0;
    }

    private static int RunVerify(string[] args)
    {
        Require(args, 2);
        using var input = TempInput.CopyArchive(args[1], FindOption(args, "--key"), FindOption(args, "--game-key"));
        using var archive = QlieArchive.Open(input.ArchivePath, input.KeyPath, input.GameKeyPath);
        var checkedEntries = 0;
        var decodedEntries = 0;
        var rawEntries = 0;
        foreach (var e in archive.Entries)
        {
            Stream stream;
            var decoded = true;
            try { stream = archive.OpenDecoded(e); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                decoded = false;
                stream = archive.OpenStored(e);
            }
            using (stream)
            {
                var length = stream.Length;
                _ = SHA256.HashData(stream);
                if (decoded)
                {
                    if (e.UnpackedSize != 0 && e.UnpackedSize != length)
                        throw new InvalidDataException($"entry {e.Index} unpacked size mismatch: expected {e.UnpackedSize}, got {length}");
                    decodedEntries++;
                }
                else rawEntries++;
            }
            checkedEntries++;
        }
        Console.WriteLine($"verified {checkedEntries} entries; decoded={decodedEntries}; rawStored={rawEntries}; FilePackVer{archive.Version}; index=0x{archive.IndexOffset:X}");
        return 0;
    }

    private static int RunRepack(string[] args)
    {
        Require(args, 3);
        var manifestPath = Path.GetFullPath(args[1]);
        var output = Path.GetFullPath(args[2]);
        var source = FindOption(args, "--source") ?? throw new ArgumentException("repack requires --source <original.pack>");
        var inputDir = FindOption(args, "--input-dir") ?? throw new ArgumentException("repack requires --input-dir <extracted-dir>");
        source = Path.GetFullPath(source);
        inputDir = Path.GetFullPath(inputDir);
        if (File.Exists(output)) throw new IOException($"refusing to overwrite {output}");
        if (string.Equals(output, source, StringComparison.OrdinalIgnoreCase)) throw new IOException("output archive must differ from source archive");
        var manifest = JsonSerializer.Deserialize<QlieManifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidDataException("invalid manifest");
        if (manifest.Format != "QLIE" || manifest.Version != "3.0") throw new InvalidDataException("manifest is not a QLIE V3.0 manifest");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        // Copy source and key material into one unique TEMP staging directory before any hash/read.
        using var input = TempInput.CopyArchive(source, FindOption(args, "--key"), FindOption(args, "--game-key"));
        if (!string.Equals(Sha256File(input.ArchivePath), manifest.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("source archive SHA-256 does not match manifest");
        using var archive = QlieArchive.Open(input.ArchivePath, input.KeyPath, input.GameKeyPath);
        QlieRepacker.Repack(archive, manifest, inputDir, output, new PlainEntryPayloadCodec(), new QlieV3EntryCipher(archive), null);
        Console.WriteLine($"repacked modified archive: {output} (opaque 0x400-byte trailer preserved; unknown signatures are not regenerated)");
        return 0;
    }

    private static int RunSelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "QlieTool-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archivePath = Path.Combine(root, "fixture.pack");
            SyntheticFixture.Create(archivePath);
            using (var archive = QlieArchive.Open(archivePath, null))
            {
                if (archive.Entries.Count != 3 || archive.Entries[0].Name != "hello.txt") throw new Exception("synthetic list failed");
                using var decoded = archive.OpenDecoded(archive.Entries[0]);
                using var sr = new StreamReader(decoded, Encoding.UTF8);
                if (sr.ReadToEnd() != "hello QLIE") throw new Exception("synthetic extraction failed");
                using var packed = archive.OpenDecoded(archive.Entries[1]);
                using var ms = new MemoryStream(); packed.CopyTo(ms);
                if (!ms.ToArray().SequenceEqual(SyntheticFixture.PackedPlain)) throw new Exception("1PC fixture failed");
            }
            var outDir = Path.Combine(root, "out"); Directory.CreateDirectory(outDir);
            var manifestPath = Path.Combine(root, "manifest.json");
            RunExtract(new[] { "extract", archivePath, outDir, "--manifest", manifestPath });
            var roundtrip = Path.Combine(root, "roundtrip.pack");
            File.WriteAllText(Path.Combine(outDir, "hello.txt"), "hello from modified repack", new UTF8Encoding(false));
            RunRepack(new[] { "repack", manifestPath, roundtrip, "--source", archivePath, "--input-dir", outDir });
            using (var rewritten = QlieArchive.Open(roundtrip, null))
            {
                using var changed = rewritten.OpenDecoded(rewritten.Entries[0]);
                using var sr = new StreamReader(changed, Encoding.UTF8);
                if (sr.ReadToEnd() != "hello from modified repack") throw new Exception("edited payload was not repacked");
                using var beforeRaw = QlieArchive.Open(archivePath, null);
                using var oldStored = beforeRaw.OpenStored(beforeRaw.Entries[1]);
                using var newStored = rewritten.OpenStored(rewritten.Entries[1]);
                using var oldBytes = new MemoryStream(); using var newBytes = new MemoryStream();
                oldStored.CopyTo(oldBytes); newStored.CopyTo(newBytes);
                if (!oldBytes.ToArray().SequenceEqual(newBytes.ToArray())) throw new Exception("unchanged stored payload was not reused");
            }
            File.WriteAllBytes(Path.Combine(outDir, "raw.bin.qlie-raw"), Encoding.ASCII.GetBytes("edited raw"));
            try
            {
                RunRepack(new[] { "repack", manifestPath, Path.Combine(root, "raw-edit.pack"), "--source", archivePath, "--input-dir", outDir });
                throw new Exception("edited raw entry was accepted");
            }
            catch (UnsupportedEncryptedRewriteException) { }
            var encryptedPath = Path.Combine(root, "encrypted.pack");
            var encryptedKeyPath = Path.Combine(root, "encrypted.key");
            var encryptedGamePath = Path.Combine(root, "encrypted.gamekey");
            SyntheticFixture.CreateEncrypted(encryptedPath, encryptedKeyPath, encryptedGamePath);
            var encryptedOut = Path.Combine(root, "encrypted-out"); Directory.CreateDirectory(encryptedOut);
            var encryptedManifest = Path.Combine(root, "encrypted-manifest.json");
            using (var encryptedArchive = QlieArchive.Open(encryptedPath, encryptedKeyPath, encryptedGamePath))
            {
                using var before = encryptedArchive.OpenDecoded(encryptedArchive.Entries[0]);
                using var beforeMs = new MemoryStream(); before.CopyTo(beforeMs);
                if (!beforeMs.ToArray().SequenceEqual(SyntheticFixture.EncryptedPlain)) throw new Exception("encrypted fixture decode failed");
            }
            RunExtract(new[] { "extract", encryptedPath, encryptedOut, "--key", encryptedKeyPath, "--game-key", encryptedGamePath, "--manifest", encryptedManifest });
            var encryptedFile = Path.Combine(encryptedOut, "cipher.bin");
            File.WriteAllBytes(encryptedFile, Enumerable.Range(0, 32).Select(i => (byte)(0x90 + i)).ToArray());
            var encryptedRoundtrip = Path.Combine(root, "encrypted-roundtrip.pack");
            RunRepack(new[] { "repack", encryptedManifest, encryptedRoundtrip, "--source", encryptedPath, "--input-dir", encryptedOut, "--key", encryptedKeyPath, "--game-key", encryptedGamePath });
            using (var rewrittenEncrypted = QlieArchive.Open(encryptedRoundtrip, encryptedKeyPath, encryptedGamePath))
            using (var after = rewrittenEncrypted.OpenDecoded(rewrittenEncrypted.Entries[0]))
            using (var afterMs = new MemoryStream())
            {
                after.CopyTo(afterMs);
                if (!afterMs.ToArray().SequenceEqual(File.ReadAllBytes(encryptedFile))) throw new Exception("encrypted repack readback failed");
            }
            Console.WriteLine("self-test passed (V3 index/name cipher, 1PC decode, modified payload, unchanged reuse, encrypted decode/repack/readback, raw-edit rejection)");
            return 0;
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static string? FindOption(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
    private static void Require(string[] args, int n) { if (args.Length < n) throw new ArgumentException("not enough arguments; use --help"); }
    private static string SafeDestination(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Contains(':')) throw new InvalidDataException($"unsafe entry path: {name}");
        var normalized = name.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var parts = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(x => x is "." or "..")) throw new InvalidDataException($"unsafe entry path: {name}");
        var full = Path.GetFullPath(Path.Combine(root, normalized));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"unsafe entry path: {name}");
        return full;
    }
    internal static string Sha256File(string path)
    {
        using var sha = SHA256.Create(); using var f = File.OpenRead(path); return Convert.ToHexString(sha.ComputeHash(f)).ToLowerInvariant();
    }
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}

public sealed class TempInput : IDisposable
{
    public string ArchivePath { get; }
    public string? KeyPath { get; }
    public string? GameKeyPath { get; }
    private readonly string dir;
    private TempInput(string archive, string? key, string? gameKey, string d) { ArchivePath = archive; KeyPath = key; GameKeyPath = gameKey; dir = d; }
    public static TempInput CopyArchive(string archive, string? key, string? gameKey)
    {
        archive = Path.GetFullPath(archive); if (!File.Exists(archive)) throw new FileNotFoundException("archive", archive);
        var d = Path.Combine(Path.GetTempPath(), "QlieTool-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d);
        var a = Path.Combine(d, Path.GetFileName(archive)); File.Copy(archive, a);
        string? k = null;
        if (key == null) {
            var candidates = new[] { Path.Combine(Path.GetDirectoryName(archive)!, "key.fkey"), Path.Combine(Path.GetDirectoryName(archive)!, "DLL", "key.fkey"), Path.Combine(Path.GetDirectoryName(archive)!, "..", "DLL", "key.fkey") };
            key = candidates.FirstOrDefault(File.Exists);
        }
        if (key != null)
        {
            if (!File.Exists(key)) throw new FileNotFoundException("key", key);
            k = Path.Combine(d, "key.fkey"); File.Copy(Path.GetFullPath(key), k);
        }
        string? g = null;
        if (gameKey != null && File.Exists(gameKey)) { g = Path.Combine(d, "game.keydata"); File.Copy(Path.GetFullPath(gameKey), g); }
        return new TempInput(a, k, g, d);
    }
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }
}

public sealed class QlieEntry
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public byte[] RawName { get; init; } = Array.Empty<byte>();
    public byte[] ClearNameBytes { get; init; } = Array.Empty<byte>();
    public long Offset { get; init; }
    public uint StoredSize { get; init; }
    public uint UnpackedSize { get; init; }
    public bool Packed { get; init; }
    public int Encryption { get; init; }
    public uint Hash { get; init; }
    public byte[]? KeyFile { get; set; }
    public byte[]? GameKeyData { get; set; }
}

public sealed class QlieManifest
{
    public string Format { get; set; } = "QLIE";
    public string Version { get; set; } = "3.0";
    public int Count { get; set; }
    public long IndexOffset { get; set; }
    public long ArchiveLength { get; set; }
    public string SourceName { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public string RepackMode { get; set; } = "modified-payload-rebuild";
    public List<ManifestEntry> Entries { get; set; } = new();
}

public sealed class ManifestEntry
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string RawNameBase64 { get; set; } = "";
    public long Offset { get; set; }
    public uint StoredSize { get; set; }
    public uint UnpackedSize { get; set; }
    public bool Packed { get; set; }
    public int Encryption { get; set; }
    public uint Hash { get; set; }
    public string PayloadSha256 { get; set; } = "";
    public string ExtractedSha256 { get; set; } = "";
    public bool Decoded { get; set; }
    public string ExtractedPath { get; set; } = "";
}

public sealed class QlieArchive : IDisposable
{
    private readonly FileStream file;
    private readonly byte[]? keyFile;
    private readonly Encoding cp932 = Encoding.GetEncoding(932);
    public int VersionMajor { get; }
    public int VersionMinor { get; }
    public string Version => $"{VersionMajor}.{VersionMinor}";
    public long IndexOffset { get; }
    public List<QlieEntry> Entries { get; } = new();
    private readonly uint arcKey;
    private readonly byte[] trailerBlock;
    private readonly long trailerStart;
    public long TrailerStart => trailerStart;
    public byte[] CopyTrailerBlock() => trailerBlock.ToArray();
    public uint ArcKey => arcKey;

    private readonly byte[]? gameKeyData;
    private QlieArchive(FileStream f, byte[]? key, byte[]? gameKey, int major, int minor, long indexOffset, uint arc, byte[] trailer, long trailerAt)
    { file = f; keyFile = key; gameKeyData = gameKey; VersionMajor = major; VersionMinor = minor; IndexOffset = indexOffset; arcKey = arc; trailerBlock = trailer; trailerStart = trailerAt; }

    public static QlieArchive Open(string path, string? keyPath, string? gameKeyPath = null)
    {
        var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        try
        {
            if (f.Length < 0x41c) throw new InvalidDataException("file is too short for QLIE V3 trailer/footer");
            var footer = f.Length - 0x1c;
            var trailerAt = f.Length - 0x41c;
            var trailer = ReadBytes(f, trailerAt, 0x400);
            var magic = ReadBytes(f, footer, 0x0e);
            var text = Encoding.ASCII.GetString(magic);
            if (!text.StartsWith("FilePackVer", StringComparison.Ordinal) || text[12] != '.') throw new InvalidDataException("not a QLIE FilePackVer archive");
            if (!int.TryParse(text.Substring(11, 1), out var major) || !int.TryParse(text.Substring(13, 1), out var minor)) throw new InvalidDataException("invalid QLIE version");
            var count = ReadInt32At(f, footer + 0x10); var index = ReadInt64At(f, footer + 0x14);
            if (major != 3 || minor != 0) throw new NotSupportedException($"FilePackVer{major}.{minor}: this tool targets V3.0");
            if (count < 0 || count > 10_000_000 || index < 0 || index >= trailerAt) throw new InvalidDataException("invalid QLIE index metadata");
            var arcBytes = trailer.AsSpan(0, 0x100).ToArray(); var arc = CalculateArcKey(arcBytes);
            byte[]? key = keyPath != null && File.Exists(keyPath) ? File.ReadAllBytes(keyPath) : null;
            byte[]? gameKey = gameKeyPath != null && File.Exists(gameKeyPath) ? File.ReadAllBytes(gameKeyPath) : null;
            var a = new QlieArchive(f, key, gameKey, major, minor, index, arc, trailer, trailerAt);
            a.ReadEntries(count, footer); return a;
        }
        catch { f.Dispose(); throw; }
    }

    private void ReadEntries(int count, long footer)
    {
        file.Position = IndexOffset;
        byte[]? activeKey = keyFile;
        for (var i = 0; i < count; i++)
        {
            var n = ReadUInt16(file); if (n > 0x100) throw new InvalidDataException($"entry {i}: invalid name length {n}");
            var raw = ReadExactly(file, n); var nameBytes = raw.ToArray(); DecryptName(nameBytes, n);
            var name = cp932.GetString(nameBytes);
            var offset = ReadInt64(file); var stored = ReadUInt32(file); var unpacked = ReadUInt32(file); var packed = ReadInt32(file) != 0; var enc = ReadInt32(file); var hash = ReadUInt32(file);
            if (offset < 0 || offset > file.Length - stored || offset >= IndexOffset) throw new InvalidDataException($"entry {i}: invalid placement");
            var entry = new QlieEntry { Index=i, Name=name, RawName=raw, ClearNameBytes=nameBytes, Offset=offset, StoredSize=stored, UnpackedSize=unpacked, Packed=packed, Encryption=enc, Hash=hash, KeyFile=activeKey, GameKeyData=gameKeyData };
            Entries.Add(entry);
            if (file.Position > trailerStart) throw new InvalidDataException("QLIE index overlaps the 0x400-byte V3 trailer block");
            var nextIndex = file.Position;
            if (activeKey != null && name.IndexOf("pack_keyfile", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var payload = ReadRawPayload(entry);
                if (entry.Encryption != 0) DecryptEntry(payload, entry, activeKey, gameKeyData);
                if (entry.Packed) payload = Decompress1Pc(payload) ?? throw new InvalidDataException($"entry {i}: invalid embedded key payload");
                activeKey = payload;
            }
            file.Position = nextIndex;
        }
    }

    public byte[] EncodeName(string name)
    {
        var bytes = cp932.GetBytes(name);
        if (bytes.Length > 0x100) throw new InvalidDataException($"entry name exceeds 256 CP932 bytes: {name}");
        var key = unchecked(bytes.Length + (unchecked((int)arcKey) ^ 0x3E));
        for (var k = 0; k < bytes.Length; k++) bytes[k] ^= unchecked((byte)(((k + 1) ^ key) + (k + 1)));
        return bytes;
    }

    private void DecryptName(byte[] bytes, int length)
    {
        var key = unchecked(length + (unchecked((int)arcKey) ^ 0x3E));
        for (var k=0;k<length;k++) bytes[k] ^= unchecked((byte)(((k+1)^key)+(k+1)));
    }

    public Stream OpenStored(QlieEntry e)
    {
        return new LimitedStream(file, e.Offset, e.StoredSize);
    }

    public Stream OpenDecoded(QlieEntry e)
    {
        if (e.StoredSize > int.MaxValue) throw new NotSupportedException("entry exceeds in-memory decrypt limit; list remains supported");
        var data = ReadRawPayload(e);
        if (e.Encryption != 0)
        {
            if (e.KeyFile == null) throw new InvalidDataException($"entry {e.Index} is encrypted; supply --key key.fkey");
            DecryptEntry(data, e, e.KeyFile, e.GameKeyData);
        }
        if (e.Packed)
        {
            var decoded = Decompress1Pc(data); if (decoded == null) throw new InvalidDataException($"entry {e.Index}: unsupported or invalid 1PC payload"); data = decoded;
        }
        return new MemoryStream(data, writable:false);
    }

    public ManifestEntry ManifestEntry(QlieEntry e, string extractedPath, bool decoded=true)
    {
        string dh; using (var stream = decoded ? OpenDecoded(e) : OpenStored(e)) dh = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        file.Position=e.Offset; using var limited = new LimitedStream(file,e.StoredSize); var ph=Convert.ToHexString(SHA256.HashData(limited)).ToLowerInvariant();
        return new ManifestEntry { Index=e.Index, Name=e.Name, RawNameBase64=Convert.ToBase64String(e.RawName), Offset=e.Offset, StoredSize=e.StoredSize, UnpackedSize=e.UnpackedSize, Packed=e.Packed, Encryption=e.Encryption, Hash=e.Hash, PayloadSha256=ph, ExtractedSha256=dh, Decoded=decoded };
    }
    public QlieManifest CreateManifest(List<ManifestEntry> rows, string sourceName)
    { return new QlieManifest { Version=Version, Count=Entries.Count, IndexOffset=IndexOffset, ArchiveLength=file.Length, SourceName=sourceName, SourceSha256=HashArchive(), Entries=rows }; }
    private string HashArchive() { file.Position=0; return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }

    private byte[] ReadRawPayload(QlieEntry e)
    {
        file.Position = e.Offset;
        return ReadExactly(file, checked((int)e.StoredSize));
    }

    private void DecryptEntry(byte[] data, QlieEntry entry, byte[] keyFileBytes, byte[]? gameKeyData)
        => QlieV3EntryCipher.Transform(data, entry, arcKey, keyFileBytes, gameKeyData, false);

    public void Dispose() => file.Dispose();
    private static byte[] ReadBytes(Stream s,long pos,int length){s.Position=pos;return ReadExactly(s,length);}
    private static byte[] ReadExactly(Stream s,int length){var b=new byte[length];var n=0;while(n<length){var r=s.Read(b,n,length-n);if(r==0)throw new EndOfStreamException();n+=r;}return b;}
    private static int ReadInt32(Stream s)=>BitConverter.ToInt32(ReadExactly(s,4));
    private static int ReadInt32At(Stream s,long pos){s.Position=pos;return ReadInt32(s);}
    private static long ReadInt64(Stream s)=>BitConverter.ToInt64(ReadExactly(s,8));
    private static long ReadInt64At(Stream s,long pos){s.Position=pos;return ReadInt64(s);}
    private static ushort ReadUInt16(Stream s)=>BitConverter.ToUInt16(ReadExactly(s,2));
    private static uint ReadUInt32(Stream s)=>BitConverter.ToUInt32(ReadExactly(s,4));
    private static uint CalculateArcKey(byte[] data)
    { ulong hash=0,key=0; for(var i=0;i+8<=data.Length;i+=8){hash=PackedMath.AddW(hash,0x0307030703070307);key=PackedMath.AddW(key,BitConverter.ToUInt64(data,i)^hash);} return (uint)(key^(key>>32))&0x0fffffff; }
    public static byte[]? Decompress1Pc(byte[] input)
    {
        if(input.Length<12||BitConverter.ToUInt32(input,0)!=0xff435031)return null; var is16=(input[4]&1)!=0; var outputLength=BitConverter.ToInt32(input,8); if(outputLength<0)return null; var output=new byte[outputLength]; var node0=new byte[256];var node1=new byte[256];var child=new byte[256];var src=12;var dst=0;
        try { while(src<input.Length){for(var i=0;i<256;i++)node0[i]=(byte)i;var i2=0;while(i2<256){var count=input[src++];if(count>127){i2+=count-127;count=0;}if(i2>255)break;count++;for(var k=0;k<count;k++){node0[i2]=input[src++];if(node0[i2]!=i2)node1[i2]=input[src++];i2++;}}var count2=is16?BitConverter.ToUInt16(input,src):BitConverter.ToInt32(input,src);src+=is16?2:4;var stack=0;while(true){int idx;if(stack>0)idx=child[--stack];else{if(count2==0)break;count2--;if(src>=input.Length)throw new InvalidDataException();idx=input[src++];}if(node0[idx]==idx){if(dst>=output.Length)throw new InvalidDataException();output[dst++]=(byte)idx;}else{if(stack+2>child.Length)throw new InvalidDataException();child[stack++]=node1[idx];child[stack++]=node0[idx];}}} } catch(IndexOutOfRangeException){return null;} if(dst!=output.Length)return null; return output;
    }
}

public sealed class UnsupportedEncryptedRewriteException : NotSupportedException
{
    public UnsupportedEncryptedRewriteException(string message) : base(message) { }
}

/// <summary>Payload encoder injection point. The built-in writer intentionally emits literal, unpacked payloads.</summary>
public interface IEntryPayloadCodec
{
    byte[] Encode(byte[] decoded, QlieEntry sourceEntry);
}

/// <summary>V3 entry cipher injection point. Encryption/signature writers are not guessed.</summary>
public interface IV3EntryCipher
{
    byte[] Encrypt(byte[] plainPayload, QlieEntry sourceEntry, string entryName);
}

public interface IEntryHashProvider
{
    uint ComputeHash(byte[] decodedPayload, QlieEntry sourceEntry);
}

public sealed class PreserveSourceEntryHashProvider : IEntryHashProvider
{
    public uint ComputeHash(byte[] decodedPayload, QlieEntry sourceEntry) => sourceEntry.Hash;
}

public sealed class PlainEntryPayloadCodec : IEntryPayloadCodec
{
    public byte[] Encode(byte[] decoded, QlieEntry sourceEntry) => decoded.ToArray();
}

public sealed class QlieRepackItem
{
    public required string Name { get; init; }
    public required byte[] Stored { get; init; }
    public required uint UnpackedSize { get; init; }
    public required bool Packed { get; init; }
    public required int Encryption { get; init; }
    public required uint Hash { get; init; }
    public required byte[] RawName { get; init; }
    public required QlieEntry Source { get; init; }
}

public sealed class QlieV3EntryCipher : IV3EntryCipher
{
    private readonly uint arcKey;
    public QlieV3EntryCipher(QlieArchive archive) { arcKey = archive.ArcKey; }

    public byte[] Encrypt(byte[] plainPayload, QlieEntry sourceEntry, string entryName)
    {
        if (sourceEntry.Encryption == 0) return plainPayload.ToArray();
        var key = sourceEntry.KeyFile ?? throw new UnsupportedEncryptedRewriteException($"entry {sourceEntry.Index} has encryption={sourceEntry.Encryption} but no key material");
        var result = plainPayload.ToArray();
        Transform(result, sourceEntry, arcKey, key, sourceEntry.GameKeyData, true);
        return result;
    }

    // GARbro's V3 reader processes only complete little-endian 64-bit blocks.
    // The remaining tail is intentionally preserved byte-for-byte, matching reader semantics.
    internal static void Transform(byte[] data, QlieEntry entry, uint arcKey, byte[] keyFile, byte[]? gameKey, bool encrypt)
    {
        if (data.Length < 8) return;
        var length = (uint)data.Length;
        if (gameKey == null || gameKey.Length == 0)
        {
            ulong state = 0xA73C5F9DA73C5F9DUL;
            ulong feedback = ((ulong)(length + arcKey) ^ 0xFEC9753Eu);
            feedback |= feedback << 32;
            for (var i = 0; i < data.Length / 8; i++)
            {
                state = PackedMath.AddD(state, 0xCE24F523CE24F523UL) ^ feedback;
                var input = BitConverter.ToUInt64(data, i * 8);
                var plain = encrypt ? input : input ^ state;
                var output = encrypt ? input ^ state : plain;
                BitConverter.GetBytes(output).CopyTo(data, i * 8);
                feedback = plain;
            }
            return;
        }

        uint hash = 0x85F532, seed = 0x33F641;
        for (uint i = 0; i < entry.RawName.Length; i++) { hash += (i & 0xff) * entry.RawName[i]; seed ^= hash; }
        seed += arcKey ^ (7 * (length & 0xffffff) + length + hash + (hash ^ length ^ 0x8f32dcu));
        seed = 9 * (seed & 0xffffff);
        seed ^= 0x453Au;
        var mt = new QlieMersenneTwister(seed);
        mt.XorState(keyFile); mt.XorState(gameKey);
        var table = new ulong[16]; for (var i = 0; i < table.Length; i++) table[i] = mt.Rand64();
        for (var i = 0; i < 9; i++) mt.Rand();
        ulong hash64 = mt.Rand64(); uint t2 = mt.Rand() & 0xf;
        for (var i = 0; i < data.Length / 8; i++)
        {
            hash64 = PackedMath.AddD(hash64 ^ table[t2], table[t2]);
            var input = BitConverter.ToUInt64(data, i * 8);
            var plain = encrypt ? input : input ^ hash64;
            var output = encrypt ? input ^ hash64 : plain;
            BitConverter.GetBytes(output).CopyTo(data, i * 8);
            hash64 = PackedMath.AddB(hash64, plain) ^ plain;
            hash64 = PackedMath.AddW(PackedMath.ShiftD(hash64, 1), plain);
            t2 = (t2 + 1) & 0xf;
        }
    }
}

public static class QlieRepacker
{
    private const int IndexEntryFixed = 2 + 8 + 4 + 4 + 4 + 4 + 4;
    private const int PayloadAlignment = 0x10;
    private const int TrailerSize = 0x400;
    private const int FooterSize = 0x1c;

    public static void Repack(QlieArchive archive, QlieManifest manifest, string inputDir, string outputPath,
        IEntryPayloadCodec codec, IV3EntryCipher? cipher, byte[]? trailerOverride, IEntryHashProvider? hashProvider = null)
    {
        if (!Directory.Exists(inputDir)) throw new DirectoryNotFoundException($"input directory not found: {inputDir}");
        if (manifest.Entries.Count != archive.Entries.Count) throw new InvalidDataException("manifest entry count does not match source archive");
        var byIndex = manifest.Entries.ToDictionary(x => x.Index);
        hashProvider ??= new PreserveSourceEntryHashProvider();
        var items = new List<QlieRepackItem>(archive.Entries.Count);
        foreach (var source in archive.Entries)
        {
            if (!byIndex.TryGetValue(source.Index, out var row) || !string.Equals(row.Name, source.Name, StringComparison.Ordinal))
                throw new InvalidDataException($"manifest entry {source.Index} does not match source name");
            var rel = string.IsNullOrWhiteSpace(row.ExtractedPath) ? source.Name + (row.Decoded ? "" : ".qlie-raw") : row.ExtractedPath;
            var path = SafeInputPath(inputDir, rel);
            if (!File.Exists(path)) throw new FileNotFoundException($"missing extracted entry {rel}", path);
            var fileHash = Program.Sha256File(path);
            var expected = row.Decoded ? row.ExtractedSha256 : row.PayloadSha256;
            if (string.Equals(fileHash, expected, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new QlieRepackItem { Name=source.Name, Stored=ReadStored(archive, source), UnpackedSize=source.UnpackedSize,
                    Packed=source.Packed, Encryption=source.Encryption, Hash=source.Hash, RawName=source.RawName.ToArray(), Source=source });
                continue;
            }
            if (!row.Decoded) throw new UnsupportedEncryptedRewriteException($"entry {source.Index} is undecoded raw data; edited .qlie-raw entries are rejected (encryption={source.Encryption}, offset=0x{source.Offset:X})");
            if (source.Name.IndexOf("pack_keyfile", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new UnsupportedEncryptedRewriteException($"entry {source.Index} is the active pack_keyfile; editing it would invalidate the active key chain for entries {source.Index + 1}..{archive.Entries.Count - 1} (offset=0x{source.Offset:X})");
            if (source.Encryption != 0 && cipher == null)
                throw new UnsupportedEncryptedRewriteException($"entry {source.Index} edited encrypted payload at offset 0x{source.Offset:X}; provide IV3EntryCipher to prove inverse V3 encryption/signature generation");
            var decoded = File.ReadAllBytes(path);
            var encoded = codec.Encode(decoded, source);
            var encryption = 0;
            var packed = false;
            if (source.Encryption != 0)
            {
                encoded = cipher!.Encrypt(encoded, source, source.Name);
                encryption = source.Encryption;
            }
            items.Add(new QlieRepackItem { Name=source.Name, Stored=encoded, UnpackedSize=checked((uint)decoded.Length), Packed=packed,
                Encryption=encryption, Hash=hashProvider.ComputeHash(decoded, source), RawName=archive.EncodeName(source.Name), Source=source });
        }
        WriteArchive(archive, items, outputPath, trailerOverride);
    }

    private static void WriteArchive(QlieArchive archive, IReadOnlyList<QlieRepackItem> items, string outputPath, byte[]? trailerOverride)
    {
        var indexOffset = 0L;
        var offsets = new long[items.Count];
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
        for (var i = 0; i < items.Count; i++)
        {
            Align(output, PayloadAlignment);
            offsets[i] = output.Position;
            output.Write(items[i].Stored, 0, items[i].Stored.Length);
        }
        Align(output, PayloadAlignment);
        indexOffset = output.Position;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var name = item.RawName.Length == 0 ? archive.EncodeName(item.Name) : item.RawName;
            if (name.Length > ushort.MaxValue) throw new InvalidDataException($"entry name too long: {item.Name}");
            output.Write(BitConverter.GetBytes((ushort)name.Length));
            output.Write(name, 0, name.Length);
            output.Write(BitConverter.GetBytes(offsets[i]));
            output.Write(BitConverter.GetBytes((uint)item.Stored.Length));
            output.Write(BitConverter.GetBytes(item.UnpackedSize));
            output.Write(BitConverter.GetBytes(item.Packed ? 1 : 0));
            output.Write(BitConverter.GetBytes(item.Encryption));
            output.Write(BitConverter.GetBytes(item.Hash));
        }
        var trailer = trailerOverride ?? archive.CopyTrailerBlock();
        if (trailer.Length != TrailerSize) throw new InvalidDataException("V3 trailer block must be exactly 0x400 bytes");
        output.Write(trailer, 0, trailer.Length);
        var footer = new byte[FooterSize];
        Encoding.ASCII.GetBytes("FilePackVer3.0").CopyTo(footer, 0);
        BitConverter.GetBytes(items.Count).CopyTo(footer, 0x10);
        BitConverter.GetBytes(indexOffset).CopyTo(footer, 0x14);
        output.Write(footer, 0, footer.Length);
    }

    private static byte[] ReadStored(QlieArchive archive, QlieEntry entry)
    {
        using var source = archive.OpenStored(entry);
        using var ms = new MemoryStream(); source.CopyTo(ms);
        return ms.ToArray();
    }

    private static string SafeInputPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException($"unsafe manifest extracted path: {relative}");
        var normalized = relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"unsafe manifest extracted path: {relative}");
        return full;
    }

    private static void Align(Stream stream, int alignment)
    {
        var rem = stream.Position % alignment;
        if (rem != 0) stream.Write(new byte[alignment - rem]);
    }
}

public static class PackedMath
{
    public static ulong AddB(ulong a,ulong b){ulong r=0;for(var i=0;i<8;i++){var x=((a>>(i*8))&255)+((b>>(i*8))&255);r|=(x&255)<<(i*8);}return r;}
    public static ulong AddW(ulong a,ulong b){ulong r=0;for(var i=0;i<4;i++){var x=((a>>(i*16))&65535)+((b>>(i*16))&65535);r|=(x&65535)<<(i*16);}return r;}
    public static ulong AddD(ulong a,ulong b){return ((a&0xffffffffUL)+(b&0xffffffffUL)&0xffffffffUL)|((((a>>32)+(b>>32))&0xffffffffUL)<<32);}
    public static ulong ShiftD(ulong a,int n){ var lo=((a&0xffffffffUL)<<n)&0xffffffffUL; var hi=(((a>>32)&0xffffffffUL)<<n)&0xffffffffUL; return lo|(hi<<32); }
}

public sealed class QlieMersenneTwister
{
    private readonly uint[] mt=new uint[64]; private int mti=64; private static readonly uint[] Mag={0,0x9908b0df};
    public QlieMersenneTwister(uint seed){mt[0]=seed;for(mti=1;mti<64;mti++)mt[mti]=0x6611bc19u*(mt[mti-1]^(mt[mti-1]>>30))+(uint)mti;}
    public void XorState(byte[] data){var n=Math.Min(data.Length/4,64);for(var i=0;i<n;i++)mt[i]^=BitConverter.ToUInt32(data,i*4);}
    public uint Rand(){if(mti>=64){int k;for(k=0;k<25;k++){var y=(mt[k]&0x80000000)|((mt[k+1]&0x7fffffff)>>1);mt[k]=mt[k+39]^y^Mag[mt[k+1]&1];}for(;k<63;k++){var y=(mt[k]&0x80000000)|((mt[k+1]&0x7fffffff)>>1);mt[k]=mt[k-25]^y^Mag[mt[k+1]&1];}var y2=(mt[63]&0x80000000)|((mt[0]&0x7fffffff)>>1);mt[63]=mt[38]^y2^Mag[mt[k-1]&1];mti=0;}var y3=mt[mti++];y3^=y3>>11;y3^=(y3<<7)&0x9c4f88e3;y3^=(y3<<15)&0xe7f70000;y3^=y3>>18;return y3;}
    public ulong Rand64(){var a=Rand();return a|((ulong)Rand()<<32);}
}

public sealed class LimitedStream : Stream
{
    private readonly Stream inner; private long left; public LimitedStream(Stream s,long length){inner=s;left=length;} public LimitedStream(Stream s,long start,long length){inner=s;inner.Position=start;left=length;} public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>left;public override long Position{get=>0;set=>throw new NotSupportedException();}public override void Flush()=>throw new NotSupportedException();public override int Read(byte[] b,int o,int c){c=(int)Math.Min(c,left);if(c<=0)return 0;var n=inner.Read(b,o,c);left-=n;return n;}public override long Seek(long o,SeekOrigin w)=>throw new NotSupportedException();public override void SetLength(long v)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
}

public static class SyntheticFixture
{
    public static readonly byte[] PackedPlain = Encoding.UTF8.GetBytes("packed fixture");
    public static void Create(string path)
    {
        var entries=new[]{(Name:"hello.txt",Data:Encoding.UTF8.GetBytes("hello QLIE"),Packed:false),(Name:"packed.bin",Data:MakePacked(),Packed:true),(Name:"raw.bin",Data:Encoding.ASCII.GetBytes("unsupported raw payload"),Packed:true)};
        using var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);var indexOffset=0L;var offsets=new List<long>();
        foreach(var e in entries){offsets.Add(f.Position);f.Write(e.Data);}
        indexOffset=f.Position;foreach(var x in entries){var raw=EncodeName(x.Name,0);f.Write(BitConverter.GetBytes((ushort)raw.Length));f.Write(raw);f.Write(BitConverter.GetBytes(offsets[Array.IndexOf(entries,x)]));f.Write(BitConverter.GetBytes((uint)x.Data.Length));f.Write(BitConverter.GetBytes((uint)(x.Packed?PackedPlain.Length:x.Data.Length)));f.Write(BitConverter.GetBytes(x.Packed?1:0));f.Write(BitConverter.GetBytes(0));f.Write(BitConverter.GetBytes(0u));}
        var pad=0x400; f.Write(new byte[pad]);var footer=f.Position;f.Write(Encoding.ASCII.GetBytes("FilePackVer3.0"));f.Write(new byte[2]);f.Write(BitConverter.GetBytes(entries.Length));f.Write(BitConverter.GetBytes(indexOffset));
    }
    private static byte[] MakePacked(){var body=PackedPlain;using var m=new MemoryStream();m.Write(BitConverter.GetBytes(0xff435031u));m.WriteByte(0);m.WriteByte(0);m.Write(BitConverter.GetBytes(body.Length));m.WriteByte(0);m.WriteByte(255); // skip 128 nodes? construct simple leaf table: 0..255 identity encoded as 255, then one entry
        // Correct table: count byte 255 => skip 128, then count 0 => one mapping for symbol 128; remaining table incomplete. Use one block with 256 identity mappings encoded as 127?
        m.SetLength(0); m.Write(BitConverter.GetBytes(0xff435031u));m.WriteByte(0);m.WriteByte(0);m.WriteByte(0);m.WriteByte(0);m.Write(BitConverter.GetBytes(body.Length));m.WriteByte(127); for(var i=0;i<128;i++)m.WriteByte((byte)i); m.WriteByte(127); for(var i=128;i<256;i++)m.WriteByte((byte)i); m.Write(BitConverter.GetBytes(body.Length)); m.Write(body); return m.ToArray();}
    private static byte[] EncodeName(string name,uint arc){var b=Encoding.GetEncoding(932).GetBytes(name);var key=unchecked(b.Length+(unchecked((int)arc)^0x3E));for(var k=0;k<b.Length;k++)b[k]^=unchecked((byte)(((k+1)^key)+(k+1)));return b;}
    public static readonly byte[] EncryptedPlain = Enumerable.Range(0, 32).Select(i => (byte)(0x40 + i)).ToArray();
    public static void CreateEncrypted(string path, string keyPath, string gameKeyPath)
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();
        var game = Enumerable.Range(0, 48).Select(i => (byte)(0xa0 + i)).ToArray();
        File.WriteAllBytes(keyPath, key); File.WriteAllBytes(gameKeyPath, game);
        var name = "cipher.bin"; var raw = EncodeName(name, 0);
        var entry = new QlieEntry { Index=0, Name=name, RawName=raw, Encryption=4, KeyFile=key, GameKeyData=game };
        var stored = EncryptedPlain.ToArray(); QlieV3EntryCipher.Transform(stored, entry, 0, key, game, true);
        using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        f.Write(stored); var indexOffset = f.Position;
        f.Write(BitConverter.GetBytes((ushort)raw.Length)); f.Write(raw); f.Write(BitConverter.GetBytes(0L)); f.Write(BitConverter.GetBytes((uint)stored.Length)); f.Write(BitConverter.GetBytes((uint)EncryptedPlain.Length)); f.Write(BitConverter.GetBytes(0)); f.Write(BitConverter.GetBytes(4)); f.Write(BitConverter.GetBytes(0u));
        f.Write(new byte[0x400]); f.Write(Encoding.ASCII.GetBytes("FilePackVer3.0")); f.Write(new byte[2]); f.Write(BitConverter.GetBytes(1)); f.Write(BitConverter.GetBytes(indexOffset));
    }

}
