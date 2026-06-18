using System.Runtime.CompilerServices;

namespace OneBrc.CSharp;

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

    public StationEntry(byte* name, in StationEntry source)
    {
        NamePointer = name;
        NameLength = source.NameLength;
        Hash = source.Hash;
        First = source.First;
        Last = source.Last;
        Min = source.Min;
        Max = source.Max;
        Sum = source.Sum;
        Count = source.Count;
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
