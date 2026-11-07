#!/usr/bin/env python3
"""Probe common 32-bit file-hash candidates used by QLIE entry TOCs.

No third-party packages are required. The TOC field is a uint32 stored
little-endian; 64-bit digest candidates are reported as all 32-bit words.
This is a hypothesis-testing utility, not a HashVer1.3 gap verifier.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import struct
import sys
import zlib

MASK32 = 0xFFFFFFFF
MASK64 = 0xFFFFFFFFFFFFFFFF


def crc32c(data: bytes) -> int:
    crc = MASK32
    for byte in data:
        crc ^= byte
        for _ in range(8):
            crc = (crc >> 1) ^ (0x82F63B78 if crc & 1 else 0)
    return crc ^ MASK32


def fnv1a(data: bytes, bits: int) -> int:
    if bits == 32:
        value, prime, mask = 0x811C9DC5, 0x01000193, MASK32
    else:
        value, prime, mask = 0xCBF29CE484222325, 0x100000001B3, MASK64
    for byte in data:
        value = ((value ^ byte) * prime) & mask
    return value


def rotl32(x: int, r: int) -> int:
    return ((x << r) | (x >> (32 - r))) & MASK32


def murmur3_x86_32(data: bytes, seed: int = 0) -> int:
    c1, c2 = 0xCC9E2D51, 0x1B873593
    h = seed & MASK32
    nblocks = len(data) // 4
    for i in range(nblocks):
        k = struct.unpack_from("<I", data, i * 4)[0]
        k = (k * c1) & MASK32
        k = rotl32(k, 15)
        k = (k * c2) & MASK32
        h ^= k
        h = rotl32(h, 13)
        h = (h * 5 + 0xE6546B64) & MASK32
    tail = data[nblocks * 4:]
    k = 0
    if len(tail) == 3:
        k ^= tail[2] << 16
    if len(tail) >= 2:
        k ^= tail[1] << 8
    if len(tail) >= 1:
        k ^= tail[0]
        k = (k * c1) & MASK32
        k = rotl32(k, 15)
        k = (k * c2) & MASK32
        h ^= k
    h ^= len(data)
    h ^= h >> 16
    h = (h * 0x85EBCA6B) & MASK32
    h ^= h >> 13
    h = (h * 0xC2B2AE35) & MASK32
    h ^= h >> 16
    return h & MASK32


def rotl64(x: int, r: int) -> int:
    return ((x << r) | (x >> (64 - r))) & MASK64


def xxh32(data: bytes, seed: int = 0) -> int:
    p1, p2, p3, p4, p5 = 0x9E3779B1, 0x85EBCA77, 0xC2B2AE3D, 0x27D4EB2F, 0x165667B1
    def round32(acc: int, word: int) -> int:
        acc = (acc + word * p2) & MASK32
        acc = rotl32(acc, 13)
        return (acc * p1) & MASK32
    n, pos = len(data), 0
    if n >= 16:
        v1, v2 = (seed + p1 + p2) & MASK32, (seed + p2) & MASK32
        v3, v4 = seed & MASK32, (seed - p1) & MASK32
        while pos <= n - 16:
            v1 = round32(v1, struct.unpack_from("<I", data, pos)[0]); pos += 4
            v2 = round32(v2, struct.unpack_from("<I", data, pos)[0]); pos += 4
            v3 = round32(v3, struct.unpack_from("<I", data, pos)[0]); pos += 4
            v4 = round32(v4, struct.unpack_from("<I", data, pos)[0]); pos += 4
        h = (rotl32(v1, 1) + rotl32(v2, 7) + rotl32(v3, 12) + rotl32(v4, 18)) & MASK32
    else:
        h = (seed + p5) & MASK32
    h = (h + n) & MASK32
    while pos <= n - 4:
        h = (h + struct.unpack_from("<I", data, pos)[0] * p3) & MASK32
        h = (rotl32(h, 17) * p4) & MASK32
        pos += 4
    while pos < n:
        h = (h + data[pos] * p5) & MASK32
        h = (rotl32(h, 11) * p1) & MASK32
        pos += 1
    h ^= h >> 15; h = (h * p2) & MASK32
    h ^= h >> 13; h = (h * p3) & MASK32
    h ^= h >> 16
    return h & MASK32


def xxh64(data: bytes, seed: int = 0) -> int:
    p1, p2, p3 = 0x9E3779B185EBCA87, 0xC2B2AE3D27D4EB4F, 0x165667B19E3779F9
    p4, p5 = 0x85EBCA77C2B2AE63, 0x27D4EB2F165667C5
    def round64(acc: int, word: int) -> int:
        acc = (acc + word * p2) & MASK64
        acc = rotl64(acc, 31)
        return (acc * p1) & MASK64
    n, pos = len(data), 0
    if n >= 32:
        v1, v2 = (seed + p1 + p2) & MASK64, (seed + p2) & MASK64
        v3, v4 = seed & MASK64, (seed - p1) & MASK64
        while pos <= n - 32:
            v1 = round64(v1, struct.unpack_from("<Q", data, pos)[0]); pos += 8
            v2 = round64(v2, struct.unpack_from("<Q", data, pos)[0]); pos += 8
            v3 = round64(v3, struct.unpack_from("<Q", data, pos)[0]); pos += 8
            v4 = round64(v4, struct.unpack_from("<Q", data, pos)[0]); pos += 8
        h = (rotl64(v1, 1) + rotl64(v2, 7) + rotl64(v3, 12) + rotl64(v4, 18)) & MASK64
        for v in (v1, v2, v3, v4):
            h ^= round64(0, v)
            h = (h * p1 + p4) & MASK64
    else:
        h = (seed + p5) & MASK64
    h = (h + n) & MASK64
    while pos <= n - 8:
        k = round64(0, struct.unpack_from("<Q", data, pos)[0])
        h ^= k
        h = (rotl64(h, 27) * p1 + p4) & MASK64
        pos += 8
    if pos <= n - 4:
        h ^= (struct.unpack_from("<I", data, pos)[0] * p1) & MASK64
        h = (rotl64(h, 23) * p2 + p3) & MASK64
        pos += 4
    while pos < n:
        h ^= (data[pos] * p5) & MASK64
        h = (rotl64(h, 11) * p1) & MASK64
        pos += 1
    h ^= h >> 33; h = (h * p2) & MASK64
    h ^= h >> 29; h = (h * p3) & MASK64
    h ^= h >> 32
    return h & MASK64


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("file", type=pathlib.Path, help="original extracted file bytes")
    ap.add_argument("--expect", required=True, help="expected TOC uint32, decimal or 0x-prefixed hex")
    ap.add_argument("--name", help="clear entry name; defaults to the input basename")
    ap.add_argument("--compressed", type=pathlib.Path, help="optional separately supplied compressed payload bytes")
    ap.add_argument("--stored", type=pathlib.Path, help="optional exact stored bytes (may be encrypted; not necessarily compressed)")
    ap.add_argument("--json", action="store_true", help="emit results as JSON")
    args = ap.parse_args()
    try:
        expected = int(args.expect, 0)
    except ValueError:
        ap.error("--expect must be decimal or 0x-prefixed hexadecimal")
    if not 0 <= expected <= MASK32:
        ap.error("--expect must fit the 32-bit TOC field")

    path = args.file
    data = path.read_bytes()
    name = args.name or path.name
    name_forms: dict[str, bytes] = {}
    for enc in ("cp932", "utf-8"):
        try:
            name_forms[f"{enc}:name"] = name.encode(enc)
            for sep in (b"", b"\0", b"/", b"\\"):
                tag = "none" if not sep else sep.hex()
                name_forms[f"{enc}:name+bytes:sep={tag}"] = name.encode(enc) + sep + data
                name_forms[f"{enc}:bytes+name:sep={tag}"] = data + sep + name.encode(enc)
        except UnicodeEncodeError:
            continue
    inputs: dict[str, bytes] = {"raw-file-bytes": data}
    if args.compressed:
        inputs["supplied-compressed-bytes"] = args.compressed.read_bytes()
    if args.stored:
        inputs["supplied-stored-bytes"] = args.stored.read_bytes()
    for label, candidate in name_forms.items():
        if ":name+bytes:" in label or ":bytes+name:" in label:
            inputs[label] = candidate

    results: list[dict[str, object]] = []
    for input_name, payload in inputs.items():
        vals: list[tuple[str, int, int]] = []
        vals.extend([
            ("CRC32-IEEE", zlib.crc32(payload) & MASK32, 32),
            ("CRC32C-Castagnoli", crc32c(payload), 32),
            ("Adler32", zlib.adler32(payload) & MASK32, 32),
            ("FNV-1a-32", fnv1a(payload, 32), 32),
            ("MurmurHash3-x86-32-seed0", murmur3_x86_32(payload, 0), 32),
            ("MurmurHash3-x86-32-seed1", murmur3_x86_32(payload, 1), 32),
            ("xxHash32-seed0", xxh32(payload, 0), 32),
            ("xxHash32-seed1", xxh32(payload, 1), 32),
        ])
        fnv64, xx64 = fnv1a(payload, 64), xxh64(payload, 0)
        for label, value in (("FNV-1a-64", fnv64), ("xxHash64-seed0", xx64)):
            vals.extend([
                (label + ":low32", value & MASK32, 64),
                (label + ":high32", value >> 32, 64),
            ])
        for label, digest in (("MD5", hashlib.md5(payload).digest()), ("SHA1", hashlib.sha1(payload).digest())):
            vals.extend([
                (label + ":first4-big-endian", int.from_bytes(digest[:4], "big"), len(digest) * 8),
                (label + ":first4-little-endian", int.from_bytes(digest[:4], "little"), len(digest) * 8),
            ])
        for algorithm, value, source_width in vals:
            results.append({
                "input": input_name,
                "algorithm": algorithm,
                "value_hex": f"0x{value:08X}",
                "value_decimal": value,
                "source_width_bits": source_width,
                "match": value == expected,
            })

    matches = [r for r in results if r["match"]]
    report = {
        "file": str(path),
        "file_bytes": len(data),
        "file_sha256": hashlib.sha256(data).hexdigest(),
        "clear_name": name,
        "expected_toc_uint32": expected,
        "expected_hex": f"0x{expected:08X}",
        "expected_little_endian_bytes": struct.pack("<I", expected).hex(),
        "expected_field_width_bytes": 4,
        "candidate_count": len(results),
        "matches": matches,
        "results": results,
    }
    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=2))
    else:
        print(f"file={path}\nbytes={len(data)} sha256={report['file_sha256']}")
        print(f"clear_name={name!r}")
        print(f"expected TOC hash=0x{expected:08X} ({expected}); field=uint32/{struct.pack('<I', expected).hex()} (4 bytes, little-endian)")
        print(f"tested={len(results)} candidate/input pairs")
        if matches:
            print("MATCHES:")
            for item in matches:
                print(f"  {item['input']} | {item['algorithm']} = {item['value_hex']}")
        else:
            print("NO MATCH in tested common candidates")
        print("\nAll candidates:")
        for item in results:
            print(f"{item['input']} | {item['algorithm']} = {item['value_hex']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
