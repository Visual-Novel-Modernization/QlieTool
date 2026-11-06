# QlieTool

Independent .NET 8 console utility for inspecting and rebuilding QLIE FilePackVer3.0 archives. The implementation follows the QLIE reader behavior in GARbro and is tested against a TEMP copy of a second Bishoujo Mangekyou game archive.

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

verify parses each entry, attempts decoding, checks unpacked size for decoded entries, and hashes bytes. When decoding fails it hashes the bounded stored payload and reports it as rawStored; this is a structural/payload-read check, not proof that unsupported raw content is valid after decryption. --game-key supplies optional GameKeyData independently of --key (the external key.fkey).

## Modified repack behavior

repack rebuilds a new archive; it never copies the source archive as the output. It reads every manifest entry from --input-dir, validates the manifest source SHA-256, and writes payloads in the source physical order without adding per-entry alignment, preserves the source IndexOffset, copies the complete source index-to-trailer HashVer1.3 region byte-for-byte, preserves the 0x400-byte V3 trailer, and writes a FilePackVer3.0 footer with the source index offset.

- Unchanged extracted files reuse their original stored payload bytes and original packed/encryption/hash metadata.
- Changed decoded files are emitted as new literal, unpacked payloads. The built-in codec intentionally does not implement 1PC compression; packed entries are rejected for edits rather than being mislabeled as packed=false. To preserve the source HashVer1.3 region and all later offsets, edited stored payloads must remain exactly the source StoredSize; size-changing edits are rejected conservatively.
- Changed raw .qlie-raw entries are rejected because their decoded representation is unknown.
- Changed encryption!=0 entries use the built-in `QlieV3EntryCipher`, which is the inverse of the GARbro reader's per-block XOR/state transform. Changed decoded payloads are emitted as literal (`packed=false`), retain the source encryption mode, and are decrypted again by the reader in the synthetic roundtrip test. The transform processes complete 64-bit blocks exactly as the reader does; a tail shorter than 8 bytes is preserved byte-for-byte, matching the reader.
- The active `pack_keyfile` entry is rejected when edited because changing it would invalidate the active key chain used by all following entries. Ordinary encrypted entries use their recorded active key.
- The 0x400-byte trailer occupies output EOF-0x41c through EOF-0x1c; its first 0x100 bytes are the ArcKey material used for name encryption. The opaque trailer is preserved byte-for-byte unless a caller supplies a validated replacement through the API. `IEntryHashProvider` is an explicit injection point; the CLI uses `PreserveSourceEntryHashProvider` because GARbro reads the V3 entry Hash but does not establish its writer algorithm, so changed entries retain the source Hash rather than receiving a guessed value.
- Payload encoding is injectable through `IEntryPayloadCodec.Encode(byte[] decoded, QlieEntry sourceEntry)`. V3 encryption is injectable through `IV3EntryCipher.Encrypt(byte[] plainPayload, QlieEntry sourceEntry, string entryName)` and the CLI supplies the built-in `QlieV3EntryCipher`.

clone remains a compatibility alias for the same rebuild operation; it is not a byte-copy command. Output must be a new path and must not point into an installed game directory.

## Build and test

```text
dotnet build QlieTool.csproj
dotnet run -- self-test
```

The synthetic test covers FilePackVer3.0 index parsing, CP932 name cipher, 1PC decoding, modified plain payload, unchanged stored-payload reuse, the built-in encryption=4 inverse cipher, encrypted decode/modify/repack/readback, path guards, and rejection of edited raw payloads.

## Tested scope and limitations

On a TEMP copy of the target second-game data2.pack, list/extract/verify completed for 1,036 entries: all 1,036 decoded, all use encryption=4, and none are packed. The encrypted file chara/motion/motion01.b was edited, rebuilt, verified, extracted again, and read back with the changed first byte matching. Editing the active pack_keyfile_kfueheish15538fa9or.key is rejected because it would invalidate the active key chain. An earlier data0 probe retained seven unsupported entries exactly with the .qlie-raw suffix; other packs/games have not been validated.

Archive payloads above the current in-memory decrypt/decompress limit are reported as unsupported; list and bounded raw payload reads use 64-bit offsets. All real archive/key inputs are copied into a unique Windows TEMP directory by the CLI before reading. The repacker writes only to the requested new output path.

## Reference and attribution

The implementation was checked against GARbro QLIE reader files ArcFormats/Qlie/ArcQLIE.cs, Encryption.cs, and QlieMersenneTwister.cs: https://github.com/morkt/GARbro/tree/master/ArcFormats/Qlie . See NOTICE.md for attribution and MIT terms.
