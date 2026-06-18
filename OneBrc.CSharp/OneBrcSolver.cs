using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace OneBrc.CSharp;

internal static unsafe class OneBrcSolver
{
    public static string Calculate(FileStream input)
    {
        ArgumentNullException.ThrowIfNull(input);

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
}
