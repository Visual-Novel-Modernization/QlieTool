using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace QlieTool;

// GARbro's QLIE GameKeyData is the first 256 icon bytes in
// RT_RCDATA/TFORM1, IconKeyImage.Picture.Data (not the external key.fkey).
internal static class QlieGameKey
{
    public static byte[]? Discover(string archivePath, string? externalKeyPath = null)
    {
        var directory = Path.GetDirectoryName(archivePath)!;
        var roots = new[] { directory, Path.GetDirectoryName(directory)!, externalKeyPath == null ? null : Path.GetDirectoryName(Path.GetFullPath(externalKeyPath)), externalKeyPath == null ? null : Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(externalKeyPath))!) }
            .Where(x => x != null).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase);
        byte[]? found = null;
        var chosen = new List<string>();
        foreach (var root in roots)
        foreach (var path in Directory.EnumerateFiles(root, "*.exe").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            PathGuards.EnsureNoReparseComponents(path, false);
            byte[]? key;
            try { key = FromExe(path); }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidDataException or EndOfStreamException) { continue; }
            if (key == null) continue;
            if (found != null && !found.SequenceEqual(key))
                throw new InvalidDataException("multiple distinct EXE GameKeyData candidates; specify --game-key explicitly");
            found = key;
            chosen.Add(path);
        }
        if (chosen.Count > 0) Console.Error.WriteLine($"GameKeyData: {chosen[0]} [RT_RCDATA/TFORM1/IconKeyImage, 256 bytes; identical candidates={chosen.Count}]");
        return found;
    }

    internal static byte[]? FromExe(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var resource = pe.PEHeaders.PEHeader?.ResourceTableDirectory ?? default;
        if (resource.RelativeVirtualAddress == 0 || resource.Size < 16) return null;
        var bytes = pe.GetSectionData(resource.RelativeVirtualAddress).GetContent(0, resource.Size).ToArray();
        uint U32(int o) { if (o < 0 || o > bytes.Length - 4) throw new InvalidDataException("invalid PE resource offset"); return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(o, 4)); }
        ushort U16(int o) { if (o < 0 || o > bytes.Length - 2) throw new InvalidDataException("invalid PE resource offset"); return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(o, 2)); }
        int? Find(int table, string? name, int? id)
        {
            var count = U16(table + 12) + U16(table + 14);
            for (var i = 0; i < count; i++)
            {
                var entry = table + 16 + i * 8;
                var n = U32(entry); var match = name == null && (id == null || n == id);
                if (name != null && (n & 0x80000000) != 0)
                {
                    var at = checked((int)(n & 0x7fffffff)); var len = U16(at);
                    if (at + 2 > bytes.Length - len * 2) throw new InvalidDataException("invalid PE resource name");
                    match = Encoding.Unicode.GetString(bytes, at + 2, len * 2) == name;
                }
                if (match) return checked((int)(U32(entry + 4) & 0x7fffffff));
            }
            return null;
        }
        var type = Find(0, null, 10); if (type == null) return null;
        var form = Find(type.Value, "TFORM1", null); if (form == null) return null;
        var data = Find(form.Value, null, null); if (data == null) return null;
        var rva = checked((int)U32(data.Value)); var size = checked((int)U32(data.Value + 4));
        if (size > 16 * 1024 * 1024) throw new InvalidDataException("oversized Delphi form resource");
        return FromForm(pe.GetSectionData(rva).GetContent(0, size).ToArray());
    }

    internal static byte[]? FromForm(byte[] form)
    {
        var span = form.AsSpan();
        if (!span.StartsWith("TPF0"u8)) return null;
        var objectAt = span.IndexOf("\x0cIconKeyImage"u8); // Delphi short string
        // IconKeyImage has 12 characters.
        if (objectAt < 0) return null;
        var fields = span.Slice(objectAt + 13, Math.Min(256, form.Length - objectAt - 13));
        var propertyAt = fields.IndexOf("\x0cPicture.Data\x0a"u8); // vaBinary
        if (propertyAt < 0) return null;
        var lengthAt = objectAt + 13 + propertyAt + 14;
        if (lengthAt > form.Length - 4) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(lengthAt, 4));
        var start = lengthAt + 4;
        if (length < 262 || start > form.Length - length || !span.Slice(start, 6).SequenceEqual("\x05TIcon"u8)) return null;
        return span.Slice(start + 6, 256).ToArray();
    }
}
