# 1BRC C# Solution

This is a standalone .NET 10 implementation of the One Billion Row Challenge. It targets `net10.0`, the latest stable .NET major version; .NET 11 is currently preview-only. In this workspace, the scripts prefer the local .NET 10.0.301 SDK/runtime when present and otherwise fall back to `dotnet` on `PATH`.

It reads `measurements.txt` from the current directory by default, or a path passed as the first argument:

```sh
./calculate_average_csharp.sh /path/to/measurements.txt
```

The script builds the release DLL only when needed. To validate it from the cloned 1BRC repository, use the untracked wrapper there:

```sh
./test.sh csharp
```

For the fastest local path, publish a NativeAOT binary first:

```sh
./publish_aot.sh
./calculate_average_csharp.sh /path/to/measurements.txt
```

To force a specific SDK, set `DOTNET`:

```sh
DOTNET=/path/to/dotnet ./publish_aot.sh
DOTNET=/path/to/dotnet ./calculate_average_csharp.sh /path/to/measurements.txt
```

`calculate_average_csharp.sh` prefers the current-platform NativeAOT binary when it exists and is newer than the source; otherwise it falls back to the release JIT DLL.

By default the program uses `Environment.ProcessorCount` workers, capped for very small files, with a measured 10-core Apple Silicon heuristic for larger local benchmark files: 13 workers for files at least 1 GiB, and 9 workers for files at least 64 MiB. For local tuning experiments, set `BRC_THREADS`:

```sh
BRC_THREADS=8 ./calculate_average_csharp.sh /path/to/measurements.txt
```

The implementation uses a memory-mapped file, partitions work by line boundaries, parses every shard on fixed worker threads, incrementally merges partial native-memory byte-key hash tables as workers join, and decodes station names only while formatting final results. The hash tables are fixed-capacity and sized for the 1BRC 10,000-station contract.
