#if BRC_PHASE_PROFILE
using System.Diagnostics;
#endif
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace OneBrc.CSharp;

internal static unsafe partial class PReadWorkerScheduler
{
    // Full-size macOS sweeps kept 16 MiB as the best shared-descriptor pread geometry.
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
}
