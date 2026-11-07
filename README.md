# QlieTool

Independent .NET 8 console utility for inspecting and rebuilding QLIE FilePackVer3.0 archives. The implementation follows the QLIE reader behavior in GARbro and includes a HashVer1.3/1PC writer reconstructed from the target EXE and data7.pack; synthetic regression coverage and isolated TEMP data7 roundtrips are included, while no installed-game runtime launch is claimed.

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

list parses the FilePackVer3.0 footer, 64-bit index offsets, and encrypted CP932 names. extract creates a JSON manifest containing original entry fields, payload/extracted SHA-256 values, and whether each file was decoded. Decodable entries are written under their indexed name. If encrypted/decompressed data is unsupported or invalid, exact stored entry bytes are written as <name>.qlie-raw; this suffix identifies raw stored payload, not the original decoded file. Path traversal is rejected and existing files/manifests are never overwritten.

verify parses each entry, attempts decoding, checks unpacked size for decoded entries, and hashes bytes. When decoding fails it hashes the bounded stored payload and reports it as rawStored; this is a structural/payload-read check, not proof that unsupported raw content is valid after decryption. --game-key supplies GameKeyData independently of --key (the external key.fkey). To reproduce GARbro's V3 reads, provide the same GameKeyData that GARbro uses; omitting it or supplying a different file changes the cipher state. The key-chain regression uses a synthetic fixture and does not confirm integration against a real game archive.

## Modified repack behavior

repack rebuilds a new archive; it never copies the source archive as the output. It validates the manifest against the source archive and writes payloads contiguously in source TOC order without adding per-entry alignment. Unchanged stored payloads are streamed directly from the source archive; edited payloads are materialized through the selected codec/cipher. The writer records actual output offsets and sizes, serializes a relocated TOC, rebuilds a recognized HashVer1.3 name-index section and terminal marker, relocates the 0x400-byte V3 trailer, then updates the FilePackVer3.0 footer index offset/count. GARbro's public QLIE implementation is a reader (`CanWrite=false`); arc_pack documents the older FilePackVer1.0 writer/checksum, not this V3 HashVer1.3 container.

- Unchanged extracted files reuse their original stored payload bytes and original packed/encryption/hash metadata.
- Changed decoded entries retain the source packed flag. Packed entries use the byte-compatible native 1PC encoder port; literal entries remain uncompressed. Edited payloads may grow or shrink; all following payload offsets, TOC location, HashVer1.3 name-index values, stored-payload checksums, and footer index offset are recalculated.
- Changed raw .qlie-raw entries are rejected because their decoded representation is unknown.
- Changed encryption!=0 entries use the built-in `QlieV3EntryCipher`, the inverse of the QLIE/GARbro per-block XOR/state transform. Edited decoded payloads are encrypted after optional 1PC encoding and decrypted again by the QlieTool reader in the synthetic roundtrip test. The transform processes complete 64-bit blocks; a tail shorter than 8 bytes is preserved byte-for-byte.
- The active `pack_keyfile` entry is rejected when edited because changing it would invalidate the active key chain used by all following entries. Ordinary encrypted entries use their recorded active key.
- For a recognized HashVer1.3 section, the writer preserves the source 64-bit per-name metadata and observed u16-per-entry tail, regenerates each stored name-key hash, 1PC-encodes the rebuilt table, transforms the stream, and emits a new trailer marker and section length. Unknown/non-HashVer gaps are preserved byte-for-byte. The V3 trailer's first 0x100 bytes supply the ArcKey material; changing that trailer is rejected unless a matching key is already available. `IEntryHashProvider` defaults to the arc_pack-compatible stored-payload checksum, verified against all 522 data7 source entries.
- Payload encoding is injectable through `IEntryPayloadCodec.Encode(byte[] decoded, QlieEntry sourceEntry)`. V3 encryption is injectable through `IV3EntryCipher.Encrypt(byte[] plainPayload, QlieEntry sourceEntry, string entryName)` and the CLI supplies the built-in `QlieV3EntryCipher`.

clone remains a compatibility alias for the same rebuild operation; it is not a byte-copy command. Output must be a new path and must not point into an installed game directory.

## Build and test

```text
dotnet build --no-restore QlieTool.csproj
dotnet run -- self-test
```

The synthetic test covers FilePackVer3.0 index parsing, CP932 name cipher, 1PC decoding, variable-size edited payload growth and shrinkage with later-offset/TOC/footer relocation, decoded readback, streamed unchanged stored-payload reuse, exact opaque gap/trailer preservation, the built-in encryption=4 inverse cipher, encrypted decode/modify/repack/readback, path guards, and rejection of edited raw payloads. Synthetic fixtures validate writer structure, not HashVer1.3 runtime acceptance.

## Tested scope and limitations

Validation covers synthetic fixtures and regression self-tests, a byte-identical no-op repack of target data7.pack in a unique TEMP directory, and a +128-byte edited data7 roundtrip with 522-entry verification and SHA-256 readback match. No installed-game launch was performed; the isolated archive result is structural/readback evidence, not a runtime compatibility claim.

Archive payloads above the current in-memory decrypt/decompress limit are reported as unsupported; list and bounded raw payload reads use 64-bit offsets. All real archive/key inputs are copied into a unique Windows TEMP directory by the CLI before reading. The repacker writes only to the requested new output path.

## Reference and attribution

The implementation was checked against GARbro QLIE reader files ArcFormats/Qlie/ArcQLIE.cs, Encryption.cs, and QlieMersenneTwister.cs: https://github.com/morkt/GARbro/tree/master/ArcFormats/Qlie . See NOTICE.md for attribution and MIT terms.
