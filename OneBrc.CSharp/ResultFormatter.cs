using System.Text;

namespace OneBrc.CSharp;

internal static unsafe class ResultFormatter
{
    public static string Format(StationTable results)
    {
        var entries = results.ToArray();
        var formatted = new StationResult[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            ref readonly var entry = ref entries[i];
            formatted[i] = new StationResult(
                Encoding.UTF8.GetString(new ReadOnlySpan<byte>(entry.NamePointer, entry.NameLength)),
                entry.Min,
                entry.Max,
                entry.Sum,
                entry.Count);
        }

        Array.Sort(formatted, static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));

        var builder = new StringBuilder(formatted.Length * 32);
        builder.Append('{');

        var first = true;
        foreach (var entry in formatted)
        {
            if (!first)
            {
                builder.Append(", ");
            }

            first = false;
            builder.Append(entry.Name);
            builder.Append('=');
            AppendTenths(builder, entry.Min);
            builder.Append('/');
            AppendTenths(builder, DivideTowardPositive(entry.Sum, entry.Count));
            builder.Append('/');
            AppendTenths(builder, entry.Max);
        }

        builder.Append('}');
        return builder.ToString();
    }

    private static long DivideTowardPositive(long numerator, long denominator)
    {
        var quotient = numerator / denominator;
        var remainder = numerator % denominator;
        return remainder > 0 ? quotient + 1 : quotient;
    }

    private static void AppendTenths(StringBuilder builder, long tenths)
    {
        if (tenths < 0)
        {
            builder.Append('-');
            tenths = -tenths;
        }

        builder.Append(tenths / 10);
        builder.Append('.');
        builder.Append(tenths % 10);
    }
}
