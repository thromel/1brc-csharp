using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OneBrc.CSharp;

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
