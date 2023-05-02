// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;

#nullable enable
#pragma warning disable CS8500 // This takes the address of, gets the size of, or declares a pointer to a managed type

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;

internal static class StringUtilities
{
    private static readonly SpanAction<char, (string? str, char separator, uint number)> s_populateSpanWithHexSuffix = PopulateSpanWithHexSuffix;

    // Null checks must be done independently of this method (if required)
    public static unsafe string GetAsciiOrUTF8String(this ReadOnlySpan<byte> span, Encoding defaultEncoding)
    {
        if (span.IsEmpty)
        {
            return string.Empty;
        }

        var resultString = string.Create(span.Length, (IntPtr)(&span), (destination, spanPtr) =>
        {
            if (Ascii.ToUtf16(*(ReadOnlySpan<byte>*)spanPtr, destination, out _) != OperationStatus.Done)
            {
                // Mark resultString for UTF-8 encoding
                destination[0] = '\0';
            }
        });

        // If resultString is marked, perform UTF-8 encoding
        if (resultString[0] == '\0')
        {
            try
            {
                resultString = defaultEncoding.GetString(span);
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidOperationException();
            }
        }

        return resultString;
    }

    // Null checks must be done independently of this method (if required)
    public static unsafe string GetAsciiString(this ReadOnlySpan<byte> span)
    {
        return string.Create(span.Length, (IntPtr)(&span), (destination, spanPtr) =>
        {
            if (Ascii.ToUtf16(*(ReadOnlySpan<byte>*)spanPtr, destination, out _) != OperationStatus.Done)
            {
                throw new InvalidOperationException();
            }
        });
    }

    // Null checks must be done independently of this method (if required)
    public static unsafe string GetLatin1String(this ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty)
        {
            return string.Empty;
        }

        return Encoding.Latin1.GetString(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool BytesOrdinalEqualsStringAndAscii(string previousValue, ReadOnlySpan<byte> newValue)
    {
        // previousValue is a previously materialized string which *must* have already passed validation.
        Debug.Assert(IsValidHeaderString(previousValue));

        // Ascii bytes => Utf-16 chars will be the same length.
        // The caller should have already compared lengths before calling this method.
        // However; let's double check, and early exit if they are not the same length.
        if (previousValue.Length != newValue.Length)
        {
            // Lengths don't match, so there cannot be an exact ascii conversion between the two.
            goto NotEqual;
        }

        // Note: Pointer comparison is unsigned, so we use the compare pattern (offset + length <= count)
        // rather than (offset <= count - length) which we'd do with signed comparison to avoid overflow.
        // This isn't problematic as we know the maximum length is max string length (from test above)
        // which is a signed value so half the size of the unsigned pointer value so we can safely add
        // a Vector<byte>.Count to it without overflowing.
        var count = (nint)newValue.Length;
        var offset = (nint)0;

        // Get references to the first byte in the span, and the first char in the string.
        ref var bytes = ref MemoryMarshal.GetReference(newValue);
        ref var str = ref MemoryMarshal.GetReference(previousValue.AsSpan());

        do
        {
            // If Vector not-accelerated or remaining less than vector size
            if (!Vector.IsHardwareAccelerated || (offset + Vector<byte>.Count) > count)
            {
                if (IntPtr.Size == 8) // Use Intrinsic switch for branch elimination
                {
                    // 64-bit: Loop longs by default
                    while ((offset + sizeof(long)) <= count)
                    {
                        if (!WidenFourAsciiBytesToUtf16AndCompareToChars(
                                ref Unsafe.Add(ref str, offset),
                                Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref bytes, offset))) ||
                            !WidenFourAsciiBytesToUtf16AndCompareToChars(
                                ref Unsafe.Add(ref str, offset + 4),
                                Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref bytes, offset + 4))))
                        {
                            goto NotEqual;
                        }

                        offset += sizeof(long);
                    }
                    if ((offset + sizeof(int)) <= count)
                    {
                        if (!WidenFourAsciiBytesToUtf16AndCompareToChars(
                            ref Unsafe.Add(ref str, offset),
                            Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref bytes, offset))))
                        {
                            goto NotEqual;
                        }

