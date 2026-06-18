namespace OneBrc.CSharp;

internal readonly record struct StationResult(
    string Name,
    int Min,
    int Max,
    long Sum,
    long Count);
