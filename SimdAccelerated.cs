using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace QlieTool;

public static class SimdAccelerated
{
    public static bool ForceScalar = false;

    public static string GetActiveSimdTier()
    {
        if (ForceScalar)
            return "Scalar (Forced)";
        if (Vector512.IsHardwareAccelerated && Avx512F.IsSupported && Avx512BW.IsSupported)
            return "AVX-512 (512-bit)";
        if (Vector256.IsHardwareAccelerated && Avx2.IsSupported)
            return "AVX2 (256-bit)";
        if (Vector128.IsHardwareAccelerated && Sse2.IsSupported)
            return "SSE2 (128-bit)";
        return "Scalar (Standard)";
    }

    #region Checksum and ArcKey (AddW) Acceleration

    public static void AccumulatePayloadWords(
        ReadOnlySpan<byte> data,
        ref ulong state,
        ref ulong acc,
        ref long qwordIndex)
    {
        int offset = 0;
        int remainingBytes = data.Length;

        if (!ForceScalar)
        {
            // Tier 1: AVX-512 (64 bytes = 8 qwords = 32 ushorts per iteration)
            if (Vector512.IsHardwareAccelerated && Avx512F.IsSupported && Avx512BW.IsSupported && remainingBytes >= 64)
            {
                AccumulateAvx512(data, ref offset, ref state, ref acc, ref qwordIndex);
            }
            // Tier 2: AVX2 (32 bytes = 4 qwords = 16 ushorts per iteration)
            else if (Vector256.IsHardwareAccelerated && Avx2.IsSupported && remainingBytes >= 32)
            {
                AccumulateAvx2(data, ref offset, ref state, ref acc, ref qwordIndex);
            }
            // Tier 3: Vector128 (16 bytes = 2 qwords = 8 ushorts per iteration)
            else if (Vector128.IsHardwareAccelerated && Sse2.IsSupported && remainingBytes >= 16)
            {
                AccumulateVector128(data, ref offset, ref state, ref acc, ref qwordIndex);
            }
        }

        // Tier 4: Scalar Fallback
        AccumulateScalar(data, ref offset, ref state, ref acc, ref qwordIndex);
    }

    public static void AccumulateAvx512(
        ReadOnlySpan<byte> data,
        ref int offset,
        ref ulong state,
        ref ulong acc,
        ref long qwordIndex)
    {
        if (!Vector512.IsHardwareAccelerated || !Avx512F.IsSupported || !Avx512BW.IsSupported) return;
        int remainingBytes = data.Length;
        if (remainingBytes - offset < 64) return;

        var vAcc = Vector512<ushort>.Zero;
        var vStep = Vector512.Create((ushort)(8 * 0x0307));

        Span<ushort> initialStates = stackalloc ushort[32];
        for (int q = 0; q < 8; q++)
        {
            ushort s = (ushort)(((qwordIndex + q + 1) * 0x0307) & 0xFFFF);
            initialStates[q * 4 + 0] = s;
            initialStates[q * 4 + 1] = s;
            initialStates[q * 4 + 2] = s;
            initialStates[q * 4 + 3] = s;
        }
        var vState = Vector512.Create<ushort>(initialStates);

        ref byte dataRef = ref MemoryMarshal.GetReference(data);
        while (offset + 64 <= remainingBytes)
        {
            var vData = Vector512.LoadUnsafe(ref dataRef, (nuint)offset).AsUInt16();
            vAcc += vData ^ vState;
            vState += vStep;

            offset += 64;
            qwordIndex += 8;
        }

        Span<ushort> accLanes = stackalloc ushort[32];
        vAcc.CopyTo(accLanes);

        ushort w0 = 0, w1 = 0, w2 = 0, w3 = 0;
        for (int q = 0; q < 8; q++)
        {
            w0 = (ushort)(w0 + accLanes[q * 4 + 0]);
            w1 = (ushort)(w1 + accLanes[q * 4 + 1]);
            w2 = (ushort)(w2 + accLanes[q * 4 + 2]);
            w3 = (ushort)(w3 + accLanes[q * 4 + 3]);
        }

        ulong reducedAcc = (ulong)w0 | ((ulong)w1 << 16) | ((ulong)w2 << 32) | ((ulong)w3 << 48);
        acc = PackedMath.AddW(acc, reducedAcc);

        ushort lastStateWord = (ushort)((qwordIndex * 0x0307) & 0xFFFF);
        state = (ulong)lastStateWord | ((ulong)lastStateWord << 16) | ((ulong)lastStateWord << 32) | ((ulong)lastStateWord << 48);
    }

