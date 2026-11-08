using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QlieTool;

/// <summary>Reader and rebuilding writer for FilePackVer3.1 / HashVer1.4.</summary>
public static class QlieModernArchive
{
    private const int HeadSize = 28, KeySize = 1060, HashHeadSize = 68, EntrySize = 28;
    private static readonly byte[] HeadMagic = Encoding.ASCII.GetBytes("FilePackVer3.1\0\0");
    private static readonly byte[] HashMagic = Encoding.ASCII.GetBytes("HashVer1.4");
    private static readonly byte[] KeySignature = Encoding.ASCII.GetBytes("8hr48uky,8ugi8ewra4g8d5vbf5hb5s6");
    private static readonly Encoding Utf16Le = new UnicodeEncoding(false, false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record Entry(string Name, ulong Offset, uint Stored, uint Original, uint Compressed, uint Obfuscated, uint Hash);
    private sealed record Archive(byte[] KeyBlock, uint ArchiveKey, List<Entry> Entries);
    public sealed record FileRecord(string Name, uint Size, uint StoredSize, uint Compressed, uint Obfuscated, uint Hash);
    public sealed class Manifest { public string Format { get; set; } = "FilePackVer3.1"; public string Source { get; set; } = ""; public List<FileRecord> Files { get; set; } = new(); }

    public static bool IsMatch(string path)
    {
        try { using var s = File.OpenRead(path); if (s.Length < HeadSize + KeySize) return false; s.Position = s.Length - HeadSize; Span<byte> b = stackalloc byte[16]; return s.Read(b) == 16 && b.SequenceEqual(HeadMagic); }
        catch { return false; }
    }

    public static IReadOnlyList<FileRecord> List(string path)
    {
        var a = Read(path);
        return a.Entries.Select(e => new FileRecord(e.Name, e.Original, e.Stored, e.Compressed, e.Obfuscated, e.Hash)).ToArray();
    }

    public static Manifest Extract(string path, string outputDirectory, bool convertDpng = false)
    {
        var a = Read(path); Directory.CreateDirectory(outputDirectory);
        byte[]? commonKeyFile = null;
        var commonEntry = a.Entries.FirstOrDefault(x => x.Name.Contains("pack_keyfile", StringComparison.OrdinalIgnoreCase));
        if (commonEntry is not null)
        {
            commonKeyFile = ReadAt(path, commonEntry.Offset, checked((int)commonEntry.Stored));
            VerifyStoredHash(commonKeyFile, commonEntry);
            if (commonEntry.Obfuscated != 0) QlieV31.TransformEntry(commonKeyFile, commonEntry.Name, a.ArchiveKey, commonEntry.Obfuscated, null, keyLength: commonEntry.Stored);
            if (commonEntry.Compressed != 0) commonKeyFile = QlieV31.Bpe(commonKeyFile, commonEntry.Original);
        }
        byte[]? commonSource = commonKeyFile;
        if (commonEntry is not null && commonEntry.Name != "pack_keyfile_kfueheish15538fa9or.key")
        {
            commonSource = null;
            throw new InvalidDataException("V3.1 keyfile must use the canonical key filename");
        }
        foreach (var e in a.Entries)
        {
            var target = SafePath(outputDirectory, e.Name); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var stored = ReadAt(path, e.Offset, checked((int)e.Stored));
            VerifyStoredHash(stored, e);
            byte[] payload = stored;
            byte[]? common = null;
            if (e.Obfuscated != 0)
            {
                if (e.Obfuscated == 2) common = commonKeyFile ?? throw new InvalidDataException("mode 2 archive has no decoded pack_keyfile source");
                payload = QlieV31.TransformEntry((byte[])stored.Clone(), e.Name, a.ArchiveKey, e.Obfuscated, common, keyLength: e.Stored);
            }
            if (e.Compressed != 0) payload = QlieV31.Bpe(payload, e.Original);
            else if (payload.Length != e.Original) throw new InvalidDataException($"{e.Name}: decoded size mismatch");
            if (convertDpng && Path.GetExtension(e.Name).Equals(".png", StringComparison.OrdinalIgnoreCase) && QlieDpng.IsDpng(payload))
            {
                payload = QlieDpng.Decode(payload);
            }
            File.WriteAllBytes(target, payload);
        }
        var manifest = new Manifest { Source = Path.GetFileName(path), Files = a.Entries.Select(e => new FileRecord(e.Name, e.Original, e.Stored, e.Compressed, e.Obfuscated, e.Hash)).ToList() };
        File.WriteAllText(Path.Combine(outputDirectory, "qlie-v31-manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
        return manifest;
    }

    /// <summary>Regenerates payloads, names, entries, HashVer1.4, key block and footer.</summary>
    public static void Repack(string manifestPath, string sourcePath, string inputDirectory, string outputPath)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath)) ?? throw new InvalidDataException("invalid V3.1 manifest");
        if (manifest.Format != "FilePackVer3.1") throw new InvalidDataException("manifest is not FilePackVer3.1");
        var source = Read(sourcePath); var names = manifest.Files.Select(f => f.Name).ToArray();
        if (!string.Equals(manifest.Source, Path.GetFileName(sourcePath), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("manifest source name mismatch");
        if (names.Length == 0 || names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new InvalidDataException("manifest has no files or duplicate names");
        var material = new List<(string Name, byte[] Data, Entry Old)>();
        foreach (var f in manifest.Files)
        {
            var old = source.Entries.FirstOrDefault(x => x.Name == f.Name) ?? throw new InvalidDataException($"new entries are unsupported: {f.Name}");
            var input = SafePath(inputDirectory, f.Name); if (!File.Exists(input)) throw new FileNotFoundException("manifest input missing", input);
            material.Add((f.Name, File.ReadAllBytes(input), old));
        }
        foreach (var old in source.Entries)
            if (!names.Contains(old.Name, StringComparer.Ordinal)) throw new InvalidDataException($"manifest omits source entry {old.Name}");
        var commonEntry = source.Entries.FirstOrDefault(x => x.Name.Contains("pack_keyfile", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("V3.1 keyfile entry is missing");
        var commonPath = SafePath(inputDirectory, commonEntry.Name);
        var commonSource = File.ReadAllBytes(commonPath);
        var entries = new List<Entry>(material.Count); var temp = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        try
        {
            using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            foreach (var item in material)
            {
                var plain = item.Data; var old = item.Old;
                var stored = old.Obfuscated == 0 ? plain : QlieV31.TransformEntry((byte[])plain.Clone(), item.Name, source.ArchiveKey, old.Obfuscated, old.Obfuscated == 2 ? commonSource : null, true, checked((uint)plain.Length));
                var offset = (ulong)output.Position; output.Write(stored);
                entries.Add(new Entry(item.Name, offset, checked((uint)stored.Length), checked((uint)plain.Length), 0, old.Obfuscated, QlieV31.Hash(stored)));
            }
            var tableOffset = (ulong)output.Position;
            foreach (var e in entries)
            {
                var name = Utf16Le.GetBytes(e.Name); if (name.Length / 2 > ushort.MaxValue) throw new InvalidDataException("entry name too long");
                WriteU16(output, checked((ushort)(name.Length / 2))); QlieV31.NameCrypt(name, checked((uint)(name.Length / 2)), source.ArchiveKey); output.Write(name);
                WriteU64(output, e.Offset); WriteU32(output, e.Stored); WriteU32(output, e.Original); WriteU32(output, e.Compressed); WriteU32(output, e.Obfuscated); WriteU32(output, e.Hash);
            }
            var hashData = BuildHashData(names); QlieV31.Chain(hashData, 0x0428, false);
            var hashHead = new byte[HashHeadSize]; HashMagic.CopyTo(hashHead, 0);
            W32(hashHead,16,256); W32(hashHead,20,checked((uint)entries.Count)); W32(hashHead,24,checked((uint)entries.Count*4)); W32(hashHead,28,checked((uint)hashData.Length)); W32(hashHead,36,4);
            output.Write(hashHead); output.Write(hashData);
            var keyBlock = (byte[])source.KeyBlock.Clone(); W32(keyBlock,32,checked((uint)(HashHeadSize+hashData.Length)));
            output.Write(keyBlock); var head = new byte[HeadSize]; HeadMagic.CopyTo(head,0); W32(head,16,checked((uint)entries.Count)); W64(head,20,tableOffset); output.Write(head); output.Flush(true);
            output.Dispose(); File.Move(temp, outputPath, true);
        }
        catch { if (File.Exists(temp)) File.Delete(temp); throw; }
    }

    private static void TransformPackKeyFile(byte[] keyFile, uint archiveKey, uint mode)
    {
        if (mode != 1) throw new InvalidDataException("pack_keyfile must use obfuscation mode 1");
        QlieV31.TransformEntry(keyFile, "pack_keyfile", archiveKey, 1, null);
    }

    private static byte[] GetCommonSource(string path, Archive a)
    {
        var e = a.Entries.FirstOrDefault(x => x.Name.Contains("pack_keyfile", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("mode 2 archive has no pack_keyfile entry");
        var b = ReadAt(path,e.Offset,checked((int)e.Stored)); VerifyStoredHash(b,e); TransformPackKeyFile(b,a.ArchiveKey,e.Obfuscated); return b;
    }

    private static Archive Read(string path)
    {
        using var s=File.OpenRead(path); if(s.Length<HeadSize+KeySize)throw new InvalidDataException("truncated V3.1 archive");
        s.Position=s.Length-HeadSize; var head=ReadExact(s,HeadSize); if(!head.AsSpan(0,16).SequenceEqual(HeadMagic))throw new InvalidDataException("invalid FilePackVer3.1 header");
        var count=R32(head,16); var table=R64(head,20); s.Position=s.Length-HeadSize-KeySize; var key=ReadExact(s,KeySize); var hashSize=R32(key,32);
        if(hashSize<HashHeadSize||hashSize>(ulong)s.Length-HeadSize-KeySize)throw new InvalidDataException("invalid V3.1 hash size");
        var hashAt=s.Length-HeadSize-KeySize-hashSize; s.Position=hashAt; var hh=ReadExact(s,HashHeadSize);
        if(!hh.AsSpan(0,10).SequenceEqual(HashMagic))throw new InvalidDataException("invalid HashVer1.4 header");
        var archiveKey=QlieV31.ArchiveKey(key.AsSpan(36,256)); var sig=key.AsSpan(0,32).ToArray(); QlieV31.Chain(sig,archiveKey,true);
        if(!sig.AsSpan().SequenceEqual(KeySignature))throw new InvalidDataException("invalid V3.1 key signature");
        if(R32(hh,20)!=count||table>(ulong)hashAt)throw new InvalidDataException("V3.1 count or table offset mismatch");
        var encrypted=ReadAt(path,checked((ulong)(hashAt+HashHeadSize)),checked((int)R32(hh,28))); QlieV31.Chain(encrypted,0x0428,true);
        s.Position=checked((long)table); var entries=new List<Entry>(checked((int)count));
        for(uint i=0;i<count;i++)
        {
            if(s.Position>hashAt-2)throw new InvalidDataException("truncated V3.1 entry table"); var n=ReadU16(s); if((ulong)n*2+EntrySize>(ulong)(hashAt-s.Position))throw new InvalidDataException("truncated V3.1 entry");
            var nameBytes=ReadExact(s,n*2); QlieV31.NameCrypt(nameBytes,n,archiveKey); var name=Utf16Le.GetString(nameBytes); var row=ReadExact(s,EntrySize);
            var e=new Entry(name,R64(row,0),R32(row,8),R32(row,12),R32(row,16),R32(row,20),R32(row,24));
            if(e.Offset>table||e.Stored>table-e.Offset||e.Offset+e.Stored>(ulong)s.Length)throw new InvalidDataException($"invalid V3.1 data range for {name}"); entries.Add(e);
        }
        return new Archive(key,archiveKey,entries);
    }

    private static byte[] BuildHashData(IReadOnlyList<string> names)
    {
        var buckets=Enumerable.Range(0,256).Select(_=>new List<(string Name,uint Hash,uint Index)>()).ToArray();
        for(int i=0;i<names.Count;i++){var n=names[i];uint h=0;for(uint c=1;c<=n.Length;c++)h=unchecked((((uint)n[(int)c-1]<<(int)(c&7))+h)&0x3fffffff);var bucket=(ushort)h+(h>>8)+(h>>16);buckets[bucket%256].Add((n,h,(uint)i));}
        using var b=new MemoryStream(); foreach(var bucket in buckets){WriteU32(b,(uint)bucket.Count);foreach(var x in bucket){var nb=Utf16Le.GetBytes(x.Name);WriteU16(b,(ushort)(nb.Length/2));b.Write(nb);WriteU64(b,(ulong)x.Index*4);WriteU32(b,x.Hash);}} foreach(uint i in Enumerable.Range(0,names.Count).Select(x=>(uint)x))WriteU32(b,i);return b.ToArray();
    }

    private static void VerifyStoredHash(byte[] data,Entry e){if(QlieV31.Hash(data)!=e.Hash)throw new InvalidDataException($"stored hash mismatch: {e.Name}");}
    private static string SafePath(string root,string name){var full=Path.GetFullPath(Path.Combine(root,name.Replace('/',Path.DirectorySeparatorChar).Replace('\\',Path.DirectorySeparatorChar)));var basePath=Path.GetFullPath(root)+Path.DirectorySeparatorChar;if(!full.StartsWith(basePath,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException($"entry escapes output directory: {name}");return full;}
    private static byte[] ReadAt(string path,ulong offset,int count){using var s=File.OpenRead(path);s.Position=checked((long)offset);return ReadExact(s,count);}
    private static byte[] ReadExact(Stream s,int n){var b=new byte[n];s.ReadExactly(b);return b;}
    private static ushort ReadU16(Stream s)=>BinaryPrimitives.ReadUInt16LittleEndian(ReadExact(s,2));
    private static void WriteU16(Stream s,ushort x){Span<byte>b=stackalloc byte[2];BinaryPrimitives.WriteUInt16LittleEndian(b,x);s.Write(b);}
    private static void WriteU32(Stream s,uint x){Span<byte>b=stackalloc byte[4];BinaryPrimitives.WriteUInt32LittleEndian(b,x);s.Write(b);}
    private static void WriteU64(Stream s,ulong x){Span<byte>b=stackalloc byte[8];BinaryPrimitives.WriteUInt64LittleEndian(b,x);s.Write(b);}
    private static uint R32(byte[] b,int p)=>BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
    private static ulong R64(byte[] b,int p)=>BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p));
    private static void W32(byte[] b,int p,uint x)=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p),x);
    private static void W64(byte[] b,int p,ulong x)=>BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(p),x);
}
