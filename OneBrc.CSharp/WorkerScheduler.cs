namespace OneBrc.CSharp;

internal static unsafe class WorkerScheduler
{
    public static StationTable ParseRangesAndMerge(byte* basePointer, InputRange[] ranges)
    {
        if (ranges.Length == 1)
        {
            return MeasurementParser.ParseRange(basePointer + ranges[0].Start, basePointer + ranges[0].End);
        }

        var results = new StationTable();
        var partials = new StationTable?[ranges.Length];
        var threads = new Thread[ranges.Length];
        var started = 0;

        try
        {
            for (var i = 0; i < ranges.Length; i++)
            {
                var index = i;
                var thread = new Thread(() =>
                {
                    partials[index] = MeasurementParser.ParseRange(basePointer + ranges[index].Start, basePointer + ranges[index].End);
                });
                threads[index] = thread;
                thread.Start();
                started++;
            }

            for (var i = 0; i < started; i++)
            {
                threads[i].Join();
                var partial = partials[i];
                partial!.MergeInto(results);
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
}
