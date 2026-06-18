namespace OneBrc.CSharp;

internal static unsafe class PReadRangePartitioner
{
    private const int BoundaryProbeBytes = 4096;

    public static InputRange[] CreateRanges(int fileDescriptor, long length)
    {
        var workerCount = GetWorkerCount(length);
        var ranges = new InputRange[workerCount];

        for (var i = 0; i < workerCount; i++)
        {
            var start = i == 0 ? 0 : MoveToNextLine(fileDescriptor, length * i / workerCount, length);
            var end = i == workerCount - 1 ? length : MoveToNextLine(fileDescriptor, length * (i + 1) / workerCount, length);
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

    private static int GetDefaultWorkerCount(long length, int processorCount)
    {
        if (processorCount == 10)
        {
            if (length >= 1L << 30)
            {
                return 8;
            }

            if (length >= 64L << 20)
            {
                return 9;
            }
        }

        return processorCount;
    }

    private static long MoveToNextLine(int fileDescriptor, long offset, long length)
    {
        Span<byte> span = stackalloc byte[BoundaryProbeBytes];
        fixed (byte* buffer = span)
        {
            while (offset < length)
            {
                var byteCount = (int)Math.Min(BoundaryProbeBytes, length - offset);
                var read = NativePRead.ReadFull(fileDescriptor, buffer, byteCount, offset);
                if (read == 0)
                {
                    return length;
                }

                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        return offset + i + 1;
                    }
                }

                offset += read;
            }
        }

        return length;
    }
}
