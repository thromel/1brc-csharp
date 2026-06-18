using System.Runtime.CompilerServices;

namespace OneBrc.CSharp;

internal static unsafe class RangePartitioner
{
    public static InputRange[] CreateRanges(byte* basePointer, long length)
    {
        var workerCount = GetWorkerCount(length);
        var ranges = new InputRange[workerCount];

        for (var i = 0; i < workerCount; i++)
        {
            var start = i == 0 ? 0 : MoveToNextLine(basePointer, length * i / workerCount, length);
            var end = i == workerCount - 1 ? length : MoveToNextLine(basePointer, length * (i + 1) / workerCount, length);
            ranges[i] = new InputRange(start, end);
        }

        return ranges;
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
}
