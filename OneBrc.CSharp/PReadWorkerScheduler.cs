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
            return ParseRange(fileDescriptor, ranges[0]);
        }

        var results = new StationTable(copyNames: true);
        var partials = new StationTable?[ranges.Length];
        var exceptions = new ExceptionDispatchInfo?[ranges.Length];
        var threads = new Thread[ranges.Length];
        var started = 0;

        try
        {
            for (var i = 0; i < ranges.Length; i++)
            {
                var index = i;
                var thread = new Thread(() =>
                {
                    try
                    {
                        partials[index] = ParseRange(fileDescriptor, ranges[index]);
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

    private static StationTable ParseRange(int fileDescriptor, InputRange range)
    {
        var table = new StationTable(copyNames: true);
        var buffer = (byte*)NativeMemory.AlignedAlloc(ChunkBytes, 64);
        if (buffer is null)
        {
            throw new OutOfMemoryException();
        }

        try
        {
            var offset = range.Start;
            while (offset < range.End)
            {
                var requested = (int)Math.Min(ChunkBytes, range.End - offset);
                var read = NativePRead.ReadFull(fileDescriptor, buffer, requested, offset);
                if (read == 0)
                {
                    break;
                }

                var parseLength = read;
                if (offset + read < range.End)
                {
                    parseLength = FindLastLineEnd(buffer, read);
                    if (parseLength == 0)
                    {
                        throw new InvalidDataException("Unable to find a complete row inside the pread chunk.");
                    }
                }

                MeasurementParser.ParseRangeInto(table, buffer, buffer + parseLength);
                offset += parseLength;
            }

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
