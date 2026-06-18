using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace OneBrc.CSharp;

internal static unsafe class MeasurementParser
{
    private const int FastRowReadableBytes = 112;

    public static StationTable ParseRange(byte* start, byte* end)
    {
        var table = new StationTable();
        ParseRangeInto(table, start, end);
        return table;
    }

    public static void ParseRangeInto(StationTable table, byte* start, byte* end)
    {
        var length = end - start;
        if (length < 1 << 16)
        {
            ParseSingleCursor(table, start, end);
            return;
        }

        var split1 = MovePointerToNextLine(start + (length / 3), end);
        var split2 = MovePointerToNextLine(start + (length * 2 / 3), end);
        if (split1 <= start || split1 >= end || split2 <= split1 || split2 >= end)
        {
            ParseSingleCursor(table, start, end);
            return;
        }

        var cursor0 = start;
        var cursor1 = split1;
        var cursor2 = split2;

        while (split1 - cursor0 >= FastRowReadableBytes &&
               split2 - cursor1 >= FastRowReadableBytes &&
               end - cursor2 >= FastRowReadableBytes)
        {
            ParseRowFast(table, ref cursor0);
            ParseRowFast(table, ref cursor1);
            ParseRowFast(table, ref cursor2);
        }

        while (cursor0 < split1 && cursor1 < split2 && cursor2 < end)
        {
            ParseRow(table, ref cursor0, split1);
            ParseRow(table, ref cursor1, split2);
            ParseRow(table, ref cursor2, end);
        }

        ParseSingleCursor(table, cursor0, split1);
        ParseSingleCursor(table, cursor1, split2);
        ParseSingleCursor(table, cursor2, end);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* MovePointerToNextLine(byte* cursor, byte* end)
    {
        while (cursor < end && *cursor != (byte)'\n')
        {
            cursor++;
        }

        return cursor < end ? cursor + 1 : end;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ParseSingleCursor(StationTable table, byte* start, byte* end)
    {
        var cursor = start;
        while (end - cursor >= FastRowReadableBytes)
        {
            ParseRowFast(table, ref cursor);
        }

        while (cursor < end)
        {
            ParseRow(table, ref cursor, end);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ParseRowFast(StationTable table, ref byte* cursor)
    {
        var nameStart = cursor;
        var nameLength = FindSemicolonFast(nameStart, out var firstWord);
        cursor += nameLength + 1;

        var temperature = ParseTemperatureWord(ref cursor);
        table.AddOrUpdate(nameStart, nameLength, StationKey.CreateFast(nameStart, nameLength, firstWord), temperature);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ParseRow(StationTable table, ref byte* cursor, byte* end)
    {
        var nameStart = cursor;
        var nameLength = FindSemicolon(nameStart, end);
        cursor += nameLength + 1;

        var temperature = ParseTemperature(ref cursor, end);
        table.AddOrUpdate(nameStart, nameLength, StationKey.Create(nameStart, nameLength), temperature);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindSemicolonFast(byte* start, out ulong firstWord)
    {
        const ulong SemicolonBytes = 0x3B3B3B3B3B3B3B3BUL;
        const ulong LowBits = 0x0101010101010101UL;
        const ulong HighBits = 0x8080808080808080UL;

        firstWord = Unsafe.ReadUnaligned<ulong>(start);
        var first = firstWord ^ SemicolonBytes;
        var firstMask = (first - LowBits) & ~first & HighBits;
        if (firstMask != 0)
        {
            return BitOperations.TrailingZeroCount(firstMask) >> 3;
        }

        var second = Unsafe.ReadUnaligned<ulong>(start + 8) ^ SemicolonBytes;
        var secondMask = (second - LowBits) & ~second & HighBits;
        if (secondMask != 0)
        {
            return 8 + (BitOperations.TrailingZeroCount(secondMask) >> 3);
        }

        var third = Unsafe.ReadUnaligned<ulong>(start + 16) ^ SemicolonBytes;
        var thirdMask = (third - LowBits) & ~third & HighBits;
        if (thirdMask != 0)
        {
            return 16 + (BitOperations.TrailingZeroCount(thirdMask) >> 3);
        }

        return 24 + FindSemicolonFastTailAfter24(start + 24);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int FindSemicolonFastTailAfter24(byte* start)
    {
        const ulong SemicolonBytes = 0x3B3B3B3B3B3B3B3BUL;
        const ulong LowBits = 0x0101010101010101UL;
        const ulong HighBits = 0x8080808080808080UL;

        var word = Unsafe.ReadUnaligned<ulong>(start) ^ SemicolonBytes;
        var mask = (word - LowBits) & ~word & HighBits;
        if (mask != 0)
        {
            return BitOperations.TrailingZeroCount(mask) >> 3;
        }

        word = Unsafe.ReadUnaligned<ulong>(start + 8) ^ SemicolonBytes;
        mask = (word - LowBits) & ~word & HighBits;
        if (mask != 0)
        {
            return 8 + (BitOperations.TrailingZeroCount(mask) >> 3);
        }

        word = Unsafe.ReadUnaligned<ulong>(start + 16) ^ SemicolonBytes;
        mask = (word - LowBits) & ~word & HighBits;
        if (mask != 0)
        {
            return 16 + (BitOperations.TrailingZeroCount(mask) >> 3);
        }

        word = Unsafe.ReadUnaligned<ulong>(start + 24) ^ SemicolonBytes;
        mask = (word - LowBits) & ~word & HighBits;
        if (mask != 0)
        {
            return 24 + (BitOperations.TrailingZeroCount(mask) >> 3);
        }

        word = Unsafe.ReadUnaligned<ulong>(start + 32) ^ SemicolonBytes;
        mask = (word - LowBits) & ~word & HighBits;
        if (mask != 0)
        {
            return 32 + (BitOperations.TrailingZeroCount(mask) >> 3);
        }

        word = Unsafe.ReadUnaligned<ulong>(start + 40) ^ SemicolonBytes;
        mask = (word - LowBits) & ~word & HighBits;
        if (mask != 0)
        {
            return 40 + (BitOperations.TrailingZeroCount(mask) >> 3);
        }

        word = Unsafe.ReadUnaligned<ulong>(start + 48) ^ SemicolonBytes;
        mask = (word - LowBits) & ~word & HighBits;
        if (mask != 0)
        {
            return 48 + (BitOperations.TrailingZeroCount(mask) >> 3);
        }

        word = Unsafe.ReadUnaligned<ulong>(start + 56) ^ SemicolonBytes;
        mask = (word - LowBits) & ~word & HighBits;
        if (mask != 0)
        {
            return 56 + (BitOperations.TrailingZeroCount(mask) >> 3);
        }

        word = Unsafe.ReadUnaligned<ulong>(start + 64) ^ SemicolonBytes;
        mask = (word - LowBits) & ~word & HighBits;
        if (mask != 0)
        {
            return 64 + (BitOperations.TrailingZeroCount(mask) >> 3);
        }

        word = Unsafe.ReadUnaligned<ulong>(start + 72) ^ SemicolonBytes;
        mask = (word - LowBits) & ~word & HighBits;
        if (mask != 0)
        {
            return 72 + (BitOperations.TrailingZeroCount(mask) >> 3);
        }

        return 80 + FindSemicolonUnbounded(start + 80);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int FindSemicolonUnbounded(byte* start)
    {
        var cursor = start;
        while (*cursor != (byte)';')
        {
            cursor++;
        }

        return checked((int)(cursor - start));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindSemicolon(byte* start, byte* end)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            var needle = Vector128.Create((byte)';');
            var cursor = start;
            var vectorEnd = end - Vector128<byte>.Count;

            while (cursor <= vectorEnd)
            {
                var matches = Vector128.Equals(Vector128.LoadUnsafe(ref *cursor), needle);
                var mask = matches.ExtractMostSignificantBits();
                if (mask != 0)
                {
                    return checked((int)(cursor - start) + BitOperations.TrailingZeroCount(mask));
                }

                cursor += Vector128<byte>.Count;
            }
        }

        var scalar = start;
        while (*scalar != (byte)';')
        {
            scalar++;
        }

        return checked((int)(scalar - start));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ParseTemperature(ref byte* cursor, byte* end)
    {
        if (cursor + sizeof(long) <= end)
        {
            return ParseTemperatureWord(ref cursor);
        }

        {
            var sign = 1;
            if (*cursor == (byte)'-')
            {
                sign = -1;
                cursor++;
            }

            var value = *cursor - (byte)'0';
            cursor++;

            if (*cursor != (byte)'.')
            {
                value = (value * 10) + (*cursor - (byte)'0');
                cursor++;
            }

            cursor++;
            value = (value * 10) + (*cursor - (byte)'0');
            cursor++;

            if (cursor < end && *cursor == (byte)'\n')
            {
                cursor++;
            }

            return sign * value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ParseTemperatureWord(ref byte* cursor)
    {
        const long DotBits = 0x10101000;
        const long Multiplier = (100 * 0x1000000 + 10 * 0x10000 + 1);

        var word = Unsafe.ReadUnaligned<long>(cursor);
        var inverted = ~word;
        var dot = BitOperations.TrailingZeroCount(inverted & DotBits);
        var signed = (inverted << 59) >> 63;
        var mask = ~(signed & 0xFF);
        var digits = ((word & mask) << (28 - dot)) & 0x0F000F0F00L;
        var absolute = ((digits * Multiplier) >>> 32) & 0x3FF;

        cursor += (dot >> 3) + 3;
        return (int)((absolute ^ signed) - signed);
    }
}
