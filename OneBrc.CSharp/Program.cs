using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics;
using System.Text;

[module: SkipLocalsInit]

namespace OneBrc.CSharp;

internal static unsafe class Program
{
    private const string DefaultInputPath = "measurements.txt";
    private const int FastRowReadableBytes = 112;

    public static int Main(string[] args)
    {
        var inputPath = args.Length > 0 ? args[0] : DefaultInputPath;
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Input file not found: {inputPath}");
            return 1;
        }

        using var input = new FileStream(
            inputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.SequentialScan);

        if (input.Length == 0)
        {
            Console.WriteLine("{}");
            return 0;
        }

        var output = Calculate(input);
        Console.WriteLine(output);
        return 0;
    }

    private static string Calculate(FileStream input)
    {
        var length = input.Length;
        using var mappedFile = MemoryMappedFile.CreateFromFile(
            input,
            mapName: null,
            capacity: 0,
            MemoryMappedFileAccess.Read,
            HandleInheritability.None,
            leaveOpen: true);
        using var accessor = mappedFile.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);

        byte* pointer = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);

        StationTable? results = null;
        try
        {
            var basePointer = pointer + accessor.PointerOffset;
            var ranges = CreateRanges(basePointer, length);
            results = ParseRangesAndMerge(basePointer, ranges);
            return Format(results);
        }
        finally
        {
            results?.Dispose();

            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private static Range[] CreateRanges(byte* basePointer, long length)
    {
        var workerCount = GetWorkerCount(length);
        var ranges = new Range[workerCount];

        for (var i = 0; i < workerCount; i++)
        {
            var start = i == 0 ? 0 : MoveToNextLine(basePointer, length * i / workerCount, length);
            var end = i == workerCount - 1 ? length : MoveToNextLine(basePointer, length * (i + 1) / workerCount, length);
            ranges[i] = new Range(start, end);
        }

        return ranges;
    }

    private static StationTable ParseRangesAndMerge(byte* basePointer, Range[] ranges)
    {
        if (ranges.Length == 1)
        {
            return ParseRange(basePointer + ranges[0].Start, basePointer + ranges[0].End);
        }

        var results = new StationTable();
        var partials = new StationTable?[ranges.Length];
        var threads = new Thread[ranges.Length];
        var started = 0;

        try
        {
            for (var i = 0; i < ranges.Length; i++)
            {
                var index = i;
                var thread = new Thread(() =>
                {
                    partials[index] = ParseRange(basePointer + ranges[index].Start, basePointer + ranges[index].End);
                });
                threads[index] = thread;
                thread.Start();
                started++;
            }

            for (var i = 0; i < started; i++)
            {
                threads[i].Join();
                var partial = partials[i];
                partial!.MergeInto(results);
                partial.Dispose();
                partials[i] = null;
            }

            return results;
        }
        catch
        {
            for (var i = 0; i < started; i++)
            {
                if (threads[i].IsAlive)
                {
                    threads[i].Join();
                }
            }

            for (var i = 0; i < partials.Length; i++)
            {
                partials[i]?.Dispose();
            }

            results.Dispose();
            throw;
        }
    }

    private static int GetWorkerCount(long length)
    {
        var maxUsefulWorkers = Math.Max(1, (int)((length + (1 << 20) - 1) >> 20));
        var processorCount = Environment.ProcessorCount;
        var workerCount = GetDefaultWorkerCount(length, processorCount);

        var configured = Environment.GetEnvironmentVariable("BRC_THREADS");
        if (configured is not null && int.TryParse(configured, out var configuredWorkerCount) && configuredWorkerCount > 0)
        {
            workerCount = configuredWorkerCount;
        }

        return Math.Min(workerCount, maxUsefulWorkers);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetDefaultWorkerCount(long length, int processorCount)
    {
        if (processorCount == 10)
        {
            if (length >= 1L << 30)
            {
                return 13;
            }

            if (length >= 64L << 20)
            {
                return 9;
            }
        }

        return processorCount;
    }

    private static long MoveToNextLine(byte* basePointer, long offset, long length)
    {
        while (offset < length && basePointer[offset] != (byte)'\n')
        {
            offset++;
        }

        return offset < length ? offset + 1 : length;
    }

    private static StationTable ParseRange(byte* start, byte* end)
    {
        var table = new StationTable();
        var length = end - start;
        if (length < 1 << 16)
        {
            ParseSingleCursor(table, start, end);
            return table;
        }

        var split1 = MovePointerToNextLine(start + (length / 3), end);
        var split2 = MovePointerToNextLine(start + (length * 2 / 3), end);
        if (split1 <= start || split1 >= end || split2 <= split1 || split2 >= end)
        {
            ParseSingleCursor(table, start, end);
            return table;
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

        return table;
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

    private static string Format(StationTable results)
    {
        var entries = results.ToArray();
        var formatted = new StationResult[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            ref readonly var entry = ref entries[i];
            formatted[i] = new StationResult(
                Encoding.UTF8.GetString(new ReadOnlySpan<byte>(entry.NamePointer, entry.NameLength)),
                entry.Min,
                entry.Max,
                entry.Sum,
                entry.Count);
        }

        Array.Sort(formatted, static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));

        var builder = new StringBuilder(formatted.Length * 32);
        builder.Append('{');

        var first = true;
        foreach (var entry in formatted)
        {
            if (!first)
            {
                builder.Append(", ");
            }

            first = false;
            builder.Append(entry.Name);
            builder.Append('=');
            AppendTenths(builder, entry.Min);
            builder.Append('/');
            AppendTenths(builder, DivideTowardPositive(entry.Sum, entry.Count));
            builder.Append('/');
            AppendTenths(builder, entry.Max);
        }

        builder.Append('}');
        return builder.ToString();
    }

    private static long DivideTowardPositive(long numerator, long denominator)
    {
        var quotient = numerator / denominator;
        var remainder = numerator % denominator;
        return remainder > 0 ? quotient + 1 : quotient;
    }

    private static void AppendTenths(StringBuilder builder, long tenths)
    {
        if (tenths < 0)
        {
            builder.Append('-');
            tenths = -tenths;
        }

        builder.Append(tenths / 10);
        builder.Append('.');
        builder.Append(tenths % 10);
    }

    private readonly record struct Range(long Start, long End);
}

internal readonly unsafe struct StationKey
{
    private const ulong HashMultiplier = 0x3D8930BCACE8B79DUL;

    private StationKey(int hash, ulong first, ulong last)
    {
        Hash = hash;
        First = first;
        Last = last;
    }

    public int Hash { get; }

    public ulong First { get; }

    public ulong Last { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StationKey Create(byte* name, int length)
    {
        var first = ReadUpToEightBytes(name, length);
        var last = length >= sizeof(ulong) ? Unsafe.ReadUnaligned<ulong>(name + length - sizeof(ulong)) : first;

        return Create(length, first, last);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StationKey CreateFast(byte* name, int length)
    {
        var first = Unsafe.ReadUnaligned<ulong>(name);
        return CreateFast(name, length, first);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StationKey CreateFast(byte* name, int length, ulong first)
    {
        if (length < sizeof(ulong))
        {
            first &= (1UL << (length * 8)) - 1;
        }

        var last = length >= sizeof(ulong) ? Unsafe.ReadUnaligned<ulong>(name + length - sizeof(ulong)) : first;
        return Create(length, first, last);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static StationKey Create(int length, ulong first, ulong last)
    {
        uint hash;
        if (Crc32.Arm64.IsSupported)
        {
            hash = Crc32.Arm64.ComputeCrc32C((uint)length, first);
            hash = Crc32.Arm64.ComputeCrc32C(hash, last);
        }
        else
        {
            var mixed = (first * HashMultiplier) ^ BitOperations.RotateLeft(last, 42) ^ (uint)length;
            mixed ^= mixed >> 32;
            hash = (uint)mixed;
        }

        return new StationKey(unchecked((int)hash), first, last);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadUpToEightBytes(byte* name, int length)
    {
        if (length >= sizeof(ulong))
        {
            return Unsafe.ReadUnaligned<ulong>(name);
        }

        ulong value = 0;
        for (var i = 0; i < length; i++)
        {
            value |= (ulong)name[i] << (i * 8);
        }

        return value;
    }
}

internal unsafe sealed class StationTable : IDisposable
{
    private const int InitialCapacity = 1 << 15;
    private const int CapacityMask = InitialCapacity - 1;

    private StationEntry* _entries = AllocateEntries(InitialCapacity);
    private int _count;

    public void Dispose()
    {
        var entries = _entries;
        if (entries == null)
        {
            return;
        }

        _entries = null;
        NativeMemory.AlignedFree(entries);
    }

    public void MergeInto(StationTable results)
    {
        var entries = _entries;
        for (var i = 0; i < InitialCapacity; i++)
        {
            ref readonly var entry = ref entries[i];
            if (entry.NameLength != 0)
            {
                results.AddOrMerge(in entry);
            }
        }
    }

    public StationEntry[] ToArray()
    {
        var result = new StationEntry[_count];
        var index = 0;
        var entries = _entries;

        for (var i = 0; i < InitialCapacity; i++)
        {
            ref readonly var entry = ref entries[i];
            if (entry.NameLength != 0)
            {
                result[index++] = entry;
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddOrUpdate(byte* name, int nameLength, StationKey key, int temperature)
    {
        while (true)
        {
            var entries = _entries;
            var index = key.Hash & CapacityMask;

            while (true)
            {
                ref var entry = ref entries[index];

                if (entry.NameLength == 0)
                {
                    entry = new StationEntry(name, nameLength, key, temperature);
                    _count++;
                    return;
                }

                if (entry.NameLength == nameLength &&
                    entry.First == key.First &&
                    entry.Last == key.Last &&
                    (nameLength <= 2 * sizeof(ulong) ||
                     new ReadOnlySpan<byte>(name, nameLength).SequenceEqual(new ReadOnlySpan<byte>(entry.NamePointer, nameLength))))
                {
                    entry.Add(temperature);
                    return;
                }

                index = (index + 1) & CapacityMask;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddOrMerge(in StationEntry source)
    {
        while (true)
        {
            var entries = _entries;
            var index = source.Hash & CapacityMask;

            while (true)
            {
                ref var entry = ref entries[index];

                if (entry.NameLength == 0)
                {
                    entry = source;
                    _count++;
                    return;
                }

                if (entry.NameLength == source.NameLength &&
                    entry.First == source.First &&
                    entry.Last == source.Last &&
                    (source.NameLength <= 2 * sizeof(ulong) ||
                     new ReadOnlySpan<byte>(source.NamePointer, source.NameLength).SequenceEqual(new ReadOnlySpan<byte>(entry.NamePointer, source.NameLength))))
                {
                    entry.Add(in source);
                    return;
                }

                index = (index + 1) & CapacityMask;
            }
        }
    }

    private static StationEntry* AllocateEntries(int capacity)
    {
        var byteCount = (nuint)(capacity * sizeof(StationEntry));
        var entries = (StationEntry*)NativeMemory.AlignedAlloc(byteCount, 64);
        if (entries is null)
        {
            throw new OutOfMemoryException();
        }

        NativeMemory.Clear(entries, byteCount);
        return entries;
    }
}

internal unsafe struct StationEntry
{
    public StationEntry(byte* name, int nameLength, StationKey key, int temperature)
    {
        NamePointer = name;
        NameLength = nameLength;
        Hash = key.Hash;
        First = key.First;
        Last = key.Last;
        Min = temperature;
        Max = temperature;
        Sum = temperature;
        Count = 1;
    }

    public byte* NamePointer { get; }

    public int NameLength { get; }

    public int Hash { get; }

    public ulong First { get; }

    public ulong Last { get; }

    public int Min { get; private set; }

    public int Max { get; private set; }

    public long Sum { get; private set; }

    public int Count { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int temperature)
    {
        if (temperature < Min)
        {
            Min = temperature;
        }

        if (temperature > Max)
        {
            Max = temperature;
        }

        Sum += temperature;
        Count++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(in StationEntry entry)
    {
        if (entry.Min < Min)
        {
            Min = entry.Min;
        }

        if (entry.Max > Max)
        {
            Max = entry.Max;
        }

        Sum += entry.Sum;
        Count += entry.Count;
    }
}

internal readonly struct StationResult
{
    public StationResult(string name, int min, int max, long sum, long count)
    {
        Name = name;
        Min = min;
        Max = max;
        Sum = sum;
        Count = count;
    }

    public string Name { get; }

    public int Min { get; }

    public int Max { get; }

    public long Sum { get; }

    public long Count { get; }
}
