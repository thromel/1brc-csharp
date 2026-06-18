#if BRC_PHASE_PROFILE
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace OneBrc.CSharp;

internal static unsafe partial class PReadWorkerScheduler
{
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
}
#endif
