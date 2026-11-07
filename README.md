# QlieTool

Independent .NET 8 console utility for inspecting, extracting and rebuilding QLIE FilePack archives in V1.0, V3.0 and V3.1 families. V3.0 supports dynamic 1PC, Footer, HashVer1.3 and the observed HashVer1.4 header/section layout. V3.1 uses the versioned UTF-16LE index, PACKKEY and HashVer1.4 structures. See [FORMAT_RESEARCH.md](FORMAT_RESEARCH.md) for the IDA-backed field findings.

## Commands

Run from this directory:

```text
dotnet run -- list ARCHIVE.pack [--key key.fkey] [--game-key keydata.bin]
dotnet run -- extract ARCHIVE.pack OUTPUT_DIR [--key key.fkey] [--game-key keydata.bin] [--manifest manifest.json]
dotnet run -- verify ARCHIVE.pack [--key key.fkey] [--game-key keydata.bin]
dotnet run -- repack manifest.json OUTPUT.pack --source ARCHIVE.pack --input-dir EXTRACTED_DIR [--key key.fkey] [--game-key keydata.bin]
dotnet run -- clone manifest.json OUTPUT.pack --source ARCHIVE.pack --input-dir EXTRACTED_DIR [--key key.fkey] [--game-key keydata.bin]
dotnet run -- self-test
```

`list`, `extract` and `verify` select the reader from archive/footer structure. V1.0 extracts literal payloads using its CP932 name transform and 28-byte metadata rows. V3.1 parses its UTF-16LE names, PACKKEY and 28-byte entries, validates stored hashes, decrypts mode 1/2 and decompresses BPE. DPNG-wrapped `.png` resources are converted to standard PNG on extraction. Existing output paths are not overwritten.

## Repack behavior

V1.0 repack streams unchanged stored entries and writes literal edited files with recalculated legacy hashes, offsets, index rows and footer. V3.0 repack retains its existing variable-size 1PC and encryption pipeline, rebuilds supported HashVer1.3 data, relocates Footer/index, and preserves opaque regions as before. V3.1 repack regenerates entry names/rows, payload offsets, stored hashes, HashVer1.4 section, PACKKEY length and PACKHEAD; it emits BPE-uncompressed payloads to support arbitrary new lengths and re-applies per-entry obfuscation. The `pack_keyfile` is a dependency for mode-2 entries; edit it only together with dependent key material (the current writer's input model rejects such changes).

V3.1 generated archives have been reopened and all 9 extracted payloads compared by SHA-256 after both unchanged repack and a variable-length text edit. The V3.0 third-title `data0.pack` path now regenerates its 68-byte HashVer1.4 header, transformed 1PC table, terminal length and relocated Footer/index; the current evidence is parser/readback evidence and remains separate from native launch evidence.

## Build and tests

```text
dotnet build --no-restore QlieTool.csproj
dotnet run -- self-test
```

Validation run in a unique Windows TEMP root. Results: V1 data8 145/145 verified; 145-entry V1 repack with one payload grown by 8,192 bytes and another shrunk to 53 bytes relocated index `0xEDF7D4` to `0xEDB0C3`, then verified all rows. V1 extraction's 97 `.png` files passed `PIL.Image.open().verify()` (97/97). V3.1 fourth-title data0 through data8 plus UnInstallData (counts 9, 29, 7513, 964, 1395, 120, 72, 576, 262, 7) all extracted. V3.1 data0 no-op repack and resized game.txt repack were re-extracted; all 9 payload hashes matched for the no-op case and the modified text survived the variable-length roundtrip. Build: 0 warnings, 0 errors. Built-in V3.0 regression self-test passed variable-size grow/shrink, 1PC, encryption, Footer relocation and gap preservation.

The third-title V3.0+HV1.4 grow and shrink tests used the source `DLL\key.fkey` plus EXE-derived GameKeyData; all 9/9 plaintext entry SHA-256 values matched after each re-extraction. The complete fourth-title clone (51 files, 3.59 GB) launched from TEMP and had exit code 0 at the 30-second check, with no `ERRORLOG.txt`; this is native launch evidence for the unmodified fourth-title sample, while the grow/shrink packages remain parser/readback evidence. Original archives and game directories were read-only; generated test files are under `%QLIETOOL_TEMP_ROOT%\QlieTool-hardening-<RUN_ID>`.