    public static void AccumulateAvx2(
        ReadOnlySpan<byte> data,
        ref int offset,
        ref ulong state,
        ref ulong acc,
        ref long qwordIndex)
    {
        if (!Vector256.IsHardwareAccelerated || !Avx2.IsSupported) return;
        int remainingBytes = data.Length;
        if (remainingBytes - offset < 32) return;

        var vAcc = Vector256<ushort>.Zero;
        var vStep = Vector256.Create((ushort)(4 * 0x0307));

        Span<ushort> initialStates = stackalloc ushort[16];
        for (int q = 0; q < 4; q++)
        {
            ushort s = (ushort)(((qwordIndex + q + 1) * 0x0307) & 0xFFFF);
            initialStates[q * 4 + 0] = s;
            initialStates[q * 4 + 1] = s;
            initialStates[q * 4 + 2] = s;
            initialStates[q * 4 + 3] = s;
        }
        var vState = Vector256.Create<ushort>(initialStates);

        ref byte dataRef = ref MemoryMarshal.GetReference(data);
        while (offset + 32 <= remainingBytes)
        {
            var vData = Vector256.LoadUnsafe(ref dataRef, (nuint)offset).AsUInt16();
            vAcc += vData ^ vState;
            vState += vStep;

            offset += 32;
            qwordIndex += 4;
        }

        Span<ushort> accLanes = stackalloc ushort[16];
        vAcc.CopyTo(accLanes);

        ushort w0 = 0, w1 = 0, w2 = 0, w3 = 0;
        for (int q = 0; q < 4; q++)
        {
            w0 = (ushort)(w0 + accLanes[q * 4 + 0]);
            w1 = (ushort)(w1 + accLanes[q * 4 + 1]);
            w2 = (ushort)(w2 + accLanes[q * 4 + 2]);
            w3 = (ushort)(w3 + accLanes[q * 4 + 3]);
        }

        ulong reducedAcc = (ulong)w0 | ((ulong)w1 << 16) | ((ulong)w2 << 32) | ((ulong)w3 << 48);
        acc = PackedMath.AddW(acc, reducedAcc);

        ushort lastStateWord = (ushort)((qwordIndex * 0x0307) & 0xFFFF);
        state = (ulong)lastStateWord | ((ulong)lastStateWord << 16) | ((ulong)lastStateWord << 32) | ((ulong)lastStateWord << 48);
    }

    public static void AccumulateVector128(
        ReadOnlySpan<byte> data,
        ref int offset,
        ref ulong state,
        ref ulong acc,
        ref long qwordIndex)
    {
        if (!Vector128.IsHardwareAccelerated || !Sse2.IsSupported) return;
        int remainingBytes = data.Length;
        if (remainingBytes - offset < 16) return;

        var vAcc = Vector128<ushort>.Zero;
        var vStep = Vector128.Create((ushort)(2 * 0x0307));

        Span<ushort> initialStates = stackalloc ushort[8];
        for (int q = 0; q < 2; q++)
        {
            ushort s = (ushort)(((qwordIndex + q + 1) * 0x0307) & 0xFFFF);
            initialStates[q * 4 + 0] = s;
            initialStates[q * 4 + 1] = s;
            initialStates[q * 4 + 2] = s;
            initialStates[q * 4 + 3] = s;
        }
        var vState = Vector128.Create<ushort>(initialStates);

        ref byte dataRef = ref MemoryMarshal.GetReference(data);
        while (offset + 16 <= remainingBytes)
        {
            var vData = Vector128.LoadUnsafe(ref dataRef, (nuint)offset).AsUInt16();
            vAcc += vData ^ vState;
            vState += vStep;

            offset += 16;
            qwordIndex += 2;
        }

        Span<ushort> accLanes = stackalloc ushort[8];
        vAcc.CopyTo(accLanes);

        ushort w0 = (ushort)(accLanes[0] + accLanes[4]);
        ushort w1 = (ushort)(accLanes[1] + accLanes[5]);
        ushort w2 = (ushort)(accLanes[2] + accLanes[6]);
        ushort w3 = (ushort)(accLanes[3] + accLanes[7]);

        ulong reducedAcc = (ulong)w0 | ((ulong)w1 << 16) | ((ulong)w2 << 32) | ((ulong)w3 << 48);
        acc = PackedMath.AddW(acc, reducedAcc);

        ushort lastStateWord = (ushort)((qwordIndex * 0x0307) & 0xFFFF);
        state = (ulong)lastStateWord | ((ulong)lastStateWord << 16) | ((ulong)lastStateWord << 32) | ((ulong)lastStateWord << 48);
    }

