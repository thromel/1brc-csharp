using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;

namespace OneBrc.CSharp;

internal static unsafe class PReadWorkerScheduler
{
    private const int ChunkBytes = 16 << 20;

    public static StationTable ParseRangesAndMerge(int fileDescriptor, InputRange[] ranges)
    {
        if (ranges.Length == 1)
        {
#if BRC_PHASE_PROFILE
            var singleProfile = new WorkerPhaseProfile[1];
            var table = ParseRange(fileDescriptor, ranges[0], 0, singleProfile);
            WriteProfiles(singleProfile);
            return table;
#else
            return ParseRange(fileDescriptor, ranges[0]);
#endif
        }

        var results = new StationTable(copyNames: true);
        var partials = new StationTable?[ranges.Length];
        var exceptions = new ExceptionDispatchInfo?[ranges.Length];
        var threads = new Thread[ranges.Length];
        var started = 0;
#if BRC_PHASE_PROFILE
        var profiles = new WorkerPhaseProfile[ranges.Length];
#endif

        try
        {
            for (var i = 0; i < ranges.Length; i++)
            {
                var index = i;
                var thread = new Thread(() =>
                {
                    try
                    {
#if BRC_PHASE_PROFILE
                        partials[index] = ParseRange(fileDescriptor, ranges[index], index, profiles);
#else
                        partials[index] = ParseRange(fileDescriptor, ranges[index]);
#endif
                    }
                    catch (Exception exception)
                    {
                        exceptions[index] = ExceptionDispatchInfo.Capture(exception);
                    }
                });
                threads[index] = thread;
                thread.Start();
                started++;
            }

            for (var i = 0; i < started; i++)
            {
                threads[i].Join();
                exceptions[i]?.Throw();

                var partial = partials[i];
                if (partial is null)
                {
                    throw new InvalidOperationException("pread worker did not produce a partial station table.");
                }

                partial.MergeInto(results);
                partial.Dispose();
                partials[i] = null;
            }

#if BRC_PHASE_PROFILE
            WriteProfiles(profiles);
#endif
            return results;
        }
        catch
        {
            for (var i = 0; i < started; i++)
            {
                if (threads[i].IsAlive)
                {
                    threads[i].Join();
                }
            }

            for (var i = 0; i < partials.Length; i++)
            {
                partials[i]?.Dispose();
            }

            results.Dispose();
            throw;
        }
    }

#if BRC_PHASE_PROFILE
    private static StationTable ParseRange(
        int fileDescriptor,
        InputRange range,
        int workerIndex,
        WorkerPhaseProfile[] profiles)
#else
    private static StationTable ParseRange(int fileDescriptor, InputRange range)
#endif
    {
        var table = new StationTable(copyNames: true);
        var buffer = (byte*)NativeMemory.AlignedAlloc(ChunkBytes, 64);
        if (buffer is null)
        {
            throw new OutOfMemoryException();
        }

#if BRC_PHASE_PROFILE
        WorkerPhaseProfile profile = default;
        profile.RangeBytes = range.End - range.Start;
#endif

        try
        {
            var offset = range.Start;
            while (offset < range.End)
            {
                var requested = (int)Math.Min(ChunkBytes, range.End - offset);
#if BRC_PHASE_PROFILE
                var readStart = Stopwatch.GetTimestamp();
#endif
                var read = NativePRead.ReadFull(fileDescriptor, buffer, requested, offset);
#if BRC_PHASE_PROFILE
                var readElapsed = Stopwatch.GetTimestamp() - readStart;
                profile.ReadTicks += readElapsed;
                profile.ReadCalls++;
                profile.BytesRead += read;
                if (read != requested)
                {
                    profile.ShortReadCalls++;
                }

                if (readElapsed > profile.MaxReadTicks)
                {
                    profile.MaxReadTicks = readElapsed;
                }
#endif
                if (read == 0)
                {
                    break;
                }

                var parseLength = read;
                if (offset + read < range.End)
                {
#if BRC_PHASE_PROFILE
                    var boundaryStart = Stopwatch.GetTimestamp();
#endif
                    parseLength = FindLastLineEnd(buffer, read);
#if BRC_PHASE_PROFILE
                    profile.BoundaryTicks += Stopwatch.GetTimestamp() - boundaryStart;
#endif
                    if (parseLength == 0)
                    {
                        throw new InvalidDataException("Unable to find a complete row inside the pread chunk.");
                    }
                }

#if BRC_PHASE_PROFILE
                var parseStart = Stopwatch.GetTimestamp();
#endif
                MeasurementParser.ParseRangeInto(table, buffer, buffer + parseLength);
#if BRC_PHASE_PROFILE
                var parseElapsed = Stopwatch.GetTimestamp() - parseStart;
                profile.ParseTicks += parseElapsed;
                profile.BytesParsed += parseLength;
                if (parseElapsed > profile.MaxParseTicks)
                {
                    profile.MaxParseTicks = parseElapsed;
                }
#endif
                offset += parseLength;
            }

#if BRC_PHASE_PROFILE
            profiles[workerIndex] = profile;
#endif
            return table;
        }
        catch
        {
            table.Dispose();
            throw;
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    private static int FindLastLineEnd(byte* buffer, int length)
    {
        for (var i = length - 1; i >= 0; i--)
        {
            if (buffer[i] == (byte)'\n')
            {
                return i + 1;
            }
        }

        return 0;
    }

#if BRC_PHASE_PROFILE
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct WorkerPhaseProfile
    {
        [FieldOffset(0)]
        public long ReadTicks;

        [FieldOffset(8)]
        public long BoundaryTicks;

        [FieldOffset(16)]
        public long ParseTicks;

        [FieldOffset(24)]
        public long BytesRead;

        [FieldOffset(32)]
        public long BytesParsed;

        [FieldOffset(40)]
        public long MaxReadTicks;

        [FieldOffset(48)]
        public long MaxParseTicks;

        [FieldOffset(56)]
        public int ReadCalls;

        [FieldOffset(60)]
        public int ShortReadCalls;

        [FieldOffset(64)]
        public long RangeBytes;
    }

    private static void WriteProfiles(WorkerPhaseProfile[] profiles)
    {
        var criticalWorker = 0;
        long criticalTicks = 0;
        long totalReadTicks = 0;
        long totalBoundaryTicks = 0;
        long totalParseTicks = 0;
        long totalBytesRead = 0;
        long totalBytesParsed = 0;

        for (var i = 0; i < profiles.Length; i++)
        {
            var profile = profiles[i];
            var totalTicks = profile.ReadTicks + profile.BoundaryTicks + profile.ParseTicks;
            if (totalTicks > criticalTicks)
            {
                criticalTicks = totalTicks;
                criticalWorker = i;
            }

            totalReadTicks += profile.ReadTicks;
            totalBoundaryTicks += profile.BoundaryTicks;
            totalParseTicks += profile.ParseTicks;
            totalBytesRead += profile.BytesRead;
            totalBytesParsed += profile.BytesParsed;

            Console.Error.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"PROFILE worker={i} rangeBytes={profile.RangeBytes} readMs={ToMilliseconds(profile.ReadTicks)} boundaryMs={ToMilliseconds(profile.BoundaryTicks)} parseMs={ToMilliseconds(profile.ParseTicks)} totalMs={ToMilliseconds(totalTicks)} maxReadMs={ToMilliseconds(profile.MaxReadTicks)} maxParseMs={ToMilliseconds(profile.MaxParseTicks)} readCalls={profile.ReadCalls} shortReads={profile.ShortReadCalls} bytesRead={profile.BytesRead} bytesParsed={profile.BytesParsed}"));
        }

        var critical = profiles[criticalWorker];
        var criticalReadBoundaryTicks = critical.ReadTicks + critical.BoundaryTicks;
        var criticalTotalTicks = critical.ReadTicks + critical.BoundaryTicks + critical.ParseTicks;

        Console.Error.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"PROFILE summary workers={profiles.Length} criticalWorker={criticalWorker} criticalTotalMs={ToMilliseconds(criticalTotalTicks)} criticalReadBoundaryPct={ToPercent(criticalReadBoundaryTicks, criticalTotalTicks)} criticalParsePct={ToPercent(critical.ParseTicks, criticalTotalTicks)} readSumMs={ToMilliseconds(totalReadTicks)} boundarySumMs={ToMilliseconds(totalBoundaryTicks)} parseSumMs={ToMilliseconds(totalParseTicks)} bytesRead={totalBytesRead} bytesParsed={totalBytesParsed}"));
    }

    private static string ToMilliseconds(long ticks)
    {
        return (ticks * 1000.0 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture);
    }

    private static string ToPercent(long ticks, long totalTicks)
    {
        if (totalTicks == 0)
        {
            return "0.000";
        }

        return (ticks * 100.0 / totalTicks).ToString("F3", CultureInfo.InvariantCulture);
    }
#endif
}
