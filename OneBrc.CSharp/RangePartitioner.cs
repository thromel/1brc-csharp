namespace OneBrc.CSharp;

internal static unsafe class RangePartitioner
{
    public static InputRange[] CreateRanges(byte* basePointer, long length)
    {
        var workerCount = RuntimeOptions.GetMemoryMappedWorkerCount(length);
        var ranges = new InputRange[workerCount];

        for (var i = 0; i < workerCount; i++)
        {
            var start = i == 0 ? 0 : MoveToNextLine(basePointer, length * i / workerCount, length);
            var end = i == workerCount - 1 ? length : MoveToNextLine(basePointer, length * (i + 1) / workerCount, length);
            ranges[i] = new InputRange(start, end);
        }

        return ranges;
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
