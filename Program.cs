using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Diagnostics;

namespace QlieTool;

public static class Program
{
    public static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h") { Usage(); return 0; }
            if (HasOption(args, "--no-simd")) SimdAccelerated.ForceScalar = true;
            return args[0].ToLowerInvariant() switch
            {
                "list" => RunList(args),
                "extract" => RunExtract(args),
                "clone" => RunRepack(args),
                "repack" => RunRepack(args),
                "verify" => RunVerify(args),
                "self-test" => RunSelfTest(),
                "bench" => RunBench(args),
                _ => throw new ArgumentException($"Unknown command: {args[0]}")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int RunBench(string[] args)
    {
        Require(args, 2);
        var sourcePath = Path.GetFullPath(args[1]);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("archive not found", sourcePath);

        Console.WriteLine("==========================================================================================");
        Console.WriteLine("                QLIE ARCHIVE TOOL - SIMD HARDWARE ACCELERATION BENCHMARK                  ");
        Console.WriteLine("==========================================================================================");
        Console.WriteLine($"Target File    : {sourcePath} ({new FileInfo(sourcePath).Length:N0} bytes)");
        Console.WriteLine($"Hardware Tier  : {SimdAccelerated.GetActiveSimdTier()}");
        Console.WriteLine($"OS Platform    : {Environment.OSVersion}");
        Console.WriteLine($"Logical Cores  : {Environment.ProcessorCount}");
        Console.WriteLine("------------------------------------------------------------------------------------------");

        byte[] payloadData;
        List<string> entryNames = new();
        if (QlieModernArchive.IsMatch(sourcePath))
        {
            var records = QlieModernArchive.List(sourcePath);
            entryNames = records.Select(r => r.Name).ToList();
            using var fs = File.OpenRead(sourcePath);
            int readLen = (int)Math.Min(fs.Length, 20 * 1024 * 1024);
            payloadData = new byte[readLen];
            fs.ReadExactly(payloadData, 0, readLen);
        }
        else
        {
            using var input = TempInput.CopyArchive(sourcePath, FindOption(args, "--key"), FindOption(args, "--game-key"));
            using var archive = QlieArchive.Open(input.ArchivePath, input.KeyPath, input.GameKeyPath);
            entryNames = archive.Entries.Select(e => e.Name).ToList();
            using var fs = File.OpenRead(input.ArchivePath);
            int readLen = (int)Math.Min(fs.Length, 20 * 1024 * 1024);
            payloadData = new byte[readLen];
            fs.ReadExactly(payloadData, 0, readLen);
        }

        Console.WriteLine($"Pack Format    : {(QlieModernArchive.IsMatch(sourcePath) ? "FilePackVer3.1" : "FilePackVer3.0/1.0")}");
        Console.WriteLine($"Indexed Entries: {entryNames.Count} entries");
        Console.WriteLine($"Payload Sample : {payloadData.Length:N0} bytes ({payloadData.Length / (1024.0 * 1024.0):F2} MB)");
        Console.WriteLine();

        // [1] Checksum & ArcKey Word Accumulator
        Console.WriteLine("[1] Stored Checksum & ArcKey Word Accumulation (64B AVX-512 vs 8B Scalar)");
        int hashRounds = 10;
        SimdAccelerated.ForceScalar = false;
        SimdAccelerated.CalculateStoredChecksum(payloadData);
        SimdAccelerated.ForceScalar = true;
        SimdAccelerated.CalculateStoredChecksum(payloadData);

        SimdAccelerated.ForceScalar = false;
        var sw = Stopwatch.StartNew();
        uint simdSum = 0;
        for (int i = 0; i < hashRounds; i++) simdSum = SimdAccelerated.CalculateStoredChecksum(payloadData);
        sw.Stop();
        double simdHashMs = sw.Elapsed.TotalMilliseconds;

        SimdAccelerated.ForceScalar = true;
        sw.Restart();
        uint scalarSum = 0;
        for (int i = 0; i < hashRounds; i++) scalarSum = SimdAccelerated.CalculateStoredChecksum(payloadData);
        sw.Stop();
        double scalarHashMs = sw.Elapsed.TotalMilliseconds;
        SimdAccelerated.ForceScalar = false;

        if (simdSum != scalarSum) throw new Exception("Checksum hash mismatch between SIMD and Scalar!");
        double hashBytesTotal = (double)payloadData.Length * hashRounds;
        double simdHashGbs = (hashBytesTotal / (1024 * 1024 * 1024)) / (simdHashMs / 1000.0);
        double scalarHashGbs = (hashBytesTotal / (1024 * 1024 * 1024)) / (scalarHashMs / 1000.0);
        double hashSpeedup = scalarHashMs / simdHashMs;
        Console.WriteLine($"  - Hardware SIMD (AVX-512) : {simdHashMs,7:F2} ms  ({simdHashGbs,6:F2} GB/s)");
        Console.WriteLine($"  - Pure Scalar (No SIMD)   : {scalarHashMs,7:F2} ms  ({scalarHashGbs,6:F2} GB/s)");
        Console.WriteLine($"  -> SPEEDUP: {hashSpeedup:F2}x faster\n");

        // [2] Name Decrypt/Encrypt
        Console.WriteLine($"[2] Index Entry Name Decrypt/Encrypt ({entryNames.Count} game file names, 50,000 iterations)");
        byte[][] rawNames = entryNames.Select(n => Encoding.Unicode.GetBytes(n)).ToArray();
        long totalNameBytes = rawNames.Sum(n => (long)n.Length) * 50000;

        SimdAccelerated.ForceScalar = false;
        sw.Restart();
        for (int r = 0; r < 50000; r++)
        {
            int seed = r * 17 + 1;
            for (int i = 0; i < rawNames.Length; i++)
            {
                SimdAccelerated.CryptName(rawNames[i], rawNames[i].Length, seed);
            }
        }
        sw.Stop();
        double simdNameMs = sw.Elapsed.TotalMilliseconds;

        SimdAccelerated.ForceScalar = true;
        sw.Restart();
        for (int r = 0; r < 50000; r++)
        {
            int seed = r * 17 + 1;
            for (int i = 0; i < rawNames.Length; i++)
            {
                SimdAccelerated.CryptName(rawNames[i], rawNames[i].Length, seed);
            }
        }
        sw.Stop();
        double scalarNameMs = sw.Elapsed.TotalMilliseconds;
        SimdAccelerated.ForceScalar = false;

        double simdNameMbs = (totalNameBytes / (1024.0 * 1024.0)) / (simdNameMs / 1000.0);
        double scalarNameMbs = (totalNameBytes / (1024.0 * 1024.0)) / (scalarNameMs / 1000.0);
        double nameSpeedup = scalarNameMs / simdNameMs;
        Console.WriteLine($"  - Hardware SIMD (AVX-512) : {simdNameMs,7:F2} ms  ({simdNameMbs,6:F2} MB/s)");
        Console.WriteLine($"  - Pure Scalar (No SIMD)   : {scalarNameMs,7:F2} ms  ({scalarNameMbs,6:F2} MB/s)");
        Console.WriteLine($"  -> SPEEDUP: {nameSpeedup:F2}x faster\n");

        // [3] 1PC / BPE Best-Pair Search
        Console.WriteLine("[3] 1PC / BPE Compression Tree Best-Pair Search (8,192 slots, 20,000 passes)");
        int[] pairCounts = new int[8192];
        int[] pairLeft = new int[8192];
        int[] pairRight = new int[8192];
        var rng = new Random(42);
        for (int i = 0; i < pairCounts.Length; i++)
        {
            pairCounts[i] = rng.Next(0, 1000);
            pairLeft[i] = i / 256;
            pairRight[i] = i % 256;
        }
        pairCounts[4500] = 9999;

        SimdAccelerated.ForceScalar = false;
        sw.Restart();
        int simdBest = 0;
        for (int r = 0; r < 20000; r++)
        {
            pairCounts[4500] = 9000 + (r % 100);
            SimdAccelerated.FindBestPair(pairCounts, pairLeft, pairRight, 0, out simdBest, out _, out _);
        }
        sw.Stop();
        double simdPairMs = sw.Elapsed.TotalMilliseconds;

        SimdAccelerated.ForceScalar = true;
        sw.Restart();
        int scalarBest = 0;
        for (int r = 0; r < 20000; r++)
        {
            pairCounts[4500] = 9000 + (r % 100);
            SimdAccelerated.FindBestPair(pairCounts, pairLeft, pairRight, 0, out scalarBest, out _, out _);
        }
        sw.Stop();
        double scalarPairMs = sw.Elapsed.TotalMilliseconds;
        SimdAccelerated.ForceScalar = false;

        if (simdBest != scalarBest) throw new Exception("Best pair mismatch between SIMD and Scalar!");
        double pairSpeedup = scalarPairMs / simdPairMs;
        Console.WriteLine($"  - Hardware SIMD (AVX-512) : {simdPairMs,7:F2} ms  ({20000.0 / (simdPairMs / 1000.0),8:F0} ops/sec)");
        Console.WriteLine($"  - Pure Scalar (No SIMD)   : {scalarPairMs,7:F2} ms  ({20000.0 / (scalarPairMs / 1000.0),8:F0} ops/sec)");
        Console.WriteLine($"  -> SPEEDUP: {pairSpeedup:F2}x faster\n");

        // [4] DPNG Scanline Unfilter
        Console.WriteLine("[4] DPNG Scanline Unfilter (Filter 2 Up - 1920-byte scanline, 20,000 scanlines)");
        byte[] scanlineCurr = new byte[1920];
        byte[] scanlinePrev = new byte[1920];
        rng.NextBytes(scanlineCurr);
        rng.NextBytes(scanlinePrev);

        SimdAccelerated.ForceScalar = false;
        sw.Restart();
        for (int r = 0; r < 20000; r++)
        {
            SimdAccelerated.Unfilter(2, scanlineCurr, scanlineCurr, scanlinePrev, 4);
        }
        sw.Stop();
        double simdUnfilterMs = sw.Elapsed.TotalMilliseconds;

        SimdAccelerated.ForceScalar = true;
        sw.Restart();
        for (int r = 0; r < 20000; r++)
        {
            SimdAccelerated.Unfilter(2, scanlineCurr, scanlineCurr, scanlinePrev, 4);
        }
        sw.Stop();
        double scalarUnfilterMs = sw.Elapsed.TotalMilliseconds;
        SimdAccelerated.ForceScalar = false;

        double unfilterSpeedup = scalarUnfilterMs / simdUnfilterMs;
        Console.WriteLine($"  - Hardware SIMD (AVX-512) : {simdUnfilterMs,7:F2} ms  ({(20000.0 * 1920) / (1024 * 1024 * (simdUnfilterMs / 1000.0)),6:F2} MB/s)");
        Console.WriteLine($"  - Pure Scalar (No SIMD)   : {scalarUnfilterMs,7:F2} ms  ({(20000.0 * 1920) / (1024 * 1024 * (scalarUnfilterMs / 1000.0)),6:F2} MB/s)");
        Console.WriteLine($"  -> SPEEDUP: {unfilterSpeedup:F2}x faster\n");

        // [5] DPNG RGB24 to RGBA32 Unpack
        Console.WriteLine("[5] DPNG RGB24 to RGBA32 Pixel Unpack (1920 pixels = 5,760B -> 7,680B, 10,000 scanlines)");
        byte[] rgbScan = new byte[1920 * 3];
        byte[] rgbaResult = new byte[1920 * 4];
        rng.NextBytes(rgbScan);

        SimdAccelerated.ForceScalar = false;
        sw.Restart();
        for (int r = 0; r < 10000; r++)
        {
            SimdAccelerated.ConvertRgbToRgba(rgbScan, rgbaResult, null);
        }
        sw.Stop();
        double simdRgbMs = sw.Elapsed.TotalMilliseconds;

        SimdAccelerated.ForceScalar = true;
        sw.Restart();
        for (int r = 0; r < 10000; r++)
        {
            SimdAccelerated.ConvertRgbToRgba(rgbScan, rgbaResult, null);
        }
        sw.Stop();
        double scalarRgbMs = sw.Elapsed.TotalMilliseconds;
        SimdAccelerated.ForceScalar = false;

        double rgbSpeedup = scalarRgbMs / simdRgbMs;
        Console.WriteLine($"  - Hardware SIMD (AVX-512) : {simdRgbMs,7:F2} ms  ({(10000.0 * 1920 * 3) / (1024 * 1024 * (simdRgbMs / 1000.0)),6:F2} MB/s)");
        Console.WriteLine($"  - Pure Scalar (No SIMD)   : {scalarRgbMs,7:F2} ms  ({(10000.0 * 1920 * 3) / (1024 * 1024 * (scalarRgbMs / 1000.0)),6:F2} MB/s)");
        Console.WriteLine($"  -> SPEEDUP: {rgbSpeedup:F2}x faster\n");

        // [6] PNG Chunk CRC-32
        Console.WriteLine("[6] PNG Chunk CRC-32 Calculation (100 MB data stream)");
        byte[] crcBuffer = new byte[10 * 1024 * 1024];
        rng.NextBytes(crcBuffer);
        int crcRounds = 10;

        SimdAccelerated.ForceScalar = false;
        sw.Restart();
        uint hwCrc = 0;
        for (int r = 0; r < crcRounds; r++) hwCrc = SimdAccelerated.ComputeCrc32(0x49444154, crcBuffer);
        sw.Stop();
        double hwCrcMs = sw.Elapsed.TotalMilliseconds;

        SimdAccelerated.ForceScalar = true;
        sw.Restart();
        uint swCrc = 0;
        for (int r = 0; r < crcRounds; r++) swCrc = SimdAccelerated.ComputeCrc32(0x49444154, crcBuffer);
        sw.Stop();
        double swCrcMs = sw.Elapsed.TotalMilliseconds;
        SimdAccelerated.ForceScalar = false;

        if (hwCrc != swCrc) throw new Exception("CRC32 mismatch between Hardware and Software!");
        double crcSpeedup = swCrcMs / hwCrcMs;
        double crcTotalMb = (double)crcBuffer.Length * crcRounds / (1024 * 1024);
        Console.WriteLine($"  - Hardware CRC32 (CLMUL)  : {hwCrcMs,7:F2} ms  ({crcTotalMb / (hwCrcMs / 1000.0),6:F2} MB/s)");
        Console.WriteLine($"  - Software Bitwise CRC32  : {swCrcMs,7:F2} ms  ({crcTotalMb / (swCrcMs / 1000.0),6:F2} MB/s)");
        Console.WriteLine($"  -> SPEEDUP: {crcSpeedup:F2}x faster\n");

        Console.WriteLine("==========================================================================================");
        Console.WriteLine("                                  SUMMARY OF RESULTS                                      ");
        Console.WriteLine("==========================================================================================");
        Console.WriteLine($"{"Kernel Operation",-38} | {"AVX-512 / HW",-14} | {"Pure Scalar",-14} | {"Speedup Ratio",-14}");
        Console.WriteLine("---------------------------------------+----------------+----------------+----------------");
        Console.WriteLine($"{"[1] Payload Word Accumulation (AddW)",-38} | {simdHashMs,11:F2} ms | {scalarHashMs,11:F2} ms | {hashSpeedup,12:F2}x");
        Console.WriteLine($"{"[2] Name Decrypt/Encrypt (CryptName)",-38} | {simdNameMs,11:F2} ms | {scalarNameMs,11:F2} ms | {nameSpeedup,12:F2}x");
        Console.WriteLine($"{"[3] 1PC BPE Best Pair (FindBestPair)",-38} | {simdPairMs,11:F2} ms | {scalarPairMs,11:F2} ms | {pairSpeedup,12:F2}x");
        Console.WriteLine($"{"[4] DPNG Scanline Unfilter (Up)",-38} | {simdUnfilterMs,11:F2} ms | {scalarUnfilterMs,11:F2} ms | {unfilterSpeedup,12:F2}x");
        Console.WriteLine($"{"[5] RGB24 to RGBA32 Unpack (Shuffle)",-38} | {simdRgbMs,11:F2} ms | {scalarRgbMs,11:F2} ms | {rgbSpeedup,12:F2}x");
        Console.WriteLine($"{"[6] Chunk CRC-32 (PCLMUL vs Bitwise)",-38} | {hwCrcMs,11:F2} ms | {swCrcMs,11:F2} ms | {crcSpeedup,12:F2}x");
        Console.WriteLine("==========================================================================================");
        return 0;
    }

    private static void Usage()
    {
        Console.WriteLine("QlieTool - QLIE FilePack archive utility (V1.0, V3.0, V3.1)");
        Console.WriteLine("  list <archive.pack> [--key key.fkey] [--game-key keydata.bin]");
        Console.WriteLine("  extract <archive.pack> <directory> [--key key.fkey] [--game-key keydata.bin] [--dpng] [--manifest manifest.json]");
        Console.WriteLine("  repack <manifest.json> <output.pack> --source <original.pack> --input-dir <extracted-dir> [--key key.fkey] [--game-key keydata.bin] [--dpng]");
        Console.WriteLine("  clone <manifest.json> <output.pack> --source <original.pack> --input-dir <extracted-dir> (compatibility alias)");
        Console.WriteLine("  verify <archive.pack> [--key key.fkey] [--game-key keydata.bin]");
        Console.WriteLine("  self-test");
        Console.WriteLine("  bench <archive.pack> [--key key.fkey] [--game-key keydata.bin]");
        Console.WriteLine("V1.0 repack requires literal entries for edits; unchanged entries stream-copy. V3.1 repack validates keyfile and rebuilds all versioned sections.");
        Console.WriteLine("repack rebuilds payloads, index and footer; unchanged payload bytes may be reused.");
    }

    private static int RunList(string[] args)
    {
        Require(args, 2);
        if (QlieV1Pack.IsV1(args[1]))
        {
            var files = QlieV1Pack.List(args[1]);
            Console.WriteLine($"QLIE FilePackVer1.0; count={files.Count}");
            foreach (var e in files) Console.WriteLine($"{e.Index,6} {e.Offset,12} {e.StoredSize,10} {e.UnpackedSize,10} flags=0x{e.Flags:X16} hash=0x{e.Hash:X8} {e.Name}");
            return 0;
        }
        if (QlieModernArchive.IsMatch(args[1]))
        {
            var files = QlieModernArchive.List(args[1]);
            Console.WriteLine($"QLIE FilePackVer3.1; count={files.Count}");
            foreach (var e in files) Console.WriteLine($"{e.StoredSize,10} {e.Size,10} compressed={e.Compressed,-5} obfuscation={e.Obfuscated,-3} {e.Name}");
            return 0;
        }
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
        var output = PathGuards.EnsureDirectoryTree(args[2]);
        if (QlieV1Pack.IsV1(args[1]))
        {
            var v1ManifestPath = FindOption(args, "--manifest");
            var writtenManifest = QlieV1Pack.Extract(args[1], output, v1ManifestPath);
            Console.WriteLine($"extracted V1.0 {QlieV1Pack.List(args[1]).Count} entries to {output}");
            Console.WriteLine($"manifest {writtenManifest}");
            return 0;
        }
        if (QlieModernArchive.IsMatch(args[1]))
        {
            var modernManifest = QlieModernArchive.Extract(args[1], output);
            var optManifest = FindOption(args, "--manifest");
            if (!string.IsNullOrEmpty(optManifest))
            {
                File.Copy(Path.Combine(output, "qlie-v31-manifest.json"), optManifest, true);
            }
            Console.WriteLine($"extracted {modernManifest.Files.Count} V3.1 entries to {output}");
            return 0;
        }
        using var input = TempInput.CopyArchive(args[1], FindOption(args, "--key"), FindOption(args, "--game-key"));
        using var archive = QlieArchive.Open(input.ArchivePath, input.KeyPath, input.GameKeyPath);
        var rows = new List<ManifestEntry>();
        foreach (var e in archive.Entries)
        {
            var destination = SafeDestination(output, e.Name);
            PathGuards.EnsureDirectoryTree(Path.GetDirectoryName(destination)!);
            PathGuards.EnsureNoReparseComponents(destination);
            if (File.Exists(destination)) throw new IOException($"refusing to overwrite {destination}");
            var decoded = true;
            Stream source;
            try { source = archive.OpenDecoded(e); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                decoded = false;
                destination = SafeDestination(output, e.Name + ".qlie-raw");
                PathGuards.EnsureDirectoryTree(Path.GetDirectoryName(destination)!);
                PathGuards.EnsureNoReparseComponents(destination);
                if (File.Exists(destination)) throw new IOException($"refusing to overwrite {destination}");
                source = archive.OpenStored(e);
            }
            using (source)
            using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan))
                source.CopyTo(target, 1 << 20);
            var row = archive.ManifestEntry(e, destination, decoded);
            if (decoded && Path.GetExtension(e.Name).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                var originalBytes = File.ReadAllBytes(destination);
                row.OriginalImageContainer = QlieDpng.IsDpng(originalBytes) ? "DPNG" : originalBytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "PNG" : "None";
                if (row.OriginalImageContainer == "DPNG")
                    File.WriteAllBytes(destination, QlieDpng.Decode(originalBytes));
            }
            row.ExtractedSha256 = Sha256File(destination);
            row.ExtractedPath = Path.GetRelativePath(output, destination);
            rows.Add(row);
        }
        var manifestPath = FindOption(args, "--manifest") ?? Path.Combine(output, "qlie-manifest.json");
        manifestPath = Path.GetFullPath(manifestPath);
        PathGuards.EnsureDirectoryTree(Path.GetDirectoryName(manifestPath)!);
        PathGuards.EnsureNoReparseComponents(manifestPath);
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
        if (QlieV1Pack.IsV1(args[1]))
        {
            var result = QlieV1Pack.Verify(args[1]);
            Console.WriteLine($"verified {result.CheckedEntries} entries; FilePackVer1.0; index=0x{result.IndexOffset:X}; sha256={result.ArchiveSha256}");
            return 0;
        }
        if (QlieModernArchive.IsMatch(args[1]))
        {
            var verifyRoot = Environment.GetEnvironmentVariable("QLIETOOL_TEMP_ROOT") ?? Path.GetTempPath();
            var verifyPath = Path.Combine(verifyRoot, "qlie-v31-verify-" + Guid.NewGuid().ToString("N"));
            try
            {
                var extracted = QlieModernArchive.Extract(args[1], verifyPath);
                Console.WriteLine($"verified {extracted.Files.Count} entries; FilePackVer3.1");
            }
            finally { if (Directory.Exists(verifyPath)) Directory.Delete(verifyPath, true); }
            return 0;
        }
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
                    if ((ulong)length != e.UnpackedSize)
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
        PathGuards.EnsureNoReparseComponents(manifestPath, false);
        var output = Path.GetFullPath(args[2]);
        PathGuards.EnsureNoReparseComponents(output);
        var source = FindOption(args, "--source") ?? throw new ArgumentException("repack requires --source <original.pack>");
        var inputDir = FindOption(args, "--input-dir") ?? throw new ArgumentException("repack requires --input-dir <extracted-dir>");
        source = Path.GetFullPath(source);
        inputDir = Path.GetFullPath(inputDir);
        if (File.Exists(output)) throw new IOException($"refusing to overwrite {output}");
        if (string.Equals(output, source, StringComparison.OrdinalIgnoreCase)) throw new IOException("output archive must differ from source archive");
        if (QlieV1Pack.IsV1(source))
        {
            QlieV1Pack.Repack(source, inputDir, output, manifestPath);
            Console.WriteLine($"repacked modified V1.0 archive: {output}");
            return 0;
        }
        if (QlieModernArchive.IsMatch(source))
        {
            QlieModernArchive.Repack(manifestPath, source, inputDir, output);
            Console.WriteLine($"repacked modified V3.1 archive: {output}");
            return 0;
        }
        var manifest = JsonSerializer.Deserialize<QlieManifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidDataException("invalid manifest");
        if (manifest.Format != "QLIE" || manifest.Version != "3.0") throw new InvalidDataException("manifest is not a QLIE V3.0 manifest");
        // Copy source and key material into one unique TEMP staging directory before any hash/read.
        using var input = TempInput.CopyArchive(source, FindOption(args, "--key"), FindOption(args, "--game-key"));
        if (!string.Equals(Sha256File(input.ArchivePath), manifest.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("source archive SHA-256 does not match manifest");
        using var archive = QlieArchive.Open(input.ArchivePath, input.KeyPath, input.GameKeyPath);
        QlieRepacker.Repack(archive, manifest, inputDir, output, new QliePayloadCodec(), new QlieV3EntryCipher(archive), null, convertDpng: HasOption(args, "--dpng"));
        Console.WriteLine($"repacked modified archive: {output}");
        Console.WriteLine("HashVer section and 1PC sections rebuilt; this archive was structurally verified but not launched in the game runtime.");
        return 0;
    }

    private static int RunSelfTest()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("QLIETOOL_TEMP_ROOT") ?? Path.GetTempPath(), "QlieTool-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archivePath = Path.Combine(root, "fixture.pack");
            SyntheticFixture.Create(archivePath);
            using (var archive = QlieArchive.Open(archivePath, null))
            {
                if (archive.Entries.Count != 4 || archive.Entries[0].Name != "hello.txt") throw new Exception("synthetic list failed");
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
            File.WriteAllText(Path.Combine(outDir, "hello.txt"), "hello grew beyond original", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(outDir, "tail.bin"), "cut", new UTF8Encoding(false));
            RunRepack(new[] { "repack", manifestPath, roundtrip, "--source", archivePath, "--input-dir", outDir });
            using (var beforeRaw = QlieArchive.Open(archivePath, null))
            using (var rewritten = QlieArchive.Open(roundtrip, null))
            {
                using (var changed = rewritten.OpenDecoded(rewritten.Entries[0]))
                using (var sr = new StreamReader(changed, Encoding.UTF8))
                    if (sr.ReadToEnd() != "hello grew beyond original") throw new Exception("grown edited payload was not repacked");
                using (var shrunk = rewritten.OpenDecoded(rewritten.Entries[3]))
                using (var sr = new StreamReader(shrunk, Encoding.UTF8))
                    if (sr.ReadToEnd() != "cut") throw new Exception("shrunken edited payload was not repacked");
                if (rewritten.IndexOffset == beforeRaw.IndexOffset) throw new Exception("variable-size edits did not relocate index");
                if (rewritten.Entries[1].Offset == beforeRaw.Entries[1].Offset || rewritten.Entries[3].Offset == beforeRaw.Entries[3].Offset)
                    throw new Exception("variable-size edits did not relocate later payload offsets");
                if (rewritten.Entries[0].StoredSize != (uint)Encoding.UTF8.GetByteCount("hello grew beyond original") || rewritten.Entries[3].StoredSize != 3)
                    throw new Exception("variable-size entry sizes were not serialized");
                using var oldStored = beforeRaw.OpenStored(beforeRaw.Entries[1]);
                using var newStored = rewritten.OpenStored(rewritten.Entries[1]);
                using var oldBytes = new MemoryStream(); using var newBytes = new MemoryStream();
                oldStored.CopyTo(oldBytes); newStored.CopyTo(newBytes);
                if (!oldBytes.ToArray().SequenceEqual(newBytes.ToArray())) throw new Exception("unchanged stored payload was not reused");
                if (!beforeRaw.CopyIndexTrailerGap().SequenceEqual(rewritten.CopyIndexTrailerGap())) throw new Exception("opaque index/trailer gap was not preserved");
                if (!beforeRaw.CopyTrailerBlock().SequenceEqual(rewritten.CopyTrailerBlock())) throw new Exception("V3 trailer block was not preserved");
                var oldFooter = beforeRaw.CopyFooter(); var newFooter = rewritten.CopyFooter();
                if (!oldFooter.AsSpan(0, 0x10).SequenceEqual(newFooter.AsSpan(0, 0x10)) ||
                    BitConverter.ToInt32(newFooter, 0x10) != rewritten.Entries.Count ||
                    BitConverter.ToInt64(newFooter, 0x14) != rewritten.IndexOffset)
                    throw new Exception("rebuilt footer metadata is incorrect");
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
            var keyChainPath = Path.Combine(root, "key-chain.pack");
            var keyChainKeyPath = Path.Combine(root, "key-chain.key");
            var keyChainGameKeyPath = Path.Combine(root, "key-chain.gamekey");
            SyntheticFixture.CreateKeyChain(keyChainPath, keyChainKeyPath, keyChainGameKeyPath);
            using (var keyChain = QlieArchive.Open(keyChainPath, keyChainKeyPath, keyChainGameKeyPath))
            {
                if (keyChain.Entries.Count != 3 || keyChain.Entries[0].Name != "pack_keyfile.bin" ||
                    keyChain.Entries[1].Name != SyntheticFixture.KeyChainPayloadName || keyChain.Entries[2].Name != SyntheticFixture.KeyChainSecondPayloadName)
                    throw new Exception("synthetic pack_keyfile fixture index failed");
                if (!keyChain.Entries[0].KeyFile!.SequenceEqual(SyntheticFixture.ExternalKey))
                    throw new Exception("synthetic pack_keyfile first entry did not retain external key");
                if (!keyChain.Entries[1].KeyFile!.SequenceEqual(SyntheticFixture.EmbeddedKey) || ReferenceEquals(keyChain.Entries[0].KeyFile, keyChain.Entries[1].KeyFile) ||
                    !keyChain.Entries[2].KeyFile!.SequenceEqual(SyntheticFixture.EmbeddedKey))
                    throw new Exception("synthetic pack_keyfile active key was not replaced by embedded key");
                if (keyChain.Entries.Any(e => e.GameKeyData == null || !e.GameKeyData.SequenceEqual(SyntheticFixture.KeyChainGameKey)))
                    throw new Exception("synthetic pack_keyfile entries did not retain the same non-empty game-key");
                if (!keyChain.Entries[1].CipherNameBytes.SequenceEqual(SyntheticFixture.KeyChainPayloadRawName) ||
                    !keyChain.Entries[2].CipherNameBytes.SequenceEqual(SyntheticFixture.KeyChainSecondPayloadRawName))
                    throw new Exception("synthetic pack_keyfile payload lost encrypted index name bytes");
                var expectedPayloads = new[] { SyntheticFixture.KeyChainPayload, SyntheticFixture.KeyChainSecondPayload };
                for (var i = 1; i < keyChain.Entries.Count; i++)
                {
                    using var decoded = keyChain.OpenDecoded(keyChain.Entries[i]);
                    using var decodedBytes = new MemoryStream(); decoded.CopyTo(decodedBytes);
                    if (!decodedBytes.ToArray().SequenceEqual(expectedPayloads[i - 1]))
                        throw new Exception($"synthetic pack_keyfile active-key/game-key payload {i} decode failed");
                }
            }
            SyntheticFixture.VerifyCipherKnownVectors();
            SimdAccelerated.RunSelfVerification();
            Console.WriteLine($"self-test passed [Active SIMD Tier: {SimdAccelerated.GetActiveSimdTier()}] (V3 index/name cipher, 1PC decode, variable-size grow/shrink repack and readback, streamed unchanged reuse, relocated index/footer, opaque gap/trailer preservation, encrypted decode/repack/readback, synthetic pack_keyfile chain with non-empty game-key, clear-name/null/empty/non-empty fixed vectors, raw-edit rejection, SIMD parity verified)");
            return 0;
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static bool HasOption(string[] args, string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);
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

internal static class PathGuards
{
    public static string EnsureDirectoryTree(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new InvalidDataException($"path has no root: {path}");
        var current = root;
        foreach (var part in full[root.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (TryGetAttributes(current, out var attrs))
            {
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"reparse point is not allowed in path: {current}");
                if (!Directory.Exists(current))
                    throw new IOException($"path component is not a directory: {current}");
            }
            else
            {
                Directory.CreateDirectory(current);
                if (TryGetAttributes(current, out attrs) && (attrs & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"reparse point is not allowed in path: {current}");
            }
        }
        return full;
    }

    public static void EnsureNoReparseComponents(string path, bool allowMissingLeaf = true)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new InvalidDataException($"path has no root: {path}");
        var current = root;
        var parts = full[root.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            if (!TryGetAttributes(current, out var attrs))
            {
                if (i == parts.Length - 1 && allowMissingLeaf) return;
                return;
            }
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"reparse point is not allowed in path: {current}");
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attrs)
    {
        try { attrs = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { attrs = default; return false; }
        catch (DirectoryNotFoundException) { attrs = default; return false; }
        catch (IOException) { attrs = default; return false; }
    }
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
        archive = Path.GetFullPath(archive);
        if (!File.Exists(archive)) throw new FileNotFoundException("archive", archive);
        PathGuards.EnsureNoReparseComponents(archive, false);
        var d = Path.Combine(Environment.GetEnvironmentVariable("QLIETOOL_TEMP_ROOT") ?? Path.GetTempPath(), "QlieTool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        try
        {
            var a = Path.Combine(d, Path.GetFileName(archive));
            File.Copy(archive, a);
            if (key == null)
            {
                var candidates = new[]
                {
                    Path.Combine(Path.GetDirectoryName(archive)!, "key.fkey"),
                    Path.Combine(Path.GetDirectoryName(archive)!, "DLL", "key.fkey"),
                    Path.Combine(Path.GetDirectoryName(archive)!, "..", "DLL", "key.fkey")
                };
                key = candidates.FirstOrDefault(File.Exists);
            }
            if (key != null)
            {
                key = Path.GetFullPath(key);
                if (!File.Exists(key)) throw new FileNotFoundException("key", key);
                PathGuards.EnsureNoReparseComponents(key, false);
                var k = Path.Combine(d, "key.fkey");
                File.Copy(key, k);
                var discovered = gameKey == null && IsEncryptedArchive(archive) && HasEncryptedKeyEntry(archive) ? QlieGameKey.Discover(archive, key) : null;
                string? g = null;
                if (discovered != null)
                {
                    g = Path.Combine(d, "game.keydata");
                    File.WriteAllBytes(g, discovered);
                }
                if (gameKey != null)
                {
                    gameKey = Path.GetFullPath(gameKey);
                    if (!File.Exists(gameKey)) throw new FileNotFoundException("game key", gameKey);
                    PathGuards.EnsureNoReparseComponents(gameKey, false);
                    g = Path.Combine(d, "game.keydata");
                    File.Copy(gameKey, g);
                }
                return new TempInput(a, k, g, d);
            }
            string? gameCopy = null;
            if (gameKey != null)
            {
                gameKey = Path.GetFullPath(gameKey);
                if (!File.Exists(gameKey)) throw new FileNotFoundException("game key", gameKey);
                PathGuards.EnsureNoReparseComponents(gameKey, false);
                gameCopy = Path.Combine(d, "game.keydata");
                File.Copy(gameKey, gameCopy);
            }
            return new TempInput(a, null, gameCopy, d);
        }
        catch
        {
            try { Directory.Delete(d, true); } catch { }
            throw;
        }
    }
    private static bool HasEncryptedKeyEntry(string path)
    {
        try
        {
            using var archive = QlieArchive.Open(path, null, null);
            return archive.Entries.Any(e => e.Encryption != 0 || e.Name.Contains("pack_keyfile", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException) { return false; }
    }
    private static bool IsEncryptedArchive(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 28) return false;
        stream.Position = stream.Length - 28;
        Span<byte> magic = stackalloc byte[16];
        stream.ReadExactly(magic);
        var version = Encoding.ASCII.GetString(magic);
        return version.StartsWith("FilePackVer3.0", StringComparison.Ordinal) || version.StartsWith("FilePackVer3.1", StringComparison.Ordinal);
    }
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }
}

public sealed class QlieEntry
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    // CipherNameBytes is the exact byte sequence stored in the index; ClearNameBytes is decrypted CP932.
    public byte[] CipherNameBytes { get; init; } = Array.Empty<byte>();
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
    public string OriginalImageContainer { get; set; } = "";
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
    public long FileLength => file.Length;
    public long IndexEnd { get; private set; }
    public List<QlieEntry> Entries { get; } = new();
    private readonly uint arcKey;
    private readonly byte[] trailerBlock;
    private readonly long trailerStart;
    public long TrailerStart => trailerStart;
    public byte[] CopyTrailerBlock() => trailerBlock.ToArray();
    public byte[] CopyFooter() => CopyRange(file.Length - 0x1c, 0x1c);
    public byte[] CopyIndexTrailerGap() => CopyRange(IndexEnd, checked((int)(trailerStart - IndexEnd)));
    public void CopyRangeTo(Stream destination, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset > file.Length - length) throw new ArgumentOutOfRangeException();
        file.Position = offset;
        var buffer = new byte[1 << 20];
        while (length > 0)
        {
            var want = (int)Math.Min(buffer.Length, length);
            var read = file.Read(buffer, 0, want);
            if (read == 0) throw new EndOfStreamException();
            destination.Write(buffer, 0, read);
            length -= read;
        }
    }
    private byte[] CopyRange(long offset, int length)
    {
        using var ms = new MemoryStream(length);
        CopyRangeTo(ms, offset, length);
        return ms.ToArray();
    }
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
            byte[]? key = null;
            if (keyPath != null)
            {
                if (!File.Exists(keyPath)) throw new FileNotFoundException("key", keyPath);
                key = File.ReadAllBytes(keyPath);
            }
            byte[]? gameKey = null;
            if (gameKeyPath != null)
            {
                if (!File.Exists(gameKeyPath)) throw new FileNotFoundException("game key", gameKeyPath);
                gameKey = File.ReadAllBytes(gameKeyPath);
            }
            var a = new QlieArchive(f, key, gameKey, major, minor, index, arc, trailer, trailerAt);
            a.ReadEntries(count, footer); return a;
        }
        catch { f.Dispose(); throw; }
    }

    private void ReadEntries(int count, long footer)
    {
        file.Position = IndexOffset;
        byte[]? activeKey = keyFile;
        var readPackKeyfile = keyFile != null;
        for (var i = 0; i < count; i++)
        {
            var n = ReadUInt16(file); if (n > 0x100) throw new InvalidDataException($"entry {i}: invalid name length {n}");
            var raw = ReadExactly(file, n); var nameBytes = raw.ToArray(); DecryptName(nameBytes, n);
            var name = cp932.GetString(nameBytes);
            var offset = ReadInt64(file); var stored = ReadUInt32(file); var unpacked = ReadUInt32(file); var packed = ReadInt32(file) != 0; var enc = ReadInt32(file); var hash = ReadUInt32(file);
            if (offset < 0 || offset >= IndexOffset || stored > (ulong)(IndexOffset - offset) || offset > file.Length - stored)
                throw new InvalidDataException($"entry {i}: invalid placement");
            var entry = new QlieEntry { Index=i, Name=name, CipherNameBytes=raw.ToArray(), ClearNameBytes=nameBytes, Offset=offset, StoredSize=stored, UnpackedSize=unpacked, Packed=packed, Encryption=enc, Hash=hash, KeyFile=activeKey, GameKeyData=gameKeyData };
            Entries.Add(entry);
            if (file.Position > trailerStart) throw new InvalidDataException("QLIE index overlaps the 0x400-byte V3 trailer block");
            var nextIndex = file.Position;
            if (readPackKeyfile && activeKey != null && name.IndexOf("pack_keyfile", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                readPackKeyfile = false;
                var payload = ReadRawPayload(entry);
                if (entry.Encryption != 0) DecryptEntry(payload, entry, activeKey, gameKeyData);
                if (entry.Packed) payload = Decompress1Pc(payload, entry.UnpackedSize) ?? throw new InvalidDataException($"entry {i}: invalid embedded key payload");
                activeKey = payload;
            }
            file.Position = nextIndex;
        }
        IndexEnd = file.Position;
        if (IndexEnd > trailerStart) throw new InvalidDataException("QLIE index overlaps the 0x400-byte V3 trailer block");
    }

    public byte[] EncodeName(string name)
    {
        var bytes = cp932.GetBytes(name);
        if (bytes.Length > 0x100) throw new InvalidDataException($"entry name exceeds 256 CP932 bytes: {name}");
        var key = unchecked(bytes.Length + (unchecked((int)arcKey) ^ 0x3E));
        SimdAccelerated.CryptName(bytes, bytes.Length, key);
        return bytes;
    }

    private void DecryptName(byte[] bytes, int length)
    {
        var key = unchecked(length + (unchecked((int)arcKey) ^ 0x3E));
        SimdAccelerated.CryptName(bytes, length, key);
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
            var decoded = Decompress1Pc(data, e.UnpackedSize); if (decoded == null) throw new InvalidDataException($"entry {e.Index}: unsupported or invalid 1PC payload"); data = decoded;
        }
        if ((ulong)data.LongLength != e.UnpackedSize)
            throw new InvalidDataException($"entry {e.Index} unpacked size mismatch: expected {e.UnpackedSize}, got {data.LongLength}");
        return new MemoryStream(data, writable:false);
    }

    public ManifestEntry ManifestEntry(QlieEntry e, string extractedPath, bool decoded=true)
    {
        string dh; using (var stream = decoded ? OpenDecoded(e) : OpenStored(e)) dh = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        file.Position=e.Offset; using var limited = new LimitedStream(file,e.StoredSize); var ph=Convert.ToHexString(SHA256.HashData(limited)).ToLowerInvariant();
        return new ManifestEntry { Index=e.Index, Name=e.Name, RawNameBase64=Convert.ToBase64String(e.CipherNameBytes), Offset=e.Offset, StoredSize=e.StoredSize, UnpackedSize=e.UnpackedSize, Packed=e.Packed, Encryption=e.Encryption, Hash=e.Hash, PayloadSha256=ph, ExtractedSha256=dh, Decoded=decoded };
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
    internal static uint CalculateArcKey(ReadOnlySpan<byte> data) => SimdAccelerated.CalculateArcKey(data);
    internal static uint CalculateStoredChecksum(ReadOnlySpan<byte> data) => SimdAccelerated.CalculateStoredChecksum(data);
    public static byte[]? Decompress1Pc(byte[] input, uint expectedSize)
    {
        const int maxOutput = 256 * 1024 * 1024;
        if (input.Length < 12 || BitConverter.ToUInt32(input, 0) != 0xff435031)
            throw new InvalidDataException("invalid 1PC header");
        if (expectedSize > maxOutput)
            throw new InvalidDataException($"1PC output exceeds supported limit: {expectedSize}");
        var is16 = (input[4] & 1) != 0;
        var outputLength = BitConverter.ToInt32(input, 8);
        if (outputLength < 0 || (uint)outputLength != expectedSize || outputLength > maxOutput)
            throw new InvalidDataException($"invalid 1PC output length: header={outputLength}, expected={expectedSize}");
        var output = new byte[outputLength];
        var node0 = new byte[256]; var node1 = new byte[256]; var child = new byte[256];
        var src = 12; var dst = 0;
        void Need(int length)
        {
            if (length < 0 || src > input.Length - length) throw new InvalidDataException("truncated 1PC input");
        }
        try
        {
            while (src < input.Length)
            {
                for (var i = 0; i < 256; i++) node0[i] = (byte)i;
                var i2 = 0;
                while (i2 < 256)
                {
                    Need(1);
                    var count = input[src++];
                    if (count > 127) { i2 += count - 127; count = 0; }
                    if (i2 > 255) break;
                    count++;
                    for (var k = 0; k < count; k++)
                    {
                        Need(1); node0[i2] = input[src++];
                        if (node0[i2] != i2) { Need(1); node1[i2] = input[src++]; }
                        i2++;
                    }
                }
                Need(is16 ? 2 : 4);
                var count2 = is16 ? BitConverter.ToUInt16(input, src) : BitConverter.ToInt32(input, src);
                src += is16 ? 2 : 4;
                if (count2 < 0) throw new InvalidDataException("negative 1PC symbol count");
                var stack = 0;
                while (true)
                {
                    int idx;
                    if (stack > 0) idx = child[--stack];
                    else
                    {
                        if (count2 == 0) break;
                        count2--; Need(1); idx = input[src++];
                    }
                    if (node0[idx] == idx)
                    {
                        if (dst >= output.Length) throw new InvalidDataException("1PC output exceeds declared length");
                        output[dst++] = (byte)idx;
                    }
                    else
                    {
                        if (stack + 2 > child.Length) throw new InvalidDataException("1PC Huffman stack overflow");
                        child[stack++] = node1[idx]; child[stack++] = node0[idx];
                    }
                }
            }
        }
        catch (IndexOutOfRangeException ex) { throw new InvalidDataException("invalid 1PC table or symbol", ex); }
        catch (ArgumentOutOfRangeException ex) { throw new InvalidDataException("truncated 1PC input", ex); }
        if (dst != output.Length) throw new InvalidDataException($"1PC output size mismatch: expected {output.Length}, got {dst}");
        return output;
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
    uint ComputeHash(byte[] storedPayload, QlieEntry sourceEntry);
}

/// <summary>V3 TOC hash: native arc_pack checksum over stored bytes, complete little-endian qwords only.</summary>
public sealed class QlieStoredPayloadHashProvider : IEntryHashProvider
{
    public uint ComputeHash(byte[] storedPayload, QlieEntry sourceEntry) => QlieArchive.CalculateStoredChecksum(storedPayload);
}

/// <summary>Retained for synthetic/compatibility paths; unsafe for a modified source entry.</summary>
public sealed class PreserveSourceEntryHashProvider : IEntryHashProvider
{
    public uint ComputeHash(byte[] storedPayload, QlieEntry sourceEntry) => sourceEntry.Hash;
}

public sealed class PlainEntryPayloadCodec : IEntryPayloadCodec
{
    public byte[] Encode(byte[] decoded, QlieEntry sourceEntry) => decoded.ToArray();
}

public sealed class QlieRepackItem
{
    public required string Name { get; init; }
    public required byte[]? Stored { get; init; }
    public required uint StoredSize { get; init; }
    public required bool ReuseSourceStored { get; init; }
    public required uint UnpackedSize { get; init; }
    public required bool Packed { get; init; }
    public required int Encryption { get; init; }
    public required uint Hash { get; init; }
    public required byte[] CipherNameBytes { get; init; } // exact ciphertext bytes for index writeback
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
        // GARbro falls back to the V2 transform only for an explicitly supplied empty
        // GameKeyData value. A null GameKeyData still uses the V3 MT/key.fkey path.
        if (gameKey is { Length: 0 })
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
        // GARbro's PackIndexReader calls DecryptName on its shared name buffer before
        // copying that buffer to QlieEntry.RawName. DecryptName mutates it in place,
        // so V3's seed is based on the decoded CP932 name bytes, not the ciphertext
        // preserved separately here for index round-tripping.
        var seedName = entry.ClearNameBytes.Length != 0 ? entry.ClearNameBytes : Encoding.GetEncoding(932).GetBytes(entry.Name);
        for (uint i = 0; i < seedName.Length; i++) { hash += (i & 0xff) * seedName[i]; seed ^= hash; }
        seed += arcKey ^ (7 * (length & 0xffffff) + length + hash + (hash ^ length ^ 0x8f32dcu));
        seed = 9 * (seed & 0xffffff);
        if (gameKey != null) seed ^= 0x453Au;
        var mt = new QlieMersenneTwister(seed);
        mt.XorState(keyFile);
        if (gameKey != null) mt.XorState(gameKey);
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
    private const int TrailerSize = 0x400;
    private const int FooterSize = 0x1c;

    public static void Repack(QlieArchive archive, QlieManifest manifest, string inputDir, string outputPath,
        IEntryPayloadCodec codec, IV3EntryCipher? cipher, byte[]? trailerOverride, IEntryHashProvider? hashProvider = null, bool convertDpng = false)
    {
        if (!Directory.Exists(inputDir)) throw new DirectoryNotFoundException($"input directory not found: {inputDir}");
        PathGuards.EnsureNoReparseComponents(inputDir, false);
        if (manifest.Count != archive.Entries.Count || manifest.Entries.Count != archive.Entries.Count)
            throw new InvalidDataException("manifest entry count does not match source archive");
        if (manifest.IndexOffset != archive.IndexOffset || manifest.ArchiveLength != archive.FileLength)
            throw new InvalidDataException("manifest archive metadata does not match source archive");
        var byIndex = manifest.Entries.ToDictionary(x => x.Index);
        hashProvider ??= new QlieStoredPayloadHashProvider();
        var items = new List<QlieRepackItem>(archive.Entries.Count);
        foreach (var source in archive.Entries)
        {
            if (!byIndex.TryGetValue(source.Index, out var row) || !string.Equals(row.Name, source.Name, StringComparison.Ordinal))
                throw new InvalidDataException($"manifest entry {source.Index} does not match source name");
            if (row.Offset != source.Offset || row.StoredSize != source.StoredSize || row.UnpackedSize != source.UnpackedSize ||
                row.Packed != source.Packed || row.Encryption != source.Encryption || row.Hash != source.Hash ||
                !string.Equals(row.RawNameBase64, Convert.ToBase64String(source.CipherNameBytes), StringComparison.Ordinal))
                throw new InvalidDataException($"manifest entry {source.Index} metadata does not match source archive");
            var rel = string.IsNullOrWhiteSpace(row.ExtractedPath) ? source.Name + (row.Decoded ? "" : ".qlie-raw") : row.ExtractedPath;
            var path = SafeInputPath(inputDir, rel);
            if (!File.Exists(path)) throw new FileNotFoundException($"missing extracted entry {rel}", path);
            var fileHash = Program.Sha256File(path);
            var expected = row.Decoded ? row.ExtractedSha256 : row.PayloadSha256;
            if (string.Equals(fileHash, expected, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new QlieRepackItem { Name=source.Name, Stored=null, StoredSize=source.StoredSize, ReuseSourceStored=true, UnpackedSize=source.UnpackedSize,
                    Packed=source.Packed, Encryption=source.Encryption, Hash=source.Hash, CipherNameBytes=source.CipherNameBytes.ToArray(), Source=source });
                continue;
            }
            if (!row.Decoded) throw new UnsupportedEncryptedRewriteException($"entry {source.Index} is undecoded raw data; edited .qlie-raw entries are rejected (encryption={source.Encryption}, offset=0x{source.Offset:X})");
            if (source.Name.IndexOf("pack_keyfile", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new UnsupportedEncryptedRewriteException($"entry {source.Index} is the active pack_keyfile; editing it would invalidate the active key chain for entries {source.Index + 1}..{archive.Entries.Count - 1} (offset=0x{source.Offset:X})");
            if (source.Encryption != 0 && cipher == null)
                throw new UnsupportedEncryptedRewriteException($"entry {source.Index} edited encrypted payload at offset 0x{source.Offset:X}; provide IV3EntryCipher to prove inverse V3 encryption/signature generation");
            var decoded = File.ReadAllBytes(path);
            if (convertDpng && row.OriginalImageContainer == "DPNG" && Path.GetExtension(source.Name).Equals(".png", StringComparison.OrdinalIgnoreCase) && decoded.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                decoded = QlieDpng.Encode(decoded);
            var encoded = codec.Encode(decoded, source);
            var packed = source.Packed;
            var encryption = 0;
            if (source.Encryption != 0)
            {
                encoded = cipher!.Encrypt(encoded, source, source.Name);
                encryption = source.Encryption;
            }
            items.Add(new QlieRepackItem { Name=source.Name, Stored=encoded, StoredSize=checked((uint)encoded.Length), ReuseSourceStored=false,
                UnpackedSize=checked((uint)decoded.Length), Packed=packed, Encryption=encryption,
                Hash=hashProvider.ComputeHash(encoded, source), CipherNameBytes=source.CipherNameBytes.ToArray(), Source=source });
        }
        WriteArchive(archive, items, outputPath, trailerOverride);
    }

    private static void WriteArchive(QlieArchive archive, IReadOnlyList<QlieRepackItem> items, string outputPath, byte[]? trailerOverride)
    {
        outputPath = Path.GetFullPath(outputPath);
        PathGuards.EnsureNoReparseComponents(outputPath);
        var directory = PathGuards.EnsureDirectoryTree(Path.GetDirectoryName(outputPath)!);
        if (File.Exists(outputPath)) throw new IOException($"refusing to overwrite {outputPath}");
        var tempPath = Path.Combine(directory, "." + Path.GetFileName(outputPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            WriteArchiveCore(archive, items, tempPath, trailerOverride);
            PathGuards.EnsureNoReparseComponents(outputPath);
            File.Move(tempPath, outputPath);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static void WriteArchiveCore(QlieArchive archive, IReadOnlyList<QlieRepackItem> items, string outputPath, byte[]? trailerOverride)
    {
        if (items.Count != archive.Entries.Count) throw new InvalidDataException("repack item count does not match source archive");
        var offsets = new long[items.Count];
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);

        // Rebuild payloads contiguously in source TOC order. The V3 reader provides no
        // per-entry alignment rule; each new index offset is derived from bytes written.
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Source.Index != archive.Entries[i].Index)
                throw new InvalidDataException($"repack item order differs from source TOC at entry {i}");
            offsets[i] = output.Position;
            if (items[i].ReuseSourceStored)
            {
                if (items[i].Stored != null || items[i].StoredSize != items[i].Source.StoredSize)
                    throw new InvalidDataException($"entry {i} source-streamed payload metadata is inconsistent");
                archive.CopyRangeTo(output, items[i].Source.Offset, items[i].StoredSize);
            }
            else
            {
                var stored = items[i].Stored ?? throw new InvalidDataException($"entry {i} rebuilt payload is missing");
                if (items[i].StoredSize != (uint)stored.Length)
                    throw new InvalidDataException($"entry {i} rebuilt payload size metadata is inconsistent");
                output.Write(stored, 0, stored.Length);
            }
        }

        var indexOffset = output.Position;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var name = item.CipherNameBytes.Length == 0 ? archive.EncodeName(item.Name) : item.CipherNameBytes;
            if (name.Length > ushort.MaxValue) throw new InvalidDataException($"entry name too long: {item.Name}");
            output.Write(BitConverter.GetBytes((ushort)name.Length));
            output.Write(name, 0, name.Length);
            output.Write(BitConverter.GetBytes(offsets[i]));
            output.Write(BitConverter.GetBytes(item.StoredSize));
            output.Write(BitConverter.GetBytes(item.UnpackedSize));
            output.Write(BitConverter.GetBytes(item.Packed ? 1 : 0));
            output.Write(BitConverter.GetBytes(item.Encryption));
            output.Write(BitConverter.GetBytes(item.Hash));
        }

        // Regenerate the v1.3 name index and its terminal marker after payload/index relocation.
        var trailer = trailerOverride ?? archive.CopyTrailerBlock();
        var hashVerGap = HashVer13.RebuildGap(archive, trailer);
        output.Write(hashVerGap, 0, hashVerGap.Length);
        if (trailer.Length != TrailerSize) throw new InvalidDataException("V3 trailer block must be exactly 0x400 bytes");
        output.Write(trailer, 0, trailer.Length);
        var footer = archive.CopyFooter();
        if (footer.Length != FooterSize) throw new InvalidDataException("V3 footer must be exactly 0x1c bytes");
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
        PathGuards.EnsureNoReparseComponents(fullRoot, false);
        var full = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"unsafe manifest extracted path: {relative}");
        PathGuards.EnsureNoReparseComponents(full, false);
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
        var entries=new[]{(Name:"hello.txt",Data:Encoding.UTF8.GetBytes("hello QLIE"),Packed:false),(Name:"packed.bin",Data:MakePacked(),Packed:true),(Name:"raw.bin",Data:Encoding.ASCII.GetBytes("unsupported raw payload"),Packed:true),(Name:"tail.bin",Data:Encoding.ASCII.GetBytes("tail payload stays untouched"),Packed:false)};
        using var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);var indexOffset=0L;var offsets=new List<long>();
        foreach(var e in entries){offsets.Add(f.Position);f.Write(e.Data);}
        indexOffset=f.Position;foreach(var x in entries){var raw=EncodeName(x.Name,0);f.Write(BitConverter.GetBytes((ushort)raw.Length));f.Write(raw);f.Write(BitConverter.GetBytes(offsets[Array.IndexOf(entries,x)]));f.Write(BitConverter.GetBytes((uint)x.Data.Length));f.Write(BitConverter.GetBytes((uint)(x.Packed?PackedPlain.Length:x.Data.Length)));f.Write(BitConverter.GetBytes(x.Packed?1:0));f.Write(BitConverter.GetBytes(0));f.Write(BitConverter.GetBytes(0u));}
        f.Write(Encoding.ASCII.GetBytes("HashVer1.3\0synthetic-opaque-gap"));
        var pad=0x400; f.Write(new byte[pad]);var footer=f.Position;f.Write(Encoding.ASCII.GetBytes("FilePackVer3.0"));f.Write(new byte[2]);f.Write(BitConverter.GetBytes(entries.Length));f.Write(BitConverter.GetBytes(indexOffset));
    }
    private static byte[] MakePacked(){var body=PackedPlain;using var m=new MemoryStream();m.Write(BitConverter.GetBytes(0xff435031u));m.WriteByte(0);m.WriteByte(0);m.Write(BitConverter.GetBytes(body.Length));m.WriteByte(0);m.WriteByte(255); // skip 128 nodes? construct simple leaf table: 0..255 identity encoded as 255, then one entry
        // Correct table: count byte 255 => skip 128, then count 0 => one mapping for symbol 128; remaining table incomplete. Use one block with 256 identity mappings encoded as 127?
        m.SetLength(0); m.Write(BitConverter.GetBytes(0xff435031u));m.WriteByte(0);m.WriteByte(0);m.WriteByte(0);m.WriteByte(0);m.Write(BitConverter.GetBytes(body.Length));m.WriteByte(127); for(var i=0;i<128;i++)m.WriteByte((byte)i); m.WriteByte(127); for(var i=128;i<256;i++)m.WriteByte((byte)i); m.Write(BitConverter.GetBytes(body.Length)); m.Write(body); return m.ToArray();}
    private static byte[] EncodeName(string name,uint arc){var b=Encoding.GetEncoding(932).GetBytes(name);var key=unchecked(b.Length+(unchecked((int)arc)^0x3E));SimdAccelerated.CryptName(b,b.Length,key);return b;}
    public static readonly byte[] EncryptedPlain = Enumerable.Range(0, 32).Select(i => (byte)(0x40 + i)).ToArray();
    public static void CreateEncrypted(string path, string keyPath, string gameKeyPath)
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();
        var game = Enumerable.Range(0, 48).Select(i => (byte)(0xa0 + i)).ToArray();
        File.WriteAllBytes(keyPath, key); File.WriteAllBytes(gameKeyPath, game);
        var name = "cipher.bin"; var clearName = Encoding.GetEncoding(932).GetBytes(name); var raw = EncodeName(name, 0);
        var entry = new QlieEntry { Index=0, Name=name, CipherNameBytes=raw.ToArray(), ClearNameBytes=clearName, Encryption=4, KeyFile=key, GameKeyData=game };
        var stored = EncryptedPlain.ToArray(); QlieV3EntryCipher.Transform(stored, entry, 0, key, game, true);
        using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        f.Write(stored); var indexOffset = f.Position;
        f.Write(BitConverter.GetBytes((ushort)raw.Length)); f.Write(raw); f.Write(BitConverter.GetBytes(0L)); f.Write(BitConverter.GetBytes((uint)stored.Length)); f.Write(BitConverter.GetBytes((uint)EncryptedPlain.Length)); f.Write(BitConverter.GetBytes(0)); f.Write(BitConverter.GetBytes(4)); f.Write(BitConverter.GetBytes(0u));
        f.Write(new byte[0x400]); f.Write(Encoding.ASCII.GetBytes("FilePackVer3.0")); f.Write(new byte[2]); f.Write(BitConverter.GetBytes(1)); f.Write(BitConverter.GetBytes(indexOffset));
    }

    public static readonly byte[] ExternalKey = Enumerable.Range(0, 32).Select(i => (byte)(0x20 + i)).ToArray();
    public static readonly byte[] EmbeddedKey = Enumerable.Range(0, 32).Select(i => (byte)(0x80 + i)).ToArray();
    public static readonly byte[] KeyChainGameKey = Enumerable.Range(0, 24).Select(i => (byte)(0x40 + i)).ToArray();
    public const string KeyChainPayloadName = "payload.bin";
    public const string KeyChainSecondPayloadName = "payload-second.bin";
    public static readonly byte[] KeyChainPayload = Enumerable.Range(0, 32).Select(i => (byte)(0xD0 - i)).ToArray();
    public static readonly byte[] KeyChainSecondPayload = Enumerable.Range(0, 32).Select(i => (byte)(0x60 + i)).ToArray();
    public static readonly byte[] KeyChainPayloadRawName = EncodeName(KeyChainPayloadName, 0);
    public static readonly byte[] KeyChainSecondPayloadRawName = EncodeName(KeyChainSecondPayloadName, 0);

    public static void CreateKeyChain(string path, string keyPath, string gameKeyPath)
    {
        File.WriteAllBytes(keyPath, ExternalKey);
        File.WriteAllBytes(gameKeyPath, KeyChainGameKey);
        var names = new[] { "pack_keyfile.bin", KeyChainPayloadName, KeyChainSecondPayloadName };
        var plains = new[] { EmbeddedKey, KeyChainPayload, KeyChainSecondPayload };
        var keys = new[] { ExternalKey, EmbeddedKey, EmbeddedKey };
        var rawNames = new byte[names.Length][];
        var stored = new byte[names.Length][];
        for (var i = 0; i < names.Length; i++)
        {
            rawNames[i] = EncodeName(names[i], 0);
            var entry = new QlieEntry { Index=i, Name=names[i], CipherNameBytes=rawNames[i], ClearNameBytes=Encoding.GetEncoding(932).GetBytes(names[i]), Encryption=4, KeyFile=keys[i], GameKeyData=KeyChainGameKey };
            stored[i] = plains[i].ToArray();
            QlieV3EntryCipher.Transform(stored[i], entry, 0, keys[i], KeyChainGameKey, true);
        }

        using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var offsets = new long[stored.Length];
        for (var i = 0; i < stored.Length; i++) { offsets[i] = f.Position; f.Write(stored[i]); }
        var indexOffset = f.Position;
        for (var i = 0; i < stored.Length; i++)
        {
            f.Write(BitConverter.GetBytes((ushort)rawNames[i].Length)); f.Write(rawNames[i]);
            f.Write(BitConverter.GetBytes(offsets[i])); f.Write(BitConverter.GetBytes((uint)stored[i].Length));
            f.Write(BitConverter.GetBytes((uint)plains[i].Length)); f.Write(BitConverter.GetBytes(0));
            f.Write(BitConverter.GetBytes(4)); f.Write(BitConverter.GetBytes(0u));
        }
        f.Write(new byte[0x400]); f.Write(Encoding.ASCII.GetBytes("FilePackVer3.0")); f.Write(new byte[2]);
        f.Write(BitConverter.GetBytes(stored.Length)); f.Write(BitConverter.GetBytes(indexOffset));
    }

    public static void VerifyCipherKnownVectors()
    {
        var plain = Convert.FromHexString("0102030405060708090A0B0C0D0E0F101112131415161718");
        var cipherName = Convert.FromHexString("7119A43302D16E88");
        var key = Convert.FromHexString("030A11181F262D343B424950575E656C737A81888F969DA4ABB2B9C0C7CED5DC");
        var game = Convert.FromHexString("E0DDDAD7D4D1CECBC8C5C2BF");
        var cases = new[]
        {
            (Cipher: "24524BB06460C18FA6C95871E612AB077A70DACCE2718067", GameKey: (byte[]?)null, Label: "null game-key"),
            (Cipher: "8066888E84628C82AC51B850AC51B840D048C032DC4CC416", GameKey: Array.Empty<byte>(), Label: "empty game-key"),
            (Cipher: "F3355FD8AC2C67A7108C483D568217098E2C0B6532217ADB", GameKey: game, Label: "non-empty game-key")
        };
        foreach (var test in cases)
        {
            var entry = new QlieEntry
            {
                Name="vector.bin", CipherNameBytes=cipherName,
                ClearNameBytes=Encoding.GetEncoding(932).GetBytes("vector.bin"), Encryption=4
            };
            var actual = Convert.FromHexString(test.Cipher);
            QlieV3EntryCipher.Transform(actual, entry, 0x1234567, key, test.GameKey, false);
            if (!actual.SequenceEqual(plain)) throw new Exception($"fixed {test.Label} clear-name cipher vector failed");
        }
    }

}
