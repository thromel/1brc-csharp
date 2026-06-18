# 1BRC C# Solution

This is a standalone .NET 10 implementation of the One Billion Row Challenge. It targets `net10.0`, the latest stable .NET major version; .NET 11 is currently preview-only. In this workspace, the scripts prefer the local bundled .NET 10 SDK/runtime when present and otherwise fall back to `dotnet` on `PATH`.

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

By default the program uses `Environment.ProcessorCount` workers, capped for very small files, with measured 10-core Apple Silicon heuristics for larger local benchmark files. The mmap path uses 13 workers for files at least 1 GiB and 9 workers for files at least 64 MiB. The full-size macOS `pread` path uses 8 workers after full-1B confirmation showed better wall time, CPU time, and RSS than the older 13-worker setting. For local tuning experiments, set `BRC_THREADS`:

```sh
BRC_THREADS=8 ./calculate_average_csharp.sh /path/to/measurements.txt
```

The default input path is workload-size aware. Files below 8 GiB use the memory-mapped parser, which remains best on bounded 100M-style local checks. On macOS, files at least 8 GiB use a native `pread` chunk pipeline which avoids the mmap page-fault cost observed on full 1B inputs. To force either path for experiments, set `BRC_IO`:

```sh
BRC_IO=mmap ./calculate_average_csharp.sh /path/to/measurements.txt
BRC_IO=pread ./calculate_average_csharp.sh /path/to/measurements.txt
```

The implementation partitions work by line boundaries, parses every shard on fixed worker threads, incrementally merges partial native-memory byte-key hash tables as workers join, and decodes station names only while formatting final results. The mmap path stores station-name pointers into the mapped file; the `pread` path stores only unique station names in native arenas so chunk buffers can be reused safely. The hash tables are fixed-capacity and sized for the 1BRC 10,000-station contract. On ARM64 hardware with CRC32C support, station keys use the hardware CRC32C intrinsic for the table hash, with a portable scalar mixer fallback for other targets.

The project is organized as focused internal components: `Program` handles process I/O, `OneBrcSolver` selects the input strategy, `RangePartitioner`/`WorkerScheduler` handle mmap work, `PReadRangePartitioner`/`PReadWorkerScheduler` handle native chunked reads, `MeasurementParser` contains the allocation-free row parser, `StationKey`/`StationTable`/`StationEntry` own aggregation storage, and `ResultFormatter` performs the final sorted rendering.

For a deeper component map, runtime tuning policy, and guidance for another CPU or OS, see [ARCHITECTURE.md](ARCHITECTURE.md).