    public static void AccumulateScalar(
        ReadOnlySpan<byte> data,
        ref int offset,
        ref ulong state,
        ref ulong acc,
        ref long qwordIndex)
    {
        int remainingBytes = data.Length;
        for (; offset + 8 <= remainingBytes; offset += 8)
        {
            state = PackedMath.AddW(state, 0x0307030703070307UL);
            ulong qw = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
            acc = PackedMath.AddW(acc, qw ^ state);
            qwordIndex++;
        }
    }

    public static uint CalculateStoredChecksum(ReadOnlySpan<byte> data)
    {
        ulong state = 0, acc = 0;
        long qwords = 0;
        AccumulatePayloadWords(data, ref state, ref acc, ref qwords);
        return (uint)(acc ^ (acc >> 32));
    }

    public static uint CalculateArcKey(ReadOnlySpan<byte> data)
    {
        ulong state = 0, acc = 0;
        long qwords = 0;
        AccumulatePayloadWords(data, ref state, ref acc, ref qwords);
        return (uint)(acc ^ (acc >> 32)) & 0x0fffffff;
    }

    public static uint ComputeLegacyPayloadHash(Stream stream, int bufferSize = 65536)
    {
        byte[] buffer = new byte[bufferSize];
        ulong state = 0, acc = 0;
        long qwords = 0;
        int leftover = 0;

        while (true)
        {
            int read = stream.Read(buffer, leftover, buffer.Length - leftover);
            if (read == 0) break;

            int totalAvailable = leftover + read;
            int processableBytes = totalAvailable & ~7; // align to 8-byte boundary

            if (processableBytes > 0)
            {
                AccumulatePayloadWords(buffer.AsSpan(0, processableBytes), ref state, ref acc, ref qwords);
                leftover = totalAvailable - processableBytes;
                if (leftover > 0)
                {
                    Buffer.BlockCopy(buffer, processableBytes, buffer, 0, leftover);
                }
            }
            else
            {
                leftover = totalAvailable;
            }
        }

        return unchecked((uint)(acc ^ (acc >> 32)));
    }

    #endregion

    #region File Name Cipher (CryptName)

    private static readonly Vector512<byte> VIdx512 = CreateIdx512();
    private static readonly Vector256<byte> VIdx256 = CreateIdx256();
    private static readonly Vector128<byte> VIdx128 = CreateIdx128();

    private static Vector512<byte> CreateIdx512()
    {
        Span<byte> s = stackalloc byte[64];
        for (int i = 0; i < 64; i++) s[i] = (byte)(i + 1);
        return Vector512.Create<byte>(s);
    }
    private static Vector256<byte> CreateIdx256()
    {
        Span<byte> s = stackalloc byte[32];
        for (int i = 0; i < 32; i++) s[i] = (byte)(i + 1);
        return Vector256.Create<byte>(s);
    }
    private static Vector128<byte> CreateIdx128()
    {
        Span<byte> s = stackalloc byte[16];
        for (int i = 0; i < 16; i++) s[i] = (byte)(i + 1);
        return Vector128.Create<byte>(s);
    }

