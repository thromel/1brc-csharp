using System.Runtime.CompilerServices;

[module: SkipLocalsInit]

namespace OneBrc.CSharp;

internal static class Program
{
    private const string DefaultInputPath = "measurements.txt";

    public static int Main(string[] args)
    {
        var inputPath = args.Length > 0 ? args[0] : DefaultInputPath;
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Input file not found: {inputPath}");
            return 1;
        }

        using var input = new FileStream(
            inputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.SequentialScan);

        if (input.Length == 0)
        {
            Console.WriteLine("{}");
            return 0;
        }

        Console.WriteLine(OneBrcSolver.Calculate(input));
        return 0;
    }
}