                        offset += sizeof(int);
                    }
                }
                else
                {
                    // 32-bit: Loop ints by default
                    while ((offset + sizeof(int)) <= count)
                    {
                        if (!WidenFourAsciiBytesToUtf16AndCompareToChars(
                            ref Unsafe.Add(ref str, offset),
                            Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref bytes, offset))))
                        {
                            goto NotEqual;
                        }

                        offset += sizeof(int);
                    }
                }
                if ((offset + sizeof(short)) <= count)
                {
                    if (!WidenTwoAsciiBytesToUtf16AndCompareToChars(
                        ref Unsafe.Add(ref str, offset),
                        Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref bytes, offset))))
                    {
                        goto NotEqual;
                    }

                    offset += sizeof(short);
                }
                if (offset < count)
                {
                    var ch = (char)Unsafe.Add(ref bytes, offset);
                    if (((ch & 0x80) != 0) || Unsafe.Add(ref str, offset) != ch)
                    {
                        goto NotEqual;
                    }
                }

                // End of input reached, there are no inequalities via widening; so the input bytes are both ascii
                // and a match to the string if it was converted via Encoding.ASCII.GetString(...)
                return true;
            }

            // Create a comparision vector for all bits being equal
            var AllTrue = new Vector<short>(-1);
            // do/while as entry condition already checked, remaining length must be Vector<byte>.Count or larger.
            do
            {
                // Read a Vector length from the input as bytes
                var vector = Unsafe.ReadUnaligned<Vector<sbyte>>(ref Unsafe.Add(ref bytes, offset));
                if (!CheckBytesInAsciiRange(vector))
                {
                    goto NotEqual;
                }
                // Widen the bytes directly to chars (ushort) as if they were ascii.
                // As widening doubles the size we get two vectors back.
                Vector.Widen(vector, out var vector0, out var vector1);
                // Read two char vectors from the string to perform the match.
                var compare0 = Unsafe.ReadUnaligned<Vector<short>>(ref Unsafe.As<char, byte>(ref Unsafe.Add(ref str, offset)));
                var compare1 = Unsafe.ReadUnaligned<Vector<short>>(ref Unsafe.As<char, byte>(ref Unsafe.Add(ref str, offset + Vector<ushort>.Count)));

                // If the string is not ascii, then the widened bytes cannot match
                // as each widened byte element as chars will be in the range 0-255
                // so cannot match any higher unicode values.

                // Compare to our all bits true comparision vector
                if (!AllTrue.Equals(
                    // BitwiseAnd the two equals together
                    Vector.BitwiseAnd(
                        // Check equality for the two widened vectors
                        Vector.Equals(compare0, vector0),
                        Vector.Equals(compare1, vector1))))
                {
                    goto NotEqual;
                }

                offset += Vector<byte>.Count;
            } while ((offset + Vector<byte>.Count) <= count);

            // Vector path done, loop back to do non-Vector
            // If is a exact multiple of vector size, bail now
        } while (offset < count);

        // If we get here (input is exactly a multiple of Vector length) then there are no inequalities via widening;
        // so the input bytes are both ascii and a match to the string if it was converted via Encoding.ASCII.GetString(...)
        return true;
    NotEqual:
        return false;
    }

    /// <summary>
    /// Given a DWORD which represents a buffer of 4 bytes, widens the buffer into 4 WORDs and
    /// compares them to the WORD buffer with machine endianness.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool WidenFourAsciiBytesToUtf16AndCompareToChars(ref char charStart, uint value)
    {
        if (!AllBytesInUInt32AreAscii(value))
        {
            return false;
        }

        // BMI2 could be used, but this variant is faster on both Intel and AMD.
        if (Sse2.X64.IsSupported)
        {
            var vecNarrow = Sse2.ConvertScalarToVector128UInt32(value).AsByte();
            var vecWide = Sse2.UnpackLow(vecNarrow, Vector128<byte>.Zero).AsUInt64();
            return Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<char, byte>(ref charStart)) ==
                Sse2.X64.ConvertToUInt64(vecWide);
        }
        else
        {
            if (BitConverter.IsLittleEndian)
            {
                return charStart == (char)(byte)value &&
                    Unsafe.Add(ref charStart, 1) == (char)(byte)(value >> 8) &&
                    Unsafe.Add(ref charStart, 2) == (char)(byte)(value >> 16) &&
                    Unsafe.Add(ref charStart, 3) == (char)(value >> 24);
            }
            else
            {
                return Unsafe.Add(ref charStart, 3) == (char)(byte)value &&
                    Unsafe.Add(ref charStart, 2) == (char)(byte)(value >> 8) &&
                    Unsafe.Add(ref charStart, 1) == (char)(byte)(value >> 16) &&
                    charStart == (char)(value >> 24);
            }
        }
    }

    /// <summary>
    /// Given a WORD which represents a buffer of 2 bytes, widens the buffer into 2 WORDs and
    /// compares them to the WORD buffer with machine endianness.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool WidenTwoAsciiBytesToUtf16AndCompareToChars(ref char charStart, ushort value)
    {
        if (!AllBytesInUInt16AreAscii(value))
        {
            return false;
        }

        // BMI2 could be used, but this variant is faster on both Intel and AMD.
        if (Sse2.IsSupported)
        {
            var vecNarrow = Sse2.ConvertScalarToVector128UInt32(value).AsByte();
            var vecWide = Sse2.UnpackLow(vecNarrow, Vector128<byte>.Zero).AsUInt32();
            return Unsafe.ReadUnaligned<uint>(ref Unsafe.As<char, byte>(ref charStart)) ==
                Sse2.ConvertToUInt32(vecWide);
        }
        else
        {
            if (BitConverter.IsLittleEndian)
            {
                return charStart == (char)(byte)value &&
                    Unsafe.Add(ref charStart, 1) == (char)(byte)(value >> 8);
            }
            else
            {
                return Unsafe.Add(ref charStart, 1) == (char)(byte)value &&
                    charStart == (char)(byte)(value >> 8);
            }
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> iff all bytes in <paramref name="value"/> are ASCII.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AllBytesInUInt32AreAscii(uint value)
    {
        return ((value & 0x80808080u) == 0);
    }

    /// <summary>
    /// Returns <see langword="true"/> iff all bytes in <paramref name="value"/> are ASCII.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AllBytesInUInt16AreAscii(ushort value)
    {
        return ((value & 0x8080u) == 0);
    }

    private static bool IsValidHeaderString(string value)
    {
        // Method for Debug.Assert to ensure BytesOrdinalEqualsStringAndAscii
        // is not called with an unvalidated string comparitor.
        try
        {
            if (value is null)
            {
                return false;
            }
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetByteCount(value);
            return !value.Contains('\0');
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// A faster version of String.Concat(<paramref name="str"/>, <paramref name="separator"/>, <paramref name="number"/>.ToString("X8"))
    /// </summary>
    /// <param name="str"></param>
    /// <param name="separator"></param>
    /// <param name="number"></param>
    /// <returns></returns>
    public static string ConcatAsHexSuffix(string str, char separator, uint number)
    {
        var length = 1 + 8;
        if (str != null)
        {
            length += str.Length;
        }

        return string.Create(length, (str, separator, number), s_populateSpanWithHexSuffix);
    }

    private static void PopulateSpanWithHexSuffix(Span<char> buffer, (string? str, char separator, uint number) tuple)
    {
        var (tupleStr, tupleSeparator, tupleNumber) = tuple;

        var i = 0;
        if (tupleStr != null)
        {
            tupleStr.AsSpan().CopyTo(buffer);
            i = tupleStr.Length;
        }

        buffer[i] = tupleSeparator;
        i++;

        if (Ssse3.IsSupported)
        {
            // The constant inline vectors are read from the data section without any additional
            // moves. See https://github.com/dotnet/runtime/issues/44115 Case 1.1 for further details.

            var lowNibbles = Ssse3.Shuffle(Vector128.CreateScalarUnsafe(tupleNumber).AsByte(), Vector128.Create(
                0xF, 0xF, 3, 0xF,
                0xF, 0xF, 2, 0xF,
                0xF, 0xF, 1, 0xF,
                0xF, 0xF, 0, 0xF
            ).AsByte());

            var highNibbles = Sse2.ShiftRightLogical(Sse2.ShiftRightLogical128BitLane(lowNibbles, 2).AsInt32(), 4).AsByte();
            var indices = Sse2.And(Sse2.Or(lowNibbles, highNibbles), Vector128.Create((byte)0xF));

            // Lookup the hex values at the positions of the indices
            var hex = Ssse3.Shuffle(Vector128.Create(
                (byte)'0', (byte)'1', (byte)'2', (byte)'3',
                (byte)'4', (byte)'5', (byte)'6', (byte)'7',
                (byte)'8', (byte)'9', (byte)'A', (byte)'B',
                (byte)'C', (byte)'D', (byte)'E', (byte)'F'
            ), indices);

            // The high bytes (0x00) of the chars have also been converted to ascii hex '0', so clear them out.
            hex = Sse2.And(hex, Vector128.Create((ushort)0xFF).AsByte());

            // This generates much more efficient asm than fixing the buffer and using
            // Sse2.Store((byte*)(p + i), chars.AsByte());
            Unsafe.WriteUnaligned(
                ref Unsafe.As<char, byte>(
                    ref Unsafe.Add(ref MemoryMarshal.GetReference(buffer), i)),
                hex);
        }
        else
        {
            var number = (int)tupleNumber;
            // Slice the buffer so we can use constant offsets in a backwards order
            // and the highest index [7] will eliminate the bounds checks for all the lower indicies.
            buffer = buffer.Slice(i);

            // This must be explicity typed as ReadOnlySpan<byte>
            // This then becomes a non-allocating mapping to the data section of the assembly.
            // If it is a var, Span<byte> or byte[], it allocates the byte array per call.
            ReadOnlySpan<byte> hexEncodeMap = "0123456789ABCDEF"u8;
            // Note: this only works with byte due to endian ambiguity for other types,
            // hence the later (char) casts

            buffer[7] = (char)hexEncodeMap[number & 0xF];
            buffer[6] = (char)hexEncodeMap[(number >> 4) & 0xF];
            buffer[5] = (char)hexEncodeMap[(number >> 8) & 0xF];
            buffer[4] = (char)hexEncodeMap[(number >> 12) & 0xF];
            buffer[3] = (char)hexEncodeMap[(number >> 16) & 0xF];
            buffer[2] = (char)hexEncodeMap[(number >> 20) & 0xF];
            buffer[1] = (char)hexEncodeMap[(number >> 24) & 0xF];
            buffer[0] = (char)hexEncodeMap[(number >> 28) & 0xF];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] // Needs a push
    private static bool CheckBytesInAsciiRange(Vector<sbyte> check)
    {
        // Vectorized byte range check, signed byte > 0 for 1-127
        return Vector.GreaterThanAll(check, Vector<sbyte>.Zero);
    }
}
