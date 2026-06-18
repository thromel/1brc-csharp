using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OneBrc.CSharp;

// mmap tables can point directly into the mapped file; pread tables copy unique
// station names because their reusable chunk buffers are overwritten.
internal unsafe sealed class StationTable : IDisposable
{
    private const int InitialCapacity = 1 << 15;
    private const int CapacityMask = InitialCapacity - 1;
    private const int NameArenaBlockSize = 1 << 20;

    private StationEntry* _entries = AllocateEntries(InitialCapacity);
    private readonly bool _copyNames;
    private readonly List<IntPtr>? _nameBlocks;
    private byte* _nameCursor;
    private byte* _nameEnd;
    private int _count;

    public StationTable(bool copyNames = false)
    {
        _copyNames = copyNames;
        _nameBlocks = copyNames ? new List<IntPtr>() : null;
    }

    public void Dispose()
    {
        var entries = _entries;
        if (entries != null)
        {
            _entries = null;
            NativeMemory.AlignedFree(entries);
        }

        if (_nameBlocks is null)
        {
            return;
        }

        for (var i = 0; i < _nameBlocks.Count; i++)
        {
            NativeMemory.AlignedFree((void*)_nameBlocks[i]);
        }

        _nameBlocks.Clear();
        _nameCursor = null;
        _nameEnd = null;
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
                    entry = new StationEntry(StoreName(name, nameLength), nameLength, key, temperature);
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
                    entry = new StationEntry(StoreName(source.NamePointer, source.NameLength), in source);
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

    private byte* StoreName(byte* name, int nameLength)
    {
        if (!_copyNames)
        {
            return name;
        }

        if (_nameCursor == null || _nameEnd - _nameCursor < nameLength)
        {
            AllocateNameBlock(nameLength);
        }

        var stored = _nameCursor;
        Buffer.MemoryCopy(name, stored, nameLength, nameLength);
        _nameCursor += nameLength;
        return stored;
    }

    private void AllocateNameBlock(int minimumLength)
    {
        var blockSize = Math.Max(NameArenaBlockSize, AlignUp(minimumLength, 64));
        var block = (byte*)NativeMemory.AlignedAlloc((nuint)blockSize, 64);
        if (block is null)
        {
            throw new OutOfMemoryException();
        }

        _nameBlocks!.Add((IntPtr)block);
        _nameCursor = block;
        _nameEnd = block + blockSize;
    }

    private static int AlignUp(int value, int alignment)
    {
        return checked((value + alignment - 1) & -alignment);
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
