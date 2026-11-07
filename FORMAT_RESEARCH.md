# QLIE Format Findings

Samples were read only. Generated tests use `%QLIETOOL_TEMP_ROOT%\QlieTool-hardening-<RUN_ID>`.

## V1.0 data8.pack

| Field | Width | Record-relative offset | Evidence / meaning |
|---|---:|---:|---|
| Payload offset | 8 | 0 | Little-endian u64 |
| Stored size | 4 | 8 | Little-endian u32 |
| Unpacked size | 4 | 12 | Little-endian u32; sample records are literal |
| Flags | 8 | 16 | Little-endian u64; zero for all 145 sample entries |
| Legacy payload hash | 4 | 24 | Little-endian u32; checked by V1 parser/repacker |

Each row follows a u16 byte-length and encrypted CP932 name. The parser consumes all 145 records exactly in the 8,548-byte index. Footer: count 145, index offset `0xEDF7D4`; archive length 15,604,052.

## V3.0 HashVer1.3 vs HashVer1.4

The actual third-title `data0..data9.pack` files are V3.0 + HashVer1.4; title 2 samples are V3.0 + HashVer1.3. The data0 decoded HashVer header is 68 bytes in both and shares bucket count 256, entry count 9, index size 18, compressed size 285. Magic changes `HashVer1.3` → `HashVer1.4`. The third-title `data0.pack` has a 389-byte section: 68-byte header + 285-byte transformed 1PC stream + 36-byte terminal lane. Its header is `+0x20=1` and `+0x24..+0x40` are all zero. IDA confirms this layout: third EXE `sub_486FAC` at `0x486FAC` zero-fills 68 bytes, writes `HashVer1.4`, fields at `+0x10..+0x20`, then writes exactly 0x44 bytes; `sub_486EE4` at `0x486EE4` dynamically serializes bucket records. The HV1.3 comparison values after +0x24 are legacy/reserved bytes and are not copied by the HV1.4 writer. The V3.0 writer now regenerates this 68-byte HV1.4 header, transforms a freshly encoded 1PC table, and rebuilds the terminal length field.

V3.0 list/extract dispatch reads all ten third-title archives. HV1.4 dynamic repack regenerates the 68-byte header, transformed 1PC name table, 36-byte terminal length lane, relocated index and Footer; source gap bytes are not reused after payload/index size changes.

## V3.1 HashVer1.4

V3.1 `PACKHEAD` is 28 bytes at EOF; `PACKKEY` is 1,060 bytes immediately before it. `PACKKEY+32` stores hash section length; key material starts +36; the hash section directly precedes the key. HashVer1.4 header is 68 bytes. Entries are 28 bytes: offset u64, stored length u32, original length u32, compressed u32, obfuscated u32, stored hash u32. Names use a u16 UTF-16LE character count and encrypted UTF-16LE bytes. BPE header is 12 bytes: `1PC\xff`, flags u32 at +4, original length u32 at +8; flags bit 0 selects the compressed block length width.
