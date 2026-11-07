"""Independent 1PC encoder reconstructed from EXE VA 0x484374.

The implementation intentionally has no dependency on the game or on
QlieTool.Program.cs.  ``encode_1pc`` emits the untransformed 1PC stream;
HashVer's rolling transform is a separate container-layer operation.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import struct

_MAGIC = b"1PC\xff"
_PAIR_SIZE = 0x2000
_BLOCK_SIZE = 0x2000
_MAX_DISTINCT = 0xC8


def _pair_slot(left: list[int], right: list[int], count: list[int], a: int, b: int) -> int:
    """sub_484064: 13-bit hash and linear-probe pair lookup/insert."""
    slot = (a ^ (b << 5)) & (_PAIR_SIZE - 1)
    while True:
        if left[slot] == a and right[slot] == b:
            return slot
        if count[slot] == 0:
            left[slot] = a
            right[slot] = b
            return slot
        slot = (slot + 1) & (_PAIR_SIZE - 1)


def _adjust_pair(left: list[int], right: list[int], count: list[int], a: int, b: int, delta: int) -> None:
    slot = _pair_slot(left, right, count, a, b)
    if delta < 0:
        # The EXE decrements only when the current count is greater than one;
        # a count of one is retained as a tree/link sentinel.
        if count[slot] > 1:
            count[slot] -= 1
    elif count[slot] < 0xFF:
        count[slot] += 1


def _gather_block(
    data: bytes, cursor: int
) -> tuple[list[int], int, bool, list[int], list[int], list[int], list[bool]]:
    """Model sub_4840C0 and return (symbols, new cursor, input_exhausted)."""
    pair_left = [0] * _PAIR_SIZE
    pair_right = [0] * _PAIR_SIZE
    pair_count = [0] * _PAIR_SIZE
    used = [False] * 256
    symbols: list[int] = []
    distinct = 0
    end = len(data)
    while cursor < end:
        cur = data[cursor]
        cursor += 1
        if symbols:
            prev = symbols[-1]
            slot = _pair_slot(pair_left, pair_right, pair_count, prev, cur)
            if pair_count[slot] < 0xFF:
                pair_count[slot] += 1
        symbols.append(cur)
        if not used[cur]:
            used[cur] = True
            distinct += 1
        if len(symbols) >= _BLOCK_SIZE or distinct >= _MAX_DISTINCT or cursor >= end:
            break

    # Preserve the pair table and metadata for the tree pass without making
    # the public API expose the large EXE stack frame.
    exhausted = cursor >= end
    return symbols, cursor, exhausted, pair_left, pair_right, pair_count, used


def _merge_tree(symbols: list[int], pair_left: list[int], pair_right: list[int], pair_count: list[int], used: list[bool]) -> tuple[list[int], list[int], list[int]]:
    """Reproduce the greedy pair replacement loop at 0x48447e..0x48470a."""
    child_left = list(range(256))
    child_right = [0] * 256

    while True:
        # Search descending for the highest identity slot not present in the
        # current block.  That slot is the next internal-node byte.
        node = 255
        while node >= 0 and (child_left[node] != node or used[node]):
            node -= 1
        if node < 0:
            break

        best_count = 2
        best_a = best_b = 0
        # The EXE scans all 8192 pair slots from low to high and updates only
        # on strict greater-than, making ties resolve to the first slot.
        for slot in range(_PAIR_SIZE):
            n = pair_count[slot]
            if n > best_count:
                best_count = n
                best_a = pair_left[slot]
                best_b = pair_right[slot]
        if best_count <= 2:
            break

        old_len = len(symbols)
        if old_len < 2:
            break
        out: list[int] = []
        i = 0
        while i < old_len - 1:
            if symbols[i] == best_a and symbols[i + 1] == best_b:
                if i > 0:
                    prev = out[-1]
                    _adjust_pair(pair_left, pair_right, pair_count, prev, best_a, -1)
                    _adjust_pair(pair_left, pair_right, pair_count, prev, node, +1)
                if i < old_len - 2:
                    nxt = symbols[i + 2]
                    _adjust_pair(pair_left, pair_right, pair_count, best_b, nxt, -1)
                    _adjust_pair(pair_left, pair_right, pair_count, node, nxt, +1)
                out.append(node)
                i += 2
            else:
                out.append(symbols[i])
                i += 1
        if i == old_len - 1:
            out.append(symbols[i])
        symbols = out

        child_left[node] = best_a
        child_right[node] = best_b
        # The merged child pair is retained at frequency one as a sentinel.
        slot = _pair_slot(pair_left, pair_right, pair_count, best_a, best_b)
        pair_count[slot] = 1

    return symbols, child_left, child_right


def _serialize_table(child_left: list[int], child_right: list[int]) -> bytes:
    """Port the branch logic of sub_4841FC's 256-entry table writer."""
    out = bytearray()
    i = 0
    while i < 256:
        if child_left[i] == i:
            # loc_484207: run of identity left children, followed by one
            # descriptor handled by the common loop at loc_4842CB.
            run = 1
            i += 1
            while run < 127 and i < 256 and child_left[i] == i:
                run += 1
                i += 1
            out.append(0x7F + run)
            if i >= 256:
                break
            out.append(child_left[i])
            if child_left[i] != i:
                out.append(child_right[i])
            i += 1
            continue

        # loc_484269: count descriptors until an identity/identity pair can
        # be compacted as the next skip token. It may bridge one identity
        # node between non-identity nodes (as seen at indices 69..71).
        start = i
        esi = 0
        ebx = i + 1
        while True:
            if esi >= 127 or ebx >= 256:
                break
            if child_left[ebx] != ebx:
                esi += 1
                ebx += 1
                continue
            if esi >= 125 or ebx >= 254:
                break
            if child_left[ebx + 1] != ebx + 1:
                esi += 1
                ebx += 1
                continue
            break
        out.append(esi)
        run = esi + 1
        for j in range(start, start + run):
            out.append(child_left[j])
            if child_left[j] != j:
                out.append(child_right[j])
        i = start + run
    return bytes(out)


