namespace OneBrc.CSharp;

internal enum InputStrategy
{
    MemoryMapped,
    PRead
}

internal static class RuntimeOptions
{
    public const string InputStrategyVariable = "BRC_IO";
    public const string WorkerCountVariable = "BRC_THREADS";

    private const long PReadAutoThresholdBytes = 8L << 30;
    private const long LargeFileThresholdBytes = 1L << 30;
    private const long MidSizeFileThresholdBytes = 64L << 20;

    public static InputStrategy SelectInputStrategy(long inputLength)
    {
        var configured = Environment.GetEnvironmentVariable(InputStrategyVariable);
        if (string.Equals(configured, "pread", StringComparison.OrdinalIgnoreCase))
        {
            EnsurePReadSupported();
            return InputStrategy.PRead;
        }

        if (string.Equals(configured, "mmap", StringComparison.OrdinalIgnoreCase))
        {
            return InputStrategy.MemoryMapped;
        }

        return OperatingSystem.IsMacOS() && inputLength >= PReadAutoThresholdBytes
            ? InputStrategy.PRead
            : InputStrategy.MemoryMapped;
    }

    public static int GetMemoryMappedWorkerCount(long inputLength)
    {
        return GetWorkerCount(inputLength, GetMemoryMappedDefaultWorkerCount(inputLength, Environment.ProcessorCount));
    }

    public static int GetPReadWorkerCount(long inputLength)
    {
        return GetWorkerCount(inputLength, GetPReadDefaultWorkerCount(inputLength, Environment.ProcessorCount));
    }

    private static int GetWorkerCount(long inputLength, int defaultWorkerCount)
    {
        var workerCount = defaultWorkerCount;
        var configured = Environment.GetEnvironmentVariable(WorkerCountVariable);
        if (configured is not null &&
            int.TryParse(configured, out var configuredWorkerCount) &&
            configuredWorkerCount > 0)
        {
            workerCount = configuredWorkerCount;
        }

        var maxUsefulWorkers = Math.Max(1, (int)((inputLength + (1 << 20) - 1) >> 20));
        return Math.Min(workerCount, maxUsefulWorkers);
    }

    private static int GetMemoryMappedDefaultWorkerCount(long inputLength, int processorCount)
    {
        // mmap and pread have different bottlenecks on the measured Apple Silicon host,
        // so their tuned defaults intentionally stay separate.
        if (processorCount == 10)
        {
            if (inputLength >= LargeFileThresholdBytes)
            {
                return 13;
            }

            if (inputLength >= MidSizeFileThresholdBytes)
            {
                return 9;
            }
        }

        return processorCount;
    }

    private static int GetPReadDefaultWorkerCount(long inputLength, int processorCount)
    {
        // Full-size pread runs were read-bound; fewer workers reduced contention and RSS.
        if (processorCount == 10)
        {
            if (inputLength >= LargeFileThresholdBytes)
            {
                return 8;
            }

            if (inputLength >= MidSizeFileThresholdBytes)
            {
                return 9;
            }
        }

        return processorCount;
    }

    private static void EnsurePReadSupported()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("BRC_IO=pread is currently implemented for macOS.");
        }
    }
}
