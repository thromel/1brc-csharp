using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace OneBrc.CSharp;

internal static unsafe class OneBrcSolver
{
    private const long PReadAutoThresholdBytes = 8L << 30;

    public static string Calculate(FileStream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var length = input.Length;

        if (UsePRead(length))
        {
            return PReadSolver.Calculate(input);
        }

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
            var ranges = RangePartitioner.CreateRanges(basePointer, length);
            results = WorkerScheduler.ParseRangesAndMerge(basePointer, ranges);
            return ResultFormatter.Format(results);
        }
        finally
        {
            results?.Dispose();

            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private static bool UsePRead(long length)
    {
        var io = Environment.GetEnvironmentVariable("BRC_IO");
        if (string.Equals(io, "pread", StringComparison.OrdinalIgnoreCase))
        {
            EnsurePReadSupported();
            return true;
        }

        if (string.Equals(io, "mmap", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return OperatingSystem.IsMacOS() && length >= PReadAutoThresholdBytes;
    }

    private static void EnsurePReadSupported()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("BRC_IO=pread is currently implemented for macOS.");
        }
    }
}