    public static void CryptName(Span<byte> bytes, int length, int key)
    {
        int k = 0;
        ref byte bytesRef = ref MemoryMarshal.GetReference(bytes);
        byte keyByte = (byte)(key & 0xFF);

        if (!ForceScalar && length >= 64 && Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
        {
            var vIdx = VIdx512;
            var vStep = Vector512.Create((byte)64);
            var vKey = Vector512.Create(keyByte);

            while (k + 64 <= length)
            {
                var vMask = (vIdx ^ vKey) + vIdx;
                var vData = Vector512.LoadUnsafe(ref bytesRef, (nuint)k);
                (vData ^ vMask).StoreUnsafe(ref bytesRef, (nuint)k);

                vIdx += vStep;
                k += 64;
            }
        }

        // Tier 4: Scalar Fallback (for remainder or short strings)
        for (; k < length; k++)
        {
            bytes[k] ^= unchecked((byte)(((k + 1) ^ key) + (k + 1)));
        }
    }

    #endregion

    #region 1PC BPE Tree Best Pair Search

    public static bool FindBestPair(
        ReadOnlySpan<int> pairCounts,
        ReadOnlySpan<int> pairLeft,
        ReadOnlySpan<int> pairRight,
        int minThreshold,
        out int bestCount,
        out int bestA,
        out int bestB)
    {
        bestCount = minThreshold;
        bestA = 0;
        bestB = 0;
        int length = pairCounts.Length;

        int maxVal = minThreshold;
        ref int countsRef = ref MemoryMarshal.GetReference(pairCounts);
        int i = 0;

        // Tier 1: AVX-512 (16 ints per iteration)
        if (!ForceScalar && Vector512.IsHardwareAccelerated && Avx512F.IsSupported && length >= 16)
        {
            var vMax = Vector512.Create(minThreshold);
            while (i + 16 <= length)
            {
                var vCurrent = Vector512.LoadUnsafe(ref countsRef, (nuint)i);
                vMax = Vector512.Max(vMax, vCurrent);
                i += 16;
            }
            Span<int> maxLanes = stackalloc int[16];
            vMax.CopyTo(maxLanes);
            for (int j = 0; j < 16; j++)
            {
                if (maxLanes[j] > maxVal) maxVal = maxLanes[j];
            }
        }
        // Tier 2: AVX2 (8 ints per iteration)
        else if (Vector256.IsHardwareAccelerated && Avx2.IsSupported && length >= 8)
        {
            var vMax = Vector256.Create(minThreshold);
            while (i + 8 <= length)
            {
                var vCurrent = Vector256.LoadUnsafe(ref countsRef, (nuint)i);
                vMax = Vector256.Max(vMax, vCurrent);
                i += 8;
            }
            Span<int> maxLanes = stackalloc int[8];
            vMax.CopyTo(maxLanes);
            for (int j = 0; j < 8; j++)
            {
                if (maxLanes[j] > maxVal) maxVal = maxLanes[j];
            }
        }
        // Tier 3: Vector128 (4 ints per iteration)
        else if (Vector128.IsHardwareAccelerated && Sse2.IsSupported && length >= 4)
        {
            var vMax = Vector128.Create(minThreshold);
            while (i + 4 <= length)
            {
                var vCurrent = Vector128.LoadUnsafe(ref countsRef, (nuint)i);
                vMax = Vector128.Max(vMax, vCurrent);
                i += 4;
            }
            Span<int> maxLanes = stackalloc int[4];
            vMax.CopyTo(maxLanes);
            for (int j = 0; j < 4; j++)
            {
                if (maxLanes[j] > maxVal) maxVal = maxLanes[j];
            }
        }

        // Tail
        for (; i < length; i++)
        {
            if (pairCounts[i] > maxVal) maxVal = pairCounts[i];
        }

        if (maxVal <= minThreshold) return false;

        // Locate first slot matching maxVal to preserve strict tie-breaking
        if (!ForceScalar && Vector512.IsHardwareAccelerated && Avx512F.IsSupported && length >= 16)
        {
            var vTarget = Vector512.Create(maxVal);
            int slot = 0;
            while (slot + 16 <= length)
            {
                var vCurrent = Vector512.LoadUnsafe(ref countsRef, (nuint)slot);
                var mask = Vector512.Equals(vCurrent, vTarget);
                ulong maskBits = mask.ExtractMostSignificantBits();
                if (maskBits != 0)
                {
                    int matchOffset = BitOperations.TrailingZeroCount(maskBits);
                    int matchSlot = slot + matchOffset;
                    bestCount = maxVal;
                    bestA = pairLeft[matchSlot];
                    bestB = pairRight[matchSlot];
                    return true;
                }
                slot += 16;
            }
            for (; slot < length; slot++)
            {
                if (pairCounts[slot] == maxVal)
                {
                    bestCount = maxVal;
                    bestA = pairLeft[slot];
                    bestB = pairRight[slot];
                    return true;
                }
            }
            return false;
        }

        for (int slot = 0; slot < length; slot++)
        {
            if (pairCounts[slot] == maxVal)
            {
                bestCount = maxVal;
                bestA = pairLeft[slot];
                bestB = pairRight[slot];
                return true;
            }
        }

        return false;
    }

    #endregion

    #region PNG Unfilter Acceleration

    public static void Unfilter(byte f, Span<byte> src, Span<byte> dst, Span<byte> prev, int bpp)
    {
        // Filter 0: None
        if (f == 0)
        {
            src.CopyTo(dst);
            return;
        }

        // Filter 2: Up (dst[i] = src[i] + prev[i])
        if (f == 2)
        {
            if (prev.Length == 0)
            {
                src.CopyTo(dst);
                return;
            }

            int i = 0;
            int length = src.Length;
            ref byte srcRef = ref MemoryMarshal.GetReference(src);
            ref byte dstRef = ref MemoryMarshal.GetReference(dst);
            ref byte prevRef = ref MemoryMarshal.GetReference(prev);

            // Tier 1: AVX-512 (256 bytes unrolled per iteration)
            if (!ForceScalar && Vector512.IsHardwareAccelerated && Avx512BW.IsSupported && length >= 64)
            {
                while (i + 256 <= length)
                {
                    var vSrc0 = Vector512.LoadUnsafe(ref srcRef, (nuint)i);
                    var vPrev0 = Vector512.LoadUnsafe(ref prevRef, (nuint)i);
                    var vSrc1 = Vector512.LoadUnsafe(ref srcRef, (nuint)(i + 64));
                    var vPrev1 = Vector512.LoadUnsafe(ref prevRef, (nuint)(i + 64));
                    var vSrc2 = Vector512.LoadUnsafe(ref srcRef, (nuint)(i + 128));
                    var vPrev2 = Vector512.LoadUnsafe(ref prevRef, (nuint)(i + 128));
                    var vSrc3 = Vector512.LoadUnsafe(ref srcRef, (nuint)(i + 192));
                    var vPrev3 = Vector512.LoadUnsafe(ref prevRef, (nuint)(i + 192));

                    (vSrc0 + vPrev0).StoreUnsafe(ref dstRef, (nuint)i);
                    (vSrc1 + vPrev1).StoreUnsafe(ref dstRef, (nuint)(i + 64));
                    (vSrc2 + vPrev2).StoreUnsafe(ref dstRef, (nuint)(i + 128));
                    (vSrc3 + vPrev3).StoreUnsafe(ref dstRef, (nuint)(i + 192));
                    i += 256;
                }
                while (i + 64 <= length)
                {
                    var vSrc = Vector512.LoadUnsafe(ref srcRef, (nuint)i);
                    var vPrev = Vector512.LoadUnsafe(ref prevRef, (nuint)i);
                    (vSrc + vPrev).StoreUnsafe(ref dstRef, (nuint)i);
                    i += 64;
                }
            }
            // Tier 2: AVX2 (32 bytes per iteration)
            else if (Vector256.IsHardwareAccelerated && Avx2.IsSupported && length >= 32)
            {
                while (i + 32 <= length)
                {
                    var vSrc = Vector256.LoadUnsafe(ref srcRef, (nuint)i);
                    var vPrev = Vector256.LoadUnsafe(ref prevRef, (nuint)i);
                    (vSrc + vPrev).StoreUnsafe(ref dstRef, (nuint)i);
                    i += 32;
                }
            }
            // Tier 3: Vector128 (16 bytes per iteration)
            else if (Vector128.IsHardwareAccelerated && Sse2.IsSupported && length >= 16)
            {
                while (i + 16 <= length)
                {
                    var vSrc = Vector128.LoadUnsafe(ref srcRef, (nuint)i);
                    var vPrev = Vector128.LoadUnsafe(ref prevRef, (nuint)i);
                    (vSrc + vPrev).StoreUnsafe(ref dstRef, (nuint)i);
                    i += 16;
                }
            }

            // Scalar fallback for remaining bytes
            for (; i < length; i++)
            {
                dst[i] = (byte)(src[i] + prev[i]);
            }
            return;
        }

        // Filter 1 (Sub), Filter 3 (Average), Filter 4 (Paeth): Standard PNG filter
        for (int i = 0; i < src.Length; i++)
        {
            int a = i >= bpp ? dst[i - bpp] : 0;
            int b = i < prev.Length ? prev[i] : 0;
            int c = i >= bpp && i < prev.Length ? prev[i - bpp] : 0;
            dst[i] = (byte)(src[i] + (f switch
            {
                1 => a,
                3 => (a + b) / 2,
                4 => Paeth(a, b, c),
                _ => throw new InvalidDataException("unsupported PNG filter")
            }));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    #endregion

    #region DPNG RGB24 to RGBA32 Unpack

    private static readonly Vector128<byte> RgbShuffleMask = Vector128.Create(
        (byte)0, (byte)1, (byte)2, (byte)0x80,
        (byte)3, (byte)4, (byte)5, (byte)0x80,
        (byte)6, (byte)7, (byte)8, (byte)0x80,
        (byte)9, (byte)10, (byte)11, (byte)0x80);

    private static readonly Vector128<byte> AlphaOrMask = Vector128.Create(
        (byte)0, (byte)0, (byte)0, (byte)0xFF,
        (byte)0, (byte)0, (byte)0, (byte)0xFF,
        (byte)0, (byte)0, (byte)0, (byte)0xFF,
        (byte)0, (byte)0, (byte)0, (byte)0xFF);

    public static void ConvertRgbToRgba(ReadOnlySpan<byte> scan, Span<byte> result, byte[]? transparency)
    {
        int s = 0, d = 0;

        // If no transparency chunk is present, use SIMD shuffle for 24-bit RGB -> 32-bit RGBA
        if (!ForceScalar && (transparency is null || transparency.Length < 6))
        {
            ref byte scanRef = ref MemoryMarshal.GetReference(scan);
            ref byte resultRef = ref MemoryMarshal.GetReference(result);

            // Tier 1: AVX-512 (16 pixels = 48 bytes in, 64 bytes out)
            if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported && scan.Length >= 64)
            {
                var v256Mask = Vector256.Create(RgbShuffleMask, RgbShuffleMask);
                var v512Mask = Vector512.Create(v256Mask, v256Mask);
                var v256Alpha = Vector256.Create(AlphaOrMask, AlphaOrMask);
                var v512Alpha = Vector512.Create(v256Alpha, v256Alpha);

                while (s + 48 + 4 <= scan.Length && d + 64 <= result.Length)
                {
                    var in0 = Vector128.LoadUnsafe(ref scanRef, (nuint)s);
                    var in1 = Vector128.LoadUnsafe(ref scanRef, (nuint)(s + 12));
                    var in2 = Vector128.LoadUnsafe(ref scanRef, (nuint)(s + 24));
                    var in3 = Vector128.LoadUnsafe(ref scanRef, (nuint)(s + 36));

                    var vIn = Vector512.Create(Vector256.Create(in0, in1), Vector256.Create(in2, in3));
                    var vOut = Avx512BW.Shuffle(vIn, v512Mask) | v512Alpha;
                    vOut.StoreUnsafe(ref resultRef, (nuint)d);

                    s += 48;
                    d += 64;
                }
            }
            // Tier 2: AVX2 (8 pixels = 24 bytes in, 32 bytes out)
            else if (Vector256.IsHardwareAccelerated && Avx2.IsSupported && scan.Length >= 32)
            {
                var v256Mask = Vector256.Create(RgbShuffleMask, RgbShuffleMask);
                var v256Alpha = Vector256.Create(AlphaOrMask, AlphaOrMask);

                while (s + 24 + 4 <= scan.Length && d + 32 <= result.Length)
                {
                    var in0 = Vector128.LoadUnsafe(ref scanRef, (nuint)s);
                    var in1 = Vector128.LoadUnsafe(ref scanRef, (nuint)(s + 12));

                    var vIn = Vector256.Create(in0, in1);
                    var vOut = Avx2.Shuffle(vIn, v256Mask) | v256Alpha;
                    vOut.StoreUnsafe(ref resultRef, (nuint)d);

                    s += 24;
                    d += 32;
                }
            }
            // Tier 3: Vector128 (4 pixels = 12 bytes in, 16 bytes out)
            else if (Ssse3.IsSupported && scan.Length >= 16)
            {
                while (s + 16 <= scan.Length && d + 16 <= result.Length)
                {
                    var vIn = Vector128.LoadUnsafe(ref scanRef, (nuint)s);
                    var vOut = Ssse3.Shuffle(vIn, RgbShuffleMask) | AlphaOrMask;
                    vOut.StoreUnsafe(ref resultRef, (nuint)d);

                    s += 12;
                    d += 16;
                }
            }
        }

        // Tier 4 / Custom transparency Fallback
        for (; d < result.Length; s += 3, d += 4)
        {
            result[d] = scan[s];
            result[d + 1] = scan[s + 1];
            result[d + 2] = scan[s + 2];
            result[d + 3] = (transparency is { Length: >= 6 } &&
                             scan[s] == transparency[1] &&
                             scan[s + 1] == transparency[3] &&
                             scan[s + 2] == transparency[5]) ? (byte)0 : (byte)255;
        }
    }

    #endregion

    #region Hardware CRC-32

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ComputeCrc32(uint chunkType, ReadOnlySpan<byte> chunkData)
    {
        if (ForceScalar)
        {
            uint c = 0xffffffff;
            Span<byte> typeBuf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(typeBuf, chunkType);
            foreach (byte x in typeBuf)
            {
                c ^= x;
                for (int i = 0; i < 8; i++)
                    c = (c & 1) != 0 ? (c >> 1) ^ 0xedb88320u : c >> 1;
            }
            foreach (byte x in chunkData)
            {
                c ^= x;
                for (int i = 0; i < 8; i++)
                    c = (c & 1) != 0 ? (c >> 1) ^ 0xedb88320u : c >> 1;
            }
            return ~c;
        }

        var crc = new Crc32();
        Span<byte> tb = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tb, chunkType);
        crc.Append(tb);
        crc.Append(chunkData);
        return crc.GetCurrentHashAsUInt32();
    }

    #endregion

    #region Verification Suite

    public static void RunSelfVerification()
    {
        // 1. Cross-tier checksum verification across various payload sizes
        int[] testLengths = { 0, 1, 7, 8, 15, 16, 23, 24, 31, 32, 47, 48, 63, 64, 79, 80, 127, 128, 255, 256, 345, 1024, 4096 };
        foreach (int len in testLengths)
        {
            byte[] buf = new byte[len];
            for (int i = 0; i < len; i++) buf[i] = (byte)((i * 31 + 17) & 0xFF);

            // Compute via pure scalar
            int sOff = 0; ulong sState = 0, sAcc = 0; long sQ = 0;
            AccumulateScalar(buf, ref sOff, ref sState, ref sAcc, ref sQ);
            uint sCheck = (uint)(sAcc ^ (sAcc >> 32));
            uint sArc = (uint)(sAcc ^ (sAcc >> 32)) & 0x0fffffff;

            // Compute via Tier 3 (Vector128) + fallback
            int v128Off = 0; ulong v128State = 0, v128Acc = 0; long v128Q = 0;
            AccumulateVector128(buf, ref v128Off, ref v128State, ref v128Acc, ref v128Q);
            AccumulateScalar(buf, ref v128Off, ref v128State, ref v128Acc, ref v128Q);
            uint v128Check = (uint)(v128Acc ^ (v128Acc >> 32));

            // Compute via Tier 2 (AVX2) + fallback
            int v256Off = 0; ulong v256State = 0, v256Acc = 0; long v256Q = 0;
            AccumulateAvx2(buf, ref v256Off, ref v256State, ref v256Acc, ref v256Q);
            AccumulateScalar(buf, ref v256Off, ref v256State, ref v256Acc, ref v256Q);
            uint v256Check = (uint)(v256Acc ^ (v256Acc >> 32));

            // Compute via Tier 1 (AVX-512) + fallback
            int v512Off = 0; ulong v512State = 0, v512Acc = 0; long v512Q = 0;
            AccumulateAvx512(buf, ref v512Off, ref v512State, ref v512Acc, ref v512Q);
            AccumulateScalar(buf, ref v512Off, ref v512State, ref v512Acc, ref v512Q);
            uint v512Check = (uint)(v512Acc ^ (v512Acc >> 32));

            // Dispatch method
            uint dispCheck = CalculateStoredChecksum(buf);
            uint dispArc = CalculateArcKey(buf);

            if (sCheck != dispCheck)
                throw new Exception($"Checksum mismatch at len={len}: scalar={sCheck:x8}, disp={dispCheck:x8}");
            if (sArc != dispArc)
                throw new Exception($"ArcKey mismatch at len={len}: scalar={sArc:x8}, disp={dispArc:x8}");

            if (Vector128.IsHardwareAccelerated && Sse2.IsSupported && sCheck != v128Check)
                throw new Exception($"Vector128 checksum mismatch at len={len}: scalar={sCheck:x8}, v128={v128Check:x8}");
            if (Vector256.IsHardwareAccelerated && Avx2.IsSupported && sCheck != v256Check)
                throw new Exception($"AVX2 checksum mismatch at len={len}: scalar={sCheck:x8}, v256={v256Check:x8}");
            if (Vector512.IsHardwareAccelerated && Avx512F.IsSupported && Avx512BW.IsSupported && sCheck != v512Check)
                throw new Exception($"AVX-512 checksum mismatch at len={len}: scalar={sCheck:x8}, v512={v512Check:x8}");
        }

        // 2. CryptName verification against scalar
        foreach (int nameLen in new[] { 1, 15, 16, 31, 32, 63, 64, 100, 255 })
        {
            byte[] scalarName = new byte[nameLen];
            byte[] simdName = new byte[nameLen];
            for (int i = 0; i < nameLen; i++) scalarName[i] = simdName[i] = (byte)(i + 0x20);
            int testKey = 0x5a3c9e12 + nameLen;

            for (var k = 0; k < scalarName.Length; k++)
                scalarName[k] ^= unchecked((byte)(((k + 1) ^ testKey) + (k + 1)));

            CryptName(simdName, simdName.Length, testKey);
            if (!scalarName.AsSpan().SequenceEqual(simdName))
                throw new Exception($"SIMD CryptName verification mismatch at length={nameLen}");
        }

        // 3. FindBestPair verification
        int[] counts = new int[8192];
        int[] left = new int[8192];
        int[] right = new int[8192];
        for (int i = 0; i < 8192; i++) { counts[i] = 1; left[i] = i; right[i] = i + 1; }
        counts[500] = 50; counts[1200] = 100; counts[7000] = 100; // 1200 is first max
        if (!FindBestPair(counts, left, right, 2, out int bCount, out int bA, out int bB) || bCount != 100 || bA != 1200 || bB != 1201)
            throw new Exception($"SIMD FindBestPair mismatch: got count={bCount}, a={bA}, b={bB}");

        // 4. CRC32 verification against old scalar bitwise loop
        uint testType = 0x49444154; // IDAT
        byte[] testChunk = new byte[128];
        for (int i = 0; i < testChunk.Length; i++) testChunk[i] = (byte)i;
        uint c = 0xffffffff;
        using (var m = new MemoryStream())
        {
            Span<byte> tb = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(tb, testType);
            m.Write(tb);
            m.Write(testChunk);
            foreach (byte x in m.ToArray())
            {
                c ^= x;
                for (int i = 0; i < 8; i++)
                    c = (c & 1) != 0 ? (c >> 1) ^ 0xedb88320u : c >> 1;
            }
        }
        uint oldCrc = ~c;
        uint newCrc = ComputeCrc32(testType, testChunk);
        if (oldCrc != newCrc)
            throw new Exception($"SIMD CRC32 verification mismatch: old={oldCrc:x8}, new={newCrc:x8}");

        // 5. RGB24 to RGBA32 verification
        byte[] rgb = new byte[300]; // 100 pixels
        for (int i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i + 1);
        byte[] rgbaScalar = new byte[400];
        byte[] rgbaSimd = new byte[400];
        for (int i = 0; i < 100; i++)
        {
            rgbaScalar[i * 4] = rgb[i * 3];
            rgbaScalar[i * 4 + 1] = rgb[i * 3 + 1];
            rgbaScalar[i * 4 + 2] = rgb[i * 3 + 2];
            rgbaScalar[i * 4 + 3] = 255;
        }
        ConvertRgbToRgba(rgb, rgbaSimd, null);
        if (!rgbaScalar.AsSpan().SequenceEqual(rgbaSimd))
            throw new Exception("SIMD ConvertRgbToRgba verification mismatch");
    }

    #endregion
}
