using System.Buffers.Binary;
using System.IO.Compression;

namespace QlieTool;

/// <summary>QLIE DPNG tiled-image codec. DPNG integers are little-endian.</summary>
public static class QlieDpng
{
    private static readonly byte[] DpngMagic = "DPNG"u8.ToArray();
    private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static bool IsDpng(ReadOnlySpan<byte> input) => input.Length >= 4 && input[..4].SequenceEqual(DpngMagic);

    public static byte[] Decode(byte[] input)
    {
        if (!IsDpng(input)) throw new InvalidDataException("not a DPNG stream");
        if (input.Length < 20) throw new InvalidDataException("truncated DPNG header");
        int tileCount = ReadI32(input, 8);
        int width = ReadI32(input, 12), height = ReadI32(input, 16);
        if (tileCount <= 0 || width <= 0 || height <= 0) throw new InvalidDataException("invalid DPNG dimensions");
        if ((long)width * height > int.MaxValue / 4) throw new InvalidDataException("DPNG image is too large");
        var rgba = new byte[checked(width * height * 4)];
        int p = 20;
        for (int i = 0; i < tileCount; i++)
        {
            if (p < 0 || p > input.Length - 28) throw new InvalidDataException("truncated DPNG tile header");
            int x = ReadI32(input, p), y = ReadI32(input, p + 4);
            int tw = ReadI32(input, p + 8), th = ReadI32(input, p + 12);
            uint size = ReadU32(input, p + 16);
            p += 28; // 16-byte coordinates/dimensions + size + 8 reserved bytes
            if (tw < 0 || th < 0 || x < 0 || y < 0 || x > width - tw || y > height - th || size > input.Length - p)
                throw new InvalidDataException("invalid DPNG tile bounds");
            if (size == 0) continue;
            var tile = DecodePng(input.AsSpan(p, checked((int)size)), out int decodedWidth, out int decodedHeight);
            if (decodedWidth != tw || decodedHeight != th) throw new InvalidDataException("DPNG tile PNG dimensions mismatch");
            for (int row = 0; row < th; row++)
                tile.AsSpan(row * tw * 4, tw * 4).CopyTo(rgba.AsSpan(((y + row) * width + x) * 4, tw * 4));
            p += checked((int)size);
        }
        return EncodePng(rgba, width, height);
    }

    public static byte[] Encode(byte[] png)
    {
        var rgba = DecodePng(png, out int width, out int height);
        using var output = new MemoryStream();
        output.Write(DpngMagic);
        WriteI32(output, 0); // format/version field used by QLIE readers
        WriteI32(output, 1);
        WriteI32(output, width);
        WriteI32(output, height);
        WriteI32(output, 0); WriteI32(output, 0); WriteI32(output, width); WriteI32(output, height);
        byte[] tilePng = EncodePng(rgba, width, height);
        WriteU32(output, checked((uint)tilePng.Length));
        output.Write(new byte[8]);
        output.Write(tilePng);
        return output.ToArray();
    }

