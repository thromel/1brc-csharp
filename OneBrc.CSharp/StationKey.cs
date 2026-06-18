using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.Arm;

namespace OneBrc.CSharp;

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