def _encode_block(data: bytes) -> bytes:
    gathered = _gather_block(data, 0)
    symbols, _, _, pair_left, pair_right, pair_count, used = gathered
    symbols, child_left, child_right = _merge_tree(symbols, pair_left, pair_right, pair_count, used)
    table = _serialize_table(child_left, child_right)
    if len(symbols) > 0xFFFF:
        raise ValueError("1PC short block length overflow")
    return table + struct.pack("<H", len(symbols)) + bytes(symbols)


def encode_1pc(data: bytes) -> bytes:
    """Encode decoded bytes to an untransformed 1PC stream."""
    out = bytearray(_MAGIC + struct.pack("<II", 1, len(data)))
    cursor = 0
    while cursor < len(data):
        symbols, new_cursor, exhausted, pair_left, pair_right, pair_count, used = _gather_block(data, cursor)
        symbols, child_left, child_right = _merge_tree(symbols, pair_left, pair_right, pair_count, used)
        table = _serialize_table(child_left, child_right)
        if len(symbols) > 0xFFFF:
            raise ValueError("1PC short block length overflow")
        out += table
        out += struct.pack("<H", len(symbols))
        out += bytes(symbols)
        cursor = new_cursor
        if exhausted:
            break
    return bytes(out)


def _main() -> None:
    ap = argparse.ArgumentParser(description="reconstructed Qlie 1PC encoder")
    ap.add_argument("decoded", type=pathlib.Path)
    ap.add_argument("--output", type=pathlib.Path, required=True)
    ap.add_argument("--compare", type=pathlib.Path)
    ap.add_argument("--compare-length", type=int, help="compare only this many leading reference bytes")
    args = ap.parse_args()
    raw = args.decoded.read_bytes()
    encoded = encode_1pc(raw)
    args.output.write_bytes(encoded)
    report = {
        "decoded": str(args.decoded),
        "encoded": str(args.output),
        "decodedLength": len(raw),
        "encodedLength": len(encoded),
        "decodedSha256": hashlib.sha256(raw).hexdigest(),
        "encodedSha256": hashlib.sha256(encoded).hexdigest(),
    }
    if args.compare is not None:
        original = args.compare.read_bytes()
        if args.compare_length is not None:
            original = original[:args.compare_length]
        report.update({
            "compare": str(args.compare),
            "compareLength": len(original),
            "byteIdentical": encoded == original,
        })
        if encoded != original:
            n = min(len(encoded), len(original))
            first = next((i for i in range(n) if encoded[i] != original[i]), n)
            report["firstDifference"] = first
            report["encodedByte"] = encoded[first] if first < len(encoded) else None
            report["originalByte"] = original[first] if first < len(original) else None
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    _main()