    private static byte[] DecodePng(ReadOnlySpan<byte> png, out int width, out int height)
    {
        if (png.Length < 33 || !png[..8].SequenceEqual(PngSignature)) throw new InvalidDataException("invalid PNG signature");
        width = 0; height = 0;
        byte[]? idat = null, palette = null, transparency = null; byte bitDepth = 0, colorType = 0, interlace = 0;
        int p = 8;
        while (p <= png.Length - 12)
        {
            int len = checked((int)ReadU32BE(png, p)); p += 4;
            if (len < 0 || len > png.Length - p - 8) throw new InvalidDataException("truncated PNG chunk");
            uint type = ReadU32BE(png, p); var data = png.Slice(p + 4, len); p += 8 + len;
            if (type == 0x49484452) { if (len != 13) throw new InvalidDataException("invalid IHDR"); width = checked((int)ReadU32BE(data, 0)); height = checked((int)ReadU32BE(data, 4)); bitDepth = data[8]; colorType = data[9]; interlace = data[12]; }
            else if (type == 0x504C5445) palette = data.ToArray();
            else if (type == 0x74524E53) transparency = data.ToArray();
            else if (type == 0x49444154) { idat ??= Array.Empty<byte>(); idat = idat.Concat(data.ToArray()).ToArray(); }
            else if (type == 0x49454E44) break;
        }
        if (width <= 0 || height <= 0 || idat is null || interlace != 0 || bitDepth != 8) throw new InvalidDataException("unsupported PNG layout");
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0 || (colorType == 3 && (palette is null || palette.Length % 3 != 0))) throw new InvalidDataException("unsupported PNG color type");
        int rowBytes = checked(width * channels); var raw = new byte[checked((rowBytes + 1) * height)];
        using (var z = new ZLibStream(new MemoryStream(idat, false), CompressionMode.Decompress)) ReadExactly(z, raw);
        var scan = new byte[checked(rowBytes * height)];
        for (int y = 0; y < height; y++) { byte filter = raw[y * (rowBytes + 1)]; var src = raw.AsSpan(y * (rowBytes + 1) + 1, rowBytes); var dst = scan.AsSpan(y * rowBytes, rowBytes); var prev = y == 0 ? Span<byte>.Empty : scan.AsSpan((y - 1) * rowBytes, rowBytes); SimdAccelerated.Unfilter(filter, src, dst, prev, channels); }
        var result = new byte[checked(width * height * 4)];
        if (!SimdAccelerated.ForceScalar && colorType == 6)
        {
            scan.AsSpan().CopyTo(result);
        }
        else if (!SimdAccelerated.ForceScalar && colorType == 2)
        {
            SimdAccelerated.ConvertRgbToRgba(scan, result, transparency);
        }
        else
        {
            for (int i = 0; i < width * height; i++) { int s = i * channels, d = i * 4; switch (colorType) { case 6: scan.AsSpan(s, 4).CopyTo(result.AsSpan(d, 4)); break; case 2: result[d]=scan[s]; result[d+1]=scan[s+1]; result[d+2]=scan[s+2]; result[d+3]=AlphaForRgb(scan, s, transparency); break; case 3: int ix=scan[s]; if (ix*3+2>=palette!.Length) throw new InvalidDataException("PNG palette index out of range"); result[d]=palette[ix*3]; result[d+1]=palette[ix*3+1]; result[d+2]=palette[ix*3+2]; result[d+3]=(transparency is not null && ix<transparency.Length)?transparency[ix]:(byte)255; break; case 0: result[d]=result[d+1]=result[d+2]=scan[s]; result[d+3]=AlphaForGray(scan[s], transparency); break; case 4: result[d]=result[d+1]=result[d+2]=scan[s]; result[d+3]=scan[s+1]; break; } }
        }
        return result;
    }

    private static byte AlphaForRgb(byte[] data, int s, byte[]? tr) => tr is { Length: >= 6 } && data[s] == tr[1] && data[s+1] == tr[3] && data[s+2] == tr[5] ? (byte)0 : (byte)255;
    private static byte AlphaForGray(byte value, byte[]? tr) => tr is { Length: >= 2 } && value == tr[1] ? (byte)0 : (byte)255;
    private static void Unfilter(byte f, Span<byte> src, Span<byte> dst, Span<byte> prev, int bpp) { for (int i=0;i<src.Length;i++) { int a=i>=bpp?dst[i-bpp]:0, b=i<prev.Length?prev[i]:0, c=i>=bpp&&i<prev.Length?prev[i-bpp]:0; dst[i]=(byte)(src[i]+(f switch {0=>0,1=>a,2=>b,3=>(a+b)/2,4=>Paeth(a,b,c),_=>throw new InvalidDataException("unsupported PNG filter")})); } }
    private static int Paeth(int a,int b,int c) { int p=a+b-c, pa=Math.Abs(p-a), pb=Math.Abs(p-b), pc=Math.Abs(p-c); return pa<=pb&&pa<=pc?a:pb<=pc?b:c; }

    private static byte[] EncodePng(byte[] rgba, int width, int height) { using var o=new MemoryStream(); o.Write(PngSignature); using var raw=new MemoryStream(); for(int y=0;y<height;y++){raw.WriteByte(0);raw.Write(rgba,y*width*4,width*4);} using var z=new MemoryStream(); raw.Position=0; using(var zs=new ZLibStream(z,CompressionLevel.Optimal,true)) raw.CopyTo(zs); WriteChunk(o,0x49484452,Header(width,height)); WriteChunk(o,0x49444154,z.ToArray()); WriteChunk(o,0x49454E44,Array.Empty<byte>()); return o.ToArray(); }
    private static byte[] Header(int w,int h){var b=new byte[13];BinaryPrimitives.WriteUInt32BigEndian(b,(uint)w);BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4),(uint)h);b[8]=8;b[9]=6;b[10]=b[11]=b[12]=0;return b;}
    private static void WriteChunk(Stream s,uint t,byte[] d){WriteU32BE(s,(uint)d.Length);WriteU32BE(s,t);s.Write(d);uint crc=SimdAccelerated.ComputeCrc32(t,d);WriteU32BE(s,crc);}
    private static int ReadI32(ReadOnlySpan<byte>b,int p)=>BinaryPrimitives.ReadInt32LittleEndian(b[p..]); private static uint ReadU32(ReadOnlySpan<byte>b,int p)=>BinaryPrimitives.ReadUInt32LittleEndian(b[p..]); private static uint ReadU32BE(ReadOnlySpan<byte>b,int p)=>BinaryPrimitives.ReadUInt32BigEndian(b[p..]); private static void WriteI32(Stream s,int v)=>WriteU32(s,unchecked((uint)v)); private static void WriteU32(Stream s,uint v){Span<byte>b=stackalloc byte[4];BinaryPrimitives.WriteUInt32LittleEndian(b,v);s.Write(b);} private static void WriteU32BE(Stream s,uint v){Span<byte>b=stackalloc byte[4];BinaryPrimitives.WriteUInt32BigEndian(b,v);s.Write(b);} private static void ReadExactly(Stream s,byte[] b){int p=0,n;while(p<b.Length&&(n=s.Read(b,p,b.Length-p))>0)p+=n;if(p!=b.Length)throw new InvalidDataException("truncated PNG data");}
}
