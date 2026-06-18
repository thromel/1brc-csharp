namespace OneBrc.CSharp;

internal static class PReadSolver
{
    public static string Calculate(FileStream input)
    {
        var length = input.Length;
        var fileDescriptor = input.SafeFileHandle.DangerousGetHandle().ToInt32();
        var ranges = PReadRangePartitioner.CreateRanges(fileDescriptor, length);

        using var results = PReadWorkerScheduler.ParseRangesAndMerge(fileDescriptor, ranges);
        return ResultFormatter.Format(results);
    }
}
